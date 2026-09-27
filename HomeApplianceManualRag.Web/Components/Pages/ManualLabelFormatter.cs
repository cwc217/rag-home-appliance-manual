using HomeApplianceManualRag.Web.Chat;

namespace HomeApplianceManualRag.Web.Components.Pages;

public static class ManualLabelFormatter
{
    public static string Format(ApplianceManualInfo manual)
    {
        var productDetails = new[]
            {
                manual.Brand,
                manual.ApplianceCategory,
                manual.ModelNumber
            }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var label = string.Join(" · ", productDetails);

        return string.IsNullOrWhiteSpace(label)
            ? string.IsNullOrWhiteSpace(manual.ProductName) ? manual.ManualName : manual.ProductName
            : label;
    }
}
