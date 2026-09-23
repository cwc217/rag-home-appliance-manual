using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Diagnostics;

public static class OperationalErrorLogRetention
{
    public const int Days = 90;
}

public interface IOperationalErrorRecorder
{
    Task RecordAsync(
        string service,
        string operation,
        Exception exception,
        string severity = "Error",
        Guid? documentId = null,
        Guid? indexingJobId = null,
        CancellationToken cancellationToken = default);
}

public sealed class OperationalErrorRecorder(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<OperationalErrorRecorder> logger) : IOperationalErrorRecorder
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim RecordGate = new(1, 1);
    private const int MaxDetailsLength = 3000;
    private static readonly Regex CredentialPattern = new(
        @"(?i)\b(api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|client[-_ ]?secret|password|authorization)\b\s*[:=]\s*[""']?[^ \r\n""'&,;]+",
        RegexOptions.Compiled);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled);
    private static readonly Regex QueryCredentialPattern = new(
        @"(?i)([?&](?:api[-_]?key|access[-_]?token|refresh[-_]?token|token|secret|password)=)[^&\s]+",
        RegexOptions.Compiled);

    public async Task RecordAsync(
        string service,
        string operation,
        Exception exception,
        string severity = "Error",
        Guid? documentId = null,
        Guid? indexingJobId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);

        await RecordGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var occurredAtUtc = DateTime.UtcNow;
            var exceptionType = exception.GetType().Name;
            var summary = GetSummary(exception);
            var details = GetSafeDetails(exception);
            var statusCode = GetHttpStatusCode(exception);
            var fingerprint = GetFingerprint(
                service,
                operation,
                severity,
                exceptionType,
                summary,
                details,
                statusCode);
            var duplicate = await db.OperationalErrorEvents
                .Where(error => error.Fingerprint == fingerprint &&
                                error.LastOccurredAtUtc >= occurredAtUtc - DuplicateWindow)
                .OrderByDescending(error => error.LastOccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (duplicate is not null)
            {
                duplicate.LastOccurredAtUtc = occurredAtUtc;
                duplicate.DocumentId = documentId ?? duplicate.DocumentId;
                duplicate.IndexingJobId = indexingJobId ?? duplicate.IndexingJobId;
                duplicate.CorrelationId =
                    System.Diagnostics.Activity.Current?.TraceId.ToString() ?? duplicate.CorrelationId;
                if (duplicate.OccurrenceCount < int.MaxValue)
                    duplicate.OccurrenceCount++;
            }
            else
            {
                db.OperationalErrorEvents.Add(new OperationalErrorEvent
                {
                    OccurredAtUtc = occurredAtUtc,
                    LastOccurredAtUtc = occurredAtUtc,
                    OccurrenceCount = 1,
                    Fingerprint = fingerprint,
                    Service = service,
                    Operation = operation,
                    Severity = severity,
                    ExceptionType = exceptionType,
                    Summary = summary,
                    Details = details,
                    HttpStatusCode = statusCode,
                    DocumentId = documentId,
                    IndexingJobId = indexingJobId,
                    CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString()
                });
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception persistenceException)
        {
            logger.LogError(
                persistenceException,
                "Could not persist operational error for {Service} during {Operation}.",
                service,
                operation);
        }
        finally
        {
            RecordGate.Release();
        }
    }

    private static string GetFingerprint(
        string service,
        string operation,
        string severity,
        string exceptionType,
        string summary,
        string details,
        int? statusCode)
    {
        var key = string.Join(
            "\n",
            service,
            operation,
            severity,
            exceptionType,
            summary,
            details,
            statusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private static string GetSummary(Exception exception)
    {
        var statusCode = GetHttpStatusCode(exception);
        if (statusCode is not null)
            return $"HTTP {statusCode} failure";
        if (FindException<HttpRequestException>(exception) is not null ||
            FindException<SocketException>(exception) is not null)
            return "Connection or request failure";
        if (FindException<OperationCanceledException>(exception) is not null)
            return "Request timed out or was cancelled";
        return "Operation failed";
    }

    private static string GetSafeDetails(Exception exception)
    {
        var details = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var item = current.GetType().Name;
            if (current is HttpRequestException or SocketException)
            {
                var message = current.Message;
                if (message.Length > MaxDetailsLength)
                    message = message[..MaxDetailsLength];
                item += ": " + Redact(message);
            }
            else if (current is OperationCanceledException)
                item += ": Request timed out or was cancelled.";
            details.Add(item);
        }

        var combined = string.Join(" -> ", details);
        return combined.Length <= MaxDetailsLength
            ? combined
            : combined[..MaxDetailsLength];
    }

    private static string Redact(string value) =>
        BearerPattern.Replace(
            QueryCredentialPattern.Replace(
                CredentialPattern.Replace(value, "$1=[REDACTED]"),
                "$1[REDACTED]"),
            "Bearer [REDACTED]");

    private static int? GetHttpStatusCode(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } statusCode })
                return (int)statusCode;
            if (current is RequestFailedException requestFailed && requestFailed.Status > 0)
                return requestFailed.Status;
            if (current is HomeApplianceManualRag.Web.Documents.DocumentParseException
                { HttpStatusCode: { } doclingStatus })
                return doclingStatus;
        }

        return null;
    }

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
                return match;
        }

        return null;
    }
}

public sealed class OperationalErrorLogRetentionService(
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task RemoveExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var expiry = DateTime.UtcNow.AddDays(-OperationalErrorLogRetention.Days);
        if (db.Database.IsRelational())
        {
            await db.OperationalErrorEvents
                .Where(error => error.OccurredAtUtc < expiry)
                .ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var expiredEvents = await db.OperationalErrorEvents
            .Where(error => error.OccurredAtUtc < expiry)
            .ToListAsync(cancellationToken);
        db.OperationalErrorEvents.RemoveRange(expiredEvents);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OperationalErrorLogRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OperationalErrorLogRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var retention = scope.ServiceProvider
                    .GetRequiredService<OperationalErrorLogRetentionService>();
                await retention.RemoveExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not remove expired operational error records.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
