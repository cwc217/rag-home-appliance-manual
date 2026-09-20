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
            db.Users.Add(new ApplicationUser { Id = "user-1", UserName = "user@example.invalid" });
            db.ManualDocuments.AddRange(
                new ManualDocument { DisplayName = "ready.pdf", Status = DocumentStatus.Ready },
                new ManualDocument { DisplayName = "failed.pdf", Status = DocumentStatus.Failed, LastError = "parse failed" });
            db.Conversations.Add(new Conversation { Title = "Anonymous question" });
            db.IndexingJobs.AddRange(
                new IndexingJob { DocumentId = Guid.NewGuid(), Status = IndexingJobStatus.Processing },
                new IndexingJob { DocumentId = Guid.NewGuid(), Status = IndexingJobStatus.Failed });
            await db.SaveChangesAsync();
        }

        var service = new AdminDashboardService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: true));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(1, snapshot.RegisteredUsers);
        Assert.Equal(2, snapshot.TotalDocuments);
        Assert.Equal(1, snapshot.DocumentsByStatus[DocumentStatus.Ready]);
        Assert.Equal(1, snapshot.DocumentsByStatus[DocumentStatus.Failed]);
        Assert.Equal(1, snapshot.TotalConversations);
        Assert.Equal(1, snapshot.ActiveIndexingJobs);
        Assert.Equal(1, snapshot.FailedIndexingJobs);
        Assert.Contains(snapshot.RecentActivity, item => item.Title == "ready.pdf");
        Assert.Contains(snapshot.RecentActivity, item => item.Detail == "Anonymous conversation");
    }

    [Fact]
    public async Task Snapshot_requires_admin_role()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var service = new AdminDashboardService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSnapshotAsync());
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
