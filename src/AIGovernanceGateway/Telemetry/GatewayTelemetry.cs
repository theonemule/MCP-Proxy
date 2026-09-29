using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIGovernanceGateway.Configuration;
using AIGovernanceGateway.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace AIGovernanceGateway.Telemetry;

/// <summary>
/// Emits structured, sink-neutral telemetry through Microsoft.Extensions.Logging. The application
/// intentionally owns no telemetry database; any ILogger provider can persist or export these events.
/// </summary>
public sealed class GatewayTelemetry(
    ILogger<GatewayTelemetry> logger,
    IOptions<LoggingOptions> options,
    IHttpContextAccessor httpContextAccessor)
{
    private readonly LoggingOptions _options = options.Value;

    /// <summary>Writes one structured telemetry envelope to the configured ILogger provider.</summary>
    public void Record(
        string eventName,
        object? data = null,
        LogLevel level = LogLevel.Information,
        Exception? exception = null)
    {
        if (!_options.TelemetryEnabled || !logger.IsEnabled(level))
        {
            return;
        }

        var context = httpContextAccessor.HttpContext;
        var activity = Activity.Current;
        var correlationId = context?.Items[GatewayTelemetryMiddleware.CorrelationItemKey]?.ToString()
            ?? context?.TraceIdentifier
            ?? (activity?.TraceId.ToString() is { Length: > 0 } trace ? trace : null);

        var envelope = new
        {
            timestamp = DateTimeOffset.UtcNow,
            eventName,
            correlationId,
            traceId = activity?.TraceId.ToString(),
            spanId = activity?.SpanId.ToString(),
            requestId = context?.TraceIdentifier,
            principal = Principal(context?.User),
            data = TelemetrySanitizer.SanitizeObject(data, _options)
        };

        var json = JsonSerializer.Serialize(envelope, TelemetrySanitizer.SerializerOptions);
        logger.Log(
            level,
            new EventId(TelemetryEventIds.StructuredTelemetry, eventName),
            exception,
            "GatewayTelemetry {TelemetryEvent} {TelemetryPayload}",
            eventName,
            json);
    }

    private static object? Principal(System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return new
        {
            name = principal.Identity.Name,
            authenticationType = principal.Identity.AuthenticationType,
            subject = principal.FindFirst("sub")?.Value
                ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            principalKind = principal.FindFirst(GatewayClaimTypes.PrincipalKind)?.Value,
            principalId = principal.FindFirst(GatewayClaimTypes.PrincipalId)?.Value
        };
    }
}

/// <summary>Stable event ids used by structured proxy telemetry.</summary>
public static class TelemetryEventIds
{
    /// <summary>All structured telemetry envelopes use the same numeric id and distinct event names.</summary>
    public const int StructuredTelemetry = 42000;
}

/// <summary>Sanitizes credentials while preserving ordinary request and response content.</summary>
public static class TelemetrySanitizer
{
    private static readonly HashSet<string> DefaultSensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key",
        "Api-Key", "X-Auth-Token", "X-Access-Token"
    };

    private static readonly HashSet<string> DefaultSensitiveJsonFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "client_secret", "clientSecret", "api_key", "apiKey",
        "access_token", "accessToken", "refresh_token", "refreshToken", "id_token",
        "idToken", "authorization", "credential", "credentials", "secret"
    };

    /// <summary>JSON settings used to serialize telemetry values without failing on reference cycles.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        WriteIndented = false
    };

    /// <summary>Returns a redacted copy of HTTP headers suitable for logging.</summary>
    public static Dictionary<string, object?> SanitizeHeaders(
        IEnumerable<KeyValuePair<string, StringValues>> headers,
        LoggingOptions options)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var custom = options.RedactedHeaders ?? [];
        foreach (var pair in headers)
        {
            result[pair.Key] = IsSensitiveHeader(pair.Key, custom)
                ? "[REDACTED]"
                : pair.Value.Count <= 1 ? pair.Value.ToString() : pair.Value.ToArray();
        }

        return result;
    }

    /// <summary>Returns a sanitized, JSON-compatible representation of an arbitrary object.</summary>
    public static object? SanitizeObject(object? value, LoggingOptions options)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var node = JsonSerializer.SerializeToNode(value, SerializerOptions);
            RedactNode(node, options);
            return node;
        }
        catch
        {
            return value.ToString();
        }
    }

    /// <summary>Returns textual payload content with credential-shaped JSON fields redacted.</summary>
    public static string SanitizePayloadText(byte[] bytes, string? contentType, LoggingOptions options)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (!IsTextual(contentType))
        {
            return options.CaptureBinaryBodies
                ? Convert.ToBase64String(bytes)
                : $"[binary payload omitted; {bytes.LongLength} bytes]";
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (!LooksLikeJson(contentType, text))
        {
            return text;
        }

        try
        {
            var node = JsonNode.Parse(text);
            RedactNode(node, options);
            return node?.ToJsonString() ?? text;
        }
        catch
        {
            return text;
        }
    }

    /// <summary>Formats a captured HTTP body, redacting credential-shaped JSON fields.</summary>
    public static object? FormatBody(
        byte[] bytes,
        long totalBytes,
        string? contentType,
        bool truncated,
        LoggingOptions options)
    {
        if (bytes.Length == 0 && totalBytes == 0)
        {
            return null;
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var textual = IsTextual(contentType);

        if (!textual)
        {
            return new
            {
                encoding = options.CaptureBinaryBodies ? "base64" : "omitted",
                value = options.CaptureBinaryBodies ? Convert.ToBase64String(bytes) : null,
                capturedBytes = bytes.LongLength,
                totalBytes,
                truncated,
                sha256
            };
        }

        var text = Encoding.UTF8.GetString(bytes);
        object? value = text;
        if (LooksLikeJson(contentType, text))
        {
            try
            {
                var node = JsonNode.Parse(text);
                RedactNode(node, options);
                value = node;
            }
            catch
            {
                // Malformed/partial JSON is still valuable telemetry. Preserve it as text.
            }
        }

        return new
        {
            encoding = "utf-8",
            value,
            capturedBytes = bytes.LongLength,
            totalBytes,
            truncated,
            sha256
        };
    }

    private static bool IsSensitiveHeader(string name, IEnumerable<string> custom) =>
        DefaultSensitiveHeaders.Contains(name) ||
        custom.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase));

    private static void RedactNode(JsonNode? node, LoggingOptions options)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (IsSensitiveJsonField(pair.Key, options.RedactedJsonFields ?? []))
                {
                    obj[pair.Key] = "[REDACTED]";
                }
                else
                {
                    RedactNode(pair.Value, options);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                RedactNode(item, options);
            }
        }
    }

    private static bool IsSensitiveJsonField(string name, IEnumerable<string> custom) =>
        DefaultSensitiveJsonFields.Contains(name) ||
        custom.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeJson(string? contentType, string value) =>
        contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
        value.TrimStart().StartsWith('{') ||
        value.TrimStart().StartsWith('[');

    private static bool IsTextual(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
    }
}