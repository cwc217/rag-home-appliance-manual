using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace HomeApplianceManualRag.Web.Documents;

public sealed record ImageAnalysisResult(string? OcrText, string? Description);

public interface IImageAnalyzer
{
    Task<ImageAnalysisResult> AnalyzeAsync(
        ParsedImage image,
        int pageNumber,
        CancellationToken cancellationToken = default);
}

public sealed class AzureOpenAiImageAnalyzer(
    IOptions<AzureOpenAiOptions> azureOptions,
    IOptions<ImageAnalysisOptions> analysisOptions,
    ILogger<AzureOpenAiImageAnalyzer> logger) : IImageAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly ChatClient client = new AzureOpenAIClient(
        new Uri(azureOptions.Value.Endpoint),
        new AzureKeyCredential(azureOptions.Value.ApiKey))
        .GetChatClient(azureOptions.Value.ChatDeployment);

    public async Task<ImageAnalysisResult> AnalyzeAsync(
        ParsedImage image,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(analysisOptions.Value.TimeoutSeconds));

        var prompt = $"""
            Analyze this appliance-manual image from page {pageNumber}.
            Return JSON only with exactly these fields:
            ocrText and description.
            ocrText must contain the visible text exactly where legible, including
            labels, button names, warnings, units, and error codes.
            description must explain the useful visual meaning, relationships,
            arrows, controls, and operating steps. Use null only when no value
            can be determined. Do not invent details.
            """;
        var content = new[]
        {
            ChatMessageContentPart.CreateTextPart(prompt),
            ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(image.Data),
                image.MimeType)
        };
        try
        {
            var response = await client.CompleteChatAsync(
                new ChatMessage[]
                {
                    new SystemChatMessage("You extract reliable information from appliance manual images."),
                    new UserChatMessage(content)
                },
                cancellationToken: timeout.Token);
            var text = response.Value.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return new(null, null);
            var result = JsonSerializer.Deserialize<ImageAnalysisResponse>(
                ExtractJson(text), JsonOptions);
            return result is null
                ? new(null, null)
                : new(Clean(result.OcrText), Clean(result.Description));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImageAnalysisException("Image analysis timed out.", retryable: true);
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Azure image analysis failed for page {PageNumber}.", pageNumber);
            throw new ImageAnalysisException("Azure image analysis failed.", retryable: ex.Status >= 500, ex);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Azure image analysis returned invalid JSON for page {PageNumber}.", pageNumber);
            throw new ImageAnalysisException("Azure image analysis returned invalid JSON.", inner: ex);
        }
    }

    private static string ExtractJson(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        return start >= 0 && end > start ? value[start..(end + 1)] : value;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ImageAnalysisResponse(
        [property: JsonPropertyName("ocrText")] string? OcrText,
        [property: JsonPropertyName("description")] string? Description);
}

public sealed class ImageAnalysisException(
    string message,
    bool retryable = false,
    Exception? inner = null) : InvalidOperationException(message, inner)
{
    public bool Retryable { get; } = retryable;
}

public sealed class NoOpImageAnalyzer : IImageAnalyzer
{
    public Task<ImageAnalysisResult> AnalyzeAsync(
        ParsedImage image,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ImageAnalysisResult(null, null));
    }
}
