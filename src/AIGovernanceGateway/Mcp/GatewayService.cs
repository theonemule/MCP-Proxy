using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using AIGovernanceGateway.Data;
using AIGovernanceGateway.Registry;
using AIGovernanceGateway.Security;
using AIGovernanceGateway.Telemetry;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AIGovernanceGateway.Mcp;

/// <summary>
/// Builds the northbound catalog from cached downstream catalogs, namespaced by each server's
/// prefix, and routes calls back to the owning server. RBAC permission checks gate every list and
/// call operation; an unauthorized item is omitted from listings and reported as unknown on call.
/// </summary>
public sealed class GatewayService(
    GovernanceDbContext db,
    CatalogCache catalogCache,
    DownstreamClientFactory clientFactory,
    IPermissionService permissions,
    GatewayTelemetry telemetry)
{
    private const string Separator = "__";

    /// <summary>Lists authorized tools with namespace-prefixed public names.</summary>
    /// <param name="user">Authenticated caller whose effective roles are evaluated.</param>
    /// <param name="serverScope">Optional namespace prefix restricting the search to one server.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    public async Task<IReadOnlyList<Tool>> ListToolsAsync(ClaimsPrincipal user, string? serverScope, CancellationToken cancellationToken)
    {
        var result = new List<Tool>();
        foreach (var server in await EnabledServersAsync(serverScope, cancellationToken))
        {
            if (!catalogCache.TryGet(server.Id, out var catalog))
            {
                continue;
            }

            foreach (var tool in catalog.Tools)
            {
                if (await permissions.CanAccessAsync(user, server.Id, CapabilityKind.Tool, tool.Name, cancellationToken))
                {
                    result.Add(WithName(tool, server.NamespacePrefix + Separator + tool.Name));
                }
            }
        }

        telemetry.Record("mcp.tools.list", new
        {
            serverScope,
            count = result.Count,
            tools = result
        });
        return result;
    }

    /// <summary>Resolves and invokes one authorized namespaced tool.</summary>
    /// <param name="user">Authenticated caller whose effective roles are evaluated.</param>
    /// <param name="publicName">Namespaced tool name exposed by the gateway.</param>
    /// <param name="arguments">Optional JSON arguments passed to the downstream tool.</param>
    /// <param name="serverScope">Optional namespace prefix restricting the search to one server.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    public async Task<CallToolResult> CallToolAsync(
        ClaimsPrincipal user,
        string publicName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        string? serverScope,
        CancellationToken cancellationToken)
    {
        var (server, nativeName) = await ResolveAsync(publicName, serverScope, cancellationToken)
            ?? throw new McpProtocolException($"Unknown tool '{publicName}'.", McpErrorCode.InvalidParams);

        if (!await permissions.CanAccessAsync(user, server.Id, CapabilityKind.Tool, nativeName, cancellationToken))
        {
            // Do not reveal whether a denied tool exists.
            throw new McpProtocolException($"Unknown tool '{publicName}'.", McpErrorCode.InvalidParams);
        }

        telemetry.Record("mcp.tool.request", new
        {
            publicName,
            nativeName,
            server = server.Name,
            server.NamespacePrefix,
            arguments
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var client = await clientFactory.CreateAsync(server, cancellationToken);
            var boxedArguments = arguments?.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal);
            var result = await client.CallToolAsync(nativeName, boxedArguments, cancellationToken: cancellationToken);
            telemetry.Record("mcp.tool.response", new
            {
                publicName,
                nativeName,
                server = server.Name,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                result
            });
            return result;
        }
        catch (Exception ex)
        {
            telemetry.Record("mcp.tool.error", new
            {
                publicName,
                nativeName,
                server = server.Name,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds
            }, LogLevel.Error, ex);
            throw;
        }
    }

    /// <summary>Lists authorized resources with namespace-prefixed public URIs.</summary>
    /// <param name="user">Authenticated caller whose effective roles are evaluated.</param>
    /// <param name="serverScope">Optional namespace prefix restricting the search to one server.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    public async Task<IReadOnlyList<Resource>> ListResourcesAsync(ClaimsPrincipal user, string? serverScope, CancellationToken cancellationToken)
    {
        var result = new List<Resource>();
        foreach (var server in await EnabledServersAsync(serverScope, cancellationToken))
        {
            if (!catalogCache.TryGet(server.Id, out var catalog))
            {
                continue;
            }

            foreach (var resource in catalog.Resources)
            {
                if (await permissions.CanAccessAsync(user, server.Id, CapabilityKind.Resource, resource.Uri, cancellationToken))
                {
                    result.Add(WithUri(resource, server.NamespacePrefix + Separator + resource.Uri));
                }
            }
        }

        telemetry.Record("mcp.resources.list", new
        {
            serverScope,
            count = result.Count,
            resources = result
        });
        return result;
    }

    /// <summary>Resolves and reads one authorized namespaced resource URI.</summary>
    /// <param name="user">Authenticated caller whose effective roles are evaluated.</param>
    /// <param name="publicUri">Namespaced resource URI exposed by the gateway.</param>
    /// <param name="serverScope">Optional namespace prefix restricting the search to one server.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    public async Task<ReadResourceResult> ReadResourceAsync(ClaimsPrincipal user, string publicUri, string? serverScope, CancellationToken cancellationToken)
    {
        var (server, nativeUri) = await ResolveAsync(publicUri, serverScope, cancellationToken)
            ?? throw new McpProtocolException($"Unknown resource '{publicUri}'.", McpErrorCode.InvalidParams);

        if (!await permissions.CanAccessAsync(user, server.Id, CapabilityKind.Resource, nativeUri, cancellationToken))
        {
            throw new McpProtocolException($"Unknown resource '{publicUri}'.", McpErrorCode.InvalidParams);
        }

        telemetry.Record("mcp.resource.request", new
        {
            publicUri,
            nativeUri,
            server = server.Name,
            server.NamespacePrefix
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var client = await clientFactory.CreateAsync(server, cancellationToken);
            var result = await client.ReadResourceAsync(nativeUri, cancellationToken: cancellationToken);
            telemetry.Record("mcp.resource.response", new
            {
                publicUri,
                nativeUri,
                server = server.Name,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                result
            });
            return result;
        }
        catch (Exception ex)
        {
            telemetry.Record("mcp.resource.error", new
            {
                publicUri,
                nativeUri,
                server = server.Name,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds
            }, LogLevel.Error, ex);
            throw;
        }
    }

    /// <summary>Lists authorized prompts with namespace-prefixed public names.</summary>
    /// <param name="user">Authenticated caller whose effective roles are evaluated.</param>
    /// <param name="serverScope">Optional namespace prefix restricting the search to one server.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    public async Task<IReadOnlyList<Prompt>> ListPromptsAsync(ClaimsPrincipal user, string? serverScope, CancellationToken cancellationToken)
    {
        var result = new List<Prompt>();
        foreach (var server in await EnabledServersAsync(serverScope, cancellationToken))
        {
            if (!catalogCache.TryGet(server.Id, out var catalog))
            {
                continue;
            }

            foreach (var prompt in catalog.Prompts)
            {
                if (await permissions.CanAccessAsync(user, server.Id, CapabilityKind.Prompt, prompt.Name, cancellationToken))
                {
                    result.Add(WithName(prompt, server.NamespacePrefix + Separator + prompt.Name));
                }
            }
        }

        telemetry.Record("mcp.prompts.list", new
        {
            serverScope,
            count = result.Count,
            prompts = result
        });
        return result;
    }

    /// <summary>Resolves and renders one authorized namespaced prompt.</summary>
    /// <param name="user">Authenticated caller whose effective roles are evaluated.</param>
    /// <param name="publicName">Namespaced prompt name exposed by the gateway.</param>
    /// <param name="arguments">Optional prompt arguments.</param>
    /// <param name="serverScope">Optional namespace prefix restricting the search to one server.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    public async Task<GetPromptResult> GetPromptAsync(
        ClaimsPrincipal user,
        string publicName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        string? serverScope,
        CancellationToken cancellationToken)
    {
        var (server, nativeName) = await ResolveAsync(publicName, serverScope, cancellationToken)
            ?? throw new McpProtocolException($"Unknown prompt '{publicName}'.", McpErrorCode.InvalidParams);

        if (!await permissions.CanAccessAsync(user, server.Id, CapabilityKind.Prompt, nativeName, cancellationToken))
        {
            throw new McpProtocolException($"Unknown prompt '{publicName}'.", McpErrorCode.InvalidParams);
        }

        telemetry.Record("mcp.prompt.request", new
        {
            publicName,
            nativeName,
            server = server.Name,
            server.NamespacePrefix,
            arguments
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var client = await clientFactory.CreateAsync(server, cancellationToken);
            var boxedArguments = arguments?.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal);
            var result = await client.GetPromptAsync(nativeName, boxedArguments, cancellationToken: cancellationToken);
            telemetry.Record("mcp.prompt.response", new
            {
                publicName,
                nativeName,
                server = server.Name,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                result
            });
            return result;
        }
        catch (Exception ex)
        {
            telemetry.Record("mcp.prompt.error", new
            {
                publicName,
                nativeName,
                server = server.Name,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds
            }, LogLevel.Error, ex);
            throw;
        }
    }

    private Task<List<McpServer>> EnabledServersAsync(string? serverScope, CancellationToken cancellationToken) =>
        db.Servers.AsNoTracking()
            .Where(x => x.Enabled && (serverScope == null || x.NamespacePrefix == serverScope))
            .ToListAsync(cancellationToken);

    /// <summary>Splits a namespaced public identifier back into its owning server and native name.</summary>
    private async Task<(McpServer Server, string NativeName)?> ResolveAsync(string publicIdentifier, string? serverScope, CancellationToken cancellationToken)
    {
        foreach (var server in await EnabledServersAsync(serverScope, cancellationToken))
        {
            var prefix = server.NamespacePrefix + Separator;
            if (publicIdentifier.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (server, publicIdentifier[prefix.Length..]);
            }
        }

        return null;
    }

    private static Tool WithName(Tool tool, string name) { var clone = Clone(tool); clone.Name = name; return clone; }
    private static Prompt WithName(Prompt prompt, string name) { var clone = Clone(prompt); clone.Name = name; return clone; }
    private static Resource WithUri(Resource resource, string uri) { var clone = Clone(resource); clone.Uri = uri; return clone; }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions),
            McpJsonUtilities.DefaultOptions)!;
}