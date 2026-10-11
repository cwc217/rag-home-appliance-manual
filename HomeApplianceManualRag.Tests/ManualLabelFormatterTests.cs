using HomeApplianceManualRag.Web.Chat;
using HomeApplianceManualRag.Web.Components.Pages;

namespace HomeApplianceManualRag.Tests;

public sealed class ManualLabelFormatterTests
{
    [Fact]
    public void Format_keeps_long_model_identifiers_intact()
    {
        var modelNumber = new string('X', 120);
        var manual = new ApplianceManualInfo(
            Guid.NewGuid(),
            "manual.pdf",
            null,
            "Air Conditioner",
            "Acme",
            modelNumber);

        Assert.Equal($"Acme · Air Conditioner · {modelNumber}", ManualLabelFormatter.Format(manual));
    }

    [Fact]
    public void Format_falls_back_to_product_then_manual_name()
    {
        var withProductName = new ApplianceManualInfo(
            Guid.NewGuid(),
            "manual.pdf",
            "Dishwasher",
            null,
            null,
            null);
        var withoutProductName = withProductName with { ProductName = null };

        Assert.Equal("Dishwasher", ManualLabelFormatter.Format(withProductName));
        Assert.Equal("manual.pdf", ManualLabelFormatter.Format(withoutProductName));
    }
}
