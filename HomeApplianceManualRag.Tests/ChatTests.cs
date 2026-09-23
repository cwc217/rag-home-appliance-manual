using HomeApplianceManualRag.Web.Chat;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace HomeApplianceManualRag.Tests;

public sealed class ChatTests
{
    [Fact]
    public void Grounded_prompt_contains_manual_excerpts_and_user_question()
    {
        var prompt = GroundedPrompt.Build("How do I clean it?", [
            new RetrievedChunk(Guid.NewGuid(), "washer.pdf", 4, "Disconnect power first.", .9)
        ]);

        Assert.Contains("<manual_excerpts>", prompt, StringComparison.Ordinal);
        Assert.Contains("[washer.pdf, p. 4] Disconnect power first.", prompt, StringComparison.Ordinal);
        Assert.Contains("</manual_excerpts>", prompt, StringComparison.Ordinal);
        Assert.Contains("<user_question>", prompt, StringComparison.Ordinal);
        Assert.Contains("How do I clean it?", prompt, StringComparison.Ordinal);
        Assert.Contains("</user_question>", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Citation_selector_keeps_only_valid_citations_referenced_by_answer()
    {
        var documentId = Guid.NewGuid();
        var candidates = new[]
        {
            new ChatCitation(documentId, "manual.pdf", 16),
            new ChatCitation(documentId, "manual.pdf", 12),
            new ChatCitation(Guid.NewGuid(), "other.pdf", 3)
        };

        var selected = CitationSelector.SelectReferenced(
            "Reset the appliance as described [manual.pdf, p. 16]. [manual.pdf, p. 16].",
            candidates);

        var citation = Assert.Single(selected);
        Assert.Equal("[manual.pdf, p. 16]", citation.Label);
    }

    [Fact]
    public void Citation_selector_ignores_untrusted_or_missing_markers()
    {
        var candidates = new[]
        {
            new ChatCitation(Guid.NewGuid(), "manual.pdf", 16)
        };

        Assert.Empty(CitationSelector.SelectReferenced(
            "See [other.pdf, p. 16] and page 16.", candidates));
    }

    [Fact]
    public void Citation_markers_are_removed_from_displayed_answer()
    {
        var citations = new[]
        {
            new ChatCitation(Guid.NewGuid(), "manual.pdf", 16)
        };

        var answer = CitationSelector.RemoveMarkers(
            "Turn it off. [manual.pdf, p. 16]",
            citations);

        Assert.Equal("Turn it off. ", answer);
    }

    [Fact]
    public void Rate_limiter_rejects_requests_over_configured_limit()
    {
        var limiter = new ChatRateLimiter(Options.Create(new RateLimitingOptions { RequestsPerMinute = 1 }));
        limiter.EnsureAllowed("user-1");

        var exception = Assert.Throws<ChatRateLimitException>(() => limiter.EnsureAllowed("user-1"));

        Assert.True(exception.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Conversation_state_notifies_navigation_list()
    {
        var state = new ChatConversationState();
        var notifications = 0;
        state.Changed += (_, _) => notifications++;

        state.NotifyChanged();

        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task In_memory_retrieval_applies_ready_and_owner_scope()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var mine = new ManualDocument { Id = Guid.NewGuid(), OwnerUserId = "user-1", DisplayName = "mine.pdf", Status = DocumentStatus.Ready };
        var other = new ManualDocument { Id = Guid.NewGuid(), OwnerUserId = "user-2", DisplayName = "other.pdf", Status = DocumentStatus.Ready };
        var pending = new ManualDocument { Id = Guid.NewGuid(), OwnerUserId = null, DisplayName = "pending.pdf", Status = DocumentStatus.Pending };
        db.AddRange(mine, other, pending);
        db.AddRange(
            Chunk(mine, "mine"),
            Chunk(other, "other"),
            Chunk(pending, "pending"));
        await db.SaveChangesAsync();

        var retriever = new ManualRetriever(db, new DeterministicFakeEmbeddingProvider(1536),
            Options.Create(new RetrievalOptions { TopK = 10 }));

        var result = await retriever.RetrieveAsync("mine", RetrievalScope.UserOwn, "user-1");

        var item = Assert.Single(result);
        Assert.Equal("mine.pdf", item.DocumentName);
    }

    [Fact]
    public async Task History_is_owned_and_ordered_for_authenticated_user()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        await using var db = new ApplicationDbContext(options);
        db.Conversations.Add(new Conversation
        {
            UserId = "other",
            Messages = [new ChatMessage { Role = ChatMessageRole.User, Content = "private" }]
        });
        await db.SaveChangesAsync();

        var current = new FakeCurrentUser("user-1");
        var service = new ChatHistoryService(new TestDbFactory(options), current, new FakeActiveAccountGuard());
        await service.PersistExchangeAsync("first", "answer", []);
        await service.PersistExchangeAsync("second", "answer 2", []);

        var history = await service.GetHistoryAsync();
        Assert.Equal(["first", "answer", "second", "answer 2"], history.Select(x => x.Content));
        Assert.DoesNotContain(history, x => x.Content == "private");
    }

    [Fact]
    public async Task Anonymous_history_requires_an_access_key()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options);
        var service = new ChatHistoryService(new TestDbFactory(options), new FakeCurrentUser(null), new FakeActiveAccountGuard());

        await service.PersistExchangeAsync("question", "answer", []);

        Assert.Empty(await service.GetHistoryAsync());
        Assert.Empty(db.Conversations);
    }

    [Fact]
    public async Task Deleting_conversation_hides_it_but_keeps_database_record()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var service = new ChatHistoryService(new TestDbFactory(options), new FakeCurrentUser("user-1"), new FakeActiveAccountGuard());
        var conversation = await service.CreateConversationAsync();

        await service.DeleteConversationAsync(conversation.Id);

        Assert.Empty(await service.GetConversationsAsync());
        await using var db = new ApplicationDbContext(options);
        var retained = await db.Conversations.SingleAsync(x => x.Id == conversation.Id);
        Assert.NotNull(retained.DeletedAtUtc);
    }

    [Fact]
    public async Task History_orders_user_before_assistant_when_timestamps_match()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var conversationId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Conversations.Add(new Conversation { Id = conversationId, UserId = "user-1" });
            db.ChatMessages.AddRange(
                new ChatMessage
                {
                    ConversationId = conversationId,
                    Role = ChatMessageRole.Assistant,
                    Content = "answer",
                    CreatedAtUtc = timestamp
                },
                new ChatMessage
                {
                    ConversationId = conversationId,
                    Role = ChatMessageRole.User,
                    Content = "question",
                    CreatedAtUtc = timestamp
                });
            await db.SaveChangesAsync();
        }

        var service = new ChatHistoryService(new TestDbFactory(options), new FakeCurrentUser("user-1"), new FakeActiveAccountGuard());
        var history = await service.GetHistoryAsync(conversationId);

        Assert.Equal([ChatMessageRole.User, ChatMessageRole.Assistant], history.Select(x => x.Role));
    }

    [Fact]
    public async Task Anonymous_conversations_are_key_owned_and_expire_after_thirty_days()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var service = new ChatHistoryService(new TestDbFactory(options), new FakeCurrentUser(null), new FakeActiveAccountGuard());
        var room = await service.CreateConversationAsync("key-a");
        await service.PersistExchangeAsync(room.Id, "question", "answer", [], "key-a");

        Assert.Single(await service.GetConversationsAsync("key-a"));
        Assert.Empty(await service.GetConversationsAsync("key-b"));
        await using (var db = new ApplicationDbContext(options))
        {
            var entity = await db.Conversations.SingleAsync();
            entity.UpdatedAtUtc = DateTime.UtcNow.AddDays(-31);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await service.DeleteExpiredAnonymousAsync());
    }

    private static ManualChunk Chunk(ManualDocument document, string text) => new()
    {
        Id = Guid.NewGuid(), DocumentId = document.Id, Document = document, Text = text,
        PageNumber = 2, Embedding = new Vector(Enumerable.Repeat(.1f, 1536).ToArray())
    };

    private sealed class FakeCurrentUser(string? id) : ICurrentUser
    {
        public string? UserId => id;
        public string? Email => null;
        public bool IsAuthenticated => id is not null;
        public bool IsInRole(string role) => false;
    }

    private sealed class TestDbFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }
}
