using System.Collections;
using System.Globalization;
using System.Resources;
using HomeApplianceManualRag.Web.Localization;
using HomeApplianceManualRag.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;

namespace HomeApplianceManualRag.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-HK, en;q=0.8", "zh-Hant")]
    [InlineData("en-US, zh-Hant;q=0.8", "en")]
    [InlineData("zh-CN, en;q=0.7", "en")]
    public async Task PreferredLanguageProvider_selects_supported_language_by_quality(
        string acceptLanguage,
        string expectedCulture)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = acceptLanguage;

        var result = await new PreferredLanguageRequestCultureProvider()
            .DetermineProviderCultureResult(context);

        Assert.Equal(expectedCulture, result?.Cultures?.FirstOrDefault());
    }

    [Fact]
    public async Task PreferredLanguageProvider_returns_null_without_supported_preferences()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "fr-FR, zh-TW;q=0";

        var result = await new PreferredLanguageRequestCultureProvider()
            .DetermineProviderCultureResult(context);

        Assert.Null(result);
    }

    [Fact]
    public void Translation_resources_have_matching_keys()
    {
        var manager = new ResourceManager(
            "HomeApplianceManualRag.Web.Resources.SharedResource",
            typeof(SharedResource).Assembly);
        var english = GetResourceKeys(manager, CultureInfo.InvariantCulture);
        var traditionalChinese = GetResourceKeys(manager, CultureInfo.GetCultureInfo("zh-Hant"));

        Assert.Equal(english.Order(), traditionalChinese.Order());
    }

    [Fact]
    public void Mud_localizer_uses_project_translations_and_framework_fallback()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        var localizer = new ResourceMudLocalizer(new StringLocalizer<SharedResource>(factory));
        var originalCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hant");

            Assert.Equal("第一頁", localizer["MudPagination_FirstPage"].Value);
            Assert.Equal("關閉", localizer["MudDialog_Close"].Value);
            Assert.Equal("Clear", localizer["MudInput_Clear"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    private static string[] GetResourceKeys(ResourceManager manager, CultureInfo culture) =>
        manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Order()
            .ToArray();
}
