using System.Globalization;

namespace HomeApplianceManualRag.Web.Localization;

public static class SupportedUiCultures
{
    public const string English = "en";
    public const string TraditionalChinese = "zh-Hant";

    public static readonly CultureInfo[] Cultures =
    [
        CultureInfo.GetCultureInfo(English),
        CultureInfo.GetCultureInfo(TraditionalChinese)
    ];

    public static bool IsSupported(string? culture) =>
        string.Equals(culture, English, StringComparison.OrdinalIgnoreCase)
        || string.Equals(culture, TraditionalChinese, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string culture) =>
        string.Equals(culture, TraditionalChinese, StringComparison.OrdinalIgnoreCase)
            ? TraditionalChinese
            : English;
}
