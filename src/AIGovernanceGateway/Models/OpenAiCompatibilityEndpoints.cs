using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;

namespace AIGovernanceGateway.Models;

/// <summary>Maps the OpenAI-compatible northbound model API.</summary>
public static class OpenAiCompatibilityEndpoints
{
    /// <summary>Maps OpenAI v1 Models, Chat Completions, and generic JSON inference endpoints.</summary>
    public static void MapOpenAiCompatibilityEndpoints(this WebApplication app, bool requireAuthorization)
    {
        var v1 = app.MapGroup("/v1");
        if (requireAuthorization)
        {
            v1.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = "OpenAiSmart" });
        }

        v1.MapGet("/models", ListModelsAsync);
        v1.MapGet("/models/{**model}", GetModelAsync);
        v1.MapPost("/chat/completions", ChatCompletionsAsync);
        v1.MapPost("/{**operation}", GenericOpenAiOperationAsync);
    }

    private static async Task ListModelsAsync(
        HttpContext context,
        OpenAiCompatibilityService service,
        CancellationToken cancellationToken)
    {
        SetRequestId(context);
        await WriteJsonAsync(
            context,
            await service.ListModelsAsync(context.User, cancellationToken),
            StatusCodes.Status200OK,
            cancellationToken);
    }

    private static async Task GetModelAsync(
        string model,
        HttpContext context,
        OpenAiCompatibilityService service,
        CancellationToken cancellationToken)
    {
        SetRequestId(context);
        try
        {
            await WriteJsonAsync(
                context,
                await service.GetModelAsync(context.User, model, cancellationToken),
                StatusCodes.Status200OK,
                cancellationToken);
        }
        catch (OpenAiCompatibilityException ex)
        {
            await WriteErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task GenericOpenAiOperationAsync(
        string operation,
        HttpContext context,
        OpenAiCompatibilityService service,
        ILogger<OpenAiCompatibilityService> logger,
        CancellationToken cancellationToken)
    {
        SetRequestId(context);

        JsonObject request;
        try
        {
            request = await ReadJsonObjectAsync(context, cancellationToken);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                context,
                new OpenAiCompatibilityException(
                    "invalid_request_error",
                    "The request body is not valid JSON.",
                    null,
                    null),
                cancellationToken);
            return;
        }

        if (request["stream"]?.GetValue<bool?>() == true)
        {
            await StreamGenericOpenAiOperationAsync(
                operation,
                context,
                service,
                request,
                logger,
                cancellationToken);
            return;
        }

        try
        {
            var response = await service.ForwardOpenAiOperationAsync(
                context.User,
                operation,
                request,
                cancellationToken);
            await WriteJsonAsync(context, response, StatusCodes.Status200OK, cancellationToken);
        }
        catch (OpenAiCompatibilityException ex)
        {
            await WriteErrorAsync(context, ex, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
        {
            logger.LogWarning(
                ex,
                "OpenAI-compatible operation {Operation} failed for model {Model}",
                operation,
                request["model"]);
            await WriteErrorAsync(
                context,
                new OpenAiCompatibilityException(
                    "api_error",
                    "The upstream model provider request failed.",
                    null,
                    "upstream_error",
                    StatusCodes.Status502BadGateway),
                cancellationToken);
        }
    }

    private static async Task StreamGenericOpenAiOperationAsync(
        string operation,
        HttpContext context,
        OpenAiCompatibilityService service,
        JsonObject request,
        ILogger<OpenAiCompatibilityService> logger,
        CancellationToken cancellationToken)
    {
        await using var enumerator = service.StreamOpenAiOperationAsync(
            context.User,
            operation,
            request,
            cancellationToken).GetAsyncEnumerator(cancellationToken);

        bool hasFirst;
        try
        {
            hasFirst = await enumerator.MoveNextAsync();
        }
        catch (OpenAiCompatibilityException ex)
        {
            await WriteErrorAsync(context, ex, cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
        {
            logger.LogWarning(
                ex,
                "OpenAI-compatible streaming operation {Operation} failed before response start for model {Model}",
                operation,
                request["model"]);
            await WriteErrorAsync(
                context,
                new OpenAiCompatibilityException(
                    "api_error",
                    "The upstream model provider request failed.",
                    null,
                    "upstream_error",
                    StatusCodes.Status502BadGateway),
                cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            if (hasFirst)
            {
                await WriteStreamRecordAsync(context.Response, enumerator.Current, cancellationToken);
            }

            while (await enumerator.MoveNextAsync())
            {
                await WriteStreamRecordAsync(context.Response, enumerator.Current, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnect is the cancellation mechanism for OpenAI SSE streams.
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or IOException or EndOfStreamException)
        {
            logger.LogWarning(
                ex,
                "OpenAI-compatible streaming operation {Operation} failed for model {Model}",
                operation,
                request["model"]);
        }
    }

    private static async Task ChatCompletionsAsync(
        HttpContext context,
        OpenAiCompatibilityService service,
        ILogger<OpenAiCompatibilityService> logger,
        CancellationToken cancellationToken)
    {
        SetRequestId(context);

        JsonObject request;
        try
        {
            request = await ReadJsonObjectAsync(context, cancellationToken);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                context,
                new OpenAiCompatibilityException(
                    "invalid_request_error",
                    "The request body is not valid JSON.",
                    null,
                    null),
                cancellationToken);
            return;
        }

        var stream = request["stream"]?.GetValue<bool?>() == true;
        if (stream)
        {
            await StreamChatCompletionsAsync(context, service, request, logger, cancellationToken);
            return;
        }

        try
        {
            var response = await service.CreateChatCompletionAsync(context.User, request, cancellationToken);
            await WriteJsonAsync(context, response, StatusCodes.Status200OK, cancellationToken);
        }
        catch (OpenAiCompatibilityException ex)
        {
            await WriteErrorAsync(context, ex, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "OpenAI-compatible chat completion failed for model {Model}", request["model"]);
            await WriteErrorAsync(
                context,
                new OpenAiCompatibilityException(
                    "api_error",
                    "The upstream model provider request failed.",
                    null,
                    "upstream_error",
                    StatusCodes.Status502BadGateway),
                cancellationToken);
        }
    }

    private static async Task StreamChatCompletionsAsync(
        HttpContext context,
        OpenAiCompatibilityService service,
        JsonObject request,
        ILogger<OpenAiCompatibilityService> logger,
        CancellationToken cancellationToken)
    {
        await using var enumerator = service.StreamChatCompletionAsync(
            context.User,
            request,
            cancellationToken).GetAsyncEnumerator(cancellationToken);

        bool hasFirst;
        try
        {
            // Resolve model authorization and establish the downstream stream before sending 200/SSE.
            hasFirst = await enumerator.MoveNextAsync();
        }
        catch (OpenAiCompatibilityException ex)
        {
            await WriteErrorAsync(context, ex, cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "OpenAI-compatible streaming request failed before response start for model {Model}", request["model"]);
            await WriteErrorAsync(
                context,
                new OpenAiCompatibilityException(
                    "api_error",
                    "The upstream model provider request failed.",
                    null,
                    "upstream_error",
                    StatusCodes.Status502BadGateway),
                cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            if (hasFirst)
            {
                await WriteChunkAsync(context.Response, enumerator.Current, cancellationToken);
            }

            while (await enumerator.MoveNextAsync())
            {
                await WriteChunkAsync(context.Response, enumerator.Current, cancellationToken);
            }

            await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnect is the cancellation mechanism for Chat Completions streams.
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or IOException or EndOfStreamException)
        {
            logger.LogWarning(ex, "OpenAI-compatible streaming request failed for model {Model}", request["model"]);
            if (!cancellationToken.IsCancellationRequested)
            {
                var error = ErrorEnvelope(
                    "The upstream model provider stream failed.",
                    "api_error",
                    null,
                    "upstream_error");
                await context.Response.WriteAsync("data: " + error.ToJsonString() + "\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken) as JsonObject
            ?? throw new JsonException("Request body must be a JSON object.");

    private static async Task WriteStreamRecordAsync(
        HttpResponse response,
        OpenAiCompatibilityService.StreamRecord record,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(record.EventName))
        {
            await response.WriteAsync($"event: {record.EventName}\n", cancellationToken);
        }

        await response.WriteAsync($"data: {record.Data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteChunkAsync(
        HttpResponse response,
        JsonObject chunk,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync("data: " + chunk.ToJsonString() + "\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        OpenAiCompatibilityException exception,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(
            context,
            ErrorEnvelope(exception.Message, exception.Type, exception.Param, exception.Code),
            exception.StatusCode,
            cancellationToken);
    }

    private static JsonObject ErrorEnvelope(string message, string type, string? param, string? code) =>
        new()
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = type,
                ["param"] = param,
                ["code"] = code
            }
        };

    private static async Task WriteJsonAsync(
        HttpContext context,
        JsonNode body,
        int statusCode,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(body.ToJsonString(), cancellationToken);
    }

    private static void SetRequestId(HttpContext context)
    {
        context.Response.Headers["x-request-id"] = "req_gw_" + context.TraceIdentifier.Replace(":", "", StringComparison.Ordinal);
    }
}