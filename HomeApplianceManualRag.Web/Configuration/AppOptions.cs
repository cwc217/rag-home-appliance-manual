using System.ComponentModel.DataAnnotations;

namespace HomeApplianceManualRag.Web.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";
    [Required] public string Default { get; init; } = string.Empty;
}

public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";
    [Required, Url] public string Endpoint { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    [Required] public string ChatDeployment { get; init; } = string.Empty;
    [Required] public string EmbeddingDeployment { get; init; } = string.Empty;
    [Required] public string ApiVersion { get; init; } = string.Empty;
    [Range(1, 4096)] public int EmbeddingDimensions { get; init; }
}

public sealed class UploadOptions
{
    public const string SectionName = "Upload";
    [Range(1, 10 * 1024 * 1024)] public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
}

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";
    [Range(1, 100)] public int TopK { get; init; } = 5;
    [Range(1, 10000)] public int ChunkSize { get; init; } = 1200;
    [Range(0, 10000)] public int ChunkOverlap { get; init; } = 200;
}

public sealed class DoclingOptions
{
    public const string SectionName = "Docling";
    [Required, Url] public string ServiceUrl { get; init; } = "http://docling:5001";
    [Range(1, 3600)] public int TimeoutSeconds { get; init; } = 300;
    [Range(1, 4)] public int MaxConcurrentJobs { get; init; } = 1;
}

public sealed class TypeSafeOptions
{
    public const string SectionName = "TypeSafe";
    [Required, Url] public string Endpoint { get; init; } = "https://api.typesafe.ai";
    public string ApiKey { get; init; } = string.Empty;
    [Required] public string Model { get; init; } = "jev-latest";
    [Range(0, 1)] public double MinimumConfidence { get; init; } = 0.85;
    [Range(1, 100000)] public int MaxStateCharacters { get; init; } = 20000;
}

public sealed class MetadataExtractionOptions
{
    public const string SectionName = "MetadataExtraction";
    [Range(1, 100000)] public int MaxSourceCharacters { get; init; } = 12000;
    [Range(0, 1)] public double MinimumConfidence { get; init; } = 0.85;
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";
    [Range(1, 10000)] public int RequestsPerMinute { get; init; } = 30;
}

public sealed class SeedAdminOptions
{
    public const string SectionName = "SeedAdmin";
    [Required, EmailAddress] public string Email { get; init; } = string.Empty;
    [Required, MinLength(8)] public string Password { get; init; } = string.Empty;
}

public static class ApplicationRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

public static class ApplicationPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string RegisteredUser = "RegisteredUser";
}

public static class ConfigurationValidation
{
    public static void ValidateProduction(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsProduction())
            return;

        var required = new[]
        {
            "ConnectionStrings:Default", "AzureOpenAI:Endpoint", "AzureOpenAI:ApiKey",
            "AzureOpenAI:ChatDeployment", "AzureOpenAI:EmbeddingDeployment",
            "AzureOpenAI:ApiVersion", "AzureOpenAI:EmbeddingDimensions",
            "TypeSafe:ApiKey",
            "SeedAdmin:Email", "SeedAdmin:Password"
        };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"Production configuration is incomplete. Set the following environment settings: {string.Join(", ", missing)}.");
    }
}
