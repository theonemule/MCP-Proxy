using System.Text.Json;
using System.Text.Json.Serialization;
using McpClient.Configuration;
using Microsoft.Extensions.Options;

namespace McpClient.Services;

/// <summary>
/// Persists switchable gateway/hosted LLM settings so the client can test the model gateway against
/// a known-good direct provider without editing files or restarting.
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

    /// <summary>Loads persisted settings or migrates the previous flat hosted-model settings.</summary>
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

    /// <summary>Atomically replaces and persists both model profiles and the active source.</summary>
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
            var json = File.ReadAllText(_filePath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Before gateway/hosted profiles were introduced, endpoint/apiKey/model lived at the root.
            // Treat that existing configuration as the hosted profile so upgrades are non-destructive.
            if (root.TryGetProperty("endpoint", out var endpoint) &&
                !root.TryGetProperty("hosted", out _))
            {
                var migrated = Clone(defaults);
                migrated.Source = LlmSource.Hosted;
                migrated.Hosted.Endpoint = endpoint.GetString() ?? migrated.Hosted.Endpoint;
                if (root.TryGetProperty("apiKey", out var apiKey))
                {
                    migrated.Hosted.ApiKey = apiKey.GetString() ?? string.Empty;
                }
                if (root.TryGetProperty("model", out var model))
                {
                    migrated.Hosted.Model = model.GetString() ?? migrated.Hosted.Model;
                }
                if (root.TryGetProperty("systemPrompt", out var systemPrompt))
                {
                    migrated.SystemPrompt = systemPrompt.GetString() ?? migrated.SystemPrompt;
                }
                if (root.TryGetProperty("temperature", out var temperature) && temperature.ValueKind == JsonValueKind.Number)
                {
                    migrated.Temperature = temperature.GetSingle();
                }
                if (root.TryGetProperty("maxOutputTokens", out var maxOutput) && maxOutput.ValueKind == JsonValueKind.Number)
                {
                    migrated.MaxOutputTokens = maxOutput.GetInt32();
                }
                if (root.TryGetProperty("maxToolIterations", out var maxIterations) && maxIterations.ValueKind == JsonValueKind.Number)
                {
                    migrated.MaxToolIterations = maxIterations.GetInt32();
                }

                return migrated;
            }

            return JsonSerializer.Deserialize<LlmOptions>(json, JsonOptions) ?? Clone(defaults);
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
        Source = settings.Source,
        Gateway = CloneConnection(settings.Gateway),
        Hosted = CloneConnection(settings.Hosted),
        SystemPrompt = settings.SystemPrompt,
        Temperature = settings.Temperature,
        MaxOutputTokens = settings.MaxOutputTokens,
        MaxToolIterations = settings.MaxToolIterations,
    };

    private static LlmConnectionOptions CloneConnection(LlmConnectionOptions? settings) => new()
    {
        Endpoint = settings?.Endpoint ?? string.Empty,
        ApiKey = settings?.ApiKey ?? string.Empty,
        Model = settings?.Model ?? string.Empty,
    };
}