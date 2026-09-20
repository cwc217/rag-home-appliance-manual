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
    IReadOnlyList<AdminActivityItem> RecentActivity);

public sealed record AdminActivityItem(
    string Category,
    string Title,
    string Detail,
    DateTime TimestampUtc);

public sealed class AdminDashboardService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser)
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

        var recentDocuments = await db.ManualDocuments
            .AsNoTracking()
            .OrderByDescending(document => document.UpdatedAtUtc)
            .Take(10)
            .Select(document => new
            {
                document.DisplayName,
                document.Status,
                document.LastError,
                document.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);
        var recentConversations = await db.Conversations
            .AsNoTracking()
            .OrderByDescending(conversation => conversation.UpdatedAtUtc)
            .Take(10)
            .Select(conversation => new AdminActivityItem(
                "Conversation",
                conversation.Title,
                conversation.UserId == null ? "Anonymous conversation" : "Authenticated conversation",
                conversation.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var recentDocumentActivity = recentDocuments.Select(document => new AdminActivityItem(
            "Document",
            document.DisplayName,
            $"Status: {GetDocumentStatus(document.Status, document.LastError)}",
            document.UpdatedAtUtc));
        var recentActivity = recentDocumentActivity
            .Concat(recentConversations)
            .OrderByDescending(activity => activity.TimestampUtc)
            .Take(8)
            .ToArray();

        return new AdminDashboardSnapshot(
            registeredUsers,
            totalDocuments,
            documentsByStatus,
            totalConversations,
            conversationsLast24Hours,
            activeIndexingJobs,
            failedIndexingJobs,
            recentActivity);
    }

    private void EnsureAdmin()
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
    }

    private static string GetDocumentStatus(DocumentStatus status, string? lastError) =>
        string.IsNullOrWhiteSpace(lastError)
            ? status.ToString()
            : "Failed";
}
