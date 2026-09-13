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

public sealed class AzureOpenAiEmbeddingProvider : IEmbeddingProvider
{
    public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Azure OpenAI embedding provider is not enabled until Phase 3.");
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
