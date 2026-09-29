using McpProxy.Data;
using McpProxy.Models;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Tests;

public sealed class ModelRouteSelectorTests
{
    [Fact]
    public async Task Higher_priority_number_is_used_as_failover_after_primary()
    {
        await using var db = CreateDb();
        var (route, backup) = SeedRoute(db);
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "backup-model",
            Priority = 100,
            Weight = 100
        });
        await db.SaveChangesAsync();

        var selector = new ModelRouteSelector(db, new ModelRoutingState());
        var candidates = await selector.GetCandidatesAsync(route, ModelRoutingOperation.Chat, default);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("primary-model", candidates[0].DownstreamModel);
        Assert.Equal("backup-model", candidates[1].DownstreamModel);
    }

    [Fact]
    public async Task Equal_priority_targets_share_first_choice_according_to_weight()
    {
        await using var db = CreateDb();
        var (route, backup) = SeedRoute(db);
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "backup-model",
            Priority = 0,
            Weight = 300
        });
        await db.SaveChangesAsync();

        var state = new ModelRoutingState();
        var selector = new ModelRouteSelector(db, state);
        var firstChoices = new List<Guid>();

        for (var i = 0; i < 40; i++)
        {
            var candidates = await selector.GetCandidatesAsync(route, ModelRoutingOperation.Chat, default);
            firstChoices.Add(candidates[0].ProviderId);
        }

        Assert.Equal(10, firstChoices.Count(id => id == route.ProviderId));
        Assert.Equal(30, firstChoices.Count(id => id == backup.Id));
    }

    [Fact]
    public async Task Open_circuit_removes_failed_target_from_rotation()
    {
        await using var db = CreateDb();
        var (route, backup) = SeedRoute(db);
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "backup-model",
            Priority = 100,
            Weight = 100
        });
        await db.SaveChangesAsync();

        var state = new ModelRoutingState
        {
            FailureThreshold = 1,
            Cooldown = TimeSpan.FromMinutes(1)
        };
        state.RecordFailure(route);

        var selector = new ModelRouteSelector(db, state);
        var candidates = await selector.GetCandidatesAsync(route, ModelRoutingOperation.Chat, default);

        var only = Assert.Single(candidates);
        Assert.Equal(backup.Id, only.ProviderId);
    }

    [Fact]
    public async Task Generic_openai_operations_skip_native_ollama_targets()
    {
        await using var db = CreateDb();
        var primary = new ModelProvider
        {
            Name = "Ollama",
            Slug = "ollama",
            Kind = ModelProviderKind.Ollama,
            BaseEndpoint = "http://ollama:11434"
        };
        var backup = new ModelProvider
        {
            Name = "OpenAI compatible",
            Slug = "openai",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://provider.example/v1"
        };
        var route = new ModelRoute
        {
            ProviderId = primary.Id,
            Provider = primary,
            PublicName = "logical-model",
            DownstreamModel = "llama"
        };
        db.AddRange(primary, backup, route);
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "embedding-model",
            Priority = 100,
            Weight = 100
        });
        await db.SaveChangesAsync();

        var selector = new ModelRouteSelector(db, new ModelRoutingState());
        var candidates = await selector.GetCandidatesAsync(
            route,
            ModelRoutingOperation.OpenAiOperation,
            default);

        var only = Assert.Single(candidates);
        Assert.Equal(backup.Id, only.ProviderId);
    }

    [Fact]
    public async Task High_reasoning_request_prefers_capable_model_over_lower_policy_priority()
    {
        await using var db = CreateDb();
        var (route, backup) = SeedRoute(db);
        route.ReasoningLevel = ModelReasoningLevel.Low;
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "deep-reasoner",
            Priority = 100,
            Weight = 100,
            ReasoningLevel = ModelReasoningLevel.High
        });
        await db.SaveChangesAsync();

        var selector = new ModelRouteSelector(db, new ModelRoutingState());
        var candidates = await selector.GetCandidatesAsync(
            route,
            ModelRoutingOperation.Chat,
            new ModelRoutingRequirements(
                ModelReasoningLevel.High,
                1000,
                1000,
                false,
                false,
                false,
                "general"),
            default);

        Assert.Equal("deep-reasoner", candidates[0].DownstreamModel);
        Assert.Equal("primary-model", candidates[1].DownstreamModel);
    }

    [Fact]
    public async Task Required_tool_capability_filters_explicitly_incompatible_models()
    {
        await using var db = CreateDb();
        var (route, backup) = SeedRoute(db);
        route.SupportsTools = false;
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "tool-model",
            Priority = 100,
            Weight = 100,
            SupportsTools = true
        });
        await db.SaveChangesAsync();

        var selector = new ModelRouteSelector(db, new ModelRoutingState());
        var candidates = await selector.GetCandidatesAsync(
            route,
            ModelRoutingOperation.Chat,
            new ModelRoutingRequirements(
                ModelReasoningLevel.Medium,
                100,
                100,
                true,
                false,
                false,
                "general"),
            default);

        var only = Assert.Single(candidates);
        Assert.Equal("tool-model", only.DownstreamModel);
    }

    [Fact]
    public async Task Context_limit_filters_models_that_cannot_fit_request()
    {
        await using var db = CreateDb();
        var (route, backup) = SeedRoute(db);
        route.MaxContextTokens = 4096;
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "long-context-model",
            Priority = 100,
            Weight = 100,
            MaxContextTokens = 131072
        });
        await db.SaveChangesAsync();

        var selector = new ModelRouteSelector(db, new ModelRoutingState());
        var candidates = await selector.GetCandidatesAsync(
            route,
            ModelRoutingOperation.Chat,
            new ModelRoutingRequirements(
                ModelReasoningLevel.Medium,
                8000,
                1000,
                false,
                false,
                false,
                "general"),
            default);

        var only = Assert.Single(candidates);
        Assert.Equal("long-context-model", only.DownstreamModel);
    }

    [Fact]
    public async Task Simple_request_prefers_lower_cost_model_when_other_fit_is_equal()
    {
        await using var db = CreateDb();
        var (route, backup) = SeedRoute(db);
        route.ReasoningLevel = ModelReasoningLevel.Low;
        route.CostTier = 5;
        route.LatencyTier = 4;
        db.Add(new ModelRouteTarget
        {
            ModelRouteId = route.Id,
            ProviderId = backup.Id,
            Provider = backup,
            DownstreamModel = "efficient-model",
            Priority = 0,
            Weight = 100,
            ReasoningLevel = ModelReasoningLevel.Low,
            CostTier = 1,
            LatencyTier = 1
        });
        await db.SaveChangesAsync();

        var selector = new ModelRouteSelector(db, new ModelRoutingState());
        var candidates = await selector.GetCandidatesAsync(
            route,
            ModelRoutingOperation.Chat,
            new ModelRoutingRequirements(
                ModelReasoningLevel.Low,
                100,
                100,
                false,
                false,
                false,
                "general"),
            default);

        Assert.Equal("efficient-model", candidates[0].DownstreamModel);
    }

    [Fact]
    public void Request_inference_honors_explicit_reasoning_and_detects_vision_and_tools()
    {
        var request = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "reasoning":{"effort":"high"},
              "messages":[
                {
                  "role":"user",
                  "content":[
                    {"type":"text","text":"Analyze this architecture"},
                    {"type":"image_url","image_url":{"url":"https://example.test/image.png"}}
                  ]
                }
              ],
              "tools":[{"type":"function","function":{"name":"lookup"}}],
              "response_format":{"type":"json_schema"}
            }
            """)!.AsObject();

        var requirements = ModelRoutingRequirements.Infer(request);

        Assert.Equal(ModelReasoningLevel.High, requirements.ReasoningLevel);
        Assert.True(requirements.RequiresVision);
        Assert.True(requirements.RequiresTools);
        Assert.True(requirements.RequiresJsonSchema);
        Assert.Equal("vision", requirements.Specialty);
    }

    private static ProxyDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProxyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProxyDbContext(options);
    }

    private static (ModelRoute Route, ModelProvider Backup) SeedRoute(ProxyDbContext db)
    {
        var primary = new ModelProvider
        {
            Name = "Primary",
            Slug = "primary",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://primary.example/v1"
        };
        var backup = new ModelProvider
        {
            Name = "Backup",
            Slug = "backup",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://backup.example/v1"
        };
        var route = new ModelRoute
        {
            ProviderId = primary.Id,
            Provider = primary,
            PublicName = "logical-model",
            DownstreamModel = "primary-model"
        };

        db.AddRange(primary, backup, route);
        return (route, backup);
    }
}
