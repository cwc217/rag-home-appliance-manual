using HomeApplianceManualRag.Web.Configuration;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HomeApplianceManualRag.Web.Providers;

public interface IChatProvider
{
    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken cancellationToken = default);
}

public interface IEmbeddingProvider
{
    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class AzureOpenAiChatProvider : IChatProvider
{
    public async IAsyncEnumerable<string> StreamAsync(string prompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("Azure OpenAI chat provider is not enabled until Phase 4.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

public sealed class AzureOpenAiEmbeddingProvider(
    HttpClient httpClient,
    IOptions<AzureOpenAiOptions> options) : IEmbeddingProvider
{
    private readonly AzureOpenAiOptions settings = options.Value;

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{settings.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(settings.EmbeddingDeployment)}/embeddings?api-version={Uri.EscapeDataString(settings.ApiVersion)}")
        {
            Content = JsonContent.Create(new
            {
                input = text,
                dimensions = settings.EmbeddingDimensions
            })
        };
        request.Headers.Add("api-key", settings.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken);
        var embedding = payload?.Data.FirstOrDefault()?.Embedding;
        if (embedding is null || embedding.Length == 0)
            throw new InvalidOperationException("Azure OpenAI returned an empty embedding.");

        return embedding;
    }

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] EmbeddingItem[] Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}

public sealed class DeterministicFakeChatProvider : IChatProvider
{
    public async IAsyncEnumerable<string> StreamAsync(string prompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var token in new[] { "Deterministic", " fake", " response." })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return token;
        }
    }
}

public sealed class DeterministicFakeEmbeddingProvider(int dimensions = 3) : IEmbeddingProvider
{
    public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = Enumerable.Range(0, dimensions)
            .Select(index => (float)((text ?? string.Empty).Sum(c => c) + index) / 1000)
            .ToArray();
        return Task.FromResult<ReadOnlyMemory<float>>(values);
    }
}
