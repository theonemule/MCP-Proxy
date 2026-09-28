using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using McpProxy.Data;
using McpProxy.Models;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Tests;

public sealed class ModelPermissionTests
{
    [Fact]
    public async Task Provider_permission_grants_native_and_all_routes_on_provider()
    {
        await using var db = CreateDb();
        var provider = new ModelProvider
        {
            Name = "Local",
            Slug = "local",
            Kind = ModelProviderKind.Ollama,
            BaseEndpoint = "http://localhost:11434"
        };
        var routeA = new ModelRoute { ProviderId = provider.Id, Provider = provider, PublicName = "coder", DownstreamModel = "qwen3" };
        var routeB = new ModelRoute { ProviderId = provider.Id, Provider = provider, PublicName = "reasoner", DownstreamModel = "gpt-oss" };
        var role = new Role { Name = "AI Users" };
        var user = new User { Username = "alice", PasswordHash = "x" };
        db.AddRange(provider, routeA, routeB, role, user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Add(new ModelPermission
        {
            RoleId = role.Id,
            Scope = ModelPermissionScope.Provider,
            ProviderId = provider.Id
        });
        await db.SaveChangesAsync();

        var service = new PermissionService(db);
        var principal = PrincipalFor(user.Id);

        Assert.True(await service.CanAccessModelProviderAsync(principal, provider.Id, default));
        Assert.True(await service.CanAccessModelRouteAsync(principal, routeA.Id, default));
        Assert.True(await service.CanAccessModelRouteAsync(principal, routeB.Id, default));
        var accessible = await service.GetAccessibleModelRouteIdsAsync(principal, default);
        Assert.Contains(routeA.Id, accessible);
        Assert.Contains(routeB.Id, accessible);
    }

    [Fact]
    public async Task Route_permission_does_not_grant_native_provider_access_or_other_models()
    {
        await using var db = CreateDb();
        var provider = new ModelProvider
        {
            Name = "Hosted",
            Slug = "hosted",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://models.example"
        };
        var allowed = new ModelRoute { ProviderId = provider.Id, Provider = provider, PublicName = "allowed", DownstreamModel = "a" };
        var denied = new ModelRoute { ProviderId = provider.Id, Provider = provider, PublicName = "denied", DownstreamModel = "b" };
        var role = new Role { Name = "Limited" };
        var user = new User { Username = "bob", PasswordHash = "x" };
        db.AddRange(provider, allowed, denied, role, user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Add(new ModelPermission
        {
            RoleId = role.Id,
            Scope = ModelPermissionScope.Route,
            ModelRouteId = allowed.Id
        });
        await db.SaveChangesAsync();

        var service = new PermissionService(db);
        var principal = PrincipalFor(user.Id);

        Assert.False(await service.CanAccessModelProviderAsync(principal, provider.Id, default));
        Assert.True(await service.CanAccessModelRouteAsync(principal, allowed.Id, default));
        Assert.False(await service.CanAccessModelRouteAsync(principal, denied.Id, default));
    }

    private static ProxyDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProxyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProxyDbContext(options);
    }

    private static ClaimsPrincipal PrincipalFor(Guid id) =>
        new(new ClaimsIdentity([
            new Claim(ProxyClaimTypes.PrincipalKind, "user"),
            new Claim(ProxyClaimTypes.PrincipalId, id.ToString())
        ], "Test"));
}

public sealed class ModelRouterServiceTests
{
    [Fact]
    public async Task OpenAi_compatible_route_translates_unified_request_and_applies_downstream_credential()
    {
        Environment.SetEnvironmentVariable("MODEL_ROUTER_TEST_KEY", "secret-value");
        try
        {
            await using var db = CreateDb();
            var provider = new ModelProvider
            {
                Name = "Foundry",
                Slug = "foundry",
                Kind = ModelProviderKind.OpenAiCompatible,
                BaseEndpoint = "https://models.example/openai",
                CredentialReference = "env:MODEL_ROUTER_TEST_KEY",
                CredentialHeader = "api-key",
                CredentialPrefix = ""
            };
            var route = new ModelRoute
            {
                ProviderId = provider.Id,
                Provider = provider,
                PublicName = "enterprise-small",
                DownstreamModel = "deployment-42"
            };
            var role = new Role { Name = "AI Users" };
            var user = new User { Username = "carol", PasswordHash = "x" };
            db.AddRange(provider, route, role, user);
            db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            db.Add(new ModelPermission
            {
                RoleId = role.Id,
                Scope = ModelPermissionScope.Route,
                ModelRouteId = route.Id
            });
            await db.SaveChangesAsync();

            var handler = new CapturingHandler();
            var router = new ModelRouterService(
                db,
                new PermissionService(db),
                new TestHttpClientFactory(new HttpClient(handler)));

            var temperature = JsonDocument.Parse("0.2").RootElement.Clone();
            var response = await router.ChatAsync(
                PrincipalFor(user.Id),
                new ModelChatRequest(
                    "enterprise-small",
                    "hello",
                    "be concise",
                    new Dictionary<string, JsonElement> { ["temperature"] = temperature }),
                default);

            Assert.Equal("done", response.Text);
            Assert.Equal("enterprise-small", response.Model);
            Assert.Equal("deployment-42", response.DownstreamModel);
            Assert.NotNull(handler.LastRequest);
            Assert.Equal("https://models.example/openai/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
            Assert.Equal("secret-value", handler.LastRequest.Headers.GetValues("api-key").Single());

            var json = JsonDocument.Parse(handler.LastBody!);
            Assert.Equal("deployment-42", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(0.2, json.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal("system", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("be concise", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("hello", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL_ROUTER_TEST_KEY", null);
        }
    }

    [Fact]
    public void Bedrock_standard_bearer_environment_variable_is_supported()
    {
        Environment.SetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK", "bedrock-test-token");
        try
        {
            var provider = new ModelProvider
            {
                Name = "Bedrock",
                Slug = "bedrock",
                Kind = ModelProviderKind.AwsBedrock,
                BaseEndpoint = ""
            };

            var headers = ModelCredentialResolver.ResolveBedrockBearerHeaders(provider);

            Assert.NotNull(headers);
            Assert.Equal("Bearer bedrock-test-token", headers!["Authorization"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK", null);
        }
    }

    [Fact]
    public void Aws_signer_adds_required_sigv4_headers()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://bedrock-runtime.us-east-1.amazonaws.com/model/test-model/converse")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"))
        };
        var credentials = new AwsCredentialSet("AKIDEXAMPLE", "secret", "token", "us-east-1");

        AwsSigV4Signer.Sign(
            request,
            Encoding.UTF8.GetBytes("{}"),
            credentials,
            "bedrock",
            new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("AWS4-HMAC-SHA256", request.Headers.Authorization?.Scheme);
        Assert.Contains("Credential=AKIDEXAMPLE/20260928/us-east-1/bedrock/aws4_request", request.Headers.Authorization?.Parameter);
        Assert.Equal("20260928T120000Z", request.Headers.GetValues("x-amz-date").Single());
        Assert.Equal("token", request.Headers.GetValues("x-amz-security-token").Single());
        Assert.NotEmpty(request.Headers.GetValues("x-amz-content-sha256").Single());
    }

    private static ProxyDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProxyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProxyDbContext(options);
    }

    private static ClaimsPrincipal PrincipalFor(Guid id) =>
        new(new ClaimsIdentity([
            new Claim(ProxyClaimTypes.PrincipalKind, "user"),
            new Claim(ProxyClaimTypes.PrincipalId, id.ToString())
        ], "Test"));

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}],"usage":{"total_tokens":5}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}

public sealed class ModelSchemaUpgradeTests
{
    [Fact]
    public async Task Upgrade_adds_model_router_tables_to_an_existing_sqlite_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp-proxy-model-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ProxyDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;

            await using var db = new ProxyDbContext(options);
            await db.Database.OpenConnectionAsync();

            // Simulate a pre-model-router database. The model tables do not exist yet.
            await db.Database.ExecuteSqlRawAsync(
                """CREATE TABLE "Roles" ("Id" TEXT NOT NULL PRIMARY KEY);""");

            await ModelSchemaUpgrade.EnsureAsync(db);

            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN ('ModelProviders', 'ModelRoutes', 'ModelPermissions');
                """;

            var count = Convert.ToInt64(await command.ExecuteScalarAsync());
            Assert.Equal(3, count);

            db.ModelProviders.Add(new ModelProvider
            {
                Name = "Local",
                Slug = "local",
                Kind = ModelProviderKind.Ollama,
                BaseEndpoint = "http://localhost:11434"
            });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.ModelProviders.CountAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
