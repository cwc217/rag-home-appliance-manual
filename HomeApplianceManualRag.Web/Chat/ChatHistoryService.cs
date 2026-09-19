using System.Text.Json;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Chat;

public sealed record ChatHistoryMessage(
    ChatMessageRole Role,
    string Content,
    IReadOnlyList<ChatCitation> Citations,
    DateTime CreatedAtUtc);

public sealed class ChatHistoryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.UserId))
            return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var messages = await db.Conversations
            .AsNoTracking()
            .Where(x => x.UserId == currentUser.UserId)
            .SelectMany(x => x.Messages)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        return messages.Select(ToHistoryMessage).ToArray();
    }

    public async Task PersistExchangeAsync(
        string question,
        string answer,
        IReadOnlyList<ChatCitation> citations,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.UserId))
            return;
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            throw new ArgumentException("A question and answer are required.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.UserId == currentUser.UserId, ct);
        if (conversation is null)
        {
            conversation = new Conversation { UserId = currentUser.UserId };
            db.Conversations.Add(conversation);
        }

        var now = DateTime.UtcNow;
        conversation.UpdatedAtUtc = now;
        db.ChatMessages.AddRange(
            new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatMessageRole.User,
                Content = question.Trim(),
                CreatedAtUtc = now
            },
            new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatMessageRole.Assistant,
                Content = answer.Trim(),
                CitationsJson = JsonSerializer.Serialize(citations),
                CreatedAtUtc = now.AddTicks(1)
            });
        await db.SaveChangesAsync(ct);
    }

    private static ChatHistoryMessage ToHistoryMessage(ChatMessage message) =>
        new(message.Role, message.Content,
            string.IsNullOrWhiteSpace(message.CitationsJson)
                ? []
                : JsonSerializer.Deserialize<IReadOnlyList<ChatCitation>>(message.CitationsJson) ?? [],
            message.CreatedAtUtc);
}
