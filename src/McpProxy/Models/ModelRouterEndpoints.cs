using McpProxy.Security;

namespace McpProxy.Models;

/// <summary>Maps the gateway's provider-neutral and native model-routing HTTP surface.</summary>
public static class ModelRouterEndpoints
{
    private static readonly string[] NativeMethods =
    [
        HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch,
        HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options
    ];

    /// <summary>Maps model discovery, unified chat, and native provider pass-through endpoints.</summary>
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

        models.MapPost("/chat", async (
            ModelChatRequest request,
            HttpContext context,
            ModelRouterService router,
            ILogger<ModelRouterService> logger,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await router.ChatAsync(context.User, request, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Model not found." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Unified model request failed for public model {Model}", request.Model);
                return Results.Problem(
                    detail: "The downstream model provider request failed.",
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Model provider request failed");
            }
        });

        models.MapMethods("/native/{providerScope}", NativeMethods, ProxyNativeAsync);
        models.MapMethods("/native/{providerScope}/{**path}", NativeMethods, ProxyNativeAsync);
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
