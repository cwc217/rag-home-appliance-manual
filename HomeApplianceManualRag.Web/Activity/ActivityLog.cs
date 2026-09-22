using System.Net;
using System.Text.Json;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeApplianceManualRag.Web.Activity;

public static class ActivityTypes
{
    public const string PageViewed = "PageViewed";
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string Logout = "Logout";
    public const string UserRegistered = "UserRegistered";
    public const string DocumentUploaded = "DocumentUploaded";
    public const string DocumentSoftDeleted = "DocumentSoftDeleted";
    public const string DocumentRestored = "DocumentRestored";
    public const string DocumentHardDeleted = "DocumentHardDeleted";
    public const string DocumentRetried = "DocumentRetried";
    public const string DocumentReindexed = "DocumentReindexed";
    public const string ConversationCreated = "ConversationCreated";
    public const string ConversationDeleted = "ConversationDeleted";
    public const string ChatExchangePersisted = "ChatExchangePersisted";

    public static IReadOnlyList<string> All { get; } =
    [
        PageViewed,
        LoginSucceeded,
        LoginFailed,
        Logout,
        UserRegistered,
        DocumentUploaded,
        DocumentSoftDeleted,
        DocumentRestored,
        DocumentHardDeleted,
        DocumentRetried,
        DocumentReindexed,
        ConversationCreated,
        ConversationDeleted,
        ChatExchangePersisted
    ];
}

public sealed class UserActivity
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string? AnonymousVisitorId { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string? Path { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string IpAddressSource { get; set; } = "Connection";
    public string? UserAgent { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class ActivityLogService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task RecordAsync(
        string activityType,
        string? targetType = null,
        string? targetId = null,
        object? metadata = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
        => RecordCoreAsync(activityType, targetType, targetId, metadata, userId, cancellationToken);

    public Task RecordPageViewAsync(
        string path,
        string? anonymousVisitorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new ArgumentException("A route path is required.", nameof(path));
        return RecordCoreAsync(
            ActivityTypes.PageViewed,
            targetType: "Page",
            targetId: path,
            metadata: null,
            userId: null,
            cancellationToken: cancellationToken,
            anonymousVisitorId: anonymousVisitorId,
            path: path);
    }

    private async Task RecordCoreAsync(
        string activityType,
        string? targetType,
        string? targetId,
        object? metadata,
        string? userId,
        CancellationToken cancellationToken,
        string? anonymousVisitorId = null,
        string? path = null)
    {
        if (string.IsNullOrWhiteSpace(activityType))
            throw new ArgumentException("Activity type is required.", nameof(activityType));

        var httpContext = httpContextAccessor.HttpContext;
        var (ipAddress, ipAddressSource) = GetClientIpAddress(httpContext);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UserActivities.Add(new UserActivity
        {
            UserId = userId ?? (currentUser.IsAuthenticated ? currentUser.UserId : null),
            AnonymousVisitorId = currentUser.IsAuthenticated ? null : anonymousVisitorId,
            ActivityType = activityType,
            Path = path,
            TargetType = targetType,
            TargetId = targetId,
            IpAddress = ipAddress,
            IpAddressSource = ipAddressSource,
            UserAgent = Truncate(httpContext?.Request.Headers.UserAgent.ToString(), 512),
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions)
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static (string? Address, string Source) GetClientIpAddress(HttpContext? httpContext)
    {
        var forwardedAddress = httpContext?.Request.Headers["CF-Connecting-IP"].ToString();
        if (IPAddress.TryParse(forwardedAddress, out var parsedForwardedAddress))
            return (parsedForwardedAddress.ToString(), "CF-Connecting-IP");

        return (httpContext?.Connection.RemoteIpAddress?.ToString(), "Connection");
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
