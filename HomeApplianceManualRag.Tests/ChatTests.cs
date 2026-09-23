using HomeApplianceManualRag.Web.Chat;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Diagnostics;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using System.Text.Json;

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
    public void Tool_call_options_disable_reasoning_effort()
    {
        var options = AzureOpenAiChatProvider.CreateToolOptions(ChatToolDefinitions.All);

#pragma warning disable OPENAI001
        Assert.Equal("none", options.ReasoningEffortLevel?.ToString());
#pragma warning restore OPENAI001
        Assert.Equal(2, options.Tools.Count);
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
    public async Task Appliance_catalog_is_scoped_to_owner_and_ready_non_deleted_manuals()
    {
        await using var db = CreateDatabase();
        db.ManualDocuments.AddRange(
            ApplianceManual("user-1", "mine.pdf"),
            ApplianceManual("user-1", "deleted.pdf", deleted: true),
            ApplianceManual("user-1", "processing.pdf", status: DocumentStatus.Processing),
            ApplianceManual("user-2", "other.pdf"),
            ApplianceManual(null, "public.pdf"));
        await db.SaveChangesAsync();
        var service = CreateTools(db, new ToolTestCurrentUser("user-1"));

        var execution = await service.ExecuteAsync(
            new ChatToolCall("call-1", "get_my_appliances", "{}"));
        var catalog = JsonSerializer.Deserialize<ApplianceCatalogResult>(
            execution.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(catalog);
        var appliance = Assert.Single(catalog.Appliances);
        Assert.Equal("mine.pdf", appliance.ManualName);
        Assert.False(catalog.IsTruncated);
    }

    [Fact]
    public async Task Anonymous_appliance_catalog_contains_only_public_ready_manuals()
    {
        await using var db = CreateDatabase();
        db.ManualDocuments.AddRange(
            ApplianceManual(null, "public.pdf"),
            ApplianceManual(null, "deleted-public.pdf", deleted: true),
            ApplianceManual("user-1", "private.pdf"));
        await db.SaveChangesAsync();
        var service = CreateTools(db, new ToolTestCurrentUser(null));

        var execution = await service.ExecuteAsync(
            new ChatToolCall("call-1", "get_my_appliances", "{}"));
        var catalog = JsonSerializer.Deserialize<ApplianceCatalogResult>(
            execution.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(catalog);
        Assert.Equal("public manuals", catalog.BasedOn);
        Assert.Equal(["public.pdf"], catalog.Appliances.Select(x => x.ManualName));
    }

    [Fact]
    public async Task Admin_appliance_catalog_includes_all_ready_non_deleted_manuals()
    {
        await using var db = CreateDatabase();
        db.ManualDocuments.AddRange(
            ApplianceManual("user-1", "user.pdf"),
            ApplianceManual(null, "public.pdf"),
            ApplianceManual("user-2", "deleted.pdf", deleted: true));
        await db.SaveChangesAsync();
        var service = CreateTools(db, new ToolTestCurrentUser("admin-1", admin: true));

        var execution = await service.ExecuteAsync(
            new ChatToolCall("call-1", "get_my_appliances", "{}"));
        var catalog = JsonSerializer.Deserialize<ApplianceCatalogResult>(
            execution.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(catalog);
        Assert.Equal(["public.pdf", "user.pdf"], catalog.Appliances.Select(x => x.ManualName));
    }

    [Fact]
    public async Task Manual_search_restricts_results_to_accessible_appliance_id()
    {
        await using var db = CreateDatabase();
        var first = ApplianceManual("user-1", "first.pdf");
        var selected = ApplianceManual("user-1", "selected.pdf");
        var privateDocument = ApplianceManual("user-2", "private.pdf");
        db.AddRange(first, selected, privateDocument);
        db.AddRange(Chunk(first, "first chunk"), Chunk(selected, "selected chunk"),
            Chunk(privateDocument, "private chunk"));
        await db.SaveChangesAsync();
        var service = CreateTools(db, new ToolTestCurrentUser("user-1"));
        var arguments = $$"""{"query":"find manual details","applianceId":"{{selected.Id}}"}""";

        var execution = await service.ExecuteAsync(
            new ChatToolCall("call-1", "search_my_manuals", arguments));
        var result = JsonSerializer.Deserialize<ManualSearchResult>(
            execution.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var chunk = Assert.Single(result!.Chunks);
        Assert.Equal(selected.Id, chunk.DocumentId);
        Assert.Equal("selected.pdf", chunk.DocumentName);
    }

    [Fact]
    public async Task Appliance_tool_rejects_caller_supplied_user_id()
    {
        await using var db = CreateDatabase();
        db.ManualDocuments.Add(ApplianceManual("user-1", "mine.pdf"));
        await db.SaveChangesAsync();
        var service = CreateTools(db, new ToolTestCurrentUser("user-1"));

        var execution = await service.ExecuteAsync(
            new ChatToolCall("call-1", "get_my_appliances", """{"userId":"user-2"}"""));

        Assert.Contains("does not accept arguments", execution.Content, StringComparison.Ordinal);
        Assert.Empty(execution.Chunks);
    }

    [Fact]
    public async Task Chat_executes_selected_tool_and_collects_manual_citations()
    {
        await using var db = CreateDatabase();
        var document = ApplianceManual("user-1", "washer.pdf");
        db.Add(document);
        db.Add(Chunk(document, "Disconnect power before cleaning."));
        await db.SaveChangesAsync();
        var provider = new ScriptedToolChatProvider(
            [
                [new ChatProviderUpdate(ToolCalls:
                    [new ChatToolCall("call-1", "search_my_manuals",
                        $$"""{"query":"cleaning instructions","applianceId":"{{document.Id}}"}""")])],
                [new ChatProviderUpdate(Text: "Disconnect power first. [washer.pdf, p. 2]")]
            ]);
        var service = CreateChatService(db, provider, new ToolTestCurrentUser("user-1"));

        var result = await service.StartAsync("How do I clean it?");
        var responseText = new System.Text.StringBuilder();
        await foreach (var part in result.Stream!)
            responseText.Append(part);

        Assert.Contains("Disconnect power first.", responseText.ToString(), StringComparison.Ordinal);
        Assert.Equal(document.Id, Assert.Single(result.Response.Context).DocumentId);
        Assert.Equal("[washer.pdf, p. 2]", Assert.Single(result.Response.Citations).Label);
        Assert.Equal(2, provider.RequestCount);
        var round = Assert.Single(provider.LastRounds!);
        Assert.Equal("search_my_manuals", Assert.Single(round.Calls).Name);
        Assert.Equal("call-1", Assert.Single(round.Results).CallId);
    }

    [Fact]
    public async Task Chat_disables_tools_after_four_calls_and_streams_a_final_answer()
    {
        await using var db = CreateDatabase();
        var user = new ToolTestCurrentUser("user-1");
        db.ManualDocuments.Add(ApplianceManual("user-1", "mine.pdf"));
        await db.SaveChangesAsync();
        var provider = new RepeatingToolChatProvider();
        var service = CreateChatService(db, provider, user);

        var result = await service.StartAsync("What appliances do I have?");
        var responseText = new System.Text.StringBuilder();
        await foreach (var part in result.Stream!)
            responseText.Append(part);

        Assert.Equal("Here is what I found.", responseText.ToString());
        Assert.Equal(4, provider.ToolEnabledRequests);
        Assert.Equal(1, provider.ToolDisabledRequests);
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

    private static ManualDocument ApplianceManual(
        string? owner,
        string name,
        bool deleted = false,
        DocumentStatus status = DocumentStatus.Ready) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = owner,
        DisplayName = name,
        ProductName = "Appliance",
        ApplianceCategory = "Washer",
        Brand = "Acme",
        ModelNumber = "WM-100",
        Status = status,
        DeletedAtUtc = deleted ? DateTime.UtcNow : null
    };

    private static ApplicationDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Chat_provider_failure_is_recorded_as_operational_error()
    {
        await using var db = CreateDatabase();
        var recorder = new RecordingErrorRecorder();
        var service = CreateChatService(
            db,
            new ThrowingChatProvider(),
            new ToolTestCurrentUser("user-1"),
            recorder);

        var (_, stream) = await service.StartAsync("How do I reset it?");
        await Assert.ThrowsAsync<ChatProviderException>(async () =>
        {
            await foreach (var _ in stream!) { }
        });

        var recorded = Assert.Single(recorder.Events);
        Assert.Equal("Chat provider", recorded.Service);
        Assert.Equal("Generate answer", recorded.Operation);
        Assert.IsType<HttpRequestException>(recorded.Exception);
    }

    private static ChatToolsService CreateTools(
        ApplicationDbContext db,
        ICurrentUser user) =>
        new(db,
            new ManualRetriever(db, new DeterministicFakeEmbeddingProvider(1536),
                Options.Create(new RetrievalOptions { TopK = 10 })),
            user,
            new FakeActiveAccountGuard(),
            Options.Create(new ChatOptions()),
            NullLogger<ChatToolsService>.Instance);

    private static ChatService CreateChatService(
        ApplicationDbContext db,
        IChatProvider provider,
        ICurrentUser user,
        IOperationalErrorRecorder? errorRecorder = null)
    {
        var retriever = new ManualRetriever(db, new DeterministicFakeEmbeddingProvider(1536),
            Options.Create(new RetrievalOptions { TopK = 10 }));
        var tools = new ChatToolsService(db, retriever, user, new FakeActiveAccountGuard(),
            Options.Create(new ChatOptions()), NullLogger<ChatToolsService>.Instance);
        return new ChatService(provider, tools,
            new ChatRateLimiter(Options.Create(new RateLimitingOptions())),
            user, new FakeActiveAccountGuard(), new HttpContextAccessor(),
            Options.Create(new ChatOptions()), errorRecorder ?? new RecordingErrorRecorder());
    }

    private sealed class RecordingErrorRecorder : IOperationalErrorRecorder
    {
        public List<(string Service, string Operation, Exception Exception)> Events { get; } = [];

        public Task RecordAsync(
            string service,
            string operation,
            Exception exception,
            string severity = "Error",
            Guid? documentId = null,
            Guid? indexingJobId = null,
            CancellationToken cancellationToken = default)
        {
            Events.Add((service, operation, exception));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingChatProvider : IChatProvider
    {
        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public async IAsyncEnumerable<ChatProviderUpdate> StreamWithToolsAsync(
            string prompt,
            IReadOnlyList<ChatToolDefinition> tools,
            IReadOnlyList<ChatToolRound> rounds,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested)
                yield break;
            throw new HttpRequestException("Connection refused.");
        }
    }

    private sealed class ToolTestCurrentUser(string? id, bool admin = false) : ICurrentUser
    {
        public string? UserId => id;
        public string? Email => null;
        public bool IsAuthenticated => id is not null;
        public bool IsInRole(string role) => admin && role == ApplicationRoles.Admin;
    }

    private sealed class ScriptedToolChatProvider(
        IReadOnlyList<IReadOnlyList<ChatProviderUpdate>> responses) : IChatProvider
    {
        public int RequestCount { get; private set; }
        public IReadOnlyList<ChatToolRound>? LastRounds { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<ChatProviderUpdate> StreamWithToolsAsync(
            string prompt,
            IReadOnlyList<ChatToolDefinition> tools,
            IReadOnlyList<ChatToolRound> rounds,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRounds = rounds;
            foreach (var update in responses[RequestCount++])
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }
        }
    }

    private sealed class RepeatingToolChatProvider : IChatProvider
    {
        public int ToolEnabledRequests { get; private set; }
        public int ToolDisabledRequests { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<ChatProviderUpdate> StreamWithToolsAsync(
            string prompt,
            IReadOnlyList<ChatToolDefinition> tools,
            IReadOnlyList<ChatToolRound> rounds,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tools.Count == 0)
            {
                ToolDisabledRequests++;
                yield return new ChatProviderUpdate(Text: "Here is what I found.");
                yield break;
            }

            ToolEnabledRequests++;
            await Task.Yield();
            yield return new ChatProviderUpdate(ToolCalls:
                [new ChatToolCall($"call-{ToolEnabledRequests}", "get_my_appliances", "{}")]);
        }
    }

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
