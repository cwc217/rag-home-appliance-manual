namespace HomeApplianceManualRag.Web.Documents;

public sealed class DocumentUploadConcurrencyGate : IDisposable
{
    public const int MaxConcurrentUploads = 3;

    private readonly SemaphoreSlim semaphore = new(MaxConcurrentUploads, MaxConcurrentUploads);

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Lease(semaphore);
    }

    public void Dispose() => semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? semaphore = semaphore;

        public void Dispose() =>
            Interlocked.Exchange(ref semaphore, null)?.Release();
    }
}
