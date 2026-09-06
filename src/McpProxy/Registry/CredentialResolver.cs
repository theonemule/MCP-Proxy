using McpProxy.Data;

namespace McpProxy.Registry;

/// <summary>
/// Resolves a registered server's static downstream credential header from an environment-variable
/// reference. Only <c>env:NAME</c> references are supported, and the header name is restricted to
/// avoid overriding protocol or forwarding headers.
/// </summary>
public static class CredentialResolver
{
    /// <summary>Headers that cannot be used for downstream credentials because they affect routing or protocol state.</summary>
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Origin", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host",
        "X-Forwarded-Proto", "MCP-Protocol-Version", "Mcp-Session-Id"
    };

    /// <summary>Resolves a server's <c>env:</c> credential reference into a single safe header.</summary>
    /// <exception cref="InvalidOperationException">Thrown for unsupported references, missing values, or reserved headers.</exception>
    public static IReadOnlyDictionary<string, string> ResolveHeaders(McpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.CredentialReference))
        {
            return new Dictionary<string, string>();
        }

        if (ForbiddenHeaders.Contains(server.CredentialHeader))
        {
            throw new InvalidOperationException(
                $"Server '{server.Name}' may not use the reserved header '{server.CredentialHeader}' for credentials.");
        }

        const string environmentPrefix = "env:";
        if (!server.CredentialReference.StartsWith(environmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only env: credential references are supported.");
        }

        var variableName = server.CredentialReference[environmentPrefix.Length..];
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Credential environment variable '{variableName}' is not set.");
        }

        return new Dictionary<string, string> { [server.CredentialHeader] = server.CredentialPrefix + value };
    }
}
