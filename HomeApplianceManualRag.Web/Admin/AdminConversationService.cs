using HomeApplianceManualRag.Web.Chat;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Admin;

public sealed record AdminConversationSummary(
    Guid Id,
    string Title,
    string Owner,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int MessageCount);

public sealed record AdminConversationDetail(
    AdminConversationSummary Summary,
    IReadOnlyList<ChatHistoryMessage> Messages);

public sealed class AdminConversationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser,
    IActiveAccountGuard accountGuard)
{
    public async Task<IReadOnlyList<AdminConversationSummary>> GetRecentSummariesAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken);
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await (
            from conversation in db.Conversations.AsNoTracking()
            join user in db.Users.AsNoTracking()
                on conversation.UserId equals user.Id into users
            from user in users.DefaultIfEmpty()
            orderby conversation.UpdatedAtUtc descending
            select new AdminConversationSummary(
                conversation.Id,
                conversation.Title,
                user != null && user.Email != null
                    ? user.Email
                    : conversation.UserId == null ? "Anonymous" : "Unknown user",
                conversation.CreatedAtUtc,
                conversation.UpdatedAtUtc,
                conversation.Messages.Count))
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<AdminConversationDetail?> GetDetailAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations
            .AsNoTracking()
            .Include(item => item.Messages)
            .SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null)
            return null;

        var owner = conversation.UserId is null
            ? "Anonymous"
            : await db.Users
                .Where(user => user.Id == conversation.UserId)
                .Select(user => user.Email)
                .SingleOrDefaultAsync(cancellationToken) ?? "Unknown user";

        var summary = new AdminConversationSummary(
            conversation.Id,
            conversation.Title,
            owner,
            conversation.CreatedAtUtc,
            conversation.UpdatedAtUtc,
            conversation.Messages.Count);
        var messages = conversation.Messages
            .OrderBy(message => message.Sequence)
            .ThenBy(message => message.CreatedAtUtc)
            .ThenBy(message => message.Role)
            .ThenBy(message => message.Id)
            .Select(ChatHistoryService.ToHistoryMessage)
            .ToArray();
        return new AdminConversationDetail(summary, messages);
    }

    private async Task EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
        await accountGuard.EnsureEnabledAsync(cancellationToken);
    }
}
