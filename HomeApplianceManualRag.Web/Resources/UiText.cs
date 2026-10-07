using System.Globalization;
using System.Resources;

namespace HomeApplianceManualRag.Web.Resources;

public static class UiText
{
    private static readonly ResourceManager ResourceManager = new(typeof(SharedResource));

    public static string Get(string key, string? culture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var cultureInfo = string.IsNullOrWhiteSpace(culture)
            ? CultureInfo.CurrentUICulture
            : CultureInfo.GetCultureInfo(culture);

        return ResourceManager.GetString(key, cultureInfo)
            ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");
    }

    public static string Format(string key, string? culture, params object?[] arguments)
    {
        var cultureInfo = string.IsNullOrWhiteSpace(culture)
            ? CultureInfo.CurrentUICulture
            : CultureInfo.GetCultureInfo(culture);

        return string.Format(cultureInfo, Get(key, cultureInfo.Name), arguments);
    }
}
