using System.Diagnostics;
using AIGovernanceGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AIGovernanceGateway.Telemetry;

/// <summary>
/// Captures outbound HTTP requests and downstream response metadata. Finite, non-streaming response
/// bodies are captured when Content-Length is known; protocol/model semantic outputs are also logged
/// by their owning services so long-lived streams are never buffered by telemetry.
/// </summary>
public sealed class GatewayTelemetryHttpHandler(
    GatewayTelemetry telemetry,
    IOptions<LoggingOptions> options) : DelegatingHandler
{
    private readonly LoggingOptions _options = options.Value;

    /// <summary>Captures one outbound HTTP exchange and forwards it to the configured primary handler.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!_options.TelemetryEnabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        byte[] requestBytes = [];
        if (_options.CaptureRequestBodies && request.Content is not null)
        {
            requestBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        telemetry.Record("http.downstream.request", new
        {
            direction = "southbound",
            method = request.Method.Method,
            uri = request.RequestUri?.ToString(),
            version = request.Version.ToString(),
            headers = _options.CaptureHeaders ? SanitizeRequestHeaders(request) : null,
            body = _options.CaptureRequestBodies
                ? TelemetrySanitizer.FormatBody(
                    Limit(requestBytes, out var requestTruncated),
                    requestBytes.LongLength,
                    request.Content?.Headers.ContentType?.ToString(),
                    requestTruncated,
                    _options)
                : null
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            object? responseBody = null;
            if (_options.CaptureResponseBodies &&
                response.Content is not null &&
                response.Content.Headers.ContentLength is long length &&
                length >= 0 &&
                length <= EffectiveMaxPayloadBytes() &&
                response.Content.Headers.ContentType?.MediaType?.Equals(
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase) != true)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                responseBody = TelemetrySanitizer.FormatBody(
                    bytes,
                    bytes.LongLength,
                    response.Content.Headers.ContentType?.ToString(),
                    false,
                    _options);
            }

            telemetry.Record("http.downstream.response", new
            {
                direction = "southbound",
                method = request.Method.Method,
                uri = request.RequestUri?.ToString(),
                statusCode = (int)response.StatusCode,
                reasonPhrase = response.ReasonPhrase,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                headers = _options.CaptureHeaders ? SanitizeResponseHeaders(response) : null,
                body = responseBody,
                bodyDeferredToSemanticTelemetry = responseBody is null && response.Content is not null
            });

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            telemetry.Record("http.downstream.exception", new
            {
                direction = "southbound",
                method = request.Method.Method,
                uri = request.RequestUri?.ToString(),
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds
            }, LogLevel.Error, ex);
            throw;
        }
    }

    private Dictionary<string, object?> SanitizeRequestHeaders(HttpRequestMessage request)
    {
        var all = request.Headers
            .Select(x => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(x.Key, x.Value.ToArray()))
            .Concat(request.Content?.Headers.Select(x =>
                new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(x.Key, x.Value.ToArray()))
                ?? []);
        return TelemetrySanitizer.SanitizeHeaders(all, _options);
    }

    private Dictionary<string, object?> SanitizeResponseHeaders(HttpResponseMessage response)
    {
        var all = response.Headers
            .Select(x => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(x.Key, x.Value.ToArray()))
            .Concat(response.Content.Headers.Select(x =>
                new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(x.Key, x.Value.ToArray())));
        return TelemetrySanitizer.SanitizeHeaders(all, _options);
    }

    private byte[] Limit(byte[] bytes, out bool truncated)
    {
        var max = EffectiveMaxPayloadBytes();
        truncated = bytes.LongLength > max;
        return truncated ? bytes[..max] : bytes;
    }

    private int EffectiveMaxPayloadBytes() => _options.MaxPayloadBytes <= 0 ? int.MaxValue : _options.MaxPayloadBytes;
}