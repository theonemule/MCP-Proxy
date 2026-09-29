namespace AIGovernanceGateway.Configuration;

/// <summary>Named logging verbosity presets for deployment configuration.</summary>
public enum LogLevelPreset
{
    /// <summary>Only high-signal operational events.</summary>
    Minimal,
    /// <summary>Normal application diagnostics.</summary>
    Normal,
    /// <summary>Detailed integration diagnostics.</summary>
    Verbose,
    /// <summary>Developer-oriented diagnostics.</summary>
    Debug
}

/// <summary>Configures logging verbosity, structured telemetry, and the selected .NET logging provider.</summary>
public sealed class LoggingOptions
{
    /// <summary>Named verbosity preset exposed for deployment configuration.</summary>
    public LogLevelPreset Preset { get; set; } = LogLevelPreset.Normal;
    /// <summary>Provider name such as Console, Debug, EventSource, or None.</summary>
    public string Provider { get; set; } = "Console";
    /// <summary>Optional syslog host reserved for a future syslog provider.</summary>
    public string? SyslogHost { get; set; }
    /// <summary>Syslog UDP port when a syslog provider is used.</summary>
    public int SyslogPort { get; set; } = 514;
    /// <summary>Optional application name for external logging providers.</summary>
    public string? ApplicationName { get; set; } = "ai-governance-gateway";
    /// <summary>Minimum standard .NET log level.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Enables structured proxy telemetry for northbound, southbound, MCP, and model traffic.</summary>
    public bool TelemetryEnabled { get; set; } = true;
    /// <summary>Captures request payloads. Credential-shaped fields are redacted before logging.</summary>
    public bool CaptureRequestBodies { get; set; } = true;
    /// <summary>Captures response payloads. Streaming responses are tee-captured without delaying delivery.</summary>
    public bool CaptureResponseBodies { get; set; } = true;
    /// <summary>Captures HTTP headers with authentication/cookie headers redacted.</summary>
    public bool CaptureHeaders { get; set; } = true;
    /// <summary>Maximum bytes captured per request or response body. Zero means unlimited.</summary>
    public int MaxPayloadBytes { get; set; } = 1048576;
    /// <summary>Whether non-text HTTP bodies are emitted as base64 instead of metadata only.</summary>
    public bool CaptureBinaryBodies { get; set; }
    /// <summary>Whether a caller-supplied X- Correlation-ID is retained; otherwise the proxy generates one.</summary>
    public bool AcceptInboundCorrelationId { get; set; } = true;
    /// <summary>Additional HTTP header names whose values must be redacted.</summary>
    public string[] RedactedHeaders { get; set; } = [];
    /// <summary>Additional JSON property names whose values must be redacted recursively.</summary>
    public string[] RedactedJsonFields { get; set; } = [];
}