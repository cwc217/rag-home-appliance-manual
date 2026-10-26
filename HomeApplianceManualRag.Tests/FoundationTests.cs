using HomeApplianceManualRag.Web;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using HomeApplianceManualRag.Web.Resources;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Tests;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void Legacy_chunk_columns_are_absent_from_the_model()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ApplicationDbContext(options);
        var chunk = db.Model.FindEntityType(typeof(ManualChunk))!;

        Assert.Null(chunk.FindProperty("LegacyText"));
        Assert.Null(chunk.FindProperty("LegacyPageNumber"));
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(32768)]
    public void Retrieval_options_accept_parent_expansion_token_budget(int tokenBudget)
    {
        var options = new RetrievalOptions { MaxExpandedParentTokens = tokenBudget };
        var validationResults = new List<ValidationResult>();

        Assert.True(
            Validator.TryValidateObject(options, new ValidationContext(options), validationResults, validateAllProperties: true),
            string.Join("; ", validationResults.Select(result => result.ErrorMessage)));
    }

    [Fact]
    public void Date_time_display_uses_fixed_utc_plus_eight_offset()
    {
        var value = new DateTime(2026, 9, 22, 9, 38, 59, DateTimeKind.Utc);

        Assert.Equal("2026-09-22 17:38 UTC+8", DateTimeDisplay.FormatUtcPlusEight(value));
        Assert.Equal("2026-09-22 17:38:59 UTC+8", DateTimeDisplay.FormatUtcPlusEight(value, includeSeconds: true));
    }

    [Fact]
    public void Date_time_display_treats_unspecified_values_as_utc()
    {
        var value = new DateTime(2026, 9, 22, 23, 30, 0, DateTimeKind.Unspecified);

        Assert.Equal("2026-09-23 07:30 UTC+8", DateTimeDisplay.FormatUtcPlusEight(value));
    }

    [Fact]
    public void Identity_roles_and_policies_are_fixed()
    {
        Assert.Equal("Admin", ApplicationRoles.Admin);
        Assert.Equal("User", ApplicationRoles.User);
        Assert.Equal("AdminOnly", ApplicationPolicies.AdminOnly);
        Assert.Equal("RegisteredUser", ApplicationPolicies.RegisteredUser);
    }

    [Fact]
    public void Current_user_reads_identity_claims()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim(ClaimTypes.Email, "user@example.invalid")
            ], "test"))
        };
        var currentUser = new CurrentUser(new HttpContextAccessor { HttpContext = context });

        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal("user-1", currentUser.UserId);
        Assert.Equal("user@example.invalid", currentUser.Email);
    }

    [Fact]
    public void Current_user_keeps_circuit_identity_when_http_context_is_unavailable()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                new Claim(ClaimTypes.Email, "admin@example.invalid"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            ], "test"))
        };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var currentUser = new CurrentUser(accessor);
        accessor.HttpContext = null;

        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal("admin-1", currentUser.UserId);
        Assert.True(currentUser.IsInRole(ApplicationRoles.Admin));
    }

    [Fact]
    public void Identity_validation_messages_follow_requested_culture()
    {
        Assert.Contains("required", IdentityMessages.Required("Email", "en-US"));
        Assert.Contains("電子郵件", IdentityMessages.Required("Email", "zh-Hant"));
        Assert.Contains("already registered", IdentityMessages.EmailTaken("en-US"));
        Assert.Contains("已註冊", IdentityMessages.EmailTaken("zh-Hant"));
    }

    [Fact]
    public void Seed_admin_password_accepts_eight_characters()
    {
        var options = new SeedAdminOptions
        {
            Email = "admin@example.invalid",
            Password = "12345678"
        };
        var results = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true));
    }

    [Fact]
    public void Production_with_missing_settings_reports_all_missing_keys()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new TestEnvironment { EnvironmentName = Environments.Production };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConfigurationValidation.ValidateProduction(configuration, environment));

        Assert.Contains("ConnectionStrings:Default", exception.Message);
        Assert.Contains("AzureOpenAI:ApiKey", exception.Message);
        Assert.Contains("SeedAdmin:Password", exception.Message);
    }

    [Fact]
    public void Development_does_not_require_production_secrets()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new TestEnvironment { EnvironmentName = Environments.Development };

        ConfigurationValidation.ValidateProduction(configuration, environment);
    }

    [Fact]
    public void Development_settings_bind_from_root_configuration_sections()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();

        var azure = configuration.GetSection(AzureOpenAiOptions.SectionName).Get<AzureOpenAiOptions>();
        var connectionString = configuration.GetConnectionString("Default");

        Assert.NotNull(azure);
        Assert.Equal("Host=localhost;Port=8580;Database=manuals;Username=postgres;Password=postgres", connectionString);
        Assert.Equal("development-chat", azure.ChatDeployment);
        Assert.Equal(1536, azure.EmbeddingDimensions);
        Assert.True(configuration.GetValue<bool>("HttpsRedirection:Enabled"));
    }

    [Fact]
    public void Development_embedding_dimension_matches_database_schema()
    {
        Assert.Equal(
            ApplicationDbContext.EmbeddingDimensions,
            new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.Development.json", optional: false)
                .Build()
                .GetValue<int>("AzureOpenAI:EmbeddingDimensions"));
    }

    [Fact]
    public void Production_with_present_settings_does_not_throw()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=manuals",
                ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
                ["AzureOpenAI:ApiKey"] = "test-key",
                ["AzureOpenAI:ChatDeployment"] = "chat",
                ["AzureOpenAI:EmbeddingDeployment"] = "embedding",
                ["AzureOpenAI:ApiVersion"] = "2024-10-21",
                ["AzureOpenAI:EmbeddingDimensions"] = "1536",
                ["TypeSafe:ApiKey"] = "test-typesafe-key",
                ["SeedAdmin:Email"] = "admin@example.invalid",
                ["SeedAdmin:Password"] = "strong-test-password"
            })
            .Build();
        var environment = new TestEnvironment { EnvironmentName = Environments.Production };

        ConfigurationValidation.ValidateProduction(configuration, environment);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

public sealed class FakeProviderTests
{
    [Fact]
    public async Task Fake_chat_is_deterministic_and_streams_tokens()
    {
        var provider = new DeterministicFakeChatProvider();
        var result = new List<string>();
        await foreach (var token in provider.StreamAsync("ignored"))
            result.Add(token);

        Assert.Equal(["Deterministic", " fake", " response."], result);
    }

    [Fact]
    public async Task Fake_embedding_is_deterministic_and_has_configured_dimensions()
    {
        var provider = new DeterministicFakeEmbeddingProvider(4);
        var first = await provider.EmbedAsync("manual");
        var second = await provider.EmbedAsync("manual");

        Assert.Equal(4, first.Length);
        Assert.Equal(first.ToArray(), second.ToArray());
    }

}
