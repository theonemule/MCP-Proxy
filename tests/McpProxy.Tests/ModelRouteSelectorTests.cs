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
