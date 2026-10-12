using System.Globalization;
using HomeApplianceManualRag.Web.Localization;
using HomeApplianceManualRag.Web.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Tests;

public sealed class UiCultureExecutionTests
{
    [Fact]
    public async Task RunAsync_restores_requested_ui_culture_during_background_callback()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var english = CultureInfo.GetCultureInfo("en");
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        var localizer = new StringLocalizer<SharedResource>(factory);

        try
        {
            CultureInfo.CurrentCulture = english;
            CultureInfo.CurrentUICulture = english;

            await UiCultureExecution.RunAsync(
                CultureInfo.GetCultureInfo("zh-Hant"),
                () =>
                {
                    Assert.Equal("我的文件", localizer["My documents"].Value);
                    return Task.CompletedTask;
                });

            Assert.Equal("en", CultureInfo.CurrentCulture.Name);
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("My documents", localizer["My documents"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task RunAsync_restores_culture_when_callback_fails()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                UiCultureExecution.RunAsync(
                    CultureInfo.GetCultureInfo("zh-Hant"),
                    () => throw new InvalidOperationException("callback failed")));

            Assert.Equal("en", CultureInfo.CurrentCulture.Name);
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
