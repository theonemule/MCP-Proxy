namespace McpProxy.Configuration;

/// <summary>Supported cache and cross-node coordination providers.</summary>
public enum CacheProvider
{
    /// <summary>Process-local cache and in-process invalidation events.</summary>
    Memory,
    /// <summary>Redis distributed cache and pub/sub invalidation.</summary>
    Redis
}

/// <summary>Configuration for local or distributed cache behavior.</summary>
public sealed class CacheOptions
{
    /// <summary>Selected cache provider.</summary>
    public CacheProvider Provider { get; set; } = CacheProvider.Memory;
    /// <summary>Redis connection string when Redis is enabled.</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>Redis logical database number.</summary>
    public int Database { get; set; } = 0;
    /// <summary>Whether the configured distributed cache is active.</summary>
    public bool Enabled { get; set; } = false;
}
