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
