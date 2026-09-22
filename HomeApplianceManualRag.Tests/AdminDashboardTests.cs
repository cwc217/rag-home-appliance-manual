using HomeApplianceManualRag.Web.Admin;
using HomeApplianceManualRag.Web.Activity;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Net;

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

    [Fact]
    public async Task User_page_returns_newest_users_with_roles_and_status()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            var user = new ApplicationUser
            {
                Id = "user-1",
                UserName = "admin@example.invalid",
                Email = "admin@example.invalid",
                CreatedAtUtc = DateTime.UtcNow,
                AccessFailedCount = 2,
                LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10)
            };
            db.Users.Add(user);
            db.Roles.Add(new Microsoft.AspNetCore.Identity.IdentityRole
            {
                Id = "role-1",
                Name = ApplicationRoles.Admin,
                NormalizedName = ApplicationRoles.Admin.ToUpperInvariant()
            });
            db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string>
            {
                UserId = user.Id,
                RoleId = "role-1"
            });
            await db.SaveChangesAsync();
        }

        var service = new AdminUserService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: true),
            new ActivityLogService(
                new TestDbFactory(options),
                new FakeCurrentUser(isAdmin: true),
                new HttpContextAccessor()));

        var page = await service.GetPageAsync(1, 20);

        var row = Assert.Single(page.Items);
        Assert.Equal("admin@example.invalid", row.Email);
        Assert.Equal([ApplicationRoles.Admin], row.Roles);
        Assert.Equal(2, row.AccessFailedCount);
        Assert.NotNull(row.LockoutEnd);
    }

    [Fact]
    public async Task User_page_requires_admin_role()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var service = new AdminUserService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: false),
            new ActivityLogService(
                new TestDbFactory(options),
                new FakeCurrentUser(isAdmin: false),
                new HttpContextAccessor()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetPageAsync(1, 20));
    }

    [Fact]
    public async Task Admin_can_disable_and_enable_user_account()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = "user-2",
                UserName = "user@example.invalid",
                Email = "user@example.invalid"
            });
            await db.SaveChangesAsync();
        }

        var currentUser = new FakeCurrentUser(isAdmin: true);
        var service = new AdminUserService(
            new TestDbFactory(options),
            currentUser,
            new ActivityLogService(new TestDbFactory(options), currentUser, new HttpContextAccessor()));

        await service.DisableAsync("user-2");
        await using (var db = new ApplicationDbContext(options))
        {
            var user = await db.Users.SingleAsync(item => item.Id == "user-2");
            Assert.True(user.AccountDisabled);
            Assert.Equal("admin-1", user.DisabledByUserId);
            Assert.NotNull(user.DisabledAtUtc);
        }

        await service.EnableAsync("user-2");
        await using (var db = new ApplicationDbContext(options))
        {
            var user = await db.Users.SingleAsync(item => item.Id == "user-2");
            Assert.False(user.AccountDisabled);
            Assert.Null(user.DisabledByUserId);
            Assert.Null(user.DisabledAtUtc);
        }

        await using var activityDb = new ApplicationDbContext(options);
        Assert.Contains(activityDb.UserActivities, activity => activity.ActivityType == ActivityTypes.AccountDisabled);
        Assert.Contains(activityDb.UserActivities, activity => activity.ActivityType == ActivityTypes.AccountEnabled);
    }

    [Fact]
    public async Task Admin_cannot_disable_last_admin_or_own_account()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new ApplicationUser { Id = "admin-1", UserName = "admin@example.invalid" });
            db.Roles.Add(new Microsoft.AspNetCore.Identity.IdentityRole
            {
                Id = "role-1",
                Name = ApplicationRoles.Admin,
                NormalizedName = ApplicationRoles.Admin.ToUpperInvariant()
            });
            db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string>
            {
                UserId = "admin-1",
                RoleId = "role-1"
            });
            await db.SaveChangesAsync();
        }

        var currentUser = new FakeCurrentUser(isAdmin: true, userId: "admin-2");
        var service = new AdminUserService(
            new TestDbFactory(options),
            currentUser,
            new ActivityLogService(new TestDbFactory(options), currentUser, new HttpContextAccessor()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisableAsync("admin-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisableAsync("admin-2"));
    }

    [Fact]
    public async Task Activity_log_records_safe_request_context_details()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        httpContext.Request.Headers.UserAgent = new string('a', 600);
        var service = new ActivityLogService(
            new TestDbFactory(options),
            new FakeAnonymousCurrentUser(),
            new HttpContextAccessor { HttpContext = httpContext });

        await service.RecordAsync(
            ActivityTypes.LoginFailed,
            metadata: new { Reason = "invalid_credentials" });

        await using var db = new ApplicationDbContext(options);
        var activity = Assert.Single(db.UserActivities);
        Assert.Equal(ActivityTypes.LoginFailed, activity.ActivityType);
        Assert.Equal("192.0.2.10", activity.IpAddress);
        Assert.Equal("Connection", activity.IpAddressSource);
        Assert.Equal(512, activity.UserAgent!.Length);
        Assert.Contains("invalid_credentials", activity.MetadataJson);
    }

    [Fact]
    public async Task Activity_log_prefers_valid_cloudflare_client_ip()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("172.29.8.1");
        httpContext.Request.Headers["CF-Connecting-IP"] = "203.0.113.25";
        var service = new ActivityLogService(
            new TestDbFactory(options),
            new FakeAnonymousCurrentUser(),
            new HttpContextAccessor { HttpContext = httpContext });

        await service.RecordAsync(ActivityTypes.PageViewed);

        await using var db = new ApplicationDbContext(options);
        var activity = Assert.Single(db.UserActivities);
        Assert.Equal("203.0.113.25", activity.IpAddress);
        Assert.Equal("CF-Connecting-IP", activity.IpAddressSource);
    }

    [Fact]
    public async Task Activity_log_falls_back_when_cloudflare_client_ip_is_invalid()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("172.29.8.1");
        httpContext.Request.Headers["CF-Connecting-IP"] = "not-an-ip";
        var service = new ActivityLogService(
            new TestDbFactory(options),
            new FakeAnonymousCurrentUser(),
            new HttpContextAccessor { HttpContext = httpContext });

        await service.RecordAsync(ActivityTypes.PageViewed);

        await using var db = new ApplicationDbContext(options);
        var activity = Assert.Single(db.UserActivities);
        Assert.Equal("172.29.8.1", activity.IpAddress);
        Assert.Equal("Connection", activity.IpAddressSource);
    }

    [Fact]
    public async Task Activity_page_returns_newest_events_for_admin()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.UserActivities.Add(new UserActivity
            {
                ActivityType = ActivityTypes.LoginSucceeded,
                UserId = "user-1",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var service = new AdminActivityService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: true));

        var page = await service.GetPageAsync(1, 20);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(ActivityTypes.LoginSucceeded, Assert.Single(page.Items).ActivityType);
    }

    [Fact]
    public async Task Activity_page_filters_by_type_email_ip_and_path()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = "user-1",
                UserName = "alice@example.invalid",
                Email = "Alice@example.invalid"
            });
            db.UserActivities.AddRange(
                new UserActivity
                {
                    ActivityType = ActivityTypes.PageViewed,
                    UserId = "user-1",
                    IpAddress = "203.0.113.10",
                    Path = "/admin/users"
                },
                new UserActivity
                {
                    ActivityType = ActivityTypes.LoginSucceeded,
                    UserId = "user-1",
                    IpAddress = "203.0.113.11",
                    Path = "/login"
                },
                new UserActivity
                {
                    ActivityType = ActivityTypes.PageViewed,
                    IpAddress = "203.0.113.10",
                    Path = "/my-documents"
                });
            await db.SaveChangesAsync();
        }

        var service = new AdminActivityService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: true));

        var page = await service.GetPageAsync(
            1,
            20,
            ActivityTypes.PageViewed,
            "alice@EXAMPLE",
            "203.0.113.10",
            "/admin");

        var activity = Assert.Single(page.Items);
        Assert.Equal("/admin/users", activity.Path);
        Assert.Equal("Alice@example.invalid", activity.UserEmail);
    }

    [Fact]
    public async Task Activity_page_ip_filter_matches_partial_address()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.UserActivities.AddRange(
                new UserActivity { ActivityType = ActivityTypes.PageViewed, IpAddress = "172.29.8.1" },
                new UserActivity { ActivityType = ActivityTypes.PageViewed, IpAddress = "203.0.113.10" });
            await db.SaveChangesAsync();
        }

        var service = new AdminActivityService(
            new TestDbFactory(options),
            new FakeCurrentUser(isAdmin: true));

        var page = await service.GetPageAsync(1, 20, ipAddress: "172");

        var activity = Assert.Single(page.Items);
        Assert.Equal("172.29.8.1", activity.IpAddress);
    }

    [Fact]
    public async Task Anonymous_page_view_records_route_and_visitor_id()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        var service = new ActivityLogService(
            new TestDbFactory(options),
            new FakeAnonymousCurrentUser(),
            new HttpContextAccessor { HttpContext = httpContext });

        await service.RecordPageViewAsync("/", "visitor-abc");

        await using var db = new ApplicationDbContext(options);
        var activity = Assert.Single(db.UserActivities);
        Assert.Equal(ActivityTypes.PageViewed, activity.ActivityType);
        Assert.Null(activity.UserId);
        Assert.Equal("visitor-abc", activity.AnonymousVisitorId);
        Assert.Equal("/", activity.Path);
        Assert.Equal("/", activity.TargetId);
        Assert.Equal("198.51.100.4", activity.IpAddress);
    }

    private sealed class FakeCurrentUser(bool isAdmin, string userId = "admin-1") : ICurrentUser
    {
        public string? UserId => isAdmin ? userId : "user-1";
        public string? Email => "admin@example.invalid";
        public bool IsAuthenticated => true;
        public bool IsInRole(string role) => isAdmin && role == ApplicationRoles.Admin;
    }

    private sealed class FakeAnonymousCurrentUser : ICurrentUser
    {
        public string? UserId => null;
        public string? Email => null;
        public bool IsAuthenticated => false;
        public bool IsInRole(string role) => false;
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
