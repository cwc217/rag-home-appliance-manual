using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Data;

public interface IActiveAccountGuard
{
    Task EnsureEnabledAsync(CancellationToken cancellationToken = default);
}

public sealed class ActiveAccountGuard(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser) : IActiveAccountGuard
{
    public async Task EnsureEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated)
            return;

        if (string.IsNullOrWhiteSpace(currentUser.UserId))
            throw new UnauthorizedAccessException("The current account is unavailable.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var isDisabled = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == currentUser.UserId)
            .Select(user => (bool?)user.AccountDisabled)
            .SingleOrDefaultAsync(cancellationToken);
        if (isDisabled != false)
            throw new UnauthorizedAccessException("The current account is disabled or no longer exists.");
    }
}
