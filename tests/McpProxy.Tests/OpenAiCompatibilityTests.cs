using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpProxy.Data;
using McpProxy.Models;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Tests;

public sealed class OpenAiCompatibilityTests
{
    [Fact]
    public async Task Models_endpoint_shape_uses_public_alias_and_provider_owner()
    {
        var handler = new OpenAiHandler();
        var fixture = await CreateAsync(ModelProviderKind.OpenAiCompatible, "https://provider.example/v1", handler);

        var result = await fixture.Service.ListModelsAsync(fixture.Principal, default);

        Assert.Equal("list", result["object"]?.GetValue<string>());
        var data = Assert.IsType<JsonArray>(result["data"]);
        var model = Assert.IsType<JsonObject>(Assert.Single(data)!);
        Assert.Equal("public-model", model["id"]?.GetValue<string>());
        Assert.Equal("model", model["object"]?.GetValue<string>());
        Assert.Equal("provider", model["owned_by"]?.GetValue<string>());
        Assert.True(model["created"]?.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Native_openai_provider_preserves_contract_and_rewrites_only_model_identity()
    {
        var handler = new OpenAiHandler();
        var fixture = await CreateAsync(ModelProviderKind.OpenAiCompatible, "https://provider.example/v1", handler);

        var request = JsonNode.Parse(
            """
            {
              "model": "public-model",
              "messages": [
                {"role":"developer","content":"Be concise."},
                {"role":"user","content":"Hello"}
              ],
              "temperature": 0.25,
              "tools": [
                {
                  "type":"function",
                  "function":{
                    "name":"lookup",
                    "description":"Lookup data",
                    "parameters":{"type":"object","properties":{"id":{"type":"string"}}}
                  }
                }
              ]
            }
            """)!.AsObject();

        var response = await fixture.Service.CreateChatCompletionAsync(fixture.Principal, request, default);

        Assert.Equal("public-model", response["model"]?.GetValue<string>());
        Assert.Equal("chat.completion", response["object"]?.GetValue<string>());
        Assert.Equal("Hello from provider", response["choices"]?[0]?["message"]?["content"]?.GetValue<string>());

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://provider.example/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());

        var downstream = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("downstream-model", downstream["model"]?.GetValue<string>());
        Assert.Equal(0.25, downstream["temperature"]?.GetValue<double>());
        Assert.Equal("developer", downstream["messages"]?[0]?["role"]?.GetValue<string>());
        Assert.Equal("lookup", downstream["tools"]?[0]?["function"]?["name"]?.GetValue<string>());
        Assert.False(downstream["stream"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Native_openai_stream_keeps_openai_chunk_shape_and_public_model()
    {
        var handler = new OpenAiHandler(streaming: true);
        var fixture = await CreateAsync(ModelProviderKind.OpenAiCompatible, "https://provider.example", handler);

        var request = JsonNode.Parse(
            """
            {
              "model":"public-model",
              "messages":[{"role":"user","content":"Hello"}],
              "stream":true,
              "stream_options":{"include_usage":true}
            }
            """)!.AsObject();

        var chunks = new List<JsonObject>();
        await foreach (var chunk in fixture.Service.StreamChatCompletionAsync(fixture.Principal, request, default))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, x => Assert.Equal("public-model", x["model"]?.GetValue<string>()));
        Assert.Equal("chat.completion.chunk", chunks[0]["object"]?.GetValue<string>());
        Assert.Equal("Hi", chunks[0]["choices"]?[0]?["delta"]?["content"]?.GetValue<string>());
        Assert.Equal("stop", chunks[1]["choices"]?[0]?["finish_reason"]?.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(chunks[2]["choices"]));
        Assert.Equal(4, chunks[2]["usage"]?["total_tokens"]?.GetValue<int>());
    }

    [Fact]
    public async Task Generic_openai_v1_operation_routes_by_model_and_rewrites_nested_model_identity()
    {
        var handler = new GenericOpenAiHandler();
        var fixture = await CreateAsync(
            ModelProviderKind.OpenAiCompatible,
            "https://provider.example/v1",
            handler);

        var request = JsonNode.Parse(
            """
            {
              "model":"public-model",
              "input":"Hello",
              "metadata":{"test":"value"}
            }
            """)!.AsObject();

        var response = await fixture.Service.ForwardOpenAiOperationAsync(
            fixture.Principal,
            "responses",
            request,
            default);

        Assert.Equal("public-model", response["model"]?.GetValue<string>());
        Assert.Equal("public-model", response["nested"]?["model"]?.GetValue<string>());
        Assert.Equal("https://provider.example/v1/responses", handler.LastRequest?.RequestUri?.ToString());

        var downstream = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("downstream-model", downstream["model"]?.GetValue<string>());
        Assert.Equal("Hello", downstream["input"]?.GetValue<string>());
        Assert.Equal("value", downstream["metadata"]?["test"]?.GetValue<string>());
    }

    [Fact]
    public async Task Generic_openai_stream_preserves_event_names_and_rewrites_model_identity()
    {
        var handler = new GenericOpenAiHandler(streaming: true);
        var fixture = await CreateAsync(
            ModelProviderKind.OpenAiCompatible,
            "https://provider.example/v1",
            handler);

        var request = JsonNode.Parse(
            """
            {
              "model":"public-model",
              "input":"Hello",
              "stream":true
            }
            """)!.AsObject();

        var records = new List<OpenAiCompatibilityService.StreamRecord>();
        await foreach (var record in fixture.Service.StreamOpenAiOperationAsync(
            fixture.Principal,
            "responses",
            request,
            default))
        {
            records.Add(record);
        }

        Assert.Equal(2, records.Count);
        Assert.Equal("response.created", records[0].EventName);
        Assert.Equal("response.completed", records[1].EventName);

        var created = JsonNode.Parse(records[0].Data)!.AsObject();
        Assert.Equal("public-model", created["response"]?["model"]?.GetValue<string>());
        var completed = JsonNode.Parse(records[1].Data)!.AsObject();
        Assert.Equal("public-model", completed["response"]?["model"]?.GetValue<string>());
    }

    [Fact]
    public async Task Bedrock_openai_v1_operation_uses_openai_runtime_path_and_bearer_key()
    {
        Environment.SetEnvironmentVariable("OPENAI_GENERIC_BEDROCK_KEY", "bedrock-openai-key");
        try
        {
            var handler = new GenericOpenAiHandler();
            var fixture = await CreateAsync(
                ModelProviderKind.AwsBedrock,
                "https://bedrock-runtime.us-east-1.amazonaws.com",
                handler,
                credentialReference: "env:OPENAI_GENERIC_BEDROCK_KEY");

            var request = JsonNode.Parse(
                """
                {
                  "model":"public-model",
                  "input":"Hello"
                }
                """)!.AsObject();

            var response = await fixture.Service.ForwardOpenAiOperationAsync(
                fixture.Principal,
                "responses",
                request,
                default);

            Assert.Equal(
                "https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1/responses",
                handler.LastRequest?.RequestUri?.ToString());
            Assert.Equal(
                "Bearer bedrock-openai-key",
                handler.LastRequest?.Headers.Authorization?.ToString());
            Assert.Equal("public-model", response["model"]?.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_GENERIC_BEDROCK_KEY", null);
        }
    }

    [Fact]
    public async Task Ollama_is_adapted_to_openai_chat_completion_shape()
    {
        var handler = new OllamaHandler();
        var fixture = await CreateAsync(ModelProviderKind.Ollama, "https://ollama.example", handler);

        var request = JsonNode.Parse(
            """
            {
              "model":"public-model",
              "messages":[
                {"role":"system","content":"Be concise."},
                {"role":"user","content":"Hello"}
              ],
              "temperature":0.2,
              "max_tokens":100
            }
            """)!.AsObject();

        var response = await fixture.Service.CreateChatCompletionAsync(fixture.Principal, request, default);

        Assert.Equal("chat.completion", response["object"]?.GetValue<string>());
        Assert.Equal("public-model", response["model"]?.GetValue<string>());
        Assert.Equal("Ollama says hi", response["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
        Assert.Equal(9, response["usage"]?["total_tokens"]?.GetValue<int>());

        var downstream = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("downstream-model", downstream["model"]?.GetValue<string>());
        Assert.Equal("system", downstream["messages"]?[0]?["role"]?.GetValue<string>());
        Assert.Equal(0.2, downstream["options"]?["temperature"]?.GetValue<double>());
        Assert.Equal(100, downstream["options"]?["num_predict"]?.GetValue<int>());
    }

    [Fact]
    public async Task Bedrock_native_adapter_returns_openai_shape_and_translates_tools()
    {
        Environment.SetEnvironmentVariable("OPENAI_COMPAT_BEDROCK_KEY", "bedrock-key");
        try
        {
            var handler = new BedrockHandler();
            var fixture = await CreateAsync(
                ModelProviderKind.AwsBedrock,
                "https://bedrock.example",
                handler,
                credentialReference: "env:OPENAI_COMPAT_BEDROCK_KEY");

            var request = JsonNode.Parse(
                """
                {
                  "model":"public-model",
                  "messages":[
                    {"role":"developer","content":"Be concise."},
                    {"role":"user","content":"Find item 42"}
                  ],
                  "tools":[
                    {
                      "type":"function",
                      "function":{
                        "name":"lookup",
                        "description":"Lookup an item",
                        "parameters":{"type":"object","properties":{"id":{"type":"string"}}}
                      }
                    }
                  ],
                  "tool_choice":"auto",
                  "max_completion_tokens":128,
                  "stop":"END"
                }
                """)!.AsObject();

            var response = await fixture.Service.CreateChatCompletionAsync(fixture.Principal, request, default);

            Assert.Equal("chat.completion", response["object"]?.GetValue<string>());
            Assert.Equal("public-model", response["model"]?.GetValue<string>());
            Assert.Equal("Bedrock says hi", response["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
            Assert.Equal("stop", response["choices"]?[0]?["finish_reason"]?.GetValue<string>());
            Assert.Equal(8, response["usage"]?["total_tokens"]?.GetValue<int>());
            Assert.Equal("Bearer bedrock-key", handler.LastRequest?.Headers.Authorization?.ToString());

            var downstream = JsonNode.Parse(handler.LastBody!)!.AsObject();
            Assert.Equal("Be concise.", downstream["system"]?[0]?["text"]?.GetValue<string>());
            Assert.Equal("Find item 42", downstream["messages"]?[0]?["content"]?[0]?["text"]?.GetValue<string>());
            Assert.Equal("lookup", downstream["toolConfig"]?["tools"]?[0]?["toolSpec"]?["name"]?.GetValue<string>());
            Assert.Equal(128, downstream["inferenceConfig"]?["maxTokens"]?.GetValue<int>());
            Assert.Equal("END", downstream["inferenceConfig"]?["stopSequences"]?[0]?.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_COMPAT_BEDROCK_KEY", null);
        }
    }

    [Theory]
    [InlineData("Bearer eyJhbGciOiJSUzI1NiIsImtpZCI6InRlc3QifQ.payload.signature")]
    [InlineData("Bearer oauth-access-token-value")]
    public void Non_gateway_bearer_credentials_route_to_jwt_or_oidc_authentication(string authorization)
    {
        Assert.Equal(
            OpenAiBearerCredentialKind.BearerToken,
            OpenAiBearerCredentialClassifier.Classify(authorization));
    }

    [Fact]
    public void Gateway_bearer_api_key_routes_to_api_key_authentication()
    {
        var generated = ApiKeyGenerator.Generate();

        Assert.Equal(
            OpenAiBearerCredentialKind.GatewayApiKey,
            OpenAiBearerCredentialClassifier.Classify("Bearer " + generated.PlaintextKey));
    }

    [Fact]
    public void Gateway_api_keys_can_be_used_as_openai_bearer_tokens()
    {
        var generated = ApiKeyGenerator.Generate();

        Assert.True(ApiKeyGenerator.TryGetBearerApiKey(
            "Bearer " + generated.PlaintextKey,
            out var extracted));
        Assert.Equal(generated.PlaintextKey, extracted);

        Assert.False(ApiKeyGenerator.TryGetBearerApiKey("Bearer eyJhbGciOi...", out _));
        Assert.False(ApiKeyGenerator.TryGetBearerApiKey("Basic abc", out _));
    }

    [Fact]
    public async Task Routed_chat_fails_over_to_higher_priority_target_on_transient_provider_error()
    {
        var handler = new FailoverOpenAiHandler();
        var fixture = await CreateAsync(
            ModelProviderKind.OpenAiCompatible,
            "https://primary.example/v1",
            handler);

        var route = await fixture.Db.ModelRoutes.SingleAsync();
        var backup = new ModelProvider
        {
            Name = "Backup",
            Slug = "backup",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://backup.example/v1"
        };
        fixture.Db.Add(backup);
        fixture.Db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "backup-model",
            Priority = 100,
            Weight = 100
        });
        await fixture.Db.SaveChangesAsync();

        var request = JsonNode.Parse(
            """
            {
              "model":"public-model",
              "messages":[{"role":"user","content":"Hello"}]
            }
            """)!.AsObject();

        var response = await fixture.Service.CreateChatCompletionAsync(
            fixture.Principal,
            request,
            default);

        Assert.Equal("public-model", response["model"]?.GetValue<string>());
        Assert.Equal("Hello from backup", response["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
        Assert.Equal(["primary.example", "backup.example"], handler.Hosts);
    }

    [Fact]
    public async Task Routed_stream_fails_over_only_before_first_chunk()
    {
        var handler = new FailoverOpenAiHandler(streaming: true);
        var fixture = await CreateAsync(
            ModelProviderKind.OpenAiCompatible,
            "https://primary.example/v1",
            handler);

        var route = await fixture.Db.ModelRoutes.SingleAsync();
        var backup = new ModelProvider
        {
            Name = "Backup",
            Slug = "backup",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://backup.example/v1"
        };
        fixture.Db.Add(backup);
        fixture.Db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "backup-model",
            Priority = 100,
            Weight = 100
        });
        await fixture.Db.SaveChangesAsync();

        var request = JsonNode.Parse(
            """
            {
              "model":"public-model",
              "messages":[{"role":"user","content":"Hello"}],
              "stream":true
            }
            """)!.AsObject();

        var chunks = new List<JsonObject>();
        await foreach (var chunk in fixture.Service.StreamChatCompletionAsync(
                           fixture.Principal,
                           request,
                           default))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        Assert.Equal("public-model", chunks[0]["model"]?.GetValue<string>());
        Assert.Equal("backup", chunks[0]["choices"]?[0]?["delta"]?["content"]?.GetValue<string>());
        Assert.Equal(["primary.example", "backup.example"], handler.Hosts);
    }

    private static async Task<Fixture> CreateAsync(
        ModelProviderKind kind,
        string baseEndpoint,
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
            BaseEndpoint = baseEndpoint,
            CredentialReference = credentialReference
        };
        var route = new ModelRoute
        {
            ProviderId = provider.Id,
            Provider = provider,
            PublicName = "public-model",
            DownstreamModel = "downstream-model"
        };
        var role = new Role { Name = "Model Users" };
        var user = new User { Username = "openai-client", PasswordHash = "x" };

        db.AddRange(provider, route, role, user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Add(new ModelPermission
        {
            RoleId = role.Id,
            Scope = ModelPermissionScope.Route,
            ModelRouteId = route.Id
        });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ProxyClaimTypes.PrincipalKind, "user"),
            new Claim(ProxyClaimTypes.PrincipalId, user.Id.ToString())
        ], "Test"));

        var routingState = new ModelRoutingState();
        var routeSelector = new ModelRouteSelector(db, routingState);
        var service = new OpenAiCompatibilityService(
            db,
            new PermissionService(db),
            new ClientFactory(new HttpClient(handler)),
            routeSelector,
            routingState);

        return new Fixture(service, principal, db, routingState);
    }

    private sealed record Fixture(
        OpenAiCompatibilityService Service,
        ClaimsPrincipal Principal,
        ProxyDbContext Db,
        ModelRoutingState RoutingState);

    private sealed class FailoverOpenAiHandler(bool streaming = false) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);

            if (host == "primary.example")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    ReasonPhrase = "Primary unavailable",
                    Content = new StringContent("""{"error":{"message":"unavailable"}}""", Encoding.UTF8, "application/json")
                };
            }

            if (streaming)
            {
                var sse =
                    "data: {\"id\":\"chatcmpl-backup\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"backup-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"backup\"},\"finish_reason\":null}]}\n\n" +
                    "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }

            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id":"chatcmpl-backup",
                      "object":"chat.completion",
                      "created":1,
                      "model":"backup-model",
                      "choices":[
                        {"index":0,"message":{"role":"assistant","content":"Hello from backup"},"finish_reason":"stop"}
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class OpenAiHandler(bool streaming = false) : HttpMessageHandler
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

            if (streaming)
            {
                const string body =
                    "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"downstream-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"downstream-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"downstream-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":2,\"total_tokens\":4}}\n\n" +
                    "data: [DONE]\n\n";
                return Response("text/event-stream", body);
            }

            return Response(
                "application/json",
                """
                {
                  "id":"chatcmpl-provider",
                  "object":"chat.completion",
                  "created":1,
                  "model":"downstream-model",
                  "choices":[
                    {
                      "index":0,
                      "message":{"role":"assistant","content":"Hello from provider"},
                      "finish_reason":"stop"
                    }
                  ],
                  "usage":{"prompt_tokens":3,"completion_tokens":3,"total_tokens":6}
                }
                """);
        }
    }

    private sealed class GenericOpenAiHandler(bool streaming = false) : HttpMessageHandler
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

            if (streaming)
            {
                const string body =
                    "event: response.created\n" +
                    "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"model\":\"downstream-model\"}}\n\n" +
                    "event: response.completed\n" +
                    "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"model\":\"downstream-model\"}}\n\n";
                return Response("text/event-stream", body);
            }

            return Response(
                "application/json",
                """
                {
                  "id":"resp_1",
                  "object":"response",
                  "model":"downstream-model",
                  "nested":{"model":"downstream-model"},
                  "output":[]
                }
                """);
        }
    }

    private sealed class OllamaHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return Response(
                "application/json",
                """
                {
                  "message":{"role":"assistant","content":"Ollama says hi"},
                  "done":true,
                  "done_reason":"stop",
                  "prompt_eval_count":5,
                  "eval_count":4
                }
                """);
        }
    }

    private sealed class BedrockHandler : HttpMessageHandler
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

            return Response(
                "application/json",
                """
                {
                  "output":{
                    "message":{
                      "role":"assistant",
                      "content":[{"text":"Bedrock says hi"}]
                    }
                  },
                  "stopReason":"end_turn",
                  "usage":{"inputTokens":5,"outputTokens":3,"totalTokens":8}
                }
                """);
        }
    }

    private static HttpResponseMessage Response(string mediaType, string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
}
