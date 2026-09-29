using System.Diagnostics;
using System.Text.Json;
using AIGovernanceGateway.Configuration;
using AIGovernanceGateway.Guardrails;
using Microsoft.Extensions.Options;

namespace AIGovernanceGateway.Telemetry;

/// <summary>
/// Captures northbound HTTP request/response telemetry and enforces optional model-based input/output
/// guardrails. Output governance intentionally buffers governed responses so blocked content is never
/// partially released to the caller.
/// </summary>
public sealed class GatewayTelemetryMiddleware(
    RequestDelegate next,
    IOptions<LoggingOptions> options,
    IOptions<GuardrailsOptions> guardrailOptions)
{
    /// <summary>HttpContext.Items key holding the correlation id for this request.</summary>
    public const string CorrelationItemKey = "ai-governance-gateway.correlation-id";

    private readonly LoggingOptions _options = options.Value;
    private readonly GuardrailsOptions _guardrails = guardrailOptions.Value;

    /// <summary>Captures and optionally governs one complete HTTP transaction.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        GatewayTelemetry telemetry,
        GuardrailService guardrails)
    {
        var governed = guardrails.AppliesTo(context.Request.Path);
        if (!_options.TelemetryEnabled && !governed)
        {
            await next(context);
            return;
        }

        var correlationId = ResolveCorrelationId(context);
        context.Items[CorrelationItemKey] = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId;

        var evaluateInput = governed && _guardrails.EvaluateInputs;
        var captureRequest = (_options.TelemetryEnabled && _options.CaptureRequestBodies) || evaluateInput;
        CapturedBody? requestBody = null;
        if (captureRequest)
        {
            requestBody = await ReadRequestBodyAsync(
                context.Request,
                RequestCaptureLimit(evaluateInput),
                context.RequestAborted);
        }

        if (_options.TelemetryEnabled)
        {
            telemetry.Record("http.request", new
            {
                direction = "northbound",
                method = context.Request.Method,
                scheme = context.Request.Scheme,
                host = context.Request.Host.ToString(),
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                protocol = context.Request.Protocol,
                contentType = context.Request.ContentType,
                contentLength = context.Request.ContentLength,
                headers = _options.CaptureHeaders
                    ? TelemetrySanitizer.SanitizeHeaders(context.Request.Headers, _options)
                    : null,
                body = requestBody is null || !_options.CaptureRequestBodies
                    ? null
                    : FormatForTelemetry(requestBody, context.Request.ContentType)
            });
        }

        if (evaluateInput && requestBody is not null && requestBody.TotalBytes > 0)
        {
            if (_guardrails.BlockOversizedInputs &&
                _guardrails.MaxEvaluationBytes > 0 &&
                requestBody.TotalBytes > _guardrails.MaxEvaluationBytes)
            {
                var decision = OversizedDecision(
                    GuardrailStage.Input,
                    requestBody.TotalBytes,
                    _guardrails.MaxEvaluationBytes);
                telemetry.Record("guardrail.blocked", new
                {
                    stage = "input",
                    decision.RiskScore,
                    decision.Reason,
                    decision.Categories,
                    oversized = true,
                    payloadBytes = requestBody.TotalBytes,
                    maxEvaluationBytes = _guardrails.MaxEvaluationBytes
                }, LogLevel.Warning);

                var refusal = await WriteRefusalAsync(
                    context,
                    GuardrailStage.Input,
                    decision,
                    correlationId,
                    context.RequestAborted);
                LogImmediateResponse(telemetry, context, refusal, correlationId);
                return;
            }

            var inputDecision = await guardrails.EvaluateAsync(
                GuardrailStage.Input,
                requestBody.Bytes,
                context.Request.ContentType,
                new
                {
                    method = context.Request.Method,
                    path = context.Request.Path.Value,
                    query = context.Request.QueryString.Value,
                    contentType = context.Request.ContentType,
                    contentLength = context.Request.ContentLength,
                    totalBytes = requestBody.TotalBytes,
                    captureTruncated = requestBody.Truncated,
                    headers = _options.CaptureHeaders
                        ? TelemetrySanitizer.SanitizeHeaders(context.Request.Headers, _options)
                        : null
                },
                context.RequestAborted);

            if (!inputDecision.Allowed)
            {
                var refusal = await WriteRefusalAsync(
                    context,
                    GuardrailStage.Input,
                    inputDecision,
                    correlationId,
                    context.RequestAborted);
                LogImmediateResponse(telemetry, context, refusal, correlationId);
                return;
            }
        }

        var evaluateOutput = governed && _guardrails.EvaluateOutputs;
        var originalBody = context.Response.Body;
        TelemetryCaptureWriteStream? telemetryCapture = null;
        GuardrailBufferingWriteStream? guardrailBuffer = null;

        if (evaluateOutput)
        {
            var bufferLimit = _guardrails.BlockOversizedResponses && _guardrails.MaxBufferedResponseBytes > 0
                ? _guardrails.MaxBufferedResponseBytes
                : 0;
            guardrailBuffer = new GuardrailBufferingWriteStream(bufferLimit);
            context.Response.Body = guardrailBuffer;
        }
        else if (_options.TelemetryEnabled && _options.CaptureResponseBodies)
        {
            telemetryCapture = new TelemetryCaptureWriteStream(
                originalBody,
                _options.MaxPayloadBytes);
            context.Response.Body = telemetryCapture;
        }

        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        byte[]? finalResponseBytes = null;
        long finalResponseTotalBytes = 0;
        bool finalResponseTruncated = false;

        try
        {
            await next(context);

            if (guardrailBuffer is not null)
            {
                context.Response.Body = originalBody;

                if (guardrailBuffer.Oversized && _guardrails.BlockOversizedResponses)
                {
                    var decision = OversizedDecision(
                        GuardrailStage.Output,
                        guardrailBuffer.TotalBytes,
                        _guardrails.MaxBufferedResponseBytes);
                    telemetry.Record("guardrail.blocked", new
                    {
                        stage = "output",
                        decision.RiskScore,
                        decision.Reason,
                        decision.Categories,
                        oversized = true,
                        payloadBytes = guardrailBuffer.TotalBytes,
                        maxBufferedResponseBytes = _guardrails.MaxBufferedResponseBytes
                    }, LogLevel.Warning);

                    finalResponseBytes = await WriteRefusalAsync(
                        context,
                        GuardrailStage.Output,
                        decision,
                        correlationId,
                        context.RequestAborted);
                    finalResponseTotalBytes = finalResponseBytes.LongLength;
                }
                else
                {
                    var buffered = guardrailBuffer.GetCapturedBytes();

                    if (_guardrails.BlockOversizedResponses &&
                        _guardrails.MaxEvaluationBytes > 0 &&
                        guardrailBuffer.TotalBytes > _guardrails.MaxEvaluationBytes)
                    {
                        var decision = OversizedDecision(
                            GuardrailStage.Output,
                            guardrailBuffer.TotalBytes,
                            _guardrails.MaxEvaluationBytes);
                        telemetry.Record("guardrail.blocked", new
                        {
                            stage = "output",
                            decision.RiskScore,
                            decision.Reason,
                            decision.Categories,
                            oversized = true,
                            payloadBytes = guardrailBuffer.TotalBytes,
                            maxEvaluationBytes = _guardrails.MaxEvaluationBytes
                        }, LogLevel.Warning);

                        finalResponseBytes = await WriteRefusalAsync(
                            context,
                            GuardrailStage.Output,
                            decision,
                            correlationId,
                            context.RequestAborted);
                        finalResponseTotalBytes = finalResponseBytes.LongLength;
                    }
                    else
                    {
                        var outputDecision = await guardrails.EvaluateAsync(
                            GuardrailStage.Output,
                            buffered,
                            context.Response.ContentType,
                            new
                            {
                                method = context.Request.Method,
                                path = context.Request.Path.Value,
                                statusCode = context.Response.StatusCode,
                                contentType = context.Response.ContentType,
                                contentLength = context.Response.ContentLength,
                                totalBytes = guardrailBuffer.TotalBytes,
                                headers = _options.CaptureHeaders
                                    ? TelemetrySanitizer.SanitizeHeaders(context.Response.Headers, _options)
                                    : null
                            },
                            context.RequestAborted);

                        if (!outputDecision.Allowed)
                        {
                            finalResponseBytes = await WriteRefusalAsync(
                                context,
                                GuardrailStage.Output,
                                outputDecision,
                                correlationId,
                                context.RequestAborted);
                            finalResponseTotalBytes = finalResponseBytes.LongLength;
                        }
                        else
                        {
                            finalResponseBytes = buffered;
                            finalResponseTotalBytes = guardrailBuffer.TotalBytes;
                            await originalBody.WriteAsync(buffered, context.RequestAborted);
                        }
                    }
                }
            }
            else if (telemetryCapture is not null)
            {
                finalResponseBytes = telemetryCapture.GetCapturedBytes();
                finalResponseTotalBytes = telemetryCapture.TotalBytes;
                finalResponseTruncated = telemetryCapture.Truncated;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            context.Response.Body = originalBody;
            telemetry.Record("http.exception", new
            {
                direction = "northbound",
                method = context.Request.Method,
                path = context.Request.Path.Value,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds
            }, LogLevel.Error, ex);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            context.Response.Body = originalBody;

            if (_options.TelemetryEnabled)
            {
                telemetry.Record("http.response", new
                {
                    direction = "northbound",
                    method = context.Request.Method,
                    path = context.Request.Path.Value,
                    statusCode = context.Response.StatusCode,
                    contentType = context.Response.ContentType,
                    contentLength = context.Response.ContentLength,
                    elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                    failed = failure is not null,
                    headers = _options.CaptureHeaders
                        ? TelemetrySanitizer.SanitizeHeaders(context.Response.Headers, _options)
                        : null,
                    body = !_options.CaptureResponseBodies || finalResponseBytes is null
                        ? null
                        : TelemetrySanitizer.FormatBody(
                            LimitForTelemetry(finalResponseBytes, out var logTruncated),
                            finalResponseTotalBytes == 0 ? finalResponseBytes.LongLength : finalResponseTotalBytes,
                            context.Response.ContentType,
                            finalResponseTruncated || logTruncated,
                            _options)
                }, failure is null ? LogLevel.Information : LogLevel.Error, failure);
            }

            if (telemetryCapture is not null)
            {
                await telemetryCapture.DisposeAsync();
            }

            if (guardrailBuffer is not null)
            {
                await guardrailBuffer.DisposeAsync();
            }
        }
    }

    private string ResolveCorrelationId(HttpContext context)
    {
        if (_options.AcceptInboundCorrelationId &&
            context.Request.Headers.TryGetValue("X-Correlation-ID", out var existing) &&
            !string.IsNullOrWhiteSpace(existing))
        {
            return existing.ToString();
        }

        if (Activity.Current?.TraceId.ToString() is { Length: > 0 } traceId)
        {
            return traceId;
        }

        return Guid.NewGuid().ToString("n");
    }

    private int RequestCaptureLimit(bool evaluateInput)
    {
        var loggingLimit = _options.CaptureRequestBodies ? NormalizeLimit(_options.MaxPayloadBytes) : 0;
        var guardrailLimit = evaluateInput ? NormalizeLimit(_guardrails.MaxEvaluationBytes) : 0;
        if (loggingLimit == int.MaxValue || guardrailLimit == int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(loggingLimit, guardrailLimit);
    }

    private object? FormatForTelemetry(CapturedBody body, string? contentType)
    {
        var bytes = LimitForTelemetry(body.Bytes, out var additionalTruncation);
        return TelemetrySanitizer.FormatBody(
            bytes,
            body.TotalBytes,
            contentType,
            body.Truncated || additionalTruncation,
            _options);
    }

    private byte[] LimitForTelemetry(byte[] bytes, out bool truncated)
    {
        var max = NormalizeLimit(_options.MaxPayloadBytes);
        truncated = bytes.LongLength > max;
        return truncated ? bytes[..max] : bytes;
    }

    private static int NormalizeLimit(int value) => value <= 0 ? int.MaxValue : value;

    private static GuardrailDecision OversizedDecision(
        GuardrailStage stage,
        long actualBytes,
        long configuredLimit) =>
        new(
            Allowed: false,
            RiskScore: 100,
            Action: "block",
            Reason: $"{stage} payload exceeds the configured governance evaluation limit.",
            Categories: ["payload_too_large"],
            EvaluatorModel: "",
            EvaluatorProvider: "");

    private async Task<CapturedBody> ReadRequestBodyAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (request.Body == Stream.Null || request.ContentLength is 0)
        {
            return new CapturedBody([], 0, false);
        }

        request.EnableBuffering();
        request.Body.Position = 0;

        using var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        long total = 0;
        var truncated = false;

        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            var remaining = maxBytes - (int)Math.Min(memory.Length, int.MaxValue);
            if (remaining > 0)
            {
                var copy = Math.Min(read, remaining);
                await memory.WriteAsync(buffer.AsMemory(0, copy), cancellationToken);
                if (copy < read)
                {
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }
        }

        request.Body.Position = 0;
        return new CapturedBody(memory.ToArray(), total, truncated);
    }

    private async Task<byte[]> WriteRefusalAsync(
        HttpContext context,
        GuardrailStage stage,
        GuardrailDecision decision,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var statusCode = stage == GuardrailStage.Input
            ? _guardrails.InputRefusalStatusCode
            : _guardrails.OutputRefusalStatusCode;
        if (statusCode is < 400 or > 599)
        {
            statusCode = StatusCodes.Status403Forbidden;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = new
            {
                message = decision.Reason,
                type = "guardrail_refusal",
                param = (string?)null,
                code = "guardrail_refusal",
                stage = stage.ToString().ToLowerInvariant(),
                risk_score = decision.RiskScore,
                categories = decision.Categories,
                evaluation_failed = decision.EvaluationFailed,
                correlation_id = correlationId
            }
        });

        context.Response.Headers.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = payload.LongLength;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await context.Response.Body.WriteAsync(payload, cancellationToken);
        return payload;
    }

    private void LogImmediateResponse(
        GatewayTelemetry telemetry,
        HttpContext context,
        byte[] body,
        string correlationId)
    {
        if (!_options.TelemetryEnabled)
        {
            return;
        }

        telemetry.Record("http.response", new
        {
            direction = "northbound",
            method = context.Request.Method,
            path = context.Request.Path.Value,
            statusCode = context.Response.StatusCode,
            contentType = context.Response.ContentType,
            contentLength = body.LongLength,
            guardrailRefusal = true,
            correlationId,
            headers = _options.CaptureHeaders
                ? TelemetrySanitizer.SanitizeHeaders(context.Response.Headers, _options)
                : null,
            body = _options.CaptureResponseBodies
                ? TelemetrySanitizer.FormatBody(
                    LimitForTelemetry(body, out var truncated),
                    body.LongLength,
                    context.Response.ContentType,
                    truncated,
                    _options)
                : null
        }, LogLevel.Warning);
    }

    private sealed record CapturedBody(byte[] Bytes, long TotalBytes, bool Truncated);
}

/// <summary>Write-through stream that captures response bytes while preserving streaming behavior.</summary>
internal sealed class TelemetryCaptureWriteStream(Stream inner, int maxBytes) : Stream
{
    private readonly MemoryStream _capture = new();
    private readonly int _maxBytes = maxBytes <= 0 ? int.MaxValue : maxBytes;

    public long TotalBytes { get; private set; }
    public bool Truncated { get; private set; }

    public byte[] GetCapturedBytes() => _capture.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        TotalBytes += bytes.Length;
        var remaining = _maxBytes - (int)_capture.Length;
        if (remaining <= 0)
        {
            Truncated = true;
            return;
        }

        var count = Math.Min(remaining, bytes.Length);
        _capture.Write(bytes[..count]);
        if (count < bytes.Length)
        {
            Truncated = true;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Buffers a governed response until its output policy decision is known.</summary>
internal sealed class GuardrailBufferingWriteStream(int maxBytes) : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly int _maxBytes = maxBytes <= 0 ? int.MaxValue : maxBytes;

    public long TotalBytes { get; private set; }
    public bool Oversized { get; private set; }

    public byte[] GetCapturedBytes() => _buffer.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count) =>
        Capture(buffer.AsSpan(offset, count));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        return ValueTask.CompletedTask;
    }

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        TotalBytes += bytes.Length;
        var remaining = _maxBytes - (int)_buffer.Length;
        if (remaining <= 0)
        {
            Oversized = true;
            return;
        }

        var count = Math.Min(remaining, bytes.Length);
        _buffer.Write(bytes[..count]);
        if (count < bytes.Length)
        {
            Oversized = true;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}