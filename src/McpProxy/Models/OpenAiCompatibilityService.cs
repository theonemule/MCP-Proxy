using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpProxy.Data;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Models;

/// <summary>
/// Exposes the model gateway through the OpenAI v1 API. Chat Completions can be translated to
/// native providers, while other JSON model operations can pass through to OpenAI-compatible
/// providers using the same public model aliases and authorization boundary.
/// </summary>
public sealed class OpenAiCompatibilityService(
    ProxyDbContext db,
    IPermissionService permissions,
    IHttpClientFactory httpClientFactory,
    ModelRouteSelector routeSelector,
    ModelRoutingState routingState)
{
    /// <summary>One SSE record from a generic OpenAI-compatible streaming operation.</summary>
    public sealed record StreamRecord(string? EventName, string Data);

    /// <summary>Returns the authorized model catalog in OpenAI's model-list shape.</summary>
    public async Task<JsonObject> ListModelsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var accessible = await permissions.GetAccessibleModelRouteIdsAsync(user, cancellationToken);
        var rows = accessible.Count == 0
            ? []
            : await db.ModelRoutes.AsNoTracking()
                .Where(x =>
                    x.Enabled &&
                    accessible.Contains(x.Id) &&
                    (x.Provider.Enabled || x.Targets.Any(t => t.Enabled && t.Provider.Enabled)))
                .OrderBy(x => x.PublicName)
                .Select(x => new
                {
                    x.PublicName,
                    x.CreatedAt,
                    ProviderSlug = x.Provider.Slug
                })
                .ToListAsync(cancellationToken);

        var data = new JsonArray();
        foreach (var row in rows)
        {
            data.Add(ModelObject(row.PublicName, row.CreatedAt, row.ProviderSlug));
        }

        return new JsonObject
        {
            ["object"] = "list",
            ["data"] = data
        };
    }

    /// <summary>Returns one authorized public model alias in OpenAI's model-object shape.</summary>
    public async Task<JsonObject> GetModelAsync(
        ClaimsPrincipal user,
        string publicModel,
        CancellationToken cancellationToken)
    {
        var route = await ResolveRouteAsync(user, publicModel, cancellationToken);
        return ModelObject(route.PublicName, route.CreatedAt, route.Provider.Slug);
    }

    /// <summary>
    /// Forwards an OpenAI v1 request to the target best suited to the request. A model may name a
    /// logical route explicitly, or be omitted when a default route is configured.
    /// </summary>
    public async Task<JsonObject> ForwardOpenAiOperationAsync(
        ClaimsPrincipal user,
        string operationPath,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        var routes = await ResolveOperationRoutesAsync(user, request, cancellationToken);
        return await ExecuteWithFailoverAsync(
            routes,
            route => ForwardOpenAiOperationOnRouteAsync(route, operationPath, request, cancellationToken),
            cancellationToken);
    }

    private async Task<JsonObject> ForwardOpenAiOperationOnRouteAsync(
        ModelRoute route,
        string operationPath,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        var (uri, signForBedrock) = ResolveOpenAiOperation(route, operationPath);
        var body = request.DeepClone().AsObject();
        body["model"] = route.DownstreamModel;

        var response = await SendJsonAsync(
            route.Provider,
            uri,
            body,
            signForBedrock,
            cancellationToken);

        RewriteModelIdentity(response, route.DownstreamModel, route.PublicName);
        return response;
    }

    /// <summary>
    /// Streams a generic OpenAI v1 operation. Failover is allowed only until the first downstream
    /// event is emitted so one caller stream is never stitched together from multiple providers.
    /// </summary>
    public async IAsyncEnumerable<StreamRecord> StreamOpenAiOperationAsync(
        ClaimsPrincipal user,
        string operationPath,
        JsonObject request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var routes = await ResolveOperationRoutesAsync(user, request, cancellationToken);
        await foreach (var record in RouteStreamWithFailover(
                           routes,
                           route => StreamOpenAiOperationOnRouteAsync(route, operationPath, request, cancellationToken),
                           cancellationToken))
        {
            yield return record;
        }
    }

    private async IAsyncEnumerable<StreamRecord> StreamOpenAiOperationOnRouteAsync(
        ModelRoute route,
        string operationPath,
        JsonObject request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (uri, signForBedrock) = ResolveOpenAiOperation(route, operationPath);
        var body = request.DeepClone().AsObject();
        body["model"] = route.DownstreamModel;
        body["stream"] = true;

        using var response = await SendResponseAsync(
            route.Provider,
            uri,
            body,
            signForBedrock,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);

        string? eventName = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                yield return new StreamRecord(eventName, data);
                eventName = null;
                continue;
            }

            try
            {
                var node = JsonNode.Parse(data);
                if (node is not null)
                {
                    RewriteModelIdentity(node, route.DownstreamModel, route.PublicName);
                    data = node.ToJsonString();
                }
            }
            catch (JsonException)
            {
                // Preserve non-JSON SSE data verbatim for forward compatibility.
            }

            yield return new StreamRecord(eventName, data);
            eventName = null;
        }
    }

    /// <summary>Creates one non-streaming OpenAI Chat Completion using the route's target policy.</summary>
    public async Task<JsonObject> CreateChatCompletionAsync(
        ClaimsPrincipal user,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        var (routes, messages) = await ValidateAndResolveAsync(user, request, cancellationToken);
        return await ExecuteWithFailoverAsync(
            routes,
            route => CreateChatCompletionOnRouteAsync(route, request, messages, cancellationToken),
            cancellationToken);
    }

    private Task<JsonObject> CreateChatCompletionOnRouteAsync(
        ModelRoute route,
        JsonObject request,
        JsonArray messages,
        CancellationToken cancellationToken) =>
        route.Provider.Kind switch
        {
            ModelProviderKind.OpenAiCompatible =>
                CreateNativeOpenAiCompletionAsync(route, request, cancellationToken),
            ModelProviderKind.Ollama =>
                CreateOllamaCompletionAsync(route, request, messages, cancellationToken),
            ModelProviderKind.AwsBedrock =>
                CreateBedrockCompletionAsync(route, request, messages, cancellationToken),
            _ => Task.FromException<JsonObject>(NoCompatibleTargets(route.PublicName))
        };

    /// <summary>Streams Chat Completions with pre-first-chunk failover across routed targets.</summary>
    public async IAsyncEnumerable<JsonObject> StreamChatCompletionAsync(
        ClaimsPrincipal user,
        JsonObject request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (routes, messages) = await ValidateAndResolveAsync(user, request, cancellationToken);
        await foreach (var chunk in RouteStreamWithFailover(
                           routes,
                           route => StreamChatCompletionOnRouteAsync(route, request, messages, cancellationToken),
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    private IAsyncEnumerable<JsonObject> StreamChatCompletionOnRouteAsync(
        ModelRoute route,
        JsonObject request,
        JsonArray messages,
        CancellationToken cancellationToken) =>
        route.Provider.Kind switch
        {
            ModelProviderKind.OpenAiCompatible => StreamNativeOpenAiAsync(route, request, cancellationToken),
            ModelProviderKind.Ollama => StreamOllamaAsync(route, request, messages, cancellationToken),
            ModelProviderKind.AwsBedrock => StreamBedrockAsync(route, request, messages, cancellationToken),
            _ => ThrowUnsupportedStream(route.PublicName)
        };

    private static async IAsyncEnumerable<JsonObject> ThrowUnsupportedStream(
        string publicModel)
    {
        await Task.Yield();
        throw NoCompatibleTargets(publicModel);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async Task<JsonObject> ExecuteWithFailoverAsync(
        IReadOnlyList<ModelRoute> routes,
        Func<ModelRoute, Task<JsonObject>> execute,
        CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
        {
            throw NoCompatibleTargets("requested");
        }

        Exception? lastFailure = null;
        foreach (var route in routes)
        {
            try
            {
                var result = await execute(route);
                routingState.RecordSuccess(route);
                return result;
            }
            catch (Exception exception) when (IsRetryableRoutingFailure(exception, cancellationToken))
            {
                routingState.RecordFailure(route);
                lastFailure = exception;
            }
        }

        throw lastFailure ?? NoCompatibleTargets(routes[0].PublicName);
    }

    private async IAsyncEnumerable<T> RouteStreamWithFailover<T>(
        IReadOnlyList<ModelRoute> routes,
        Func<ModelRoute, IAsyncEnumerable<T>> streamFactory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
        {
            throw NoCompatibleTargets("requested");
        }

        Exception? lastFailure = null;

        foreach (var route in routes)
        {
            await using var enumerator = streamFactory(route).GetAsyncEnumerator(cancellationToken);
            var emitted = false;

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (Exception exception) when (!emitted && IsRetryableRoutingFailure(exception, cancellationToken))
                {
                    routingState.RecordFailure(route);
                    lastFailure = exception;
                    break;
                }

                if (!hasNext)
                {
                    routingState.RecordSuccess(route);
                    yield break;
                }

                if (!emitted)
                {
                    routingState.RecordSuccess(route);
                    emitted = true;
                }

                yield return enumerator.Current;
            }
        }

        throw lastFailure ?? NoCompatibleTargets(routes[0].PublicName);
    }

    private static bool IsRetryableRoutingFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception is OpenAiCompatibilityException)
        {
            return false;
        }

        if (exception is DownstreamModelException downstream)
        {
            return downstream.StatusCode is 408 or 409 or 425 or 429 || downstream.StatusCode >= 500;
        }

        return exception is HttpRequestException or InvalidOperationException or TaskCanceledException;
    }

    private static OpenAiCompatibilityException NoCompatibleTargets(string publicModel) => new(
        "invalid_request_error",
        $"Model '{publicModel}' has no enabled routing target compatible with this operation.",
        "model",
        "unsupported_model_provider");

    private async Task<JsonObject> CreateNativeOpenAiCompletionAsync(
        ModelRoute route,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        var body = request.DeepClone().AsObject();
        body["model"] = route.DownstreamModel;
        body["stream"] = false;

        var response = await SendJsonAsync(
            route.Provider,
            BuildOpenAiChatUri(route.Provider),
            body,
            signForBedrock: false,
            cancellationToken);

        response["model"] = route.PublicName;
        response["object"] ??= "chat.completion";
        response["created"] ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return response;
    }

    private async IAsyncEnumerable<JsonObject> StreamNativeOpenAiAsync(
        ModelRoute route,
        JsonObject request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = request.DeepClone().AsObject();
        body["model"] = route.DownstreamModel;
        body["stream"] = true;

        using var response = await SendResponseAsync(
            route.Provider,
            BuildOpenAiChatUri(route.Provider),
            body,
            signForBedrock: false,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                yield break;
            }

            JsonObject? chunk;
            try
            {
                chunk = JsonNode.Parse(data)?.AsObject();
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk is null)
            {
                continue;
            }

            chunk["model"] = route.PublicName;
            chunk["object"] ??= "chat.completion.chunk";
            yield return chunk;
        }
    }

    private async Task<JsonObject> CreateOllamaCompletionAsync(
        ModelRoute route,
        JsonObject request,
        JsonArray messages,
        CancellationToken cancellationToken)
    {
        var body = BuildOllamaRequest(route, request, messages, stream: false);
        var path = string.IsNullOrWhiteSpace(route.Provider.ChatPath) ? "/api/chat" : route.Provider.ChatPath!;
        var response = await SendJsonAsync(
            route.Provider,
            ModelRouterService.BuildProviderUri(route.Provider, path),
            body,
            signForBedrock: false,
            cancellationToken);

        var message = response["message"] as JsonObject ?? new JsonObject();
        var openAiMessage = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = message["content"]?.DeepClone() ?? "",
            ["refusal"] = null
        };

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            openAiMessage["tool_calls"] = NormalizeOllamaToolCalls(toolCalls);
        }

        var finishReason = MapFinishReason(response["done_reason"]?.GetValue<string>(), openAiMessage["tool_calls"] is not null);
        return ChatCompletion(
            route.PublicName,
            openAiMessage,
            finishReason,
            Usage(
                response["prompt_eval_count"]?.GetValue<int?>(),
                response["eval_count"]?.GetValue<int?>()));
    }

    private async IAsyncEnumerable<JsonObject> StreamOllamaAsync(
        ModelRoute route,
        JsonObject request,
        JsonArray messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildOllamaRequest(route, request, messages, stream: true);
        var path = string.IsNullOrWhiteSpace(route.Provider.ChatPath) ? "/api/chat" : route.Provider.ChatPath!;
        using var response = await SendResponseAsync(
            route.Provider,
            ModelRouterService.BuildProviderUri(route.Provider, path),
            body,
            signForBedrock: false,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);

        var id = CompletionId();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var first = true;
        var includeUsage = IncludeUsage(request);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var chunk = JsonNode.Parse(line)?.AsObject()
                ?? throw new InvalidOperationException("Ollama returned an invalid streaming JSON object.");
            var message = chunk["message"] as JsonObject;

            if (message is not null)
            {
                var delta = new JsonObject();
                if (first)
                {
                    delta["role"] = "assistant";
                    first = false;
                }

                if (message["content"] is not null)
                {
                    delta["content"] = message["content"]!.DeepClone();
                }

                if (message["tool_calls"] is JsonArray toolCalls)
                {
                    delta["tool_calls"] = NormalizeOllamaToolCalls(toolCalls, streaming: true);
                }

                if (delta.Count > 0)
                {
                    yield return ChatChunk(route.PublicName, id, created, delta, finishReason: null);
                }
            }

            if (chunk["done"]?.GetValue<bool>() == true)
            {
                var hasTools = message?["tool_calls"] is JsonArray { Count: > 0 };
                yield return ChatChunk(
                    route.PublicName,
                    id,
                    created,
                    new JsonObject(),
                    MapFinishReason(chunk["done_reason"]?.GetValue<string>(), hasTools));

                if (includeUsage)
                {
                    var usage = Usage(
                        chunk["prompt_eval_count"]?.GetValue<int?>(),
                        chunk["eval_count"]?.GetValue<int?>());
                    if (usage is not null)
                    {
                        yield return UsageChunk(route.PublicName, id, created, usage);
                    }
                }

                yield break;
            }
        }
    }

    private async Task<JsonObject> CreateBedrockCompletionAsync(
        ModelRoute route,
        JsonObject request,
        JsonArray messages,
        CancellationToken cancellationToken)
    {
        var body = BuildBedrockRequest(request, messages);
        var uri = BuildBedrockUri(route, streaming: false);
        var response = await SendJsonAsync(route.Provider, uri, body, signForBedrock: true, cancellationToken);
        var message = response["output"]?["message"] as JsonObject
            ?? throw new InvalidOperationException("Bedrock response did not contain an assistant message.");

        var openAiMessage = BedrockMessageToOpenAi(message);
        return ChatCompletion(
            route.PublicName,
            openAiMessage,
            MapBedrockFinishReason(response["stopReason"]?.GetValue<string>()),
            BedrockUsage(response["usage"]));
    }

    private async IAsyncEnumerable<JsonObject> StreamBedrockAsync(
        ModelRoute route,
        JsonObject request,
        JsonArray messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBedrockRequest(request, messages);
        using var response = await SendResponseAsync(
            route.Provider,
            BuildBedrockUri(route, streaming: true),
            body,
            signForBedrock: true,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var id = CompletionId();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var first = true;
        var includeUsage = IncludeUsage(request);
        JsonNode? usage = null;

        await foreach (var message in AwsEventStreamReader.ReadAsync(stream, cancellationToken))
        {
            message.Headers.TryGetValue(":event-type", out var eventType);
            message.Headers.TryGetValue(":message-type", out var messageType);

            var payload = message.Payload.Length == 0
                ? new JsonObject()
                : JsonNode.Parse(message.Payload)?.AsObject()
                    ?? throw new InvalidOperationException("Bedrock returned invalid EventStream JSON.");

            if (string.Equals(messageType, "exception", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Bedrock streaming request failed with event '{eventType ?? "unknown"}'.");
            }

            if (eventType == "contentBlockStart")
            {
                var start = payload["start"] ?? payload["contentBlockStart"]?["start"];
                if (start?["toolUse"] is JsonObject toolUse)
                {
                    var delta = new JsonObject();
                    if (first)
                    {
                        delta["role"] = "assistant";
                        first = false;
                    }

                    delta["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = payload["contentBlockIndex"]?.DeepClone() ?? 0,
                            ["id"] = toolUse["toolUseId"]?.DeepClone(),
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = toolUse["name"]?.DeepClone(),
                                ["arguments"] = ""
                            }
                        }
                    };
                    yield return ChatChunk(route.PublicName, id, created, delta, null);
                }
            }
            else if (eventType == "contentBlockDelta")
            {
                var deltaNode = payload["delta"] ?? payload["contentBlockDelta"]?["delta"];
                var delta = new JsonObject();
                if (first)
                {
                    delta["role"] = "assistant";
                    first = false;
                }

                if (deltaNode?["text"] is JsonNode text)
                {
                    delta["content"] = text.DeepClone();
                }
                else if (deltaNode?["toolUse"]?["input"] is JsonNode input)
                {
                    delta["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = payload["contentBlockIndex"]?.DeepClone() ?? 0,
                            ["function"] = new JsonObject
                            {
                                ["arguments"] = input.DeepClone()
                            }
                        }
                    };
                }

                if (delta.Count > 0)
                {
                    yield return ChatChunk(route.PublicName, id, created, delta, null);
                }
            }
            else if (eventType == "messageStop")
            {
                var stopReason = payload["stopReason"]?.GetValue<string>()
                    ?? payload["messageStop"]?["stopReason"]?.GetValue<string>();
                yield return ChatChunk(
                    route.PublicName,
                    id,
                    created,
                    new JsonObject(),
                    MapBedrockFinishReason(stopReason));
            }
            else if (eventType == "metadata")
            {
                usage = BedrockUsage(payload["usage"] ?? payload["metadata"]?["usage"]);
            }
        }

        if (includeUsage && usage is not null)
        {
            yield return UsageChunk(route.PublicName, id, created, usage);
        }
    }

    private async Task<IReadOnlyList<ModelRoute>> ResolveOperationRoutesAsync(
        ClaimsPrincipal user,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        var model = request["model"]?.GetValue<string>();
        var logical = await ResolveRouteAsync(user, model, cancellationToken);
        var candidates = await routeSelector.GetCandidatesAsync(
            logical,
            ModelRoutingOperation.OpenAiOperation,
            request,
            cancellationToken);
        var routes = await FilterAuthorizedTargetsAsync(
            user, logical.Id, candidates, cancellationToken);
        if (routes.Count == 0)
        {
            throw NoCompatibleTargets(logical.PublicName);
        }

        return routes;
    }

    private async Task<(IReadOnlyList<ModelRoute> Routes, JsonArray Messages)> ValidateAndResolveAsync(
        ClaimsPrincipal user,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        var model = request["model"]?.GetValue<string>();
        var logical = await ResolveRouteAsync(user, model, cancellationToken);
        if (request["messages"] is not JsonArray { Count: > 0 } messages)
        {
            throw new OpenAiCompatibilityException(
                "invalid_request_error",
                "You must provide at least one message.",
                "messages",
                null);
        }

        var candidates = await routeSelector.GetCandidatesAsync(
            logical,
            ModelRoutingOperation.Chat,
            request,
            cancellationToken);
        var routes = await FilterAuthorizedTargetsAsync(
            user, logical.Id, candidates, cancellationToken);
        if (routes.Count == 0)
        {
            throw NoCompatibleTargets(logical.PublicName);
        }

        return (routes, messages);
    }

    private async Task<IReadOnlyList<ModelRoute>> FilterAuthorizedTargetsAsync(
        ClaimsPrincipal user,
        Guid logicalRouteId,
        IReadOnlyList<ModelRoute> candidates,
        CancellationToken cancellationToken)
    {
        var allowed = new List<ModelRoute>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (await permissions.CanAccessModelRouteTargetAsync(
                    user, logicalRouteId, candidate.ProviderId, cancellationToken))
            {
                allowed.Add(candidate);
            }
        }

        return allowed;
    }

    private async Task<ModelRoute> ResolveRouteAsync(
        ClaimsPrincipal user,
        string? publicModel,
        CancellationToken cancellationToken)
    {
        ModelRoute? route;
        if (string.IsNullOrWhiteSpace(publicModel))
        {
            route = await db.ModelRoutes.AsNoTracking()
                .Include(x => x.Provider)
                .Where(x => x.IsDefault && x.Enabled)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (route is null)
            {
                throw new OpenAiCompatibilityException(
                    "invalid_request_error",
                    "You must provide a model or configure a default model router.",
                    "model",
                    "model_required");
            }
        }
        else
        {
            route = await db.ModelRoutes.AsNoTracking()
                .Include(x => x.Provider)
                .SingleOrDefaultAsync(
                    x => x.PublicName == publicModel && x.Enabled,
                    cancellationToken);
        }

        if (route is null ||
            !await permissions.CanAccessModelRouteAsync(user, route.Id, cancellationToken))
        {
            var name = string.IsNullOrWhiteSpace(publicModel) ? "default router" : $"model '{publicModel}'";
            throw new OpenAiCompatibilityException(
                "invalid_request_error",
                $"The {name} does not exist or you do not have access to it.",
                "model",
                "model_not_found",
                StatusCodes.Status404NotFound);
        }

        return route;
    }

    private static JsonObject BuildOllamaRequest(
        ModelRoute route,
        JsonObject request,
        JsonArray messages,
        bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = route.DownstreamModel,
            ["stream"] = stream,
            ["messages"] = NormalizeMessagesForOllama(messages)
        };

        if (request["tools"] is JsonArray tools)
        {
            body["tools"] = tools.DeepClone();
        }

        var options = new JsonObject();
        CopyIfPresent(request, options, "temperature", "temperature");
        CopyIfPresent(request, options, "top_p", "top_p");
        CopyIfPresent(request, options, "seed", "seed");
        CopyIfPresent(request, options, "stop", "stop");
        CopyIfPresent(request, options, "max_tokens", "num_predict");
        CopyIfPresent(request, options, "max_completion_tokens", "num_predict");

        if (options.Count > 0)
        {
            body["options"] = options;
        }

        var responseFormatType = request["response_format"]?["type"]?.GetValue<string>();
        if (responseFormatType == "json_object")
        {
            body["format"] = "json";
        }
        else if (responseFormatType == "json_schema" &&
                 request["response_format"]?["json_schema"]?["schema"] is JsonNode schema)
        {
            body["format"] = schema.DeepClone();
        }

        return body;
    }

    private static JsonArray NormalizeMessagesForOllama(JsonArray messages)
    {
        var normalized = new JsonArray();
        foreach (var node in messages)
        {
            if (node is not JsonObject message)
            {
                continue;
            }

            var role = message["role"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            if (role == "developer")
            {
                role = "system";
            }

            var output = new JsonObject
            {
                ["role"] = role,
                ["content"] = ExtractTextContent(message["content"])
            };

            if (message["tool_calls"] is JsonArray toolCalls)
            {
                output["tool_calls"] = toolCalls.DeepClone();
            }

            normalized.Add(output);
        }

        return normalized;
    }

    private static JsonObject BuildBedrockRequest(JsonObject request, JsonArray messages)
    {
        var bedrockMessages = new JsonArray();
        var system = new JsonArray();

        foreach (var node in messages)
        {
            if (node is not JsonObject message)
            {
                continue;
            }

            var role = message["role"]?.GetValue<string>();
            if (role is "system" or "developer")
            {
                var text = ExtractTextContent(message["content"]);
                if (!string.IsNullOrEmpty(text))
                {
                    system.Add(new JsonObject { ["text"] = text });
                }
                continue;
            }

            if (role == "tool")
            {
                var toolUseId = message["tool_call_id"]?.GetValue<string>();
                bedrockMessages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["toolResult"] = new JsonObject
                            {
                                ["toolUseId"] = toolUseId,
                                ["content"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = ExtractTextContent(message["content"]) }
                                }
                            }
                        }
                    }
                });
                continue;
            }

            if (role is not ("user" or "assistant"))
            {
                continue;
            }

            var content = new JsonArray();
            var textContent = ExtractTextContent(message["content"]);
            if (!string.IsNullOrEmpty(textContent))
            {
                content.Add(new JsonObject { ["text"] = textContent });
            }

            if (role == "assistant" && message["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var tool in toolCalls.OfType<JsonObject>())
                {
                    var function = tool["function"] as JsonObject;
                    if (function is null)
                    {
                        continue;
                    }

                    JsonNode? input;
                    var arguments = function["arguments"]?.GetValue<string>() ?? "{}";
                    try
                    {
                        input = JsonNode.Parse(arguments);
                    }
                    catch (JsonException)
                    {
                        input = new JsonObject();
                    }

                    content.Add(new JsonObject
                    {
                        ["toolUse"] = new JsonObject
                        {
                            ["toolUseId"] = tool["id"]?.DeepClone() ?? "call_" + Guid.NewGuid().ToString("N"),
                            ["name"] = function["name"]?.DeepClone(),
                            ["input"] = input
                        }
                    });
                }
            }

            bedrockMessages.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content
            });
        }

        var body = new JsonObject { ["messages"] = bedrockMessages };
        if (system.Count > 0)
        {
            body["system"] = system;
        }

        var inference = new JsonObject();
        CopyIfPresent(request, inference, "max_completion_tokens", "maxTokens");
        if (inference["maxTokens"] is null)
        {
            CopyIfPresent(request, inference, "max_tokens", "maxTokens");
        }
        CopyIfPresent(request, inference, "temperature", "temperature");
        CopyIfPresent(request, inference, "top_p", "topP");
        if (request["stop"] is JsonArray stopSequences)
        {
            inference["stopSequences"] = stopSequences.DeepClone();
        }
        else if (request["stop"] is JsonValue stopValue &&
                 stopValue.TryGetValue<string>(out var stopSequence))
        {
            inference["stopSequences"] = new JsonArray(stopSequence);
        }
        if (inference.Count > 0)
        {
            body["inferenceConfig"] = inference;
        }

        if (request["tools"] is JsonArray tools && tools.Count > 0)
        {
            var bedrockTools = new JsonArray();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                if (tool["type"]?.GetValue<string>() != "function" ||
                    tool["function"] is not JsonObject function)
                {
                    continue;
                }

                bedrockTools.Add(new JsonObject
                {
                    ["toolSpec"] = new JsonObject
                    {
                        ["name"] = function["name"]?.DeepClone(),
                        ["description"] = function["description"]?.DeepClone(),
                        ["inputSchema"] = new JsonObject
                        {
                            ["json"] = function["parameters"]?.DeepClone() ?? new JsonObject()
                        }
                    }
                });
            }

            if (bedrockTools.Count > 0)
            {
                var toolConfig = new JsonObject { ["tools"] = bedrockTools };
                var toolChoice = request["tool_choice"];
                if (toolChoice is JsonValue value && value.TryGetValue<string>(out var choice))
                {
                    if (choice == "required")
                    {
                        toolConfig["toolChoice"] = new JsonObject { ["any"] = new JsonObject() };
                    }
                    else if (choice == "none")
                    {
                        toolConfig = null;
                    }
                }
                else if (toolChoice?["function"]?["name"] is JsonNode name)
                {
                    toolConfig["toolChoice"] = new JsonObject
                    {
                        ["tool"] = new JsonObject { ["name"] = name.DeepClone() }
                    };
                }

                if (toolConfig is not null)
                {
                    body["toolConfig"] = toolConfig;
                }
            }
        }

        return body;
    }

    private static JsonObject BedrockMessageToOpenAi(JsonObject message)
    {
        var content = message["content"] as JsonArray;
        var text = new StringBuilder();
        var toolCalls = new JsonArray();

        if (content is not null)
        {
            foreach (var part in content.OfType<JsonObject>())
            {
                if (part["text"] is JsonNode textNode)
                {
                    text.Append(textNode.GetValue<string>());
                }

                if (part["toolUse"] is JsonObject toolUse)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = toolUse["toolUseId"]?.DeepClone(),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = toolUse["name"]?.DeepClone(),
                            ["arguments"] = toolUse["input"]?.ToJsonString() ?? "{}"
                        }
                    });
                }
            }
        }

        var output = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? text.ToString() : null,
            ["refusal"] = null
        };
        if (toolCalls.Count > 0)
        {
            output["tool_calls"] = toolCalls;
        }
        return output;
    }

    private static JsonArray NormalizeOllamaToolCalls(JsonArray toolCalls, bool streaming = false)
    {
        var output = new JsonArray();
        var index = 0;
        foreach (var node in toolCalls.OfType<JsonObject>())
        {
            var function = node["function"] as JsonObject;
            if (function is null)
            {
                continue;
            }

            var item = new JsonObject
            {
                ["index"] = index++,
                ["id"] = node["id"]?.DeepClone() ?? "call_" + Guid.NewGuid().ToString("N"),
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = function["name"]?.DeepClone()
                }
            };

            var args = function["arguments"];
            item["function"]!["arguments"] = args switch
            {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                null => "{}",
                _ => args.ToJsonString()
            };

            if (!streaming)
            {
                item.Remove("index");
            }
            output.Add(item);
        }
        return output;
    }

    private static JsonObject ChatCompletion(
        string model,
        JsonObject message,
        string finishReason,
        JsonNode? usage) =>
        new()
        {
            ["id"] = CompletionId(),
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["logprobs"] = null,
                    ["finish_reason"] = finishReason
                }
            },
            ["usage"] = usage
        };

    private static JsonObject ChatChunk(
        string model,
        string id,
        long created,
        JsonObject delta,
        string? finishReason) =>
        new()
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["logprobs"] = null,
                    ["finish_reason"] = finishReason
                }
            }
        };

    private static JsonObject UsageChunk(
        string model,
        string id,
        long created,
        JsonNode usage) =>
        new()
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray(),
            ["usage"] = usage
        };

    private static JsonObject? Usage(int? promptTokens, int? completionTokens)
    {
        if (promptTokens is null && completionTokens is null)
        {
            return null;
        }

        var prompt = promptTokens ?? 0;
        var completion = completionTokens ?? 0;
        return new JsonObject
        {
            ["prompt_tokens"] = prompt,
            ["completion_tokens"] = completion,
            ["total_tokens"] = prompt + completion
        };
    }

    private static JsonObject? BedrockUsage(JsonNode? source)
    {
        if (source is null)
        {
            return null;
        }

        var prompt = source["inputTokens"]?.GetValue<int?>() ?? 0;
        var completion = source["outputTokens"]?.GetValue<int?>() ?? 0;
        var total = source["totalTokens"]?.GetValue<int?>() ?? prompt + completion;
        return new JsonObject
        {
            ["prompt_tokens"] = prompt,
            ["completion_tokens"] = completion,
            ["total_tokens"] = total
        };
    }

    private static JsonObject ModelObject(string id, DateTimeOffset createdAt, string owner) =>
        new()
        {
            ["id"] = id,
            ["object"] = "model",
            ["created"] = createdAt.ToUnixTimeSeconds(),
            ["owned_by"] = owner
        };

    private static string CompletionId() => "chatcmpl-gw-" + Guid.NewGuid().ToString("N");

    private static string MapFinishReason(string? reason, bool hasToolCalls = false)
    {
        if (hasToolCalls)
        {
            return "tool_calls";
        }

        return reason switch
        {
            "length" => "length",
            "content_filter" => "content_filter",
            _ => "stop"
        };
    }

    private static string MapBedrockFinishReason(string? reason) =>
        reason switch
        {
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            "content_filtered" or "guardrail_intervened" => "content_filter",
            _ => "stop"
        };

    private static bool IncludeUsage(JsonObject request) =>
        request["stream_options"]?["include_usage"]?.GetValue<bool?>() == true;

    private static string ExtractTextContent(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is not JsonArray parts)
        {
            return "";
        }

        return string.Concat(parts
            .OfType<JsonObject>()
            .Where(x => x["type"]?.GetValue<string>() is "text" or "input_text")
            .Select(x => x["text"]?.GetValue<string>() ?? ""));
    }

    private static void CopyIfPresent(JsonObject source, JsonObject destination, string sourceName, string destinationName)
    {
        if (source[sourceName] is JsonNode value)
        {
            destination[destinationName] = value.DeepClone();
        }
    }

    private (Uri Uri, bool SignForBedrock) ResolveOpenAiOperation(ModelRoute route, string operationPath)
    {
        var normalized = operationPath.Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Equals("models", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenAiCompatibilityException(
                "invalid_request_error",
                "This OpenAI operation is not available through model inference routing.",
                null,
                "unsupported_operation",
                StatusCodes.Status404NotFound);
        }

        return route.Provider.Kind switch
        {
            ModelProviderKind.OpenAiCompatible =>
                (BuildOpenAiOperationUri(route.Provider, normalized, bedrockRuntime: false), false),
            ModelProviderKind.AwsBedrock =>
                (BuildOpenAiOperationUri(route.Provider, normalized, bedrockRuntime: true), true),
            _ => throw new OpenAiCompatibilityException(
                "invalid_request_error",
                $"Model '{route.PublicName}' is not backed by a provider that exposes this OpenAI v1 operation.",
                "model",
                "unsupported_model_provider")
        };
    }

    private static Uri BuildOpenAiOperationUri(
        ModelProvider provider,
        string operationPath,
        bool bedrockRuntime)
    {
        var endpoint = provider.BaseEndpoint;
        if (bedrockRuntime && string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = $"https://bedrock-runtime.{ModelCredentialResolver.ResolveAwsRegion(provider)}.amazonaws.com";
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Model provider '{provider.Name}' has an invalid base endpoint.");
        }

        var root = baseUri.ToString().TrimEnd('/');
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var suffix = "/" + operationPath.TrimStart('/');

        if (basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(root + suffix, UriKind.Absolute);
        }

        return new Uri(
            root + (bedrockRuntime ? "/openai/v1" : "/v1") + suffix,
            UriKind.Absolute);
    }

    private static void RewriteModelIdentity(JsonNode node, string downstreamModel, string publicModel)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Key.Equals("model", StringComparison.OrdinalIgnoreCase) &&
                    pair.Value is JsonValue value &&
                    value.TryGetValue<string>(out var model) &&
                    model == downstreamModel)
                {
                    obj[pair.Key] = publicModel;
                    continue;
                }

                if (pair.Value is not null)
                {
                    RewriteModelIdentity(pair.Value, downstreamModel, publicModel);
                }
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RewriteModelIdentity(item, downstreamModel, publicModel);
                }
            }
        }
    }

    private static Uri BuildOpenAiChatUri(ModelProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.ChatPath))
        {
            return ModelRouterService.BuildProviderUri(provider, provider.ChatPath!);
        }

        return BuildOpenAiOperationUri(provider, "chat/completions", bedrockRuntime: false);
    }

    private static Uri BuildBedrockUri(ModelRoute route, bool streaming)
    {
        var provider = route.Provider;
        var endpoint = !string.IsNullOrWhiteSpace(provider.BaseEndpoint)
            ? provider.BaseEndpoint
            : $"https://bedrock-runtime.{ModelCredentialResolver.ResolveAwsRegion(provider)}.amazonaws.com";
        var operation = streaming ? "converse-stream" : "converse";
        return new Uri(
            endpoint.TrimEnd('/') + "/model/" + Uri.EscapeDataString(route.DownstreamModel) + "/" + operation,
            UriKind.Absolute);
    }

    private async Task<JsonObject> SendJsonAsync(
        ModelProvider provider,
        Uri uri,
        JsonObject body,
        bool signForBedrock,
        CancellationToken cancellationToken)
    {
        using var response = await SendResponseAsync(provider, uri, body, signForBedrock, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(payload)?.AsObject()
                ?? throw new InvalidOperationException("Downstream provider returned an empty JSON response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Downstream provider returned invalid JSON.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendResponseAsync(
        ModelProvider provider,
        Uri uri,
        JsonObject body,
        bool signForBedrock,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body.ToJsonString());
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (signForBedrock)
        {
            var bearer = ModelCredentialResolver.ResolveBedrockBearerHeaders(provider);
            if (bearer is not null)
            {
                foreach (var header in bearer)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            else
            {
                AwsSigV4Signer.Sign(
                    request,
                    payload,
                    ModelCredentialResolver.ResolveAwsCredentials(provider),
                    "bedrock",
                    DateTimeOffset.UtcNow);
            }
        }
        else
        {
            foreach (var header in ModelCredentialResolver.ResolveHeaders(provider))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var client = httpClientFactory.CreateClient("model-downstream");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var safeBody = text.Length > 2048 ? text[..2048] : text;
            throw new DownstreamModelException(
                (int)response.StatusCode,
                $"Downstream model provider returned {(int)response.StatusCode} {response.ReasonPhrase}: {safeBody}");
        }
        finally
        {
            response.Dispose();
        }
    }
}

/// <summary>Downstream HTTP failure used by the router to decide whether failover is safe.</summary>
public sealed class DownstreamModelException(int statusCode, string message) : InvalidOperationException(message)
{
    /// <summary>HTTP status returned by the downstream provider.</summary>
    public int StatusCode { get; } = statusCode;
}

/// <summary>Error mapped to the standard OpenAI API error envelope.</summary>
public sealed class OpenAiCompatibilityException(
    string type,
    string message,
    string? param,
    string? code,
    int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    /// <summary>OpenAI error type.</summary>
    public string Type { get; } = type;
    /// <summary>OpenAI parameter name associated with the error.</summary>
    public string? Param { get; } = param;
    /// <summary>OpenAI machine-readable error code.</summary>
    public string? Code { get; } = code;
    /// <summary>HTTP status code returned to the caller.</summary>
    public int StatusCode { get; } = statusCode;
}
