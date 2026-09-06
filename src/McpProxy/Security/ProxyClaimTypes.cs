using System.Security.Claims;

namespace McpProxy.Security;

/// <summary>Well-known claim types used to identify the authenticated principal internally.</summary>
public static class ProxyClaimTypes
{
    /// <summary>"user" or "apikey".</summary>
    public const string PrincipalKind = "mcpproxy_kind";
    /// <summary>Claim containing the local principal identifier.</summary>
    public const string PrincipalId = "mcpproxy_id";
}

/// <summary>Helpers for reading proxy-specific identity claims.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>Reads and validates the proxy principal kind and identifier claims.</summary>
    public static bool TryGetPrincipalId(this ClaimsPrincipal user, out string kind, out Guid id)
    {
        kind = user.FindFirstValue(ProxyClaimTypes.PrincipalKind) ?? "";
        id = Guid.Empty;
        var idValue = user.FindFirstValue(ProxyClaimTypes.PrincipalId);
        return idValue is not null && Guid.TryParse(idValue, out id);
    }
}
