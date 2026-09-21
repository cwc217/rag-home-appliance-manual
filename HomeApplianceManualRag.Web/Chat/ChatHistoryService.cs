using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Configuration;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Chat;

public sealed record ChatHistoryMessage(
    ChatMessageRole Role,
    string Content,
    IReadOnlyList<ChatCitation> Citations,
    DateTime CreatedAtUtc);

public sealed record ChatConversation(Guid Id, string Title, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed class ChatHistoryService(IDbContextFactory<ApplicationDbContext> dbFactory, ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<ChatConversation>> GetConversationsAsync(
        string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = OwnedConversations(db, anonymousAccessKey);
        return await query.OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new ChatConversation(x.Id, x.Title, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(
        Guid? conversationId = null, string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = conversationId is null
            ? await OwnedConversations(db, anonymousAccessKey).OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct)
            : await FindOwnedConversation(db, conversationId, anonymousAccessKey, ct);
        if (conversation is null) return [];
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(x => x.ConversationId == conversation.Id)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
        return messages.Select(ToHistoryMessage).ToArray();
    }

    public async Task<ChatConversation> CreateConversationAsync(
        string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated && string.IsNullOrWhiteSpace(anonymousAccessKey))
            throw new UnauthorizedAccessException("An anonymous access key is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = new Conversation
        {
            UserId = currentUser.IsAuthenticated ? currentUser.UserId : null,
            AnonymousAccessKeyHash = currentUser.IsAuthenticated ? null : HashKey(anonymousAccessKey!),
            Title = "New chat"
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return new ChatConversation(conversation.Id, conversation.Title, conversation.CreatedAtUtc, conversation.UpdatedAtUtc);
    }

    public async Task PersistExchangeAsync(
        Guid conversationId, string question, string answer, IReadOnlyList<ChatCitation> citations,
        string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            throw new ArgumentException("A question and answer are required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = await FindOwnedConversation(db, conversationId, anonymousAccessKey, ct)
            ?? throw new UnauthorizedAccessException("Conversation access denied.");
        var now = DateTime.UtcNow;
        conversation.UpdatedAtUtc = now;
        if (conversation.Title == "New chat")
            conversation.Title = question.Trim().Length > 80 ? question.Trim()[..80] : question.Trim();
        db.ChatMessages.AddRange(
            new ChatMessage { ConversationId = conversation.Id, Role = ChatMessageRole.User, Content = question.Trim(), CreatedAtUtc = now },
            new ChatMessage { ConversationId = conversation.Id, Role = ChatMessageRole.Assistant, Content = answer.Trim(),
                CitationsJson = JsonSerializer.Serialize(citations), CreatedAtUtc = now.AddTicks(1) });
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistExchangeAsync(string question, string answer, IReadOnlyList<ChatCitation> citations,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.UserId)) return;
        var conversation = (await GetConversationsAsync(null, ct)).LastOrDefault();
        if (conversation is null) conversation = await CreateConversationAsync(null, ct);
        await PersistExchangeAsync(conversation.Id, question, answer, citations, null, ct);
    }

    public async Task<int> DeleteExpiredAnonymousAsync(DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-30);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expired = await db.Conversations.Where(x => x.UserId == null && x.UpdatedAtUtc < cutoff).ToListAsync(ct);
        db.Conversations.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    private IQueryable<Conversation> OwnedConversations(ApplicationDbContext db, string? key) =>
        currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.UserId)
            ? db.Conversations.Where(x => x.UserId == currentUser.UserId)
            : !string.IsNullOrWhiteSpace(key)
                ? db.Conversations.Where(x => x.UserId == null && x.AnonymousAccessKeyHash == HashKey(key))
                : db.Conversations.Where(_ => false);

    private async Task<Conversation?> FindOwnedConversation(ApplicationDbContext db, Guid? id, string? key, CancellationToken ct)
    {
        if (id is null) return null;
        return await OwnedConversations(db, key).SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public sealed class AnonymousChatCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<AnonymousChatCleanupWorker> logger)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ChatHistoryService>()
                        .DeleteExpiredAnonymousAsync(ct: stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) { logger.LogError(ex, "Anonymous chat cleanup failed."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }

    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    internal static ChatHistoryMessage ToHistoryMessage(ChatMessage message) =>
        new(message.Role, message.Content, string.IsNullOrWhiteSpace(message.CitationsJson)
            ? [] : CitationSelector.SelectReferenced(
                message.Content,
                JsonSerializer.Deserialize<IReadOnlyList<ChatCitation>>(message.CitationsJson) ?? []),
            message.CreatedAtUtc);
}
