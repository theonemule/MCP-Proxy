using System.Text.Json;
using System.Text.Json.Serialization;
using McpClient.Configuration;
using Microsoft.Extensions.Options;

namespace McpClient.Services;

/// <summary>
/// Persists the LLM connection settings so they can be changed from the Settings page without
/// editing configuration files or restarting the app. Seeded from appsettings on first run.
/// </summary>
public sealed class LlmSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly string _filePath;
    private LlmOptions _settings;

    /// <summary>Loads persisted settings from the client data directory or configured defaults.</summary>
    public LlmSettingsStore(IWebHostEnvironment environment, IOptions<LlmOptions> defaults, ILogger<LlmSettingsStore> logger)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "Data", "llm-settings.json");
        _settings = Load(defaults.Value, logger);
    }

    /// <summary>Returns a defensive copy of the current LLM settings.</summary>
    public LlmOptions Get()
    {
        lock (_lock)
        {
            return Clone(_settings);
        }
    }

    /// <summary>Atomically replaces and persists the current LLM settings.</summary>
    public LlmOptions Update(LlmOptions settings)
    {
        lock (_lock)
        {
            _settings = Clone(settings);
            Save();
            return Clone(_settings);
        }
    }

    private LlmOptions Load(LlmOptions defaults, ILogger logger)
    {
        if (!File.Exists(_filePath))
        {
            return Clone(defaults);
        }

        try
        {
            return JsonSerializer.Deserialize<LlmOptions>(File.ReadAllText(_filePath), JsonOptions) ?? Clone(defaults);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not load LLM settings from {Path}.", _filePath);
            return Clone(defaults);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():n}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_settings, JsonOptions));
        File.Move(temporaryPath, _filePath, true);
    }

    private static LlmOptions Clone(LlmOptions settings) => new()
    {
        Endpoint = settings.Endpoint,
        ApiKey = settings.ApiKey,
        Model = settings.Model,
        SystemPrompt = settings.SystemPrompt,
        Temperature = settings.Temperature,
        MaxOutputTokens = settings.MaxOutputTokens,
        MaxToolIterations = settings.MaxToolIterations,
    };
}
