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
    public void Grounded_prompt_requires_context_and_citations()
    {
        var prompt = GroundedPrompt.Build("How do I clean it?", [
            new RetrievedChunk(Guid.NewGuid(), "washer.pdf", 4, "Disconnect power first.", .9)
        ]);

        Assert.Contains("only the manual excerpts", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[washer.pdf, p. 4]", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not invent", prompt, StringComparison.Ordinal);
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
        var service = new ChatHistoryService(new TestDbFactory(options), current);
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
        var service = new ChatHistoryService(new TestDbFactory(options), new FakeCurrentUser(null));

        await service.PersistExchangeAsync("question", "answer", []);

        Assert.Empty(await service.GetHistoryAsync());
        Assert.Empty(db.Conversations);
    }

    [Fact]
    public async Task Anonymous_conversations_are_key_owned_and_expire_after_thirty_days()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var service = new ChatHistoryService(new TestDbFactory(options), new FakeCurrentUser(null));
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
