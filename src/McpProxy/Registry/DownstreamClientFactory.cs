using McpProxy.Data;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpProxy.Registry;

/// <summary>Creates short-lived MCP client sessions to a registered downstream server.</summary>
public sealed class DownstreamClientFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
{
    /// <summary>Creates and connects an MCP client for a registered downstream server.</summary>
    /// <param name="server">Server endpoint and credential configuration.</param>
    /// <param name="cancellationToken">Connection cancellation token.</param>
    /// <returns>A connected MCP client owned by the caller.</returns>
    public async Task<McpClient> CreateAsync(McpServer server, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"Server '{server.Name}' has an invalid HTTP endpoint.");
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = $"mcp-proxy:{server.Name}",
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = CredentialResolver.ResolveHeaders(server).ToDictionary(x => x.Key, x => x.Value)
            },
            httpClientFactory.CreateClient("mcp-downstream"),
            loggerFactory);

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = "mcp-proxy", Version = "2.0.0" } },
            loggerFactory,
            cancellationToken);
    }
}
