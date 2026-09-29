using AIGovernanceGateway.Configuration;

namespace AIGovernanceGateway.Tests;

public class ProductionConfigurationTests
{
    [Fact]
    public void Default_database_provider_is_sqlite()
    {
        var options = new DatabaseOptions();
        Assert.Equal(DatabaseProvider.Sqlite, options.Provider);
    }

    [Fact]
    public void Environment_secret_provider_reads_values_from_process_environment()
    {
        Environment.SetEnvironmentVariable("AI_GOVERNANCE_GATEWAY_TEST_SECRET", "super-secret-value");
        try
        {
            var provider = new EnvironmentSecretProvider();
            Assert.Equal("super-secret-value", provider.GetSecret("AI_GOVERNANCE_GATEWAY_TEST_SECRET"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_GOVERNANCE_GATEWAY_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Secret_reference_resolver_resolves_environment_references()
    {
        Environment.SetEnvironmentVariable("AI_GOVERNANCE_GATEWAY_TEST_SECRET", "resolved-value");
        try
        {
            var provider = new EnvironmentSecretProvider();
            Assert.Equal("resolved-value", SecretReferenceResolver.Resolve("env:AI_GOVERNANCE_GATEWAY_TEST_SECRET", provider));
            Assert.Equal("literal", SecretReferenceResolver.Resolve("literal", provider));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_GOVERNANCE_GATEWAY_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Redis_cache_defaults_to_memory_when_not_configured()
    {
        var options = new CacheOptions();
        Assert.Equal(CacheProvider.Memory, options.Provider);
    }
}