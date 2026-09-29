using System.Collections.Concurrent;
using AIGovernanceGateway.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace AIGovernanceGateway.Registry;

/// <summary>Last known tools, resources, and prompts for one downstream server.</summary>
public sealed record ServerCatalog(
    IReadOnlyList<Tool> Tools,
    IReadOnlyList<Resource> Resources,
    IReadOnlyList<Prompt> Prompts);

/// <summary>
/// Holds the last successfully synchronized tool/resource/prompt catalog for each enabled
/// downstream server. Refreshed on a timer and on demand; northbound requests always read the
/// cache rather than calling downstream inline.
/// </summary>
public sealed class CatalogCache(
    IServiceScopeFactory scopeFactory,
    DownstreamClientFactory clientFactory,
    ICatalogInvalidationBus invalidationBus,
    ILogger<CatalogCache> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, ServerCatalog> _catalogs = new();

    /// <summary>Returns the most recently synchronized catalog for a server, if available.</summary>
    public bool TryGet(Guid serverId, out ServerCatalog catalog) => _catalogs.TryGetValue(serverId, out catalog!);

    /// <summary>Returns the current local catalog snapshot for diagnostics and administration.</summary>
    public IReadOnlyDictionary<Guid, ServerCatalog> Snapshot() => _catalogs;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        invalidationBus.Invalidated += HandleInvalidationAsync;
        stoppingToken.Register(() => invalidationBus.Invalidated -= HandleInvalidationAsync);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAllAsync(stoppingToken);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Refreshes all enabled servers from the database and downstream MCP endpoints.</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
        var servers = await db.Servers.AsNoTracking().Where(x => x.Enabled).ToListAsync(cancellationToken);
        foreach (var server in servers)
        {
            await RefreshOneAsync(server, cancellationToken);
        }
    }

    /// <summary>Refreshes one server, records synchronization status, and publishes invalidation.</summary>
    public async Task RefreshOneAsync(McpServer server, CancellationToken cancellationToken, bool publish = true)
    {
        try
        {
            await using var client = await clientFactory.CreateAsync(server, cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);
            var prompts = await client.ListPromptsAsync(cancellationToken: cancellationToken);

            _catalogs[server.Id] = new ServerCatalog(
                tools.Select(x => x.ProtocolTool).ToArray(),
                resources.Select(x => x.ProtocolResource).ToArray(),
                prompts.Select(x => x.ProtocolPrompt).ToArray());

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
            await db.Servers.Where(x => x.Id == server.Id)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(s => s.LastSyncedAt, DateTimeOffset.UtcNow)
                    .SetProperty(s => s.LastError, (string?)null), cancellationToken);

            if (publish)
            {
                await invalidationBus.PublishAsync(server.Id, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Catalog sync failed for server {ServerName}", server.Name);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
            await db.Servers.Where(x => x.Id == server.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.LastError, exception.Message), cancellationToken);
        }
    }

    private async Task HandleInvalidationAsync(Guid serverId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId && x.Enabled);
        if (server is not null)
        {
            await RefreshOneAsync(server, CancellationToken.None, publish: false);
        }
    }
}