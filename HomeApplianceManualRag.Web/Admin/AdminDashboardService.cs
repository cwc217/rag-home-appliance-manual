using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Admin;

public sealed record AdminDashboardSnapshot(
    int RegisteredUsers,
    int TotalDocuments,
    IReadOnlyDictionary<DocumentStatus, int> DocumentsByStatus,
    int TotalConversations,
    int ConversationsLast24Hours,
    int ActiveIndexingJobs,
    int FailedIndexingJobs,
    IReadOnlyList<AdminConversationSummary> RecentConversations);

public sealed record AdminIndexingJobRow(
    Guid Id,
    Guid DocumentId,
    string DocumentName,
    string? OwnerUserId,
    IndexingJobStatus Status,
    int Attempts,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? NextAttemptAtUtc,
    string? LastError,
    DateTime? DeletedAtUtc);

public sealed record AdminIndexingJobPage(
    IReadOnlyList<AdminIndexingJobRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed class AdminDashboardService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser,
    AdminConversationService conversations)
{
    public async Task<AdminDashboardSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var since = DateTime.UtcNow.AddHours(-24);

        var registeredUsers = await db.Users.CountAsync(cancellationToken);
        var totalDocuments = await db.ManualDocuments.CountAsync(cancellationToken);
        var documentsByStatus = await db.ManualDocuments
            .GroupBy(document => document.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);
        var totalConversations = await db.Conversations.CountAsync(cancellationToken);
        var conversationsLast24Hours = await db.Conversations
            .CountAsync(conversation => conversation.CreatedAtUtc >= since, cancellationToken);
        var activeIndexingJobs = await db.IndexingJobs
            .CountAsync(job => job.Status == IndexingJobStatus.Pending ||
                               job.Status == IndexingJobStatus.Processing,
                cancellationToken);
        var failedIndexingJobs = await db.IndexingJobs
            .CountAsync(job => job.Status == IndexingJobStatus.Failed, cancellationToken);

        var recentConversations = await conversations.GetRecentSummariesAsync(10, cancellationToken);

        return new AdminDashboardSnapshot(
            registeredUsers,
            totalDocuments,
            documentsByStatus,
            totalConversations,
            conversationsLast24Hours,
            activeIndexingJobs,
            failedIndexingJobs,
            recentConversations);
    }

    private void EnsureAdmin()
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
    }
}

public sealed class AdminIndexingJobService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser)
{
    public async Task<AdminIndexingJobPage> GetPageAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin();

        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.IndexingJobs
            .AsNoTracking()
            .Include(job => job.Document)
            .OrderByDescending(job => job.CreatedAtUtc)
            .ThenByDescending(job => job.Id);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(job => new AdminIndexingJobRow(
                job.Id,
                job.DocumentId,
                job.Document == null ? "(deleted document)" : job.Document.DisplayName,
                job.Document == null ? null : job.Document.OwnerUserId,
                job.Status,
                job.Attempts,
                job.CreatedAtUtc,
                job.StartedAtUtc,
                job.CompletedAtUtc,
                job.NextAttemptAtUtc,
                job.LastError,
                job.Document == null ? null : job.Document.DeletedAtUtc))
            .ToListAsync(cancellationToken);

        return new AdminIndexingJobPage(items, totalCount, pageNumber, pageSize);
    }

    private void EnsureAdmin()
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
    }
}
