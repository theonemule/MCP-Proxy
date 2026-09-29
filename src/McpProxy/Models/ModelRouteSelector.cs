using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
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

/// <summary>Capabilities inferred from one northbound inference request.</summary>
public sealed record ModelRoutingRequirements(
    ModelReasoningLevel ReasoningLevel,
    int EstimatedInputTokens,
    int RequestedOutputTokens,
    bool RequiresTools,
    bool RequiresVision,
    bool RequiresJsonSchema,
    string Specialty)
{
    /// <summary>Infers routing requirements from OpenAI-compatible request fields and request content.</summary>
    public static ModelRoutingRequirements Infer(JsonObject request)
    {
        var text = CollectText(request);
        var estimatedInputTokens = Math.Max(1, (text.Length + 3) / 4);
        var requestedOutputTokens = ReadInt(request, "max_completion_tokens")
            ?? ReadInt(request, "max_output_tokens")
            ?? ReadInt(request, "max_tokens")
            ?? 0;

        var requiresTools = request["tools"] is JsonArray { Count: > 0 };
        var requiresVision = ContainsVisionInput(request);
        var requiresJson = RequiresStructuredJson(request);
        var reasoning = ExplicitReasoningLevel(request) ?? InferReasoningLevel(text, requiresTools, estimatedInputTokens);
        var specialty = InferSpecialty(text, requiresVision);

        return new ModelRoutingRequirements(
            reasoning,
            estimatedInputTokens,
            Math.Max(0, requestedOutputTokens),
            requiresTools,
            requiresVision,
            requiresJson,
            specialty);
    }

    private static ModelReasoningLevel? ExplicitReasoningLevel(JsonObject request)
    {
        string? effort = null;
        if (request["reasoning_effort"] is JsonValue direct &&
            direct.TryGetValue<string>(out var directText))
        {
            effort = directText;
        }
        else if (request["reasoning"] is JsonObject reasoning &&
                 reasoning["effort"] is JsonValue nested &&
                 nested.TryGetValue<string>(out var nestedText))
        {
            effort = nestedText;
        }

        return effort?.Trim().ToLowerInvariant() switch
        {
            "none" or "minimal" => ModelReasoningLevel.None,
            "low" => ModelReasoningLevel.Low,
            "medium" => ModelReasoningLevel.Medium,
            "high" or "xhigh" or "extra_high" => ModelReasoningLevel.High,
            _ => null
        };
    }

    private static ModelReasoningLevel InferReasoningLevel(
        string text,
        bool requiresTools,
        int estimatedInputTokens)
    {
        var lower = text.ToLowerInvariant();
        var highMarkers = new[]
        {
            "formal proof", "root cause", "threat model", "deep analysis", "deeply analyze",
            "complex architecture", "multi-step", "multistep", "derive", "prove that",
            "optimize this algorithm", "race condition"
        };
        if (highMarkers.Any(lower.Contains))
        {
            return ModelReasoningLevel.High;
        }

        var mediumMarkers = new[]
        {
            "analyze", "reason", "debug", "refactor", "architecture", "tradeoff", "trade-off",
            "compare", "design", "plan", "algorithm", "security", "diagnose", "implement",
            "code review", "why does", "explain why"
        };
        if (requiresTools || estimatedInputTokens >= 12_000 || mediumMarkers.Any(lower.Contains))
        {
            return ModelReasoningLevel.Medium;
        }

        return ModelReasoningLevel.Low;
    }

    private static string InferSpecialty(string text, bool vision)
    {
        if (vision)
        {
            return "vision";
        }

        var lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "code", "coding", "function", "class ", "api ", "bug", "debug", "refactor", "compiler", "sql", "javascript", "python", "c#"))
        {
            return "coding";
        }

        if (ContainsAny(lower, "equation", "calculate", "mathemat", "algebra", "geometry", "probability", "theorem", "proof"))
        {
            return "math";
        }

        if (ContainsAny(lower, "story", "poem", "creative", "character", "screenplay", "novel"))
        {
            return "creative";
        }

        if (ContainsAny(lower, "summarize", "summary", "extract", "classify", "translate", "rewrite"))
        {
            return "summarization";
        }

        return "general";
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(text.Contains);

    private static int? ReadInt(JsonObject request, string name)
    {
        if (request[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return (int)Math.Clamp(longValue, 0, int.MaxValue);
        }

        return null;
    }

    private static bool RequiresStructuredJson(JsonObject request)
    {
        if (request["response_format"] is JsonObject responseFormat)
        {
            var type = responseFormat["type"]?.GetValue<string>();
            if (type is "json_object" or "json_schema")
            {
                return true;
            }
        }

        if (request["text"] is JsonObject text &&
            text["format"] is JsonObject format)
        {
            var type = format["type"]?.GetValue<string>();
            if (type is "json_object" or "json_schema")
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsVisionInput(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals("image_url", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (pair.Key.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                    pair.Value is JsonValue value &&
                    value.TryGetValue<string>(out var type) &&
                    type is "image_url" or "input_image" or "image")
                {
                    return true;
                }

                if (ContainsVisionInput(pair.Value))
                {
                    return true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            return array.Any(ContainsVisionInput);
        }

        return false;
    }

    private static string CollectText(JsonNode? node)
    {
        var builder = new StringBuilder();
        AppendText(node, builder);
        return builder.ToString();
    }

    private static void AppendText(JsonNode? node, StringBuilder builder)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key is "model" or "metadata" or "type" or "role" or
                    "reasoning_effort" or "effort")
                {
                    continue;
                }

                AppendText(pair.Value, builder);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                AppendText(item, builder);
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            builder.Append(' ').Append(text);
        }
    }
}

/// <summary>
/// In-memory routing state. Consecutive downstream failures temporarily open a circuit for one
/// provider/model target and weighted selection resolves otherwise equivalent candidates.
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

    /// <summary>Smooth weighted round-robin selection among otherwise equivalent candidates.</summary>
    public Guid SelectWeighted(
        Guid routeId,
        int bucketId,
        IReadOnlyList<(Guid Id, int Weight)> candidates)
    {
        var bucket = _weightedBuckets.GetOrAdd(
            $"{routeId:n}:{bucketId}",
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
/// Ranks downstream targets for a logical model route. Request compatibility and reasoning fit are
/// evaluated first; administrative priority, efficiency metadata, and weight refine the choice.
/// Circuit state then provides operational failover.
/// </summary>
public sealed class ModelRouteSelector(ProxyDbContext db, ModelRoutingState state)
{
    private sealed record Candidate(
        ModelRoute Route,
        int Priority,
        int Weight,
        Guid StableId,
        ModelReasoningLevel ReasoningLevel,
        int MaxContextTokens,
        int MaxOutputTokens,
        bool? SupportsTools,
        bool? SupportsVision,
        bool? SupportsJsonSchema,
        int CostTier,
        int LatencyTier,
        string? Specialties,
        int SuitabilityScore);

    /// <summary>Backward-compatible selection when no request body is available.</summary>
    public Task<IReadOnlyList<ModelRoute>> GetCandidatesAsync(
        ModelRoute logicalRoute,
        ModelRoutingOperation operation,
        CancellationToken cancellationToken) =>
        GetCandidatesAsync(
            logicalRoute,
            operation,
            new ModelRoutingRequirements(ModelReasoningLevel.Medium, 1, 0, false, false, false, "general"),
            cancellationToken);

    /// <summary>Infers requirements from the request and returns healthy candidates in intelligent routing order.</summary>
    public Task<IReadOnlyList<ModelRoute>> GetCandidatesAsync(
        ModelRoute logicalRoute,
        ModelRoutingOperation operation,
        JsonObject request,
        CancellationToken cancellationToken) =>
        GetCandidatesAsync(logicalRoute, operation, ModelRoutingRequirements.Infer(request), cancellationToken);

    /// <summary>Returns compatible candidates ordered by suitability, policy priority, and weighted tie-breaking.</summary>
    public async Task<IReadOnlyList<ModelRoute>> GetCandidatesAsync(
        ModelRoute logicalRoute,
        ModelRoutingOperation operation,
        ModelRoutingRequirements requirements,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();

        if (logicalRoute.Provider.Enabled && Supports(logicalRoute.Provider.Kind, operation))
        {
            TryAddCandidate(
                candidates,
                Clone(logicalRoute, logicalRoute.Provider, logicalRoute.DownstreamModel),
                logicalRoute.Priority,
                logicalRoute.Weight,
                logicalRoute.Id,
                logicalRoute.ReasoningLevel,
                logicalRoute.MaxContextTokens,
                logicalRoute.MaxOutputTokens,
                logicalRoute.SupportsTools,
                logicalRoute.SupportsVision,
                logicalRoute.SupportsJsonSchema,
                logicalRoute.CostTier,
                logicalRoute.LatencyTier,
                logicalRoute.Specialties,
                requirements);
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

            TryAddCandidate(
                candidates,
                Clone(logicalRoute, target.Provider, target.DownstreamModel),
                target.Priority,
                target.Weight,
                target.Id,
                target.ReasoningLevel,
                target.MaxContextTokens,
                target.MaxOutputTokens,
                target.SupportsTools,
                target.SupportsVision,
                target.SupportsJsonSchema,
                target.CostTier,
                target.LatencyTier,
                target.Specialties,
                requirements);
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var healthy = candidates.Where(x => state.IsAvailable(x.Route)).ToList();
        var usable = healthy.Count > 0 ? healthy : candidates;

        var ordered = new List<ModelRoute>(usable.Count);
        var groups = usable
            .GroupBy(x => new { x.SuitabilityScore, x.Priority })
            .OrderBy(x => x.Key.SuitabilityScore)
            .ThenBy(x => x.Key.Priority);

        var bucket = 0;
        foreach (var group in groups)
        {
            var groupCandidates = group.OrderBy(x => x.StableId).ToList();
            if (groupCandidates.Count == 1)
            {
                ordered.Add(groupCandidates[0].Route);
                bucket++;
                continue;
            }

            var selectedId = state.SelectWeighted(
                logicalRoute.Id,
                bucket++,
                groupCandidates.Select(x => (x.StableId, Math.Clamp(x.Weight, 1, 10000))).ToList());
            var selectedIndex = groupCandidates.FindIndex(x => x.StableId == selectedId);

            ordered.Add(groupCandidates[selectedIndex].Route);
            for (var offset = 1; offset < groupCandidates.Count; offset++)
            {
                ordered.Add(groupCandidates[(selectedIndex + offset) % groupCandidates.Count].Route);
            }
        }

        return ordered;
    }

    private static void TryAddCandidate(
        ICollection<Candidate> candidates,
        ModelRoute route,
        int priority,
        int weight,
        Guid stableId,
        ModelReasoningLevel reasoningLevel,
        int maxContextTokens,
        int maxOutputTokens,
        bool? supportsTools,
        bool? supportsVision,
        bool? supportsJsonSchema,
        int costTier,
        int latencyTier,
        string? specialties,
        ModelRoutingRequirements requirements)
    {
        if (requirements.RequiresTools && supportsTools == false ||
            requirements.RequiresVision && supportsVision == false ||
            requirements.RequiresJsonSchema && supportsJsonSchema == false)
        {
            return;
        }

        var requiredContext = requirements.EstimatedInputTokens + requirements.RequestedOutputTokens;
        if (maxContextTokens > 0 && requiredContext > maxContextTokens)
        {
            return;
        }

        if (maxOutputTokens > 0 &&
            requirements.RequestedOutputTokens > 0 &&
            requirements.RequestedOutputTokens > maxOutputTokens)
        {
            return;
        }

        var score = SuitabilityScore(
            reasoningLevel,
            maxContextTokens,
            supportsTools,
            supportsVision,
            supportsJsonSchema,
            costTier,
            latencyTier,
            specialties,
            requirements);

        candidates.Add(new Candidate(
            route,
            Math.Max(0, priority),
            Math.Clamp(weight, 1, 10000),
            stableId,
            reasoningLevel,
            Math.Max(0, maxContextTokens),
            Math.Max(0, maxOutputTokens),
            supportsTools,
            supportsVision,
            supportsJsonSchema,
            Math.Clamp(costTier, 0, 5),
            Math.Clamp(latencyTier, 0, 5),
            specialties,
            score));
    }

    private static int SuitabilityScore(
        ModelReasoningLevel reasoningLevel,
        int maxContextTokens,
        bool? supportsTools,
        bool? supportsVision,
        bool? supportsJsonSchema,
        int costTier,
        int latencyTier,
        string? specialties,
        ModelRoutingRequirements requirements)
    {
        var required = (int)requirements.ReasoningLevel;
        var available = (int)reasoningLevel;

        // A reasoning deficit is the strongest soft signal. It remains soft so the router can
        // still fall back to a weaker model when nothing better exists.
        var score = Math.Max(0, required - available) * 10_000;
        score += Math.Max(0, available - required) * 100;

        if (requirements.RequiresTools && supportsTools is null) score += 500;
        if (requirements.RequiresVision && supportsVision is null) score += 500;
        if (requirements.RequiresJsonSchema && supportsJsonSchema is null) score += 500;
        if (maxContextTokens == 0 && requirements.EstimatedInputTokens >= 8_000) score += 250;

        var specialtySet = ParseSpecialties(specialties);
        if (specialtySet.Count > 0)
        {
            score += specialtySet.Contains(requirements.Specialty) || specialtySet.Contains("general")
                ? -750
                : 250;
        }

        // Simple work benefits more from cheap/fast models; difficult work still considers these
        // signals but places much less emphasis on them.
        var efficiencyMultiplier = requirements.ReasoningLevel <= ModelReasoningLevel.Low ? 25 : 5;
        if (costTier > 0) score += Math.Clamp(costTier, 1, 5) * efficiencyMultiplier;
        if (latencyTier > 0) score += Math.Clamp(latencyTier, 1, 5) * efficiencyMultiplier;

        return score;
    }

    private static HashSet<string> ParseSpecialties(string? specialties)
    {
        if (string.IsNullOrWhiteSpace(specialties))
        {
            return [];
        }

        return specialties
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        IsDefault = logical.IsDefault,
        CreatedAt = logical.CreatedAt
    };
}
