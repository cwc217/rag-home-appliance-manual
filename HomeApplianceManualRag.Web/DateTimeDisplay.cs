using System.Globalization;

namespace HomeApplianceManualRag.Web;

public static class DateTimeDisplay
{
    private static readonly TimeSpan UtcPlusEight = TimeSpan.FromHours(8);

    public static string FormatUtcPlusEight(DateTime value, bool includeSeconds = false)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return new DateTimeOffset(utc, TimeSpan.Zero)
            .ToOffset(UtcPlusEight)
            .ToString(includeSeconds ? "yyyy-MM-dd HH:mm:ss 'UTC+8'" : "yyyy-MM-dd HH:mm 'UTC+8'", CultureInfo.InvariantCulture);
    }

    public static string FormatUtcPlusEight(DateTimeOffset value, bool includeSeconds = false) =>
        value.ToOffset(UtcPlusEight)
            .ToString(includeSeconds ? "yyyy-MM-dd HH:mm:ss 'UTC+8'" : "yyyy-MM-dd HH:mm 'UTC+8'", CultureInfo.InvariantCulture);
}
