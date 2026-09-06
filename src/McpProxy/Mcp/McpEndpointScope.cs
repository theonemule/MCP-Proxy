namespace McpProxy.Mcp;

/// <summary>
/// Identifies which registered server a northbound MCP request is confined to. The combined
/// endpoint carries no scope and exposes everything the caller is authorized for. A per-server
/// endpoint carries that server's namespace prefix and must expose nothing outside it, regardless
/// of what the caller may reach through other endpoints. Scoping narrows the catalog only; it
/// never widens it, so authorization still runs unchanged on whatever remains.
/// </summary>
public sealed class McpEndpointScope(IHttpContextAccessor httpContextAccessor)
{
    /// <summary>Route parameter carrying the namespace prefix on per-server MCP endpoints.</summary>
    public const string RouteValueName = "serverScope";

    /// <summary>The namespace prefix this request is confined to, or null on the combined endpoint.</summary>
    public string? NamespacePrefix =>
        httpContextAccessor.HttpContext?.Request.RouteValues.TryGetValue(RouteValueName, out var value) == true
            ? value as string
            : null;
}
