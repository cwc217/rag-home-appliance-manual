using System.Text.Json;
using System.Net.Http.Headers;
using HomeApplianceManualRag.Web.Configuration;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Web.Documents;

public sealed record DocumentParseRequest(
    Guid DocumentId,
    string DisplayName,
    Stream Content,
    CancellationToken CancellationToken = default);

public sealed record ParsedDocument(IReadOnlyList<ParsedPage> Pages, string ParserVersion);

public sealed record ParsedPage(int PageNumber, IReadOnlyList<ParsedBlock> Blocks);

public sealed record ParsedBlock(
    string Text,
    string BlockType,
    int Order,
    string? HeadingPath = null,
    string? SourceReference = null);

public sealed class DocumentParseException(string message, bool retryable = false, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public bool Retryable { get; } = retryable;
}

public interface IDocumentParser
{
    Task<ParsedDocument> ParseAsync(DocumentParseRequest request);
}

public sealed class DoclingHttpParser(
    HttpClient httpClient,
    IOptions<DoclingOptions> options) : IDocumentParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ParsedDocument> ParseAsync(DocumentParseRequest request)
    {
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(request.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "files", Path.GetFileName(request.DisplayName));
        form.Add(new StringContent("pdf"), "from_formats");
        form.Add(new StringContent("json"), "to_formats");
        form.Add(new StringContent("false"), "do_ocr");
        form.Add(new StringContent("true"), "do_table_structure");
        form.Add(new StringContent("true"), "do_pdf_heading_hierarchy");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync("v1/convert/file", form, timeout.Token);
        }
        catch (OperationCanceledException) when (!request.CancellationToken.IsCancellationRequested)
        {
            throw new DocumentParseException("Docling parsing timed out.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new DocumentParseException(
                "Docling service could not be reached.", retryable: true, ex);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new DocumentParseException(
                    $"Docling service returned HTTP {(int)response.StatusCode}: {TrimError(body)}",
                    retryable: (int)response.StatusCode >= 500);

            var parsed = DeserializeResponse(body);
            if (!string.Equals(parsed.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parsed.Status, "partial_success", StringComparison.OrdinalIgnoreCase))
                throw new DocumentParseException(
                    $"Docling failed to parse the document: {TrimError(parsed.Errors)}");

            var result = ToParsedDocument(parsed.Document);
            if (result.Pages.Count == 0 || result.Pages.All(x => x.Blocks.All(block => string.IsNullOrWhiteSpace(block.Text))))
                throw new DocumentParseException("The document has no extractable text.");
            return result;
        }
    }

    private static DoclingResponse DeserializeResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DoclingResponse>(json, JsonOptions)
                ?? throw new DocumentParseException("Docling returned an empty result.");
        }
        catch (JsonException ex)
        {
            throw new DocumentParseException("Docling returned malformed JSON.", retryable: true, ex);
        }
    }

    private static ParsedDocument ToParsedDocument(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("json_content", out var content) ||
            content.ValueKind != JsonValueKind.Object)
            throw new DocumentParseException("Docling returned no structured JSON document.");

        var blocksByPage = new SortedDictionary<int, List<ParsedBlock>>();
        AddItems(content, "texts", blocksByPage, "text");
        AddItems(content, "tables", blocksByPage, "table");
        AddItems(content, "pictures", blocksByPage, "picture");
        return new ParsedDocument(
            blocksByPage.Select(page => new ParsedPage(page.Key, page.Value)).ToArray(),
            content.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? $"docling-{version.GetString()}"
                : "docling");
    }

    private static void AddItems(
        JsonElement content,
        string propertyName,
        SortedDictionary<int, List<ParsedBlock>> blocksByPage,
        string defaultBlockType)
    {
        if (!content.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        var order = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("prov", out var provenance) || provenance.ValueKind != JsonValueKind.Array)
                continue;
            var text = GetText(item, defaultBlockType);
            foreach (var proof in provenance.EnumerateArray())
            {
                if (!proof.TryGetProperty("page_no", out var page) || !page.TryGetInt32(out var pageNumber) || pageNumber < 1)
                    continue;
                if (!blocksByPage.TryGetValue(pageNumber, out var blocks))
                    blocksByPage[pageNumber] = blocks = [];
                var blockType = item.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String
                    ? label.GetString() ?? defaultBlockType
                    : defaultBlockType;
                var headingPath = blockType.Contains("heading", StringComparison.OrdinalIgnoreCase) ||
                                  blockType.Contains("section_header", StringComparison.OrdinalIgnoreCase) ||
                                  blockType.Contains("title", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : null;
                blocks.Add(new ParsedBlock(text, blockType, order++, headingPath, $"page:{pageNumber}"));
            }
        }
    }

    private static string GetText(JsonElement item, string blockType)
    {
        foreach (var name in new[] { "text", "orig", "exported" })
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        return $"[{blockType}]";
    }

    private static string TrimError(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text)
            ? "No diagnostic was returned."
            : text.Trim()[..Math.Min(text.Trim().Length, 1000)];
    }

    private static string TrimError(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "No diagnostic was returned."
            : value.Trim()[..Math.Min(value.Trim().Length, 1000)];

    private sealed class DoclingResponse
    {
        public string? Status { get; set; }
        public JsonElement Document { get; set; }
        public JsonElement Errors { get; set; }
    }
}
