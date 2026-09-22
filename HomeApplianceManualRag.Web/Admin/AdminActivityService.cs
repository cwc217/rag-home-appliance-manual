using HomeApplianceManualRag.Web.Activity;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Admin;

public sealed record AdminActivityRow(
    long Id,
    string ActivityType,
    string? UserEmail,
    string? UserId,
    string? AnonymousVisitorId,
    string? Path,
    string? TargetType,
    string? TargetId,
    DateTime CreatedAtUtc,
    string? IpAddress,
    string? MetadataJson);

public sealed record AdminActivityPage(
    IReadOnlyList<AdminActivityRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed class AdminActivityService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser)
{
    public async Task<AdminActivityPage> GetPageAsync(
        int pageNumber,
        int pageSize,
        string? activityType = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin();
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, 100);
        activityType = activityType?.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query =
            from activity in db.UserActivities.AsNoTracking()
            join user in db.Users.AsNoTracking() on activity.UserId equals user.Id into users
            from user in users.DefaultIfEmpty()
            select new { activity, UserEmail = user == null ? null : user.Email };
        if (!string.IsNullOrWhiteSpace(activityType))
            query = query.Where(item => item.activity.ActivityType == activityType);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.activity.CreatedAtUtc)
            .ThenByDescending(item => item.activity.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new AdminActivityRow(
                item.activity.Id,
                item.activity.ActivityType,
                item.UserEmail,
                item.activity.UserId,
                item.activity.AnonymousVisitorId,
                item.activity.Path,
                item.activity.TargetType,
                item.activity.TargetId,
                item.activity.CreatedAtUtc,
                item.activity.IpAddress,
                item.activity.MetadataJson))
            .ToListAsync(cancellationToken);

        return new AdminActivityPage(items, totalCount, pageNumber, pageSize);
    }

    private void EnsureAdmin()
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
    }
}
