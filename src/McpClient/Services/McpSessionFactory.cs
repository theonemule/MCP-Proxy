using McpClient.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using McpSdkClient = ModelContextProtocol.Client.McpClient;

namespace McpClient.Services;

/// <summary>
/// Creates MCP client sessions, forwarding the signed-in user's OIDC credential to the server.
/// </summary>
public sealed class McpSessionFactory(
    IOptions<McpOptions> options,
    McpServerRegistry registry,
    UserTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    ILogger<McpSessionFactory> logger)
{
    private readonly McpOptions _options = options.Value;

    /// <summary>Enabled servers with non-empty endpoints.</summary>
    public IReadOnlyList<McpServerOptions> EnabledServers =>
        registry.GetAll().Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Endpoint)).ToList();

    /// <summary>Finds an enabled server by case-insensitive display name.</summary>
    public McpServerOptions? Find(string name) =>
        EnabledServers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Connects to one server and forwards the configured user or API-key credential.</summary>
    public async Task<McpClientSession> ConnectAsync(McpServerOptions server, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(server.AdditionalHeaders, StringComparer.OrdinalIgnoreCase);

        if (server.ForwardToken == ForwardedToken.ApiKey)
        {
            if (!string.IsNullOrWhiteSpace(server.ApiKey))
            {
                headers[server.ApiKeyHeaderName] = string.IsNullOrWhiteSpace(server.AuthorizationScheme)
                    ? server.ApiKey
                    : $"{server.AuthorizationScheme} {server.ApiKey}";
            }
            else
            {
                logger.LogWarning("No API key configured for MCP server '{Server}'. Connecting without a credential.", server.Name);
            }
        }
        else
        {
            var token = await tokenProvider.GetTokenAsync(server.ForwardToken);
            if (!string.IsNullOrEmpty(token))
            {
                headers["Authorization"] = $"{server.AuthorizationScheme} {token}";
            }
            else if (server.ForwardToken != ForwardedToken.None)
            {
                logger.LogWarning(
                    "No {TokenKind} available for MCP server '{Server}'. Connecting without user credentials.",
                    server.ForwardToken, server.Name);
            }
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Endpoint),
                TransportMode = Enum.TryParse<HttpTransportMode>(server.TransportMode, ignoreCase: true, out var mode)
                    ? mode
                    : HttpTransportMode.AutoDetect,
                ConnectionTimeout = TimeSpan.FromSeconds(server.ConnectionTimeoutSeconds),
                AdditionalHeaders = headers,
            },
            httpClientFactory.CreateClient("mcp"),
            loggerFactory,
            false);

        var client = await McpSdkClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = _options.ClientName,
                    Version = _options.ClientVersion,
                },
            },
            loggerFactory,
            cancellationToken);

        return new McpClientSession(server, client);
    }

    /// <summary>Connects to every enabled server, skipping (and reporting) any that fail.</summary>
    public async Task<McpConnectionSet> ConnectAllAsync(CancellationToken cancellationToken)
    {
        var sessions = new List<McpClientSession>();
        var failures = new List<McpConnectionFailure>();

        foreach (var server in EnabledServers)
        {
            try
            {
                sessions.Add(await ConnectAsync(server, cancellationToken));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to connect to MCP server '{Server}' at '{Endpoint}'.", server.Name, server.Endpoint);
                failures.Add(new McpConnectionFailure(server.Name, $"[{server.Endpoint}] {ex.Message}"));
            }
        }

        return new McpConnectionSet(sessions, failures);
    }
}

/// <summary>Describes one server connection that failed during a multi-server connection attempt.</summary>
public sealed record McpConnectionFailure(string Server, string Error);

/// <summary>Connected MCP client paired with the options used to create it.</summary>
public sealed class McpClientSession(McpServerOptions options, McpSdkClient client)
    : IAsyncDisposable
{
    /// <summary>Server options used for this session.</summary>
    public McpServerOptions Options { get; } = options;
    /// <summary>Connected SDK client.</summary>
    public McpSdkClient Client { get; } = client;

    /// <summary>Disposes the underlying MCP client.</summary>
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

/// <summary>Successful sessions and failures from connecting to all enabled servers.</summary>
public sealed class McpConnectionSet(
    IReadOnlyList<McpClientSession> sessions,
    IReadOnlyList<McpConnectionFailure> failures) : IAsyncDisposable
{
    /// <summary>Successfully connected sessions.</summary>
    public IReadOnlyList<McpClientSession> Sessions { get; } = sessions;
    /// <summary>Failures that were isolated so other servers could still connect.</summary>
    public IReadOnlyList<McpConnectionFailure> Failures { get; } = failures;

    /// <summary>Disposes every successful session without masking the original operation result.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch
            {
                // Disposal failures must not mask the original result.
            }
        }
    }
}
