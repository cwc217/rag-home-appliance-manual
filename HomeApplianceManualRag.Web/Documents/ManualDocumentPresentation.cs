using HomeApplianceManualRag.Web.Data;

namespace HomeApplianceManualRag.Web.Documents;

public static class ManualDocumentPresentation
{
    public static string? FormatProductSummary(ManualDocument document)
    {
        var fields = new List<string>(3);
        AddField(fields, "Brand", document.Brand);
        AddField(fields, "Category", document.ApplianceCategory);
        AddField(fields, "Model", document.ModelNumber);

        return fields.Count == 0 ? null : string.Join(" · ", fields);
    }

    private static void AddField(List<string> fields, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add($"{label}: {value.Trim()}");
    }
}
