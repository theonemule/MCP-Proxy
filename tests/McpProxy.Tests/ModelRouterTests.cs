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

public sealed class ModelStreamingTests
{
    [Fact]
    public async Task OpenAi_stream_is_normalized_to_start_delta_usage_done_events()
    {
        var handler = new ProviderStreamingHandler();
        var (router, principal) = await CreateRouterAsync(
            ModelProviderKind.OpenAiCompatible,
            "https://models.example",
            "openai-model",
            handler);

        var events = await CollectAsync(router.StreamChatAsync(
            principal,
            new ModelChatRequest("public-model", "hello", null, null, Stream: true),
            default));

        Assert.Equal(["start", "delta", "delta", "usage", "done"], events.Select(x => x.Event).ToArray());
        Assert.Equal("Hello", events[1].Text);
        Assert.Equal(" world", events[2].Text);
        Assert.Equal("stop", events[^1].FinishReason);
        Assert.Equal(3, events[^1].Usage?["total_tokens"]?.GetValue<int>());

        var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Ollama_stream_is_normalized_to_start_delta_usage_done_events()
    {
        var handler = new ProviderStreamingHandler();
        var (router, principal) = await CreateRouterAsync(
            ModelProviderKind.Ollama,
            "https://ollama.example",
            "qwen3:14b",
            handler);

        var events = await CollectAsync(router.StreamChatAsync(
            principal,
            new ModelChatRequest("public-model", "hello", null, null, Stream: true),
            default));

        Assert.Equal(["start", "delta", "delta", "usage", "done"], events.Select(x => x.Event).ToArray());
        Assert.Equal("Hi", events[1].Text);
        Assert.Equal(" there", events[2].Text);
        Assert.Equal("stop", events[^1].FinishReason);
        Assert.Equal(4, events[^1].Usage?["promptTokens"]?.GetValue<int>());
        Assert.Equal(2, events[^1].Usage?["completionTokens"]?.GetValue<int>());
    }

    [Fact]
    public async Task Bedrock_converse_stream_eventstream_is_normalized()
    {
        Environment.SetEnvironmentVariable("BEDROCK_STREAM_TEST_KEY", "bedrock-bearer");
        try
        {
            var handler = new ProviderStreamingHandler();
            var (router, principal) = await CreateRouterAsync(
                ModelProviderKind.AwsBedrock,
                "https://bedrock.example",
                "amazon.nova-test",
                handler,
                credentialReference: "env:BEDROCK_STREAM_TEST_KEY");

            var events = await CollectAsync(router.StreamChatAsync(
                principal,
                new ModelChatRequest("public-model", "hello", null, null, Stream: true),
                default));

            Assert.Equal(["start", "delta", "delta", "usage", "done"], events.Select(x => x.Event).ToArray());
            Assert.Equal("Bed", events[1].Text);
            Assert.Equal("rock", events[2].Text);
            Assert.Equal("end_turn", events[^1].FinishReason);
            Assert.Equal(7, events[^1].Usage?["totalTokens"]?.GetValue<int>());
            Assert.Equal("Bearer bedrock-bearer", handler.LastRequest?.Headers.Authorization?.ToString());
            Assert.EndsWith("/model/amazon.nova-test/converse-stream", handler.LastRequest?.RequestUri?.AbsolutePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BEDROCK_STREAM_TEST_KEY", null);
        }
    }

    private static async Task<(ModelRouterService Router, ClaimsPrincipal Principal)> CreateRouterAsync(
        ModelProviderKind kind,
        string endpoint,
        string downstreamModel,
        HttpMessageHandler handler,
        string? credentialReference = null)
    {
        var options = new DbContextOptionsBuilder<ProxyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ProxyDbContext(options);

        var provider = new ModelProvider
        {
            Name = "Provider",
            Slug = "provider",
            Kind = kind,
            BaseEndpoint = endpoint,
            CredentialReference = credentialReference,
            CredentialHeader = "Authorization",
            CredentialPrefix = "Bearer "
        };
        var route = new ModelRoute
        {
            ProviderId = provider.Id,
            Provider = provider,
            PublicName = "public-model",
            DownstreamModel = downstreamModel
        };
        var role = new Role { Name = "AI Users" };
        var user = new User { Username = "stream-user", PasswordHash = "x" };
        db.AddRange(provider, route, role, user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Add(new ModelPermission
        {
            RoleId = role.Id,
            Scope = ModelPermissionScope.Route,
            ModelRouteId = route.Id
        });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ProxyClaimTypes.PrincipalKind, "user"),
            new Claim(ProxyClaimTypes.PrincipalId, user.Id.ToString())
        ], "Test"));

        return (
            new ModelRouterService(
                db,
                new PermissionService(db),
                new StreamingHttpClientFactory(new HttpClient(handler))),
            principal);
    }

    private static async Task<List<ModelChatStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelChatStreamEvent> source)
    {
        var result = new List<ModelChatStreamEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }
        return result;
    }

    private sealed class StreamingHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ProviderStreamingHandler : HttpMessageHandler
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

            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.Ordinal))
            {
                const string sse =
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"content\":\" world\"},\"finish_reason\":\"stop\"}]}\n\n" +
                    "data: {\"choices\":[],\"usage\":{\"total_tokens\":3}}\n\n" +
                    "data: [DONE]\n\n";
                return StreamingResponse("text/event-stream", Encoding.UTF8.GetBytes(sse));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                const string ndjson =
                    "{\"message\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"done\":false}\n" +
                    "{\"message\":{\"role\":\"assistant\",\"content\":\" there\"},\"done\":false}\n" +
                    "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":4,\"eval_count\":2}\n";
                return StreamingResponse("application/x-ndjson", Encoding.UTF8.GetBytes(ndjson));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/converse-stream", StringComparison.Ordinal))
            {
                var bytes = BuildAwsEventStream(
                    ("contentBlockDelta", """{"delta":{"text":"Bed"},"contentBlockIndex":0}"""),
                    ("contentBlockDelta", """{"delta":{"text":"rock"},"contentBlockIndex":0}"""),
                    ("messageStop", """{"stopReason":"end_turn"}"""),
                    ("metadata", """{"usage":{"inputTokens":5,"outputTokens":2,"totalTokens":7},"metrics":{"latencyMs":1}}"""));
                return StreamingResponse("application/vnd.amazon.eventstream", bytes);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage StreamingResponse(string mediaType, byte[] body) =>
            new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) }
                }
            };

        private static byte[] BuildAwsEventStream(params (string EventType, string Payload)[] events)
        {
            using var output = new MemoryStream();
            var crcBytes = new byte[4];
            foreach (var item in events)
            {
                var headers = BuildStringHeader(":event-type", item.EventType);
                var payload = Encoding.UTF8.GetBytes(item.Payload);
                var totalLength = 12 + headers.Length + payload.Length + 4;

                var prelude = new byte[12];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prelude.AsSpan(0, 4), (uint)totalLength);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prelude.AsSpan(4, 4), (uint)headers.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prelude.AsSpan(8, 4), Crc32(prelude.AsSpan(0, 8)));

                var frameStart = output.Position;
                output.Write(prelude);
                output.Write(headers);
                output.Write(payload);

                var frameWithoutCrc = output.GetBuffer().AsSpan(
                    checked((int)frameStart),
                    prelude.Length + headers.Length + payload.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc32(frameWithoutCrc));
                output.Write(crcBytes);
            }

            return output.ToArray();
        }

        private static byte[] BuildStringHeader(string name, string value)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            using var output = new MemoryStream();
            output.WriteByte((byte)nameBytes.Length);
            output.Write(nameBytes);
            output.WriteByte(7);
            Span<byte> length = stackalloc byte[2];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)valueBytes.Length));
            output.Write(length);
            output.Write(valueBytes);
            return output.ToArray();
        }

        private static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
                }
            }
            return ~crc;
        }
    }
}
