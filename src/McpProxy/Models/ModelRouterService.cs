using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpProxy.Data;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Models;

/// <summary>Northbound request accepted by the gateway's provider-neutral model endpoint.</summary>
public sealed record ModelChatRequest(
    string Model,
    string Prompt,
    string? SystemPrompt,
    Dictionary<string, JsonElement>? Parameters);

/// <summary>Provider-neutral response returned by the unified model endpoint.</summary>
public sealed record ModelChatResponse(
    string Model,
    string Provider,
    string DownstreamModel,
    string Text,
    string? FinishReason,
    JsonNode? Usage);

/// <summary>Routes provider-neutral model requests to provider-specific HTTP protocols.</summary>
public sealed class ModelRouterService(
    ProxyDbContext db,
    IPermissionService permissions,
    IHttpClientFactory httpClientFactory)
{
    /// <summary>Lists public model aliases visible to the caller.</summary>
    public async Task<IReadOnlyList<object>> ListAsync(
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var accessible = await permissions.GetAccessibleModelRouteIdsAsync(user, cancellationToken);
        if (accessible.Count == 0)
        {
            return [];
        }

        return await db.ModelRoutes.AsNoTracking()
            .Where(x => x.Enabled && x.Provider.Enabled && accessible.Contains(x.Id))
            .OrderBy(x => x.PublicName)
            .Select(x => (object)new
            {
                name = x.PublicName,
                provider = x.Provider.Name,
                providerKind = x.Provider.Kind.ToString()
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>Invokes a public model alias after RBAC authorization.</summary>
    public async Task<ModelChatResponse> ChatAsync(
        System.Security.Claims.ClaimsPrincipal user,
        ModelChatRequest request,
        CancellationToken cancellationToken)
    {
        var route = await db.ModelRoutes.AsNoTracking()
            .Include(x => x.Provider)
            .SingleOrDefaultAsync(x => x.PublicName == request.Model && x.Enabled && x.Provider.Enabled, cancellationToken);

        if (route is null ||
            !await permissions.CanAccessModelRouteAsync(user, route.Id, cancellationToken))
        {
            throw new KeyNotFoundException("Model not found.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(request));
        }

        return route.Provider.Kind switch
        {
            ModelProviderKind.OpenAiCompatible => await ChatOpenAiCompatibleAsync(route, request, cancellationToken),
            ModelProviderKind.Ollama => await ChatOllamaAsync(route, request, cancellationToken),
            ModelProviderKind.AwsBedrock => await ChatBedrockAsync(route, request, cancellationToken),
            ModelProviderKind.GenericHttp => throw new InvalidOperationException(
                $"Provider '{route.Provider.Name}' is native-proxy only and has no unified chat adapter."),
            _ => throw new InvalidOperationException($"Unsupported model provider kind '{route.Provider.Kind}'.")
        };
    }

    private async Task<ModelChatResponse> ChatOpenAiCompatibleAsync(
        ModelRoute route,
        ModelChatRequest request,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = route.DownstreamModel,
            ["stream"] = false,
            ["messages"] = BuildMessages(request)
        };
        ApplyParameters(body, request.Parameters, ["model", "messages", "stream"]);

        var provider = route.Provider;
        var path = string.IsNullOrWhiteSpace(provider.ChatPath) ? "/v1/chat/completions" : provider.ChatPath!;
        var response = await SendJsonAsync(provider, BuildProviderUri(provider, path), body, false, cancellationToken);
        var text = response["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? ExtractTextArray(response["choices"]?[0]?["message"]?["content"])
            ?? throw new InvalidOperationException("Downstream OpenAI-compatible response did not contain assistant text.");

        return new ModelChatResponse(
            route.PublicName,
            provider.Name,
            route.DownstreamModel,
            text,
            response["choices"]?[0]?["finish_reason"]?.GetValue<string>(),
            response["usage"]?.DeepClone());
    }

    private async Task<ModelChatResponse> ChatOllamaAsync(
        ModelRoute route,
        ModelChatRequest request,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = route.DownstreamModel,
            ["stream"] = false,
            ["messages"] = BuildMessages(request)
        };

        if (request.Parameters is { Count: > 0 })
        {
            var options = new JsonObject();
            foreach (var pair in request.Parameters)
            {
                if (pair.Key.Equals("stream", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("model", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("messages", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (pair.Key is "format" or "keep_alive" or "think" or "tools")
                {
                    body[pair.Key] = ParseElement(pair.Value);
                }
                else if (pair.Key == "options" && pair.Value.ValueKind == JsonValueKind.Object)
                {
                    body["options"] = ParseElement(pair.Value);
                }
                else
                {
                    options[pair.Key] = ParseElement(pair.Value);
                }
            }

            if (options.Count > 0 && body["options"] is null)
            {
                body["options"] = options;
            }
        }

        var provider = route.Provider;
        var path = string.IsNullOrWhiteSpace(provider.ChatPath) ? "/api/chat" : provider.ChatPath!;
        var response = await SendJsonAsync(provider, BuildProviderUri(provider, path), body, false, cancellationToken);
        var text = response["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Downstream Ollama response did not contain assistant text.");

        JsonNode? usage = null;
        if (response["prompt_eval_count"] is not null || response["eval_count"] is not null)
        {
            usage = new JsonObject
            {
                ["promptTokens"] = response["prompt_eval_count"]?.DeepClone(),
                ["completionTokens"] = response["eval_count"]?.DeepClone()
            };
        }

        return new ModelChatResponse(
            route.PublicName,
            provider.Name,
            route.DownstreamModel,
            text,
            response["done_reason"]?.GetValue<string>(),
            usage);
    }

    private async Task<ModelChatResponse> ChatBedrockAsync(
        ModelRoute route,
        ModelChatRequest request,
        CancellationToken cancellationToken)
    {
        var provider = route.Provider;
        var credentials = ModelCredentialResolver.ResolveAwsCredentials(provider);
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { new JsonObject { ["text"] = request.Prompt } }
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["system"] = new JsonArray { new JsonObject { ["text"] = request.SystemPrompt } };
        }

        var inference = new JsonObject();
        var additional = new JsonObject();
        if (request.Parameters is not null)
        {
            foreach (var pair in request.Parameters)
            {
                var normalized = pair.Key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                switch (normalized)
                {
                    case "maxtokens":
                        inference["maxTokens"] = ParseElement(pair.Value);
                        break;
                    case "temperature":
                        inference["temperature"] = ParseElement(pair.Value);
                        break;
                    case "topp":
                        inference["topP"] = ParseElement(pair.Value);
                        break;
                    case "stop":
                    case "stopsequences":
                        inference["stopSequences"] = ParseElement(pair.Value);
                        break;
                    default:
                        additional[pair.Key] = ParseElement(pair.Value);
                        break;
                }
            }
        }

        if (inference.Count > 0) body["inferenceConfig"] = inference;
        if (additional.Count > 0) body["additionalModelRequestFields"] = additional;

        var endpoint = string.IsNullOrWhiteSpace(provider.BaseEndpoint)
            ? $"https://bedrock-runtime.{credentials.Region}.amazonaws.com"
            : provider.BaseEndpoint;
        var uri = new Uri(
            endpoint.TrimEnd('/') + "/model/" + Uri.EscapeDataString(route.DownstreamModel) + "/converse",
            UriKind.Absolute);

        var response = await SendJsonAsync(provider, uri, body, true, cancellationToken);
        var content = response["output"]?["message"]?["content"] as JsonArray;
        var text = content is null
            ? null
            : string.Concat(content
                .Select(x => x?["text"]?.GetValue<string>())
                .Where(x => !string.IsNullOrEmpty(x)));

        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Downstream Bedrock response did not contain assistant text.");
        }

        return new ModelChatResponse(
            route.PublicName,
            provider.Name,
            route.DownstreamModel,
            text,
            response["stopReason"]?.GetValue<string>(),
            response["usage"]?.DeepClone());
    }

    private async Task<JsonObject> SendJsonAsync(
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
            var bearerHeaders = ModelCredentialResolver.ResolveBedrockBearerHeaders(provider);
            if (bearerHeaders is null)
            {
                var credentials = ModelCredentialResolver.ResolveAwsCredentials(provider);
                AwsSigV4Signer.Sign(request, payload, credentials, "bedrock", DateTimeOffset.UtcNow);
            }
            else
            {
                foreach (var header in bearerHeaders)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
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
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var safeBody = text.Length > 2048 ? text[..2048] : text;
            throw new InvalidOperationException(
                $"Downstream model provider returned {(int)response.StatusCode} {response.ReasonPhrase}: {safeBody}");
        }

        try
        {
            return JsonNode.Parse(text)?.AsObject()
                ?? throw new InvalidOperationException("Downstream model provider returned an empty JSON response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Downstream model provider returned invalid JSON.", ex);
        }
    }

    private static JsonArray BuildMessages(ModelChatRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
        return messages;
    }

    private static void ApplyParameters(JsonObject body, Dictionary<string, JsonElement>? parameters, string[] reserved)
    {
        if (parameters is null) return;
        foreach (var pair in parameters)
        {
            if (reserved.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            body[pair.Key] = ParseElement(pair.Value);
        }
    }

    private static JsonNode? ParseElement(JsonElement value) =>
        JsonNode.Parse(value.GetRawText());

    private static string? ExtractTextArray(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        return string.Concat(array
            .Select(x => x?["text"]?.GetValue<string>())
            .Where(x => !string.IsNullOrEmpty(x)));
    }

    internal static Uri BuildProviderUri(ModelProvider provider, string path)
    {
        if (!Uri.TryCreate(provider.BaseEndpoint, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Model provider '{provider.Name}' has an invalid HTTP base endpoint.");
        }

        var root = baseUri.ToString().TrimEnd('/');
        var suffix = path.StartsWith('/') ? path : "/" + path;
        return new Uri(root + suffix, UriKind.Absolute);
    }
}
