using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Admin;

public sealed record AdminOperationalErrorRow(
    long Id,
    DateTime OccurredAtUtc,
    DateTime LastOccurredAtUtc,
    int OccurrenceCount,
    string Service,
    string Operation,
    string Severity,
    string ExceptionType,
    string Summary,
    string Details,
    int? HttpStatusCode,
    Guid? DocumentId,
    Guid? IndexingJobId,
    string? CorrelationId);

public sealed record AdminOperationalErrorPage(
    IReadOnlyList<AdminOperationalErrorRow> Items,
    IReadOnlyList<string> Services,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed class AdminOperationalErrorLogService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser,
    IActiveAccountGuard accountGuard)
{
    public async Task<AdminOperationalErrorPage> GetPageAsync(
        int pageNumber,
        int pageSize,
        string? serviceFilter = null,
        string? severityFilter = null,
        DateTime? occurredAfterUtc = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken);

        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var retentionCutoff = DateTime.UtcNow.AddDays(-OperationalErrorLogRetention.Days);
        var services = await db.OperationalErrorEvents
            .AsNoTracking()
            .Where(error => error.LastOccurredAtUtc >= retentionCutoff)
            .Select(error => error.Service)
            .Distinct()
            .OrderBy(service => service)
            .ToListAsync(cancellationToken);

        var query = db.OperationalErrorEvents
            .AsNoTracking()
            .Where(error => error.LastOccurredAtUtc >= retentionCutoff);
        if (!string.IsNullOrWhiteSpace(serviceFilter))
            query = query.Where(error => error.Service == serviceFilter);
        if (!string.IsNullOrWhiteSpace(severityFilter))
            query = query.Where(error => error.Severity == severityFilter);
        if (occurredAfterUtc is not null)
            query = query.Where(error => error.LastOccurredAtUtc >= occurredAfterUtc.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(error => error.LastOccurredAtUtc)
            .ThenByDescending(error => error.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(error => new AdminOperationalErrorRow(
                error.Id,
                error.OccurredAtUtc,
                error.LastOccurredAtUtc,
                error.OccurrenceCount,
                error.Service,
                error.Operation,
                error.Severity,
                error.ExceptionType,
                error.Summary,
                error.Details,
                error.HttpStatusCode,
                error.DocumentId,
                error.IndexingJobId,
                error.CorrelationId))
            .ToListAsync(cancellationToken);

        return new AdminOperationalErrorPage(items, services, totalCount, pageNumber, pageSize);
    }

    private async Task EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || !currentUser.IsInRole(ApplicationRoles.Admin))
            throw new UnauthorizedAccessException("Administrator access is required.");
        await accountGuard.EnsureEnabledAsync(cancellationToken);
    }
}
