using StackExchange.Redis;

namespace McpProxy.Registry;

/// <summary>Publishes catalog refresh notifications between proxy instances.</summary>
public interface ICatalogInvalidationBus
{
    /// <summary>Raised when a server's catalog should be refreshed locally.</summary>
    event Func<Guid, Task>? Invalidated;
    /// <summary>Publishes a server identifier to every subscriber.</summary>
    Task PublishAsync(Guid serverId, CancellationToken cancellationToken);
}

/// <summary>In-process invalidation bus used with memory caching and local development.</summary>
public sealed class InMemoryCatalogInvalidationBus : ICatalogInvalidationBus
{
    /// <inheritdoc />
    public event Func<Guid, Task>? Invalidated;

    /// <inheritdoc />
    public async Task PublishAsync(Guid serverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handlers = Invalidated?.GetInvocationList().Cast<Func<Guid, Task>>().ToArray() ?? [];
        foreach (var handler in handlers)
        {
            await handler(serverId);
        }
    }
}

/// <summary>Redis pub/sub invalidation bus used to coordinate catalog refreshes across nodes.</summary>
public sealed class RedisCatalogInvalidationBus : ICatalogInvalidationBus, IAsyncDisposable
{
    private const string ChannelName = "mcp-proxy:catalog-invalidated";
    private readonly ISubscriber _subscriber;

    /// <summary>Creates a subscriber and listens on the shared catalog channel.</summary>
    public RedisCatalogInvalidationBus(IConnectionMultiplexer connection)
    {
        _subscriber = connection.GetSubscriber();
        _subscriber.SubscribeAsync(RedisChannel.Literal(ChannelName), async (_, value) =>
        {
            if (Guid.TryParse(value.ToString(), out var serverId) && Invalidated is not null)
            {
                foreach (var handler in Invalidated.GetInvocationList().Cast<Func<Guid, Task>>())
                {
                    await handler(serverId);
                }
            }
        }).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public event Func<Guid, Task>? Invalidated;

    /// <inheritdoc />
    public Task PublishAsync(Guid serverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _subscriber.PublishAsync(RedisChannel.Literal(ChannelName), serverId.ToString());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}