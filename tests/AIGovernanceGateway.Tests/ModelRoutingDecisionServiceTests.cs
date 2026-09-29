using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIGovernanceGateway.Data;
using AIGovernanceGateway.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIGovernanceGateway.Tests;

public sealed class ModelRoutingDecisionServiceTests
{
    [Fact]
    public async Task Routing_model_summarizes_intent_and_selects_best_inference_task()
    {
        await using var db = CreateDb();

        var routingProvider = new ModelProvider
        {
            Name = "Routing Provider",
            Slug = "routing",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://routing.example/v1"
        };
        var routingModel = new ModelRoute
        {
            ProviderId = routingProvider.Id,
            Provider = routingProvider,
            PublicName = "routing-controller",
            DownstreamModel = "router-small",
            IsRoutingModel = true
        };

        db.AddRange(routingProvider, routingModel);
        await db.SaveChangesAsync();

        var handler = new RoutingHandler(
            """{"intent_summary":"Fix a concurrency bug in C# code","inference_task":"coding","reasoning_level":"high","selected_candidate":1,"rationale":"The second model is specialized for coding and has sufficient reasoning capability."}""");
        var modelRouter = new ModelRouterService(
            db,
            null!,
            new ClientFactory(new HttpClient(handler)));
        var service = new ModelRoutingDecisionService(
            db,
            modelRouter,
            NullLogger<ModelRoutingDecisionService>.Instance);

        var generalProvider = Provider("general", "https://general.example/v1");
        var codingProvider = Provider("coder", "https://coder.example/v1");
        var logical = Route(generalProvider, "router", "general-model");
        var general = Route(generalProvider, "router", "general-model");
        general.ReasoningLevel = ModelReasoningLevel.Medium;
        general.Specialties = "summarization,writing";
        general.CostTier = 1;
        general.LatencyTier = 1;

        var coder = Route(codingProvider, "router", "coder-model");
        coder.ReasoningLevel = ModelReasoningLevel.High;
        coder.Specialties = "coding,debugging,software-engineering";
        coder.CostTier = 2;
        coder.LatencyTier = 2;

        var request = JsonNode.Parse(
            """
            {
              "messages":[
                {"role":"user","content":"Find and fix the race condition in this C# worker and explain the locking issue."}
              ]
            }
            """)!.AsObject();

        var ranked = await service.RankAsync(
            logical,
            ModelRoutingOperation.Chat,
            request,
            [general, coder],
            default);

        Assert.Equal("coder-model", ranked[0].DownstreamModel);
        Assert.Equal("general-model", ranked[1].DownstreamModel);
        Assert.Equal(1, handler.CallCount);

        var downstream = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("router-small", downstream["model"]?.GetValue<string>());
        var prompt = downstream["messages"]?[1]?["content"]?.GetValue<string>() ?? "";
        Assert.Contains("inference_tasks=coding,debugging,software-engineering", prompt);
        Assert.Contains("Find and fix the race condition", prompt);
        Assert.Contains("AVAILABLE INFERENCE CANDIDATES", prompt);
    }

    [Fact]
    public async Task Invalid_routing_model_response_falls_back_to_deterministic_order()
    {
        await using var db = CreateDb();

        var routingProvider = Provider("routing", "https://routing.example/v1");
        var routingModel = Route(routingProvider, "routing-controller", "router-small");
        routingModel.IsRoutingModel = true;
        db.AddRange(routingProvider, routingModel);
        await db.SaveChangesAsync();

        var handler = new RoutingHandler("not json");
        var service = new ModelRoutingDecisionService(
            db,
            new ModelRouterService(db, null!, new ClientFactory(new HttpClient(handler))),
            NullLogger<ModelRoutingDecisionService>.Instance);

        var firstProvider = Provider("first", "https://first.example/v1");
        var secondProvider = Provider("second", "https://second.example/v1");
        var logical = Route(firstProvider, "router", "first-model");
        var first = Route(firstProvider, "router", "first-model");
        var second = Route(secondProvider, "router", "second-model");

        var ranked = await service.RankAsync(
            logical,
            ModelRoutingOperation.Chat,
            JsonNode.Parse("""{"messages":[{"role":"user","content":"Hello"}]}""")!.AsObject(),
            [first, second],
            default);

        Assert.Same(first, ranked[0]);
        Assert.Same(second, ranked[1]);
        Assert.Equal(1, handler.CallCount);
    }

    private static GovernanceDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<GovernanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new GovernanceDbContext(options);
    }

    private static ModelProvider Provider(string slug, string endpoint) => new()
    {
        Name = slug,
        Slug = slug,
        Kind = ModelProviderKind.OpenAiCompatible,
        BaseEndpoint = endpoint
    };

    private static ModelRoute Route(ModelProvider provider, string publicName, string downstreamModel) => new()
    {
        ProviderId = provider.Id,
        Provider = provider,
        PublicName = publicName,
        DownstreamModel = downstreamModel
    };

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RoutingHandler(string decision) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var body = new JsonObject
            {
                ["id"] = "chatcmpl-router",
                ["object"] = "chat.completion",
                ["created"] = 1,
                ["model"] = "router-small",
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = decision
                        },
                        ["finish_reason"] = "stop"
                    }
                }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
        }
    }
}