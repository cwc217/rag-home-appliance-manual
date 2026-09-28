namespace HomeApplianceManualRag.Web.Documents;

public sealed class IndexingCancellationRegistry
{
    private readonly Dictionary<Guid, CancellationRegistration> activeJobs = [];
    private readonly object sync = new();

    public CancellationRegistration Register(Guid jobId, CancellationToken applicationStopping)
    {
        var registration = new CancellationRegistration(this, jobId, applicationStopping);
        lock (sync)
        {
            if (!activeJobs.TryAdd(jobId, registration))
            {
                registration.Dispose();
                throw new InvalidOperationException($"Indexing job {jobId} is already registered.");
            }
        }

        return registration;
    }

    public bool Cancel(Guid jobId)
    {
        CancellationRegistration? registration;
        lock (sync)
            activeJobs.TryGetValue(jobId, out registration);
        return registration?.RequestCancellation() ?? false;
    }

    private void Remove(Guid jobId, CancellationRegistration registration)
    {
        lock (sync)
        {
            if (activeJobs.TryGetValue(jobId, out var current) &&
                ReferenceEquals(current, registration))
                activeJobs.Remove(jobId);
        }
    }

    public sealed class CancellationRegistration : IDisposable
    {
        private readonly IndexingCancellationRegistry owner;
        private readonly Guid jobId;
        private readonly CancellationTokenSource source;
        private bool disposed;
        private readonly object sync = new();

        internal CancellationRegistration(
            IndexingCancellationRegistry owner,
            Guid jobId,
            CancellationToken applicationStopping)
        {
            this.owner = owner;
            this.jobId = jobId;
            source = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        }

        public CancellationToken Token => source.Token;

        internal bool RequestCancellation()
        {
            lock (sync)
            {
                if (disposed)
                    return false;
                source.Cancel();
                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                source.Dispose();
            }

            owner.Remove(jobId, this);
        }
    }
}

public sealed class IndexingJobCancelledException()
    : OperationCanceledException("Indexing was cancelled by the user.");
