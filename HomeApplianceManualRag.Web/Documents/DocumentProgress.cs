using HomeApplianceManualRag.Web.Data;

namespace HomeApplianceManualRag.Web.Documents;

public sealed record DocumentProgressUpdate(
    Guid DocumentId,
    string? OwnerUserId,
    DocumentStatus Status,
    string? LastError);

public sealed class DocumentProgressNotifier(ILogger<DocumentProgressNotifier> logger)
{
    private readonly object sync = new();
    private readonly List<Subscription> subscriptions = [];

    public IDisposable Subscribe(
        string? userId,
        bool isAdmin,
        Func<DocumentProgressUpdate, Task> handler)
    {
        var subscription = new Subscription(this, userId, isAdmin, handler);
        lock (sync)
            subscriptions.Add(subscription);
        return subscription;
    }

    public async Task PublishAsync(DocumentProgressUpdate update)
    {
        Subscription[] matching;
        lock (sync)
        {
            matching = subscriptions
                .Where(x => x.IsAdmin || x.UserId == update.OwnerUserId)
                .ToArray();
        }

        foreach (var subscription in matching)
        {
            try
            {
                await subscription.Handler(update);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Document progress subscriber failed for {DocumentId}.", update.DocumentId);
            }
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (sync)
            subscriptions.Remove(subscription);
    }

    private sealed class Subscription(
        DocumentProgressNotifier owner,
        string? userId,
        bool isAdmin,
        Func<DocumentProgressUpdate, Task> handler) : IDisposable
    {
        public string? UserId { get; } = userId;
        public bool IsAdmin { get; } = isAdmin;
        public Func<DocumentProgressUpdate, Task> Handler { get; } = handler;

        public void Dispose() => owner.Unsubscribe(this);
    }
}
