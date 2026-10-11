using HomeApplianceManualRag.Web.Admin;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace HomeApplianceManualRag.Tests;

public sealed class OperationalErrorLoggingTests
{
    [Fact]
    public async Task Recorder_stores_safe_connection_diagnostics_and_related_ids()
    {
        var options = CreateOptions();
        var factory = new TestDbFactory(options);
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var recorder = new OperationalErrorRecorder(
            factory,
            NullLogger<OperationalErrorRecorder>.Instance);
        var exception = new HomeApplianceManualRag.Web.Documents.DocumentParseException(
            "Parser returned sensitive document content.",
            retryable: true,
            inner: new HttpRequestException(
                "Connection refused; api-key=secret-value; Bearer token-value"),
            httpStatusCode: (int)HttpStatusCode.ServiceUnavailable);

        await recorder.RecordAsync(
            "Docling",
            "Index document",
            exception,
            "Warning",
            documentId,
            jobId);

        await using var db = new ApplicationDbContext(options);
        var saved = Assert.Single(db.OperationalErrorEvents);
        Assert.Equal("Docling", saved.Service);
        Assert.Equal("Warning", saved.Severity);
        Assert.Equal("HTTP 503 failure", saved.Summary);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, saved.HttpStatusCode);
        Assert.Equal(documentId, saved.DocumentId);
        Assert.Equal(jobId, saved.IndexingJobId);
        Assert.Contains("Connection refused", saved.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", saved.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", saved.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive document content", saved.Details, StringComparison.Ordinal);
        Assert.True(saved.OccurredAtUtc.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task Recorder_stores_redacted_docling_response_diagnostics()
    {
        var options = CreateOptions();
        var recorder = new OperationalErrorRecorder(
            new TestDbFactory(options),
            NullLogger<OperationalErrorRecorder>.Instance);
        var exception = new HomeApplianceManualRag.Web.Documents.DocumentParseException(
            "Docling returned HTTP 504.",
            retryable: true,
            httpStatusCode: (int)HttpStatusCode.GatewayTimeout,
            diagnosticDetails: "Processing exceeded timeout; api-key=secret-value");

        await recorder.RecordAsync("Docling", "Index document", exception, "Warning");

        await using var db = new ApplicationDbContext(options);
        var saved = Assert.Single(db.OperationalErrorEvents);
        Assert.Contains("Processing exceeded timeout", saved.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", saved.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recorder_removes_events_past_the_90_day_retention_period()
    {
        var options = CreateOptions();
        await using (var db = new ApplicationDbContext(options))
        {
            db.OperationalErrorEvents.Add(new OperationalErrorEvent
            {
                Service = "Old service",
                Operation = "Old operation",
                ExceptionType = "Exception",
                Summary = "Old",
                Details = "Old",
                OccurredAtUtc = DateTime.UtcNow.AddDays(-91),
                LastOccurredAtUtc = DateTime.UtcNow.AddDays(-91),
                Fingerprint = Guid.NewGuid().ToString("N")
            });
            await db.SaveChangesAsync();
        }

        var retention = new OperationalErrorLogRetentionService(new TestDbFactory(options));
        await retention.RemoveExpiredAsync();

        await using var resultDb = new ApplicationDbContext(options);
        Assert.Empty(resultDb.OperationalErrorEvents);
    }

    [Fact]
    public async Task Recorder_coalesces_matching_errors_within_five_minutes()
    {
        var options = CreateOptions();
        var recorder = new OperationalErrorRecorder(
            new TestDbFactory(options),
            NullLogger<OperationalErrorRecorder>.Instance);

        await recorder.RecordAsync("Docling", "Index document", new HttpRequestException("Connection refused."));
        await recorder.RecordAsync("Docling", "Index document", new HttpRequestException("Connection refused."));

        await using var db = new ApplicationDbContext(options);
        var saved = Assert.Single(db.OperationalErrorEvents);
        Assert.Equal(2, saved.OccurrenceCount);
        Assert.True(saved.LastOccurredAtUtc >= saved.OccurredAtUtc);
    }

    [Fact]
    public async Task Admin_error_page_filters_and_paginates_newest_events()
    {
        var options = CreateOptions();
        var now = DateTime.UtcNow;
        await using (var db = new ApplicationDbContext(options))
        {
            db.OperationalErrorEvents.AddRange(
                MakeEvent("Docling", "Warning", now.AddMinutes(-3)),
                MakeEvent("Docling", "Error", now.AddMinutes(-2)),
                MakeEvent("Chat provider", "Error", now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        var service = CreateAdminService(options, isAdmin: true);

        var page = await service.GetPageAsync(
            pageNumber: 1,
            pageSize: 1,
            serviceFilter: "Docling",
            severityFilter: "Error",
            occurredAfterUtc: now.AddHours(-1));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("Docling", Assert.Single(page.Items).Service);
        Assert.Contains("Chat provider", page.Services);
    }

    [Fact]
    public async Task Admin_error_page_requires_admin_role()
    {
        var service = CreateAdminService(CreateOptions(), isAdmin: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetPageAsync(1, 20));
    }

    private static OperationalErrorEvent MakeEvent(string service, string severity, DateTime occurredAtUtc) =>
        new()
        {
            Service = service,
            Operation = "Test operation",
            Severity = severity,
            ExceptionType = "HttpRequestException",
            Summary = "Connection or request failure",
            Details = "HttpRequestException: Connection refused.",
            OccurredAtUtc = occurredAtUtc,
            LastOccurredAtUtc = occurredAtUtc,
            Fingerprint = Guid.NewGuid().ToString("N")
        };

    private static AdminOperationalErrorLogService CreateAdminService(
        DbContextOptions<ApplicationDbContext> options,
        bool isAdmin) =>
        new(
            new TestDbFactory(options),
            new TestCurrentUser(isAdmin),
            new FakeActiveAccountGuard());

    private static DbContextOptions<ApplicationDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private sealed class TestCurrentUser(bool isAdmin) : ICurrentUser
    {
        public string? UserId => isAdmin ? "admin-1" : "user-1";
        public string? Email => null;
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
