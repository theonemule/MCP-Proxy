using System.Security.Claims;
using McpProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Security;

/// <summary>A restricted administrative domain a role may be granted independent of <c>IsGlobalAdmin</c>.</summary>
public enum AdminScope
{
    /// <summary>Users, roles, API keys, claim mappings, and permission assignment.</summary>
    UserAdmin,

    /// <summary>Registered MCP server management.</summary>
    ServerAdmin
}

/// <summary>Evaluates effective roles, administration scopes, and capability permissions.</summary>
public interface IPermissionService
{
    /// <summary>True when the caller has any role marked as a global administrator.</summary>
    Task<bool> IsGlobalAdminAsync(ClaimsPrincipal user, CancellationToken cancellationToken);

    /// <summary>True when the caller is a global administrator or holds a role granting the given scope.</summary>
    Task<bool> HasAdminScopeAsync(ClaimsPrincipal user, AdminScope scope, CancellationToken cancellationToken);

    /// <summary>True when the caller may use the named tool/resource/prompt, or the whole server if grantsWholeServer covers it.</summary>
    /// <summary>Tests whether the caller has a permission covering a server capability.</summary>
    Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid serverId, CapabilityKind kind, string itemName, CancellationToken cancellationToken);

    /// <summary>The set of server ids the caller has at least one permission on, for catalog filtering.</summary>
    Task<IReadOnlySet<Guid>> GetAccessibleServerIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the effective roles for an authenticated principal - from direct assignment (user or
/// API key) plus any claim-based role mappings - and answers authorization questions against the
/// permissions those roles carry. This is the sole authorization boundary; authentication only
/// establishes who the caller is.
/// </summary>
public sealed class PermissionService(ProxyDbContext db) : IPermissionService
{
    private async Task<List<Guid>> ResolveRoleIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var roleIds = new List<Guid>();

        if (user.TryGetPrincipalId(out var kind, out var id))
        {
            roleIds.AddRange(kind switch
            {
                "user" => await db.Set<UserRole>().Where(x => x.UserId == id).Select(x => x.RoleId).ToListAsync(cancellationToken),
                "apikey" => await db.Set<ApiKeyRole>().Where(x => x.ApiKeyId == id).Select(x => x.RoleId).ToListAsync(cancellationToken),
                _ => []
            });
        }

        var claimPairs = user.Claims.Select(c => new { c.Type, c.Value }).Distinct().ToList();
        if (claimPairs.Count > 0)
        {
            var mappings = await db.ClaimRoleMappings.AsNoTracking().ToListAsync(cancellationToken);
            foreach (var mapping in mappings)
            {
                if (claimPairs.Any(c => EqualsClaimType(c.Type, mapping.ClaimType) && string.Equals(c.Value, mapping.ClaimValue, StringComparison.OrdinalIgnoreCase)))
                {
                    roleIds.Add(mapping.RoleId);
                }
            }
        }

        return roleIds.Distinct().ToList();
    }

    private static bool EqualsClaimType(string userClaimType, string mappingClaimType)
    {
        if (string.Equals(userClaimType, mappingClaimType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var shortUserType = userClaimType.Contains('/') ? userClaimType[(userClaimType.LastIndexOf('/') + 1)..] : userClaimType;
        var shortMappingType = mappingClaimType.Contains('/') ? mappingClaimType[(mappingClaimType.LastIndexOf('/') + 1)..] : mappingClaimType;

        if (string.Equals(shortUserType, shortMappingType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Handle role / roles aliases
        var normUser = shortUserType.TrimEnd('s');
        var normMapping = shortMappingType.TrimEnd('s');
        return string.Equals(normUser, normMapping, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<bool> IsGlobalAdminAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var roleIds = await ResolveRoleIdsAsync(user, cancellationToken);
        if (roleIds.Count == 0)
        {
            return false;
        }

        return await db.Roles.AnyAsync(x => roleIds.Contains(x.Id) && x.IsGlobalAdmin, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HasAdminScopeAsync(ClaimsPrincipal user, AdminScope scope, CancellationToken cancellationToken)
    {
        var roleIds = await ResolveRoleIdsAsync(user, cancellationToken);
        if (roleIds.Count == 0)
        {
            return false;
        }

        return await db.Roles.AnyAsync(
            x => roleIds.Contains(x.Id) && (x.IsGlobalAdmin ||
                (scope == AdminScope.UserAdmin && x.IsUserAdmin) ||
                (scope == AdminScope.ServerAdmin && x.IsServerAdmin)),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, Guid serverId, CapabilityKind kind, string itemName, CancellationToken cancellationToken)
    {
        var roleIds = await ResolveRoleIdsAsync(user, cancellationToken);
        if (roleIds.Count == 0)
        {
            return false;
        }

        return await db.Permissions.AnyAsync(
            p => roleIds.Contains(p.RoleId) && p.ServerId == serverId &&
                 (p.Kind == CapabilityKind.Server || (p.Kind == kind && p.ItemName == itemName)),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> GetAccessibleServerIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var roleIds = await ResolveRoleIdsAsync(user, cancellationToken);
        if (roleIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = await db.Permissions.Where(p => roleIds.Contains(p.RoleId))
            .Select(p => p.ServerId).Distinct().ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }
}
