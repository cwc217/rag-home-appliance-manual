using HomeApplianceManualRag.Web.Chat;
using HomeApplianceManualRag.Web.Admin;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Tests;

public sealed class ActiveAccountGuardTests
{
    [Fact]
    public async Task Disabled_account_cannot_use_chat_history_service()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = "disabled-user",
                UserName = "disabled@example.invalid",
                AccountDisabled = true
            });
            await db.SaveChangesAsync();
        }

        var factory = new TestDbFactory(options);
        var currentUser = new FakeCurrentUser("disabled-user");
        var guard = new ActiveAccountGuard(factory, currentUser);
        var service = new ChatHistoryService(factory, currentUser, guard);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetConversationsAsync());
    }

    [Fact]
    public async Task Active_account_and_anonymous_user_pass_account_guard()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = "active-user",
                UserName = "active@example.invalid"
            });
            await db.SaveChangesAsync();
        }

        var factory = new TestDbFactory(options);
        await new ActiveAccountGuard(factory, new FakeCurrentUser("active-user")).EnsureEnabledAsync();
        await new ActiveAccountGuard(factory, new FakeCurrentUser(null)).EnsureEnabledAsync();
    }

    [Fact]
    public async Task Disabled_admin_cannot_read_admin_activity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = "disabled-admin",
                UserName = "disabled-admin@example.invalid",
                AccountDisabled = true
            });
            await db.SaveChangesAsync();
        }

        var factory = new TestDbFactory(options);
        var currentUser = new FakeCurrentUser("disabled-admin", isAdmin: true);
        var service = new AdminActivityService(
            factory,
            currentUser,
            new ActiveAccountGuard(factory, currentUser));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetPageAsync(1, 20));
    }

    [Fact]
    public async Task Missing_authenticated_user_is_rejected()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var guard = new ActiveAccountGuard(
            new TestDbFactory(options),
            new FakeCurrentUser("deleted-user"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.EnsureEnabledAsync());
    }

    private sealed class FakeCurrentUser(string? userId, bool isAdmin = false) : ICurrentUser
    {
        public string? UserId => userId;
        public string? Email => null;
        public bool IsAuthenticated => userId is not null;
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
