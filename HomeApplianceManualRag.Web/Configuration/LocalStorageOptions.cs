namespace HomeApplianceManualRag.Web.Configuration;

public sealed class LocalStorageOptions
{
    public const string SectionName = "Storage";
    public string RootPath { get; init; } = "App_Data/uploads";
}
