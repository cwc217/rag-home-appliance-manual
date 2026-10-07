using System.Globalization;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Documents;

namespace HomeApplianceManualRag.Tests;

public sealed class ManualDocumentPresentationTests
{
    [Fact]
    public void FormatProductSummary_includes_brand_category_and_model_in_order()
    {
        WithUiCulture("en", () =>
        {
            var document = new ManualDocument
            {
                Brand = "  Acme ",
                ApplianceCategory = "Washing Machine",
                ModelNumber = "WM-100"
            };

            var summary = ManualDocumentPresentation.FormatProductSummary(document);

            Assert.Equal("Brand: Acme · Category: Washing Machine · Model: WM-100", summary);
        });
    }

    [Fact]
    public void FormatProductSummary_localizes_field_labels()
    {
        WithUiCulture("zh-Hant", () =>
        {
            var document = new ManualDocument
            {
                Brand = "Acme",
                ApplianceCategory = "洗衣機",
                ModelNumber = "WM-100"
            };

            var summary = ManualDocumentPresentation.FormatProductSummary(document);

            Assert.Equal("品牌: Acme · 類別: 洗衣機 · 型號: WM-100", summary);
        });
    }

    [Fact]
    public void FormatProductSummary_omits_missing_fields_and_returns_null_when_empty()
    {
        WithUiCulture("en", () =>
        {
            var document = new ManualDocument
            {
                Brand = "Acme",
                ApplianceCategory = " ",
                ModelNumber = null
            };

            Assert.Equal("Brand: Acme", ManualDocumentPresentation.FormatProductSummary(document));
            Assert.Null(ManualDocumentPresentation.FormatProductSummary(new ManualDocument()));
        });
    }

    private static void WithUiCulture(string cultureName, Action action)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
