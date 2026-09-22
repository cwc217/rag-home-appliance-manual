using System.Security.Cryptography;

namespace HomeApplianceManualRag.Web.Activity;

public sealed class PageViewLoggingMiddleware(
    RequestDelegate next,
    ILogger<PageViewLoggingMiddleware> logger)
{
    public const string VisitorCookie = "AnonymousVisitorId";

    public async Task InvokeAsync(HttpContext context, ActivityLogService activityLog)
    {
        if (ShouldLog(context))
        {
            var visitorId = GetOrCreateVisitorId(context);
            var path = context.Request.Path.Value!;
            try
            {
                await activityLog.RecordPageViewAsync(path, visitorId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to record page view for {Path}.", context.Request.Path);
            }
        }

        await next(context);
    }

    private static bool ShouldLog(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        && context.Request.Path.HasValue
        && !context.Request.Path.StartsWithSegments("/_blazor")
        && !context.Request.Path.StartsWithSegments("/_framework")
        && context.Request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase);

    public static string GetOrCreateVisitorId(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(VisitorCookie, out var existing)
            && Guid.TryParse(existing, out _))
            return existing;

        var visitorId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        context.Response.Cookies.Append(VisitorCookie, visitorId, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(180),
            Path = "/"
        });
        return visitorId;
    }
}
