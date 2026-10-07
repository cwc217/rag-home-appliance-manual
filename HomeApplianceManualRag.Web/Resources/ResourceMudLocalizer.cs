using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace HomeApplianceManualRag.Web.Resources;

public sealed class ResourceMudLocalizer(IStringLocalizer<SharedResource> localizer) : MudLocalizer
{
    private static readonly ResourceManager MudBlazorResources = new(
        "MudBlazor.Resources.LanguageResource",
        typeof(MudLocalizer).Assembly);

    public override LocalizedString this[string key] => GetLocalizedString(key, []);

    public override LocalizedString this[string key, params object[] arguments] =>
        GetLocalizedString(key, arguments);

    private LocalizedString GetLocalizedString(string key, object[] arguments)
    {
        var localized = localizer[key, arguments];
        if (!localized.ResourceNotFound)
            return localized;

        var fallback = MudBlazorResources.GetString(key, CultureInfo.CurrentUICulture);
        if (fallback is null)
            return localized;

        var value = arguments.Length == 0
            ? fallback
            : string.Format(CultureInfo.CurrentUICulture, fallback, arguments);
        return new LocalizedString(key, value);
    }
}
