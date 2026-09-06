using System.Text.Json;
using System.Text.Json.Serialization;
using McpClient.Configuration;

namespace McpClient.Services;

/// <summary>Thread-safe JSON-backed registry of client-side downstream MCP servers.</summary>
public sealed class McpServerRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly string _filePath;
    private List<McpServerOptions> _servers;

    /// <summary>Loads the registry from <c>Data/mcp-servers.json</c>.</summary>
    public McpServerRegistry(IWebHostEnvironment environment, ILogger<McpServerRegistry> logger)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "Data", "mcp-servers.json");
        _servers = Load(logger);
    }

    /// <summary>Returns defensive copies of every configured server.</summary>
    public IReadOnlyList<McpServerOptions> GetAll()
    {
        lock (_lock)
        {
            return _servers.Select(Clone).ToList();
        }
    }

    /// <summary>Adds a server with a new identifier and persists the registry.</summary>
    public McpServerOptions Add(McpServerOptions server)
    {
        lock (_lock)
        {
            server.Id = Guid.NewGuid().ToString("n");
            EnsureUniqueName(server.Name, null);
            _servers.Add(Clone(server));
            Save();
            return Clone(server);
        }
    }

    /// <summary>Replaces a server by identifier, or returns null when it does not exist.</summary>
    public McpServerOptions? Update(string id, McpServerOptions server)
    {
        lock (_lock)
        {
            var index = _servers.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            server.Id = id;
            EnsureUniqueName(server.Name, id);
            _servers[index] = Clone(server);
            Save();
            return Clone(server);
        }
    }

    /// <summary>Deletes a server by identifier and reports whether anything was removed.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _servers.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    private List<McpServerOptions> Load(ILogger logger)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var servers = JsonSerializer.Deserialize<List<McpServerOptions>>(
                File.ReadAllText(_filePath), JsonOptions) ?? [];

            foreach (var server in servers.Where(server => string.IsNullOrWhiteSpace(server.Id)))
            {
                server.Id = Guid.NewGuid().ToString("n");
            }

            return servers;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not load MCP server configuration from {Path}.", _filePath);
            return [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():n}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_servers, JsonOptions));
        File.Move(temporaryPath, _filePath, true);
    }

    private void EnsureUniqueName(string name, string? excludedId)
    {
        if (_servers.Any(server =>
                !string.Equals(server.Id, excludedId, StringComparison.Ordinal)
                && string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"An MCP server named '{name}' already exists.");
        }
    }

    private static McpServerOptions Clone(McpServerOptions server) => new()
    {
        Id = server.Id,
        Name = server.Name,
        Description = server.Description,
        Endpoint = server.Endpoint,
        TransportMode = server.TransportMode,
        Enabled = server.Enabled,
        ForwardToken = server.ForwardToken,
        AuthorizationScheme = server.AuthorizationScheme,
        ApiKey = server.ApiKey,
        ApiKeyHeaderName = server.ApiKeyHeaderName,
        AdditionalHeaders = new Dictionary<string, string>(server.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
        ConnectionTimeoutSeconds = server.ConnectionTimeoutSeconds,
    };
}