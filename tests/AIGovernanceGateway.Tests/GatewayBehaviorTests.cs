using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AIGovernanceGateway.Configuration;
using AIGovernanceGateway.Data;
using AIGovernanceGateway.Registry;
using AIGovernanceGateway.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AIGovernanceGateway.Tests;

public sealed class JwtTokenServiceTests
{
    [Fact]
    public void IssueUserToken_contains_reserved_identity_claims_and_custom_claims()
    {
        var key = new SymmetricSecurityKey(new byte[32]);
        var service = new JwtTokenService(key, Options.Create(new AuthOptions { TokenLifetimeMinutes = 15 }));

        var token = new JwtSecurityTokenHandler().ReadJwtToken(service.IssueUserToken(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "alice",
            [new Claim("roles", "Administrator"), new Claim(JwtRegisteredClaimNames.Sub, "attacker")]));

        Assert.Equal("alice", token.Subject);
        Assert.Contains(token.Claims, claim => claim.Type == GatewayClaimTypes.PrincipalKind && claim.Value == "user");
        Assert.Contains(token.Claims, claim => claim.Type == GatewayClaimTypes.PrincipalId && claim.Value == "11111111-1111-1111-1111-111111111111");
        Assert.Contains(token.Claims, claim => claim.Type == "roles" && claim.Value == "Administrator");
        Assert.DoesNotContain(token.Claims, claim => claim.Value == "attacker");
        Assert.InRange(token.ValidTo, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
    }
}

public sealed class DatabaseOptionsTests
{
    [Fact]
    public void GetConnectionString_selects_the_configured_provider_specific_value()
    {
        var options = new DatabaseOptions
        {
            ConnectionString = "fallback",
            SqliteConnectionString = "sqlite",
            SqlServerConnectionString = "sqlserver",
            PostgresConnectionString = "postgres"
        };

        options.Provider = DatabaseProvider.Sqlite;
        Assert.Equal("sqlite", options.GetConnectionString());
        options.Provider = DatabaseProvider.SqlServer;
        Assert.Equal("sqlserver", options.GetConnectionString());
        options.Provider = DatabaseProvider.Postgres;
        Assert.Equal("postgres", options.GetConnectionString());
    }

    [Fact]
    public void GetConnectionString_falls_back_when_provider_specific_value_is_empty()
    {
        var options = new DatabaseOptions
        {
            Provider = DatabaseProvider.Postgres,
            ConnectionString = "fallback",
            PostgresConnectionString = ""
        };

        Assert.Equal("fallback", options.GetConnectionString());
    }
}

public sealed class CredentialResolverTests
{
    [Fact]
    public void ResolveHeaders_reads_environment_credentials_and_applies_prefix()
    {
        Environment.SetEnvironmentVariable("AI_GOVERNANCE_GATEWAY_TEST_DOWNSTREAM", "secret");
        try
        {
            var server = new McpServer
            {
                Name = "test",
                NamespacePrefix = "test",
                Endpoint = "https://example.test/mcp",
                CredentialReference = "env:AI_GOVERNANCE_GATEWAY_TEST_DOWNSTREAM",
                CredentialHeader = "X-Api-Key",
                CredentialPrefix = "Token "
            };

            var headers = CredentialResolver.ResolveHeaders(server);

            Assert.Equal("Token secret", headers["X-Api-Key"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_GOVERNANCE_GATEWAY_TEST_DOWNSTREAM", null);
        }
    }

    [Fact]
    public void ResolveHeaders_rejects_reserved_headers()
    {
        var server = new McpServer
        {
            Name = "test",
            NamespacePrefix = "test",
            Endpoint = "https://example.test/mcp",
            CredentialReference = "env:AI_GOVERNANCE_GATEWAY_TEST_DOWNSTREAM",
            CredentialHeader = "Host"
        };

        Assert.Throws<InvalidOperationException>(() => CredentialResolver.ResolveHeaders(server));
    }
}

public sealed class CatalogInvalidationBusTests
{
    [Fact]
    public async Task InMemory_bus_notifies_all_subscribers()
    {
        var bus = new InMemoryCatalogInvalidationBus();
        var received = new List<Guid>();
        bus.Invalidated += serverId =>
        {
            received.Add(serverId);
            return Task.CompletedTask;
        };

        var expected = Guid.NewGuid();
        await bus.PublishAsync(expected, default);

        Assert.Equal([expected], received);
    }

    [Fact]
    public async Task InMemory_bus_honors_cancellation_before_dispatch()
    {
        var bus = new InMemoryCatalogInvalidationBus();
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => bus.PublishAsync(Guid.NewGuid(), cancellation.Token));
    }
}