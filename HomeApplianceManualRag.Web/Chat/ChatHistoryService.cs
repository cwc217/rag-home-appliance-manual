using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Activity;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Chat;

public sealed record ChatHistoryMessage(
    ChatMessageRole Role,
    string Content,
    IReadOnlyList<ChatCitation> Citations,
    DateTime CreatedAtUtc);

public interface IChatConversationContext
{
    Task<IReadOnlyList<ChatHistoryMessage>> GetRecentHistoryForModelAsync(
        Guid conversationId, string? anonymousAccessKey = null, CancellationToken ct = default);
}

public sealed record ChatConversation(Guid Id, string Title, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed class ChatHistoryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser,
    IActiveAccountGuard accountGuard,
    ActivityLogService? activityLog = null) : IChatConversationContext
{
    public const int MaxActiveConversations = 3;
    public const int MaxConversationExchanges = 50;
    public const int MaxConversationMessages = MaxConversationExchanges * 2;
    public const int ContextExchangeCount = 4;

    public async Task<IReadOnlyList<ChatConversation>> GetConversationsAsync(
        string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = OwnedConversations(db, anonymousAccessKey);
        return await query.OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new ChatConversation(x.Id, x.Title, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<int> GetActiveConversationCountAsync(
        string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await CountActiveConversationsAsync(db, anonymousAccessKey, ct);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(
        Guid? conversationId = null, string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = conversationId is null
            ? await OwnedConversations(db, anonymousAccessKey).OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct)
            : await FindOwnedConversation(db, conversationId, anonymousAccessKey, ct);
        if (conversation is null) return [];
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(x => x.ConversationId == conversation.Id)
            .OrderBy(x => x.Sequence).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Role).ThenBy(x => x.Id)
            .ToListAsync(ct);
        return messages.Select(ToHistoryMessage).ToArray();
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetRecentHistoryForModelAsync(
        Guid conversationId, string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = await FindOwnedConversation(db, conversationId, anonymousAccessKey, ct)
            ?? throw new UnauthorizedAccessException("Conversation access denied.");
        var messageCount = await db.ChatMessages.CountAsync(
            x => x.ConversationId == conversation.Id, ct);
        if (messageCount >= MaxConversationMessages)
            throw new ConversationMessageLimitExceededException();

        var messages = await db.ChatMessages.AsNoTracking()
            .Where(x => x.ConversationId == conversation.Id)
            .OrderByDescending(x => x.Sequence)
            .Take(ContextExchangeCount * 4)
            .ToListAsync(ct);
        messages.Reverse();
        var exchanges = new List<ChatMessage>();
        for (var index = 0; index + 1 < messages.Count; index++)
        {
            var userMessage = messages[index];
            var assistantMessage = messages[index + 1];
            if (userMessage.Role != ChatMessageRole.User ||
                assistantMessage.Role != ChatMessageRole.Assistant ||
                assistantMessage.Sequence != userMessage.Sequence + 1)
                continue;

            exchanges.Add(userMessage);
            exchanges.Add(assistantMessage);
        }

        return exchanges.TakeLast(ContextExchangeCount * 2)
            .Select(ToHistoryMessage)
            .ToArray();
    }

    public async Task<ChatConversation> CreateConversationAsync(
        string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        if (!currentUser.IsAuthenticated && string.IsNullOrWhiteSpace(anonymousAccessKey))
            throw new UnauthorizedAccessException("An anonymous access key is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await CountActiveConversationsAsync(db, anonymousAccessKey, ct) >= MaxActiveConversations)
            throw new ConversationLimitExceededException();

        var conversation = new Conversation
        {
            UserId = currentUser.IsAuthenticated ? currentUser.UserId : null,
            AnonymousAccessKeyHash = currentUser.IsAuthenticated ? null : HashKey(anonymousAccessKey!),
            Title = "New chat"
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        if (activityLog is not null)
            await activityLog.RecordAsync(
                ActivityTypes.ConversationCreated,
                "Conversation",
                conversation.Id.ToString());
        return new ChatConversation(conversation.Id, conversation.Title, conversation.CreatedAtUtc, conversation.UpdatedAtUtc);
    }

    public async Task DeleteConversationAsync(
        Guid conversationId, string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = await FindOwnedConversation(db, conversationId, anonymousAccessKey, ct)
            ?? throw new UnauthorizedAccessException("Conversation access denied.");
        conversation.DeletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (activityLog is not null)
            await activityLog.RecordAsync(
                ActivityTypes.ConversationDeleted,
                "Conversation",
                conversation.Id.ToString());
    }

    public async Task PersistExchangeAsync(
        Guid conversationId, string question, string answer, IReadOnlyList<ChatCitation> citations,
        string? anonymousAccessKey = null, CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            throw new ArgumentException("A question and answer are required.");
        if (!currentUser.IsAuthenticated && string.IsNullOrWhiteSpace(anonymousAccessKey))
            throw new UnauthorizedAccessException("An anonymous access key is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        if (db.Database.IsNpgsql())
        {
            var lockKey = currentUser.IsAuthenticated
                ? $"chat-conversation-limit:user:{currentUser.UserId}"
                : $"chat-conversation-limit:anonymous:{HashKey(anonymousAccessKey!)}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                ct);
        }

        var conversation = await FindOwnedConversation(db, conversationId, anonymousAccessKey, ct)
            ?? throw new UnauthorizedAccessException("Conversation access denied.");
        var now = DateTime.UtcNow;
        var messageCount = await db.ChatMessages.CountAsync(
            x => x.ConversationId == conversation.Id, ct);
        if (messageCount + 2 > MaxConversationMessages)
            throw new ConversationMessageLimitExceededException();

        var hasMessages = messageCount > 0;
        if (!hasMessages &&
            await CountActiveConversationsAsync(db, anonymousAccessKey, ct) >= MaxActiveConversations)
        {
            conversation.DeletedAtUtc = now;
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            throw new ConversationLimitExceededException();
        }

        var nextSequence = (await db.ChatMessages
            .Where(x => x.ConversationId == conversation.Id)
            .MaxAsync(x => (int?)x.Sequence, ct) ?? 0) + 1;
        conversation.UpdatedAtUtc = now;
        if (conversation.Title == "New chat")
            conversation.Title = question.Trim().Length > 80 ? question.Trim()[..80] : question.Trim();
        db.ChatMessages.AddRange(
            new ChatMessage { ConversationId = conversation.Id, Role = ChatMessageRole.User, Content = question.Trim(),
                Sequence = nextSequence, CreatedAtUtc = now },
            new ChatMessage { ConversationId = conversation.Id, Role = ChatMessageRole.Assistant, Content = answer.Trim(),
                CitationsJson = JsonSerializer.Serialize(citations), Sequence = nextSequence + 1,
                CreatedAtUtc = now.AddMilliseconds(1) });
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        if (activityLog is not null)
            await activityLog.RecordAsync(
                ActivityTypes.ChatExchangePersisted,
                "Conversation",
                conversation.Id.ToString(),
                new { QuestionLength = question.Length, AnswerLength = answer.Length });
    }

    public async Task PersistExchangeAsync(string question, string answer, IReadOnlyList<ChatCitation> citations,
        CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
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
            ? db.Conversations.Where(x => x.UserId == currentUser.UserId && x.DeletedAtUtc == null)
            : !string.IsNullOrWhiteSpace(key)
                ? db.Conversations.Where(x => x.UserId == null && x.AnonymousAccessKeyHash == HashKey(key)
                    && x.DeletedAtUtc == null)
                : db.Conversations.Where(_ => false);

    private Task<int> CountActiveConversationsAsync(
        ApplicationDbContext db, string? key, CancellationToken ct) =>
        OwnedConversations(db, key).CountAsync(x => x.Messages.Any(), ct);

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

    internal static ChatHistoryMessage ToHistoryMessage(ChatMessage message)
    {
        IReadOnlyList<ChatCitation> citations = string.IsNullOrWhiteSpace(message.CitationsJson)
            ? []
            : CitationSelector.SelectReferenced(
                message.Content,
                JsonSerializer.Deserialize<IReadOnlyList<ChatCitation>>(message.CitationsJson) ?? []);
        return new ChatHistoryMessage(
            message.Role,
            CitationSelector.RemoveMarkers(message.Content, citations).TrimEnd(),
            citations,
            message.CreatedAtUtc);
    }
}

public sealed class ConversationLimitExceededException()
    : InvalidOperationException(
        $"You can have up to {ChatHistoryService.MaxActiveConversations} active conversations. " +
        "Delete an existing conversation to start a new one.");

public sealed class ConversationMessageLimitExceededException()
    : InvalidOperationException(
        $"This conversation has reached its limit of {ChatHistoryService.MaxConversationExchanges} question-and-answer exchanges. " +
        "Start a new conversation to continue.");
