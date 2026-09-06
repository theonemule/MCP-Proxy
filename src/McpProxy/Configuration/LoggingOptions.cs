namespace McpProxy.Configuration;

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

/// <summary>Configures logging verbosity and the selected .NET logging provider.</summary>
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
    public string? ApplicationName { get; set; } = "mcp-proxy";
    /// <summary>Minimum standard .NET log level.</summary>
    public string MinimumLevel { get; set; } = "Information";
}
