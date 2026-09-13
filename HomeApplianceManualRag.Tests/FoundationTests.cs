using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace HomeApplianceManualRag.Tests;

public sealed class ConfigurationValidationTests
{
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
