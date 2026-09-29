using System.Collections.Concurrent;
using McpProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Models;

/// <summary>The protocol capability needed for a routed request.</summary>
public enum ModelRoutingOperation
{
    /// <summary>OpenAI Chat Completions, including native Ollama/Bedrock translation.</summary>
    Chat,
    /// <summary>Generic OpenAI-v1 JSON operation such as Responses or Embeddings.</summary>
    OpenAiOperation
}

/// <summary>
/// In-memory routing state. Consecutive downstream failures temporarily open a circuit for one
/// provider/model target and the counters also provide deterministic weighted target selection.
/// </summary>
public sealed class ModelRoutingState
{
    private sealed class Health
    {
        public int ConsecutiveFailures;
        public DateTimeOffset? OpenUntil;
    }

    private sealed class WeightedBucket
    {
        public Dictionary<Guid, long> Current { get; } = [];
    }

    private readonly ConcurrentDictionary<string, Health> _health = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WeightedBucket> _weightedBuckets = new(StringComparer.Ordinal);

    /// <summary>Failures required before a target is temporarily removed from selection.</summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>How long an unhealthy target remains out of rotation.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>True when the target's circuit is closed or its cooldown has expired.</summary>
    public bool IsAvailable(ModelRoute route)
    {
        var key = HealthKey(route);
        if (!_health.TryGetValue(key, out var health) || health.OpenUntil is null)
        {
            return true;
        }

        if (health.OpenUntil <= DateTimeOffset.UtcNow)
        {
            _health.TryRemove(key, out _);
            return true;
        }

        return false;
    }

    /// <summary>Records a successful downstream attempt and closes any open circuit.</summary>
    public void RecordSuccess(ModelRoute route) => _health.TryRemove(HealthKey(route), out _);

    /// <summary>Records a failed downstream attempt and opens the circuit after the threshold.</summary>
    public void RecordFailure(ModelRoute route)
    {
        var health = _health.GetOrAdd(HealthKey(route), _ => new Health());
        lock (health)
        {
            health.ConsecutiveFailures++;
            if (health.ConsecutiveFailures >= FailureThreshold)
            {
                health.OpenUntil = DateTimeOffset.UtcNow.Add(Cooldown);
            }
        }
    }

    /// <summary>
    /// Smooth weighted round-robin selection. Each call adds every target's configured weight,
    /// selects the largest current value, then subtracts the total weight from that target.
    /// </summary>
    public Guid SelectWeighted(
        Guid routeId,
        int priority,
        IReadOnlyList<(Guid Id, int Weight)> candidates)
    {
        var bucket = _weightedBuckets.GetOrAdd(
            $"{routeId:n}:{priority}",
            _ => new WeightedBucket());

        lock (bucket)
        {
            var valid = candidates.Select(x => x.Id).ToHashSet();
            foreach (var stale in bucket.Current.Keys.Where(x => !valid.Contains(x)).ToList())
            {
                bucket.Current.Remove(stale);
            }

            var total = 0L;
            Guid? selected = null;
            long selectedCurrent = long.MinValue;

            foreach (var candidate in candidates.OrderBy(x => x.Id))
            {
                total += candidate.Weight;
                bucket.Current.TryGetValue(candidate.Id, out var current);
                current += candidate.Weight;
                bucket.Current[candidate.Id] = current;

                if (selected is null || current > selectedCurrent)
                {
                    selected = candidate.Id;
                    selectedCurrent = current;
                }
            }

            if (selected is null)
            {
                throw new InvalidOperationException("Weighted routing requires at least one candidate.");
            }

            bucket.Current[selected.Value] -= total;
            return selected.Value;
        }
    }

    private static string HealthKey(ModelRoute route) =>
        $"{route.ProviderId:n}:{route.DownstreamModel}";
}

/// <summary>
/// Expands one logical public model alias into ordered downstream candidates. The original
/// ModelRoute provider/model is the implicit primary target at priority 0 and weight 100.
/// Additional targets at the same priority share traffic by weight; higher priorities are failover.
/// </summary>
public sealed class ModelRouteSelector(ProxyDbContext db, ModelRoutingState state)
{
    private sealed record Candidate(ModelRoute Route, int Priority, int Weight, Guid StableId);

    /// <summary>Returns healthy candidates in routing order for the requested protocol operation.</summary>
    public async Task<IReadOnlyList<ModelRoute>> GetCandidatesAsync(
        ModelRoute logicalRoute,
        ModelRoutingOperation operation,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();

        if (logicalRoute.Provider.Enabled && Supports(logicalRoute.Provider.Kind, operation))
        {
            candidates.Add(new Candidate(
                Clone(logicalRoute, logicalRoute.Provider, logicalRoute.DownstreamModel),
                Priority: 0,
                Weight: 100,
                StableId: logicalRoute.Id));
        }

        var targets = await db.ModelRouteTargets.AsNoTracking()
            .Include(x => x.Provider)
            .Where(x => x.ModelRouteId == logicalRoute.Id && x.Enabled && x.Provider.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var target in targets)
        {
            if (!Supports(target.Provider.Kind, operation))
            {
                continue;
            }

            candidates.Add(new Candidate(
                Clone(logicalRoute, target.Provider, target.DownstreamModel),
                Math.Max(0, target.Priority),
                Math.Clamp(target.Weight, 1, 10000),
                target.Id));
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var healthy = candidates.Where(x => state.IsAvailable(x.Route)).ToList();
        var usable = healthy.Count > 0 ? healthy : candidates;
        var ordered = new List<ModelRoute>(usable.Count);

        foreach (var tier in usable.GroupBy(x => x.Priority).OrderBy(x => x.Key))
        {
            var tierCandidates = tier
                .OrderBy(x => x.StableId)
                .ToList();

            if (tierCandidates.Count == 1)
            {
                ordered.Add(tierCandidates[0].Route);
                continue;
            }

            var selectedId = state.SelectWeighted(
                logicalRoute.Id,
                tier.Key,
                tierCandidates.Select(x => (x.StableId, x.Weight)).ToList());
            var selectedIndex = tierCandidates.FindIndex(x => x.StableId == selectedId);

            ordered.Add(tierCandidates[selectedIndex].Route);
            for (var offset = 1; offset < tierCandidates.Count; offset++)
            {
                ordered.Add(tierCandidates[(selectedIndex + offset) % tierCandidates.Count].Route);
            }
        }

        return ordered;
    }

    private static bool Supports(ModelProviderKind kind, ModelRoutingOperation operation) =>
        operation switch
        {
            ModelRoutingOperation.Chat =>
                kind is ModelProviderKind.OpenAiCompatible or ModelProviderKind.Ollama or ModelProviderKind.AwsBedrock,
            ModelRoutingOperation.OpenAiOperation =>
                kind is ModelProviderKind.OpenAiCompatible or ModelProviderKind.AwsBedrock,
            _ => false
        };

    private static ModelRoute Clone(ModelRoute logical, ModelProvider provider, string downstreamModel) => new()
    {
        Id = logical.Id,
        ProviderId = provider.Id,
        Provider = provider,
        PublicName = logical.PublicName,
        DownstreamModel = downstreamModel,
        Enabled = logical.Enabled,
        CreatedAt = logical.CreatedAt
    };
}
