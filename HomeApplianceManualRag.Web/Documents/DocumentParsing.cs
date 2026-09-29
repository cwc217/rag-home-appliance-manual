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

public sealed record ParsedImage(byte[] Data, string MimeType);

public sealed record ParsedBlock(
    string Text,
    string BlockType,
    int Order,
    string? HeadingPath = null,
    string? SourceReference = null,
    ParsedImage? Image = null,
    ParsedTable? Table = null);

public sealed record ParsedTable(
    int RowCount,
    int ColumnCount,
    IReadOnlyList<ParsedTableCell> Cells);

public sealed record ParsedTableCell(
    int Row,
    int Column,
    string Text,
    int RowSpan = 1,
    int ColumnSpan = 1,
    bool IsColumnHeader = false,
    bool IsRowHeader = false);

public sealed class DocumentParseException(
    string message,
    bool retryable = false,
    Exception? inner = null,
    int? httpStatusCode = null)
    : InvalidOperationException(message, inner)
{
    public bool Retryable { get; } = retryable;
    public int? HttpStatusCode { get; } = httpStatusCode;
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
        form.Add(new StringContent("embedded"), "image_export_mode");
        form.Add(new StringContent(options.Value.IncludeImages ? "true" : "false"), "include_images");
        form.Add(new StringContent(options.Value.ImagesScale.ToString(System.Globalization.CultureInfo.InvariantCulture)), "images_scale");

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
                    retryable: (int)response.StatusCode >= 500,
                    httpStatusCode: (int)response.StatusCode);

            var parsed = DeserializeResponse(body);
            if (!string.Equals(parsed.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parsed.Status, "partial_success", StringComparison.OrdinalIgnoreCase))
                throw new DocumentParseException(
                    $"Docling failed to parse the document: {TrimError(parsed.Errors)}");

            var result = ToParsedDocument(parsed.Document, options.Value.MaxEmbeddedImageBytes);
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

    private static ParsedDocument ToParsedDocument(JsonElement document, int maxEmbeddedImageBytes)
    {
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("json_content", out var content) ||
            content.ValueKind != JsonValueKind.Object)
            throw new DocumentParseException("Docling returned no structured JSON document.");

        var blocksByPage = new SortedDictionary<int, List<ParsedBlock>>();
        AddItems(content, "texts", blocksByPage, "text");
        AddItems(content, "tables", blocksByPage, "table");
        AddItems(content, "pictures", blocksByPage, "picture", maxEmbeddedImageBytes);
        foreach (var page in blocksByPage.Values)
            RemoveDuplicateBlocks(page);
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
        string defaultBlockType,
        int maxEmbeddedImageBytes = 0)
    {
        if (!content.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        var order = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("prov", out var provenance) || provenance.ValueKind != JsonValueKind.Array)
                continue;
            var table = string.Equals(defaultBlockType, "table", StringComparison.OrdinalIgnoreCase)
                ? GetStructuredTable(item)
                : null;
            var text = GetText(item, defaultBlockType, table);
            var image = string.Equals(defaultBlockType, "picture", StringComparison.OrdinalIgnoreCase)
                ? GetEmbeddedImage(item, maxEmbeddedImageBytes)
                : null;
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
                blocks.Add(new ParsedBlock(text, blockType, order++, headingPath, $"page:{pageNumber}", image, table));
            }
        }
    }

    private static string GetText(JsonElement item, string blockType, ParsedTable? table)
    {
        if (table is not null)
            return FormatTable(table);

        foreach (var name in new[] { "text", "orig", "exported" })
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) &&
                !string.Equals(value.GetString(), $"[{blockType}]", StringComparison.OrdinalIgnoreCase))
                return RemoveConsecutiveDuplicateLines(value.GetString());
        return $"[{blockType}]";
    }

    private static ParsedTable? GetStructuredTable(JsonElement item)
    {
        if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            data = item;

        foreach (var propertyName in new[] { "table_cells", "cells" })
        {
            if (data.TryGetProperty(propertyName, out var cells) && cells.ValueKind == JsonValueKind.Array)
            {
                var parsedCells = ReadTableCells(cells);
                if (parsedCells.Count > 0)
                    return CreateTable(data, parsedCells);
            }
        }

        if (data.TryGetProperty("grid", out var grid) && grid.ValueKind == JsonValueKind.Array)
        {
            var parsedCells = new List<ParsedTableCell>();
            var rowIndex = 0;
            foreach (var row in grid.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array)
                {
                    rowIndex++;
                    continue;
                }

                var columnIndex = 0;
                foreach (var cell in row.EnumerateArray())
                {
                    var text = GetCellText(cell);
                    if (!string.IsNullOrWhiteSpace(text))
                        parsedCells.Add(new ParsedTableCell(rowIndex, columnIndex, text));
                    columnIndex++;
                }
                rowIndex++;
            }
            if (parsedCells.Count > 0)
                return new ParsedTable(rowIndex, parsedCells.Max(cell => cell.Column) + 1, parsedCells);
        }

        return null;
    }

    private static ParsedTable CreateTable(JsonElement data, List<ParsedTableCell> cells)
    {
        var rowCount = GetInteger(data, "num_rows") ??
                       cells.Max(cell => cell.Row + cell.RowSpan);
        var columnCount = GetInteger(data, "num_cols") ??
                          cells.Max(cell => cell.Column + cell.ColumnSpan);
        return new ParsedTable(rowCount, columnCount, cells);
    }

    private static ParsedImage? GetEmbeddedImage(JsonElement item, int maxBytes)
    {
        if (!item.TryGetProperty("image", out var image) ||
            image.ValueKind != JsonValueKind.Object ||
            !image.TryGetProperty("uri", out var uri) ||
            uri.ValueKind != JsonValueKind.String)
            return null;

        var value = uri.GetString();
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var separator = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (separator <= "data:".Length)
            return null;

        var mimeType = value["data:".Length..separator];
        var encoded = value[(separator + ";base64,".Length)..];
        if (string.IsNullOrWhiteSpace(mimeType) || string.IsNullOrWhiteSpace(encoded))
            return null;

        try
        {
            var estimatedBytes = encoded.Length * 3L / 4;
            if (estimatedBytes > maxBytes)
                return null;
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length <= maxBytes ? new ParsedImage(bytes, mimeType) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static List<ParsedTableCell> ReadTableCells(JsonElement cells)
    {
        var positioned = new List<ParsedTableCell>();
        var fallbackRow = 0;
        foreach (var cell in cells.EnumerateArray())
        {
            var text = GetCellText(cell);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var row = GetInteger(cell, "start_row_offset_idx")
                ?? GetInteger(cell, "row_idx")
                ?? GetInteger(cell, "row")
                ?? fallbackRow;
            var column = GetInteger(cell, "start_col_offset_idx")
                ?? GetInteger(cell, "col_idx")
                ?? GetInteger(cell, "column")
                ?? 0;
            positioned.Add(new ParsedTableCell(
                row,
                column,
                text,
                Math.Max(1, GetInteger(cell, "row_span") ?? 1),
                Math.Max(1, GetInteger(cell, "col_span") ?? 1),
                GetBoolean(cell, "column_header"),
                GetBoolean(cell, "row_header")));
            fallbackRow = row;
        }

        return positioned
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Column)
            .ToList();
    }

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string? GetCellText(JsonElement cell)
    {
        if (cell.ValueKind == JsonValueKind.String)
            return NormalizeWhitespace(cell.GetString());
        if (cell.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in new[] { "text", "orig", "exported", "value" })
            if (cell.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return NormalizeWhitespace(value.GetString());
        return null;
    }

    private static int? GetInteger(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string FormatTable(ParsedTable table)
    {
        var rows = table.Cells
            .GroupBy(cell => cell.Row)
            .OrderBy(row => row.Key)
            .Select(row => string.Join(" | ", row.OrderBy(cell => cell.Column).Select(cell => cell.Text)));
        return "[Table]\n" + string.Join('\n', rows);
    }

    private static void RemoveDuplicateBlocks(List<ParsedBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        blocks.RemoveAll(block =>
        {
            var normalized = NormalizeWhitespace(block.Text);
            var key = $"{block.BlockType}\n{normalized}";
            return string.IsNullOrWhiteSpace(normalized) || !seen.Add(key);
        });
    }

    private static string NormalizeWhitespace(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string RemoveConsecutiveDuplicateLines(string? value)
    {
        var lines = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var result = new List<string>(lines.Length);
        string? previousLine = null;

        foreach (var line in lines)
        {
            var normalizedLine = line.Trim();
            if (normalizedLine.Length > 0 &&
                string.Equals(normalizedLine, previousLine, StringComparison.Ordinal))
                continue;

            result.Add(normalizedLine);
            previousLine = normalizedLine.Length == 0 ? null : normalizedLine;
        }

        return string.Join('\n', result).Trim();
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
