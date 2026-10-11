using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Activity;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Admin;

public sealed record AdminUserRow(
    string Id,
    string Email,
    IReadOnlyList<string> Roles,
    DateTime CreatedAtUtc,
    DateTimeOffset? LockoutEnd,
    int AccessFailedCount,
    bool AccountDisabled);

public sealed record AdminUserPage(
    IReadOnlyList<AdminUserRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed class AdminUserService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser,
    ActivityLogService activityLog,
    IActiveAccountGuard accountGuard)
{
    public async Task<AdminUserPage> GetPageAsync(
        int pageNumber,
        int pageSize,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken);

        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, 100);
        search = search?.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(user =>
                (user.Email != null && user.Email.Contains(search)) ||
                user.Id.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var users = await query
            .OrderByDescending(user => user.CreatedAtUtc)
            .ThenBy(user => user.Email)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.CreatedAtUtc,
                user.LockoutEnd,
                user.AccessFailedCount,
                user.AccountDisabled
            })
            .ToListAsync(cancellationToken);

        var userIds = users.Select(user => user.Id).ToArray();
        var roles = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userIds.Contains(userRole.UserId)
            select new { userRole.UserId, Role = role.Name! })
            .ToListAsync(cancellationToken);
        var rolesByUser = roles
            .GroupBy(item => item.UserId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group
                .Select(item => item.Role)
                .OrderBy(role => role)
                .ToArray());

        return new AdminUserPage(
            users.Select(user => new AdminUserRow(
                user.Id,
                user.Email ?? "(no email)",
                rolesByUser.GetValueOrDefault(user.Id, []),
                user.CreatedAtUtc,
                user.LockoutEnd,
                user.AccessFailedCount,
                user.AccountDisabled)).ToArray(),
            totalCount,
            pageNumber,
            pageSize);
    }

    public Task DisableAsync(string userId, CancellationToken cancellationToken = default) =>
        SetAccountDisabledAsync(userId, disabled: true, cancellationToken);

    public Task EnableAsync(string userId, CancellationToken cancellationToken = default) =>
        SetAccountDisabledAsync(userId, disabled: false, cancellationToken);

    private async Task SetAccountDisabledAsync(
        string userId,
        bool disabled,
        CancellationToken cancellationToken)
    {
        await EnsureAdminAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A user ID is required.", nameof(userId));
        if (string.Equals(currentUser.UserId, userId, StringComparison.Ordinal))
            throw new InvalidOperationException("You cannot change your own account status.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new KeyNotFoundException("User was not found.");

        if (disabled && user.AccountDisabled)
            return;
        if (!disabled && !user.AccountDisabled)
            return;

        if (disabled)
        {
            var adminCount = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                where role.Name == ApplicationRoles.Admin
                select userRole.UserId).CountAsync(cancellationToken);
            if (await IsAdminAsync(db, userId, cancellationToken) && adminCount <= 1)
                throw new InvalidOperationException("The last administrator cannot be disabled.");
        }

        user.AccountDisabled = disabled;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.DisabledAtUtc = disabled ? DateTime.UtcNow : null;
        user.DisabledByUserId = disabled ? currentUser.UserId : null;
        if (!disabled)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
        }
        await db.SaveChangesAsync(cancellationToken);

        await activityLog.RecordAsync(
            disabled ? ActivityTypes.AccountDisabled : ActivityTypes.AccountEnabled,
            targetType: "User",
            targetId: user.Id,
            metadata: new { Email = user.Email });
    }

    private static Task<bool> IsAdminAsync(
        ApplicationDbContext db,
        string userId,
        CancellationToken cancellationToken) =>
        (from userRole in db.UserRoles
         join role in db.Roles on userRole.RoleId equals role.Id
         where userRole.UserId == userId && role.Name == ApplicationRoles.Admin
         select userRole.UserId).AnyAsync(cancellationToken);

    private async Task EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
        await accountGuard.EnsureEnabledAsync(cancellationToken);
    }
}
