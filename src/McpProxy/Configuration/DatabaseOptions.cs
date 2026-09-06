namespace McpProxy.Configuration;

/// <summary>Supported relational database engines.</summary>
public enum DatabaseProvider
{
    /// <summary>Embedded SQLite database for development and single-node deployments.</summary>
    Sqlite,
    /// <summary>Microsoft SQL Server for shared production deployments.</summary>
    SqlServer,
    /// <summary>PostgreSQL for shared production deployments.</summary>
    Postgres
}

/// <summary>Configuration for selecting the proxy's relational database.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Selected provider; defaults to SQLite.</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
    /// <summary>Fallback connection string when a provider-specific value is absent.</summary>
    public string ConnectionString { get; set; } = "Data Source=mcp-proxy.db";
    /// <summary>SQLite connection string.</summary>
    public string SqliteConnectionString { get; set; } = "Data Source=mcp-proxy.db";
    /// <summary>SQL Server connection string.</summary>
    public string SqlServerConnectionString { get; set; } = "";
    /// <summary>PostgreSQL connection string.</summary>
    public string PostgresConnectionString { get; set; } = "";

    /// <summary>Returns the connection string appropriate for <see cref="Provider"/>.</summary>
    public string GetConnectionString()
    {
        return Provider switch
        {
            DatabaseProvider.Sqlite => string.IsNullOrWhiteSpace(SqliteConnectionString) ? ConnectionString : SqliteConnectionString,
            DatabaseProvider.SqlServer => string.IsNullOrWhiteSpace(SqlServerConnectionString) ? ConnectionString : SqlServerConnectionString,
            DatabaseProvider.Postgres => string.IsNullOrWhiteSpace(PostgresConnectionString) ? ConnectionString : PostgresConnectionString,
            _ => ConnectionString
        };
    }
}
