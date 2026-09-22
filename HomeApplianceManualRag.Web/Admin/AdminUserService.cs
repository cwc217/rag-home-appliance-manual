using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Admin;

public sealed record AdminUserRow(
    string Id,
    string Email,
    IReadOnlyList<string> Roles,
    DateTime CreatedAtUtc,
    DateTimeOffset? LockoutEnd,
    int AccessFailedCount);

public sealed record AdminUserPage(
    IReadOnlyList<AdminUserRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed class AdminUserService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser)
{
    public async Task<AdminUserPage> GetPageAsync(
        int pageNumber,
        int pageSize,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin();

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
                user.AccessFailedCount
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
                user.AccessFailedCount)).ToArray(),
            totalCount,
            pageNumber,
            pageSize);
    }

    private void EnsureAdmin()
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
    }
}
