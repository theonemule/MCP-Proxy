using System.Text.Json;
using AIGovernanceGateway.Security;

namespace AIGovernanceGateway.Models;

/// <summary>Maps the gateway's provider-neutral and native model-routing HTTP surface.</summary>
public static class ModelRouterEndpoints
{
    private static readonly string[] NativeMethods =
    [
        HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch,
        HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options
    ];

    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Maps model discovery, unified chat, streaming chat, and native provider pass-through endpoints.</summary>
    public static void MapModelRouterEndpoints(this WebApplication app, bool requireAuthorization)
    {
        var models = app.MapGroup("/models");
        if (requireAuthorization)
        {
            models.RequireAuthorization();
        }

        models.MapGet("/", async (
            HttpContext context,
            ModelRouterService router,
            CancellationToken ct) =>
            Results.Ok(await router.ListAsync(context.User, ct)));

        // POST /models/chat supports both buffered JSON and SSE by setting "stream": true.
        models.MapPost("/chat", HandleChatAsync);

        // Explicit streaming alias for clients that prefer the transport semantics in the URL.
        models.MapPost("/chat/stream", HandleStreamingChatAsync);

        models.MapMethods("/native/{providerScope}", NativeMethods, ProxyNativeAsync);
        models.MapMethods("/native/{providerScope}/{**path}", NativeMethods, ProxyNativeAsync);
    }

    private static async Task HandleChatAsync(
        ModelChatRequest request,
        HttpContext context,
        ModelRouterService router,
        ILogger<ModelRouterService> logger,
        CancellationToken ct)
    {
        if (request.Stream)
        {
            await WriteStreamingChatAsync(request, context, router, logger, ct);
            return;
        }

        try
        {
            var response = await router.ChatAsync(context.User, request, ct);
            await context.Response.WriteAsJsonAsync(response, cancellationToken: ct);
        }
        catch (KeyNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "Model not found." }, cancellationToken: ct);
        }
        catch (ArgumentException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken: ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Unified model request failed for public model {Model}", request.Model);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(
                new { error = "The downstream model provider request failed." },
                cancellationToken: ct);
        }
    }

    private static Task HandleStreamingChatAsync(
        ModelChatRequest request,
        HttpContext context,
        ModelRouterService router,
        ILogger<ModelRouterService> logger,
        CancellationToken ct) =>
        WriteStreamingChatAsync(request with { Stream = true }, context, router, logger, ct);

    private static async Task WriteStreamingChatAsync(
        ModelChatRequest request,
        HttpContext context,
        ModelRouterService router,
        ILogger<ModelRouterService> logger,
        CancellationToken ct)
    {
        await using var enumerator = router.StreamChatAsync(context.User, request, ct).GetAsyncEnumerator(ct);

        try
        {
            // Resolve authorization and input validation before committing an SSE response.
            if (!await enumerator.MoveNextAsync())
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(
                    new { error = "The downstream model provider returned no stream." },
                    cancellationToken: ct);
                return;
            }
        }
        catch (KeyNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "Model not found." }, cancellationToken: ct);
            return;
        }
        catch (ArgumentException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken: ct);
            return;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Streaming model request failed before start for public model {Model}", request.Model);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(
                new { error = "The downstream model provider request failed." },
                cancellationToken: ct);
            return;
        }

        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        await WriteSseEventAsync(context.Response, enumerator.Current, ct);

        try
        {
            while (await enumerator.MoveNextAsync())
            {
                await WriteSseEventAsync(context.Response, enumerator.Current, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closing the HTTP response is the cancellation mechanism for streaming callers.
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or IOException or EndOfStreamException)
        {
            logger.LogWarning(ex, "Streaming model request failed for public model {Model}", request.Model);
            if (!ct.IsCancellationRequested)
            {
                await WriteRawSseEventAsync(
                    context.Response,
                    "error",
                    JsonSerializer.Serialize(new
                    {
                        model = request.Model,
                        error = "The downstream model provider stream failed."
                    }, StreamJsonOptions),
                    ct);
            }
        }
    }

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        ModelChatStreamEvent item,
        CancellationToken cancellationToken)
    {
        await WriteRawSseEventAsync(
            response,
            item.Event,
            JsonSerializer.Serialize(item, StreamJsonOptions),
            cancellationToken);
    }

    private static async Task WriteRawSseEventAsync(
        HttpResponse response,
        string eventName,
        string json,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task ProxyNativeAsync(
        string providerScope,
        string? path,
        HttpContext context,
        NativeModelProxyService proxy,
        ILogger<NativeModelProxyService> logger,
        CancellationToken ct)
    {
        try
        {
            await proxy.ProxyAsync(context, providerScope, path, ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Native model proxy request failed for provider scope {ProviderScope}", providerScope);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(
                    new { error = "The downstream model provider request failed." },
                    cancellationToken: ct);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Native model proxy HTTP request failed for provider scope {ProviderScope}", providerScope);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(
                    new { error = "The downstream model provider request failed." },
                    cancellationToken: ct);
            }
        }
    }
}