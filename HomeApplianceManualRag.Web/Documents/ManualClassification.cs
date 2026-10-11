using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeApplianceManualRag.Web.Configuration;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Web.Documents;

public sealed record ManualClassificationResult(bool IsManual, double Confidence);

public interface IManualClassifier
{
    Task<ManualClassificationResult> ClassifyAsync(
        string displayName,
        ParsedDocument document,
        CancellationToken cancellationToken = default);
}

public sealed class ManualClassificationException(
    string message,
    bool retryable = false,
    Exception? inner = null) : InvalidOperationException(message, inner)
{
    public bool Retryable { get; } = retryable;
}

public sealed class TypeSafeManualClassifier(
    HttpClient httpClient,
    IOptions<TypeSafeOptions> options)
    : IManualClassifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ManualClassificationResult> ClassifyAsync(
        string displayName,
        ParsedDocument document,
        CancellationToken cancellationToken = default)
    {
        var state = string.Join(
            "\n\n",
            document.Pages
                .Select(page => $"Page {page.PageNumber}\n{string.Join("\n", page.Blocks.Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)))}"))
            .Trim();
        if (string.IsNullOrWhiteSpace(state))
            throw new ManualClassificationException("The document has no extractable text.");

        if (state.Length > options.Value.MaxStateCharacters)
            state = state[..options.Value.MaxStateCharacters];

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
                state = new
                {
                    file_name = displayName,
                    extracted_text = state
                },
                model = options.Value.Model,
                questions = new
                {
                    document_type = new
                    {
                        type = "choice",
                        instructions = "What kind of document is this based on its extracted text?",
                        criteria = new
                        {
                            appliance_manual = "A household appliance user, installation, operating, safety, troubleshooting, or care manual.",
                            other_document = "Anything else, including invoices, catalogues, product brochures, policies, reports, or unrelated documents."
                        }
                    }
                }
            }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ManualClassificationException(
                "The manual classification service could not be reached.",
                retryable: true,
                ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ManualClassificationException(
                "The manual classification service timed out.",
                retryable: true);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ManualClassificationException(
                    $"The manual classification service returned HTTP {(int)response.StatusCode}.",
                    retryable: response.StatusCode is HttpStatusCode.TooManyRequests or
                        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
                        HttpStatusCode.GatewayTimeout);

            try
            {
                using var json = JsonDocument.Parse(body);
                var answer = json.RootElement
                    .GetProperty("answers")
                    .GetProperty("document_type");
                var choice = answer.GetProperty("choice").GetString();
                var confidence = answer.GetProperty("confidence").GetDouble();
                if (string.IsNullOrWhiteSpace(choice) || confidence is < 0 or > 1)
                    throw new JsonException("The classification answer was invalid.");

                return new ManualClassificationResult(
                    string.Equals(choice, "appliance_manual", StringComparison.Ordinal),
                    confidence);
            }
            catch (JsonException ex)
            {
                throw new ManualClassificationException(
                    "The manual classification service returned an invalid response.",
                    retryable: true,
                    ex);
            }
        }
    }
}
