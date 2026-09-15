using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
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

public sealed class DoclingProcessParser(
    IOptions<DoclingOptions> options,
    ILogger<DoclingProcessParser> logger) : IDocumentParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ParsedDocument> ParseAsync(DocumentParseRequest request)
    {
        var root = Path.Combine(Path.GetTempPath(), "home-appliance-manual-rag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputPath = Path.Combine(root, Path.GetFileName(request.DisplayName));
        var outputPath = Path.Combine(root, "docling.json");
        try
        {
            await using (var input = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await request.Content.CopyToAsync(input, request.CancellationToken);

            var arguments = options.Value.ArgumentsTemplate
                .Replace("{input}", Quote(inputPath), StringComparison.Ordinal)
                .Replace("{output}", Quote(outputPath), StringComparison.Ordinal);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = options.Value.ExecutablePath,
                    Arguments = arguments,
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            if (!process.Start())
                throw new DocumentParseException("Docling process could not be started.", retryable: true);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new DocumentParseException(
                    $"Docling failed to parse the document: {TrimError(error)}", retryable: true);

            var json = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, timeout.Token) : await stdout;
            var parsed = JsonSerializer.Deserialize<DoclingJsonDocument>(json, JsonOptions)
                ?? throw new DocumentParseException("Docling returned an empty result.");
            var result = parsed.ToParsedDocument();
            if (result.Pages.Count == 0 || result.Pages.All(x => x.Blocks.All(block => string.IsNullOrWhiteSpace(block.Text))))
                throw new DocumentParseException("The document has no extractable text.");
            return result;
        }
        catch (OperationCanceledException) when (!request.CancellationToken.IsCancellationRequested)
        {
            throw new DocumentParseException("Docling parsing timed out.", retryable: true);
        }
        catch (Win32Exception ex)
        {
            throw new DocumentParseException(
                $"Docling executable '{options.Value.ExecutablePath}' is unavailable.", retryable: false, ex);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) { logger.LogDebug(ex, "Failed to remove Docling temporary directory {Path}", root); }
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static string TrimError(string value) => string.IsNullOrWhiteSpace(value) ? "No diagnostic was returned." : value.Trim()[..Math.Min(value.Trim().Length, 1000)];

    private sealed class DoclingJsonDocument
    {
        public string? ParserVersion { get; set; }
        public List<DoclingJsonPage> Pages { get; set; } = [];

        public ParsedDocument ToParsedDocument() =>
            new(Pages.Select(page => new ParsedPage(
                page.PageNumber,
                page.Blocks.OrderBy(block => block.Order)
                    .Select(block => new ParsedBlock(block.Text ?? string.Empty, block.BlockType ?? "text", block.Order, block.HeadingPath, block.SourceReference))
                    .ToArray())).ToArray(), ParserVersion ?? "docling");
    }

    private sealed class DoclingJsonPage
    {
        public int PageNumber { get; set; }
        public List<DoclingJsonBlock> Blocks { get; set; } = [];
    }

    private sealed class DoclingJsonBlock
    {
        public string? Text { get; set; }
        public string? BlockType { get; set; }
        public int Order { get; set; }
        public string? HeadingPath { get; set; }
        public string? SourceReference { get; set; }
    }
}
