using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Web.Documents;

public sealed record ManualMetadataExtractionResult(
    string? ProductName,
    string? ApplianceCategory,
    string? Brand,
    string? ModelNumber,
    string? Language,
    double Confidence,
    MetadataExtractionStatus Status);

public interface IManualMetadataExtractor
{
    Task<ManualMetadataExtractionResult> ExtractAsync(
        string displayName,
        ParsedDocument document,
        CancellationToken cancellationToken = default);
}

public sealed class AzureOpenAiManualMetadataExtractor(
    IChatProvider chatProvider,
    IOptions<MetadataExtractionOptions> options,
    ILogger<AzureOpenAiManualMetadataExtractor> logger) : IManualMetadataExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    public async Task<ManualMetadataExtractionResult> ExtractAsync(
        string displayName,
        ParsedDocument document,
        CancellationToken cancellationToken = default)
    {
        var sourceText = BuildSourceText(document, options.Value.MaxSourceCharacters);
        if (string.IsNullOrWhiteSpace(sourceText))
            return Failed();

        var citationSuffix = CreateCitationSuffixPattern(displayName, document.Pages);
        var prompt = $$"""
            Extract appliance metadata from the document text below.
            Return JSON only, with exactly these fields:
            productName, applianceCategory, brand, modelNumber, language, confidence.
            Use null when a value is not stated. Do not guess or infer missing values.
            confidence must be a number from 0 to 1 describing the complete extraction.
            applianceCategory should be a concise English category such as Refrigerator,
            Washing Machine, Air Conditioner, Oven, Microwave, Dishwasher, or Other.
            Normalize whitespace but preserve model numbers exactly.

            File name: {{displayName}}
            Document text:
            {{sourceText}}
            """;

        try
        {
            var response = new StringBuilder();
            await foreach (var part in chatProvider.StreamMetadataAsync(prompt, cancellationToken))
                response.Append(part);

            var metadata = JsonSerializer.Deserialize<ManualMetadataResponse>(
                ExtractJson(response.ToString()), JsonOptions);
            if (metadata is null)
                return Failed();

            var confidence = Math.Clamp(metadata.Confidence, 0, 1);
            var status = confidence >= options.Value.MinimumConfidence
                ? MetadataExtractionStatus.Completed
                : MetadataExtractionStatus.NeedsReview;
            return new(
                Clean(metadata.ProductName, citationSuffix),
                Clean(metadata.ApplianceCategory, citationSuffix),
                Clean(metadata.Brand, citationSuffix),
                Clean(metadata.ModelNumber, citationSuffix),
                Clean(metadata.Language, citationSuffix),
                confidence,
                status);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Manual metadata response was not valid JSON.");
            return Failed();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Manual metadata extraction failed.");
            return Failed();
        }
    }

    private static string BuildSourceText(ParsedDocument document, int maxCharacters)
    {
        var pages = document.Pages
            .OrderBy(page => page.PageNumber)
            .Select(page =>
                $"[Page {page.PageNumber}]\n" +
                string.Join("\n", page.Blocks.OrderBy(block => block.Order).Select(block => block.Text.Trim())))
            .ToList();
        var prioritized = pages.Take(5)
            .Concat(pages.Skip(5).Where(page =>
                page.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                page.Contains("brand", StringComparison.OrdinalIgnoreCase) ||
                page.Contains("product", StringComparison.OrdinalIgnoreCase)))
            .Distinct();
        return Limit(string.Join("\n\n", prioritized), maxCharacters);
    }

    private static string ExtractJson(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        return start >= 0 && end > start ? response[start..(end + 1)] : response;
    }

    private static string? Clean(string? value, Regex citationSuffix)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = value.Trim();
        Match match;
        while ((match = citationSuffix.Match(cleaned)).Success)
            cleaned = cleaned[..match.Index].TrimEnd();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static Regex CreateCitationSuffixPattern(
        string displayName,
        IEnumerable<ParsedPage> pages)
    {
        var pageNumbers = pages
            .Select(page => page.PageNumber)
            .Distinct()
            .Order()
            .Select(page => page.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        if (pageNumbers.Length == 0)
            return new Regex(@"(?!)", RegexOptions.CultureInvariant);

        return new Regex(
            $@"\s*\[\s*{Regex.Escape(displayName)}\s*,\s*p\.\s*(?:{string.Join("|", pageNumbers)})\s*\]\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Limit(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[..maxCharacters];

    private static ManualMetadataExtractionResult Failed() =>
        new(null, null, null, null, null, 0, MetadataExtractionStatus.Failed);

    private sealed record ManualMetadataResponse(
        [property: JsonPropertyName("productName")] string? ProductName,
        [property: JsonPropertyName("applianceCategory")] string? ApplianceCategory,
        [property: JsonPropertyName("brand")] string? Brand,
        [property: JsonPropertyName("modelNumber")] string? ModelNumber,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("confidence")] double Confidence);
}
