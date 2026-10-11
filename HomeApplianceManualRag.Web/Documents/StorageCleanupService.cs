using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Documents;

public sealed class StorageCleanupService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IFileStorage storage,
    ILogger<StorageCleanupService> logger)
{
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(1);

    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var pending = await db.StorageCleanups
            .Where(x => x.Status == StorageCleanupStatus.Pending &&
                        (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var cleanup in pending)
        {
            try
            {
                await storage.DeleteAsync(cleanup.StorageKey, cancellationToken);
                cleanup.Status = StorageCleanupStatus.Completed;
                cleanup.CompletedAtUtc = DateTime.UtcNow;
                cleanup.LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                cleanup.Attempts++;
                cleanup.LastError = ex.Message;
                cleanup.NextAttemptAtUtc = DateTime.UtcNow.Add(GetRetryDelay(cleanup.Attempts));
                logger.LogWarning(ex, "Storage cleanup failed for key {StorageKey}; retry {Attempt}", cleanup.StorageKey, cleanup.Attempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReconcileOrphansAsync(CancellationToken cancellationToken = default)
    {
        var files = await storage.ListAsync(cancellationToken);
        if (files.Count == 0)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var referencedKeys = await db.ManualDocuments.Select(x => x.StorageKey).ToHashSetAsync(cancellationToken);
        var cleanupKeys = await db.StorageCleanups
            .Where(x => x.Status == StorageCleanupStatus.Pending)
            .Select(x => x.StorageKey)
            .ToHashSetAsync(cancellationToken);
        var cutoff = DateTime.UtcNow - OrphanGracePeriod;

        foreach (var file in files.Where(x => x.LastWriteTimeUtc < cutoff &&
                                              !referencedKeys.Contains(x.StorageKey) &&
                                              !cleanupKeys.Contains(x.StorageKey)))
        {
            try
            {
                await storage.DeleteAsync(file.StorageKey, cancellationToken);
                logger.LogInformation("Reconciled orphaned storage file {StorageKey}", file.StorageKey);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(ex, "Failed to reconcile orphaned storage file {StorageKey}", file.StorageKey);
            }
        }
    }

    private static TimeSpan GetRetryDelay(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempts, 6))));
}

public sealed class StorageCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<StorageCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<StorageCleanupService>();
                await cleanup.ProcessPendingAsync(stoppingToken);
                await cleanup.ReconcileOrphansAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Storage cleanup cycle failed.");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
