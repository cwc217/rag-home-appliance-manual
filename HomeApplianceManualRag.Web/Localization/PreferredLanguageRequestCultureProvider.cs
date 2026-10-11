using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace HomeApplianceManualRag.Web.Localization;

public sealed class PreferredLanguageRequestCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var languages = httpContext.Request.Headers.AcceptLanguage
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select((entry, index) => ParseLanguage(entry, index))
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .Where(item => item.Quality > 0)
            .OrderByDescending(item => item.Quality)
            .ThenBy(item => item.Index);

        foreach (var language in languages)
        {
            if (IsTraditionalChinese(language.Name))
                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(SupportedUiCultures.TraditionalChinese));

            if (language.Name.Equals("en", StringComparison.OrdinalIgnoreCase)
                || language.Name.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(SupportedUiCultures.English));
        }

        return Task.FromResult<ProviderCultureResult?>(null);
    }

    private static (string Name, double Quality, int Index)? ParseLanguage(string entry, int index)
    {
        var segments = entry.Split(';', StringSplitOptions.TrimEntries);
        var name = segments[0].Trim();
        if (name.Length == 0)
            return null;

        var quality = 1d;
        foreach (var segment in segments.Skip(1))
        {
            var pair = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(pair[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality)
                    || quality is < 0 or > 1)
                    return null;
                break;
            }
        }

        return (name, quality, index);
    }

    private static bool IsTraditionalChinese(string culture) =>
        culture.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)
        || culture.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase)
        || culture.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
        || culture.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
        || culture.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);
}
