using HomeApplianceManualRag.Web.Admin;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Tests;

public sealed class AdminDashboardTests
{
    [Fact]
    public async Task Snapshot_aggregates_existing_operational_data()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = "user-1",
                UserName = "user@example.invalid",
                Email = "user@example.invalid"
            });
            db.ManualDocuments.AddRange(
                new ManualDocument { DisplayName = "ready.pdf", Status = DocumentStatus.Ready },
                new ManualDocument { DisplayName = "failed.pdf", Status = DocumentStatus.Failed, LastError = "parse failed" });
            db.Conversations.AddRange(
                new Conversation { Title = "Anonymous question" },
                new Conversation { Title = "User question", UserId = "user-1" });
            db.IndexingJobs.AddRange(
                new IndexingJob { DocumentId = Guid.NewGuid(), Status = IndexingJobStatus.Processing },
                new IndexingJob { DocumentId = Guid.NewGuid(), Status = IndexingJobStatus.Failed });
            await db.SaveChangesAsync();
        }

        var currentUser = new FakeCurrentUser(isAdmin: true);
        var factory = new TestDbFactory(options);
        var conversationService = new AdminConversationService(factory, currentUser);
        var service = new AdminDashboardService(factory, currentUser, conversationService);

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(1, snapshot.RegisteredUsers);
        Assert.Equal(2, snapshot.TotalDocuments);
        Assert.Equal(1, snapshot.DocumentsByStatus[DocumentStatus.Ready]);
        Assert.Equal(1, snapshot.DocumentsByStatus[DocumentStatus.Failed]);
        Assert.Equal(2, snapshot.TotalConversations);
        Assert.Equal(1, snapshot.ActiveIndexingJobs);
        Assert.Equal(1, snapshot.FailedIndexingJobs);
        Assert.Contains(snapshot.RecentConversations, item => item.Title == "Anonymous question" && item.Owner == "Anonymous");
        Assert.Contains(snapshot.RecentConversations, item => item.Title == "User question" && item.Owner == "user@example.invalid");
    }

    [Fact]
    public async Task Snapshot_requires_admin_role()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var currentUser = new FakeCurrentUser(isAdmin: false);
        var service = new AdminDashboardService(
            new TestDbFactory(options),
            currentUser,
            new AdminConversationService(new TestDbFactory(options), currentUser));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSnapshotAsync());
    }

    [Fact]
    public async Task Conversation_detail_returns_messages_for_admin()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var conversationId = Guid.NewGuid();
        await using (var db = new ApplicationDbContext(options))
        {
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                Title = "Details",
                Messages =
                [
                    new ChatMessage { Role = ChatMessageRole.User, Content = "Question" },
                    new ChatMessage { Role = ChatMessageRole.Assistant, Content = "Answer" }
                ]
            });
            await db.SaveChangesAsync();
        }

        var service = new AdminConversationService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: true));

        var detail = await service.GetDetailAsync(conversationId);

        Assert.NotNull(detail);
        Assert.Equal("Anonymous", detail.Summary.Owner);
        Assert.Equal(["Question", "Answer"], detail.Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task Indexing_job_page_returns_newest_jobs_and_total_count()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var firstDocument = new ManualDocument { DisplayName = "first.pdf" };
        var secondDocument = new ManualDocument { DisplayName = "second.pdf" };
        await using (var db = new ApplicationDbContext(options))
        {
            db.ManualDocuments.AddRange(firstDocument, secondDocument);
            db.IndexingJobs.AddRange(
                new IndexingJob
                {
                    Document = firstDocument,
                    Status = IndexingJobStatus.Completed,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
                },
                new IndexingJob
                {
                    Document = secondDocument,
                    Status = IndexingJobStatus.Failed,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastError = "parse failed"
                });
            await db.SaveChangesAsync();
        }

        var service = new AdminIndexingJobService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: true));

        var page = await service.GetPageAsync(1, 1);

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal("second.pdf", page.Items[0].DocumentName);
        Assert.Equal(IndexingJobStatus.Failed, page.Items[0].Status);
        Assert.Equal("parse failed", page.Items[0].LastError);
    }

    private sealed class FakeCurrentUser(bool isAdmin) : ICurrentUser
    {
        public string? UserId => isAdmin ? "admin-1" : "user-1";
        public string? Email => "admin@example.invalid";
        public bool IsAuthenticated => true;
        public bool IsInRole(string role) => isAdmin && role == ApplicationRoles.Admin;
    }

    private sealed class TestDbFactory(DbContextOptions<ApplicationDbContext> options)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);

        public Task<ApplicationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }
}
