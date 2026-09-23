using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Documents;

namespace HomeApplianceManualRag.Tests;

public sealed class ManualDocumentPresentationTests
{
    [Fact]
    public void FormatProductSummary_includes_brand_category_and_model_in_order()
    {
        var document = new ManualDocument
        {
            Brand = "  Acme ",
            ApplianceCategory = "Washing Machine",
            ModelNumber = "WM-100"
        };

        var summary = ManualDocumentPresentation.FormatProductSummary(document);

        Assert.Equal("Brand: Acme · Category: Washing Machine · Model: WM-100", summary);
    }

    [Fact]
    public void FormatProductSummary_omits_missing_fields_and_returns_null_when_empty()
    {
        var document = new ManualDocument
        {
            Brand = "Acme",
            ApplianceCategory = " ",
            ModelNumber = null
        };

        Assert.Equal("Brand: Acme", ManualDocumentPresentation.FormatProductSummary(document));
        Assert.Null(ManualDocumentPresentation.FormatProductSummary(new ManualDocument()));
    }
}
