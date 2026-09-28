using McpProxy.Data;
using McpProxy.Registry;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Admin;

/// <summary>Request to create or replace a role and its administrative flags.</summary>
public sealed record CreateRoleRequest(string Name, string? Description, bool IsGlobalAdmin, bool IsUserAdmin, bool IsServerAdmin);
/// <summary>Request to grant a role access to a server capability.</summary>
public sealed record CreatePermissionRequest(Guid RoleId, Guid ServerId, CapabilityKind Kind, string? ItemName);
/// <summary>Request to create a local account, an externally linked account, or both.</summary>
public sealed record CreateUserRequest(string Username, string? Password, string? ExternalSubject);
/// <summary>Request to attach a role to a user or API key.</summary>
public sealed record AssignRoleRequest(Guid RoleId);
/// <summary>Request to create a machine API key.</summary>
public sealed record CreateApiKeyRequest(string Name, DateTimeOffset? ExpiresAt);
/// <summary>Request to map an identity claim to a role.</summary>
public sealed record CreateClaimMappingRequest(string ClaimType, string ClaimValue, Guid RoleId);
/// <summary>Request to register or update a downstream MCP server.</summary>
public sealed record CreateServerRequest(
    string Name, string NamespacePrefix, string Endpoint,
    string? CredentialReference, string CredentialHeader = "Authorization", string CredentialPrefix = "Bearer ");

/// <summary>
/// Administrative REST surface for roles, permissions, users, API keys, claim-role mappings, and
/// server registration. Users/roles/API-keys/claim-mappings/permissions require the UserAdmin scope;
/// server registration requires the ServerAdmin scope. A global admin role satisfies both.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Maps the authenticated administrative REST API under <c>/admin</c>.</summary>
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization();

        MapRoles(admin.MapGroup("/roles").RequireAdminScope(AdminScope.UserAdmin));
        MapPermissions(admin.MapGroup("/permissions").RequireAdminScope(AdminScope.UserAdmin));
        MapUsers(admin.MapGroup("/users").RequireAdminScope(AdminScope.UserAdmin));
        MapApiKeys(admin.MapGroup("/apikeys").RequireAdminScope(AdminScope.UserAdmin));
        MapClaimMappings(admin.MapGroup("/claim-mappings").RequireAdminScope(AdminScope.UserAdmin));
        MapServers(admin.MapGroup("/servers").RequireAdminScope(AdminScope.ServerAdmin));
        admin.MapModelAdminEndpoints();
    }

    private static void MapRoles(RouteGroupBuilder roles)
    {
        roles.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.Roles.AsNoTracking()
                .Select(x => new { x.Id, x.Name, x.Description, x.IsGlobalAdmin, x.IsUserAdmin, x.IsServerAdmin })
                .ToListAsync()));

        roles.MapPost("/", async (CreateRoleRequest request, ProxyDbContext db) =>
        {
            var role = new Role
            {
                Name = request.Name,
                Description = request.Description,
                IsGlobalAdmin = request.IsGlobalAdmin,
                IsUserAdmin = request.IsUserAdmin,
                IsServerAdmin = request.IsServerAdmin
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/roles/{role.Id}", new { role.Id });
        });

        roles.MapPut("/{id:guid}", async (Guid id, CreateRoleRequest request, ProxyDbContext db) =>
        {
            var role = await db.Roles.SingleOrDefaultAsync(x => x.Id == id);
            if (role is null)
            {
                return Results.NotFound();
            }

            role.Name = request.Name;
            role.Description = request.Description;
            role.IsGlobalAdmin = request.IsGlobalAdmin;
            role.IsUserAdmin = request.IsUserAdmin;
            role.IsServerAdmin = request.IsServerAdmin;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        roles.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.Roles.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });

        roles.MapGet("/{id:guid}/permissions", async (Guid id, ProxyDbContext db) =>
            Results.Ok(await db.Permissions.AsNoTracking().Where(x => x.RoleId == id)
                .Select(x => new { x.Id, x.ServerId, x.Kind, x.ItemName }).ToListAsync()));

        roles.MapPost("/{id:guid}/permissions", async (Guid id, CreatePermissionRequest request, ProxyDbContext db) =>
        {
            if (request.Kind != CapabilityKind.Server && string.IsNullOrWhiteSpace(request.ItemName))
            {
                return Results.BadRequest("ItemName is required for a non-Server permission.");
            }

            var permission = new Permission
            {
                RoleId = id,
                ServerId = request.ServerId,
                Kind = request.Kind,
                ItemName = request.Kind == CapabilityKind.Server ? null : request.ItemName
            };
            db.Permissions.Add(permission);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/permissions/{permission.Id}", new { permission.Id });
        });
    }

    /// <summary>
    /// A flat, name-joined view of every permission grant, independent of whether the caller is
    /// browsing by role, by server, or by primitive - the three entry points all read and write
    /// through this same shape.
    /// </summary>
    private static void MapPermissions(RouteGroupBuilder permissions)
    {
        permissions.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.Permissions.AsNoTracking()
                .Select(x => new
                {
                    x.Id,
                    x.RoleId,
                    RoleName = x.Role.Name,
                    x.ServerId,
                    ServerName = x.Server.Name,
                    x.Kind,
                    x.ItemName
                })
                .ToListAsync()));

        permissions.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.Permissions.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapUsers(RouteGroupBuilder users)
    {
        users.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.Users.AsNoTracking()
                .Select(x => new
                {
                    x.Id,
                    x.Username,
                    x.Enabled,
                    x.ExternalSubject,
                    Roles = x.UserRoles.Select(r => new { r.RoleId, RoleName = r.Role.Name })
                })
                .ToListAsync()));

        users.MapPost("/", async (CreateUserRequest request, ProxyDbContext db) =>
        {
            if (await db.Users.AnyAsync(x => x.Username == request.Username))
            {
                return Results.Conflict("Username already exists.");
            }

            if (string.IsNullOrWhiteSpace(request.Password) && string.IsNullOrWhiteSpace(request.ExternalSubject))
            {
                return Results.BadRequest("Either a password (internal login) or an external subject (OIDC) is required.");
            }

            var user = new User
            {
                Username = request.Username,
                PasswordHash = string.IsNullOrWhiteSpace(request.Password) ? null : PasswordHasher.Hash(request.Password),
                ExternalSubject = string.IsNullOrWhiteSpace(request.ExternalSubject) ? null : request.ExternalSubject
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/users/{user.Id}", new { user.Id });
        });

        users.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.Users.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });

        users.MapPost("/{id:guid}/roles", async (Guid id, AssignRoleRequest request, ProxyDbContext db) =>
        {
            if (!await db.Users.AnyAsync(x => x.Id == id) || !await db.Roles.AnyAsync(x => x.Id == request.RoleId))
            {
                return Results.NotFound();
            }

            if (!await db.Set<UserRole>().AnyAsync(x => x.UserId == id && x.RoleId == request.RoleId))
            {
                db.Add(new UserRole { UserId = id, RoleId = request.RoleId });
                await db.SaveChangesAsync();
            }

            return Results.NoContent();
        });

        users.MapDelete("/{id:guid}/roles/{roleId:guid}", async (Guid id, Guid roleId, ProxyDbContext db) =>
        {
            var affected = await db.Set<UserRole>().Where(x => x.UserId == id && x.RoleId == roleId).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapApiKeys(RouteGroupBuilder apiKeys)
    {
        apiKeys.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.ApiKeys.AsNoTracking()
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.KeyPrefix,
                    x.Enabled,
                    x.ExpiresAt,
                    x.LastUsedAt,
                    Roles = x.ApiKeyRoles.Select(r => new { r.RoleId, RoleName = r.Role.Name })
                })
                .ToListAsync()));

        apiKeys.MapPost("/", async (CreateApiKeyRequest request, ProxyDbContext db) =>
        {
            var (prefix, plaintextKey, secretHash) = ApiKeyGenerator.Generate();
            var apiKey = new ApiKey { Name = request.Name, KeyPrefix = prefix, SecretHash = secretHash, ExpiresAt = request.ExpiresAt };
            db.ApiKeys.Add(apiKey);
            await db.SaveChangesAsync();
            // The plaintext key is returned exactly once and is not recoverable afterward.
            return Results.Created($"/admin/apikeys/{apiKey.Id}", new { apiKey.Id, key = plaintextKey });
        });

        apiKeys.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.ApiKeys.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });

        apiKeys.MapPost("/{id:guid}/roles", async (Guid id, AssignRoleRequest request, ProxyDbContext db) =>
        {
            if (!await db.ApiKeys.AnyAsync(x => x.Id == id) || !await db.Roles.AnyAsync(x => x.Id == request.RoleId))
            {
                return Results.NotFound();
            }

            if (!await db.Set<ApiKeyRole>().AnyAsync(x => x.ApiKeyId == id && x.RoleId == request.RoleId))
            {
                db.Add(new ApiKeyRole { ApiKeyId = id, RoleId = request.RoleId });
                await db.SaveChangesAsync();
            }

            return Results.NoContent();
        });

        apiKeys.MapDelete("/{id:guid}/roles/{roleId:guid}", async (Guid id, Guid roleId, ProxyDbContext db) =>
        {
            var affected = await db.Set<ApiKeyRole>().Where(x => x.ApiKeyId == id && x.RoleId == roleId).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapClaimMappings(RouteGroupBuilder mappings)
    {
        mappings.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.ClaimRoleMappings.AsNoTracking()
                .Select(x => new { x.Id, x.ClaimType, x.ClaimValue, x.RoleId, RoleName = x.Role.Name }).ToListAsync()));

        mappings.MapPost("/", async (CreateClaimMappingRequest request, ProxyDbContext db) =>
        {
            var mapping = new ClaimRoleMapping { ClaimType = request.ClaimType, ClaimValue = request.ClaimValue, RoleId = request.RoleId };
            db.ClaimRoleMappings.Add(mapping);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/claim-mappings/{mapping.Id}", new { mapping.Id });
        });

        mappings.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.ClaimRoleMappings.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapServers(RouteGroupBuilder servers)
    {
        servers.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.Servers.AsNoTracking().Select(x => new
            {
                x.Id, x.Name, x.NamespacePrefix, x.Endpoint, x.Enabled, x.LastSyncedAt, x.LastError,
                x.CredentialReference, x.CredentialHeader, x.CredentialPrefix
            }).ToListAsync()));

        servers.MapPost("/", async (CreateServerRequest request, ProxyDbContext db) =>
        {
            if (await db.Servers.AnyAsync(x => x.NamespacePrefix == request.NamespacePrefix))
            {
                return Results.Conflict("Namespace prefix already in use.");
            }

            var server = new McpServer
            {
                Name = request.Name,
                NamespacePrefix = request.NamespacePrefix,
                Endpoint = request.Endpoint,
                CredentialReference = request.CredentialReference,
                CredentialHeader = request.CredentialHeader,
                CredentialPrefix = request.CredentialPrefix
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/servers/{server.Id}", new { server.Id });
        });

        servers.MapPut("/{id:guid}", async (Guid id, CreateServerRequest request, ProxyDbContext db) =>
        {
            var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id);
            if (server is null)
            {
                return Results.NotFound();
            }

            if (await db.Servers.AnyAsync(x => x.Id != id && x.NamespacePrefix == request.NamespacePrefix))
            {
                return Results.Conflict("Namespace prefix already in use.");
            }

            server.Name = request.Name;
            server.NamespacePrefix = request.NamespacePrefix;
            server.Endpoint = request.Endpoint;
            server.CredentialReference = request.CredentialReference;
            server.CredentialHeader = request.CredentialHeader;
            server.CredentialPrefix = request.CredentialPrefix;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        servers.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.Servers.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });

        servers.MapPost("/{id:guid}/sync", async (Guid id, ProxyDbContext db, CatalogCache cache, CancellationToken ct) =>
        {
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (server is null)
            {
                return Results.NotFound();
            }

            await cache.RefreshOneAsync(server, ct);
            return Results.NoContent();
        });

        // Lets the UI populate a per-primitive permission picker, and browse full metadata, from the last synced catalog.
        servers.MapGet("/{id:guid}/catalog", async (Guid id, ProxyDbContext db, CatalogCache cache, CancellationToken ct) =>
        {
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (server is null)
            {
                return Results.NotFound();
            }

            if (!cache.TryGet(id, out var catalog))
            {
                return Results.Ok(new { tools = Array.Empty<object>(), resources = Array.Empty<object>(), prompts = Array.Empty<object>() });
            }

            return Results.Ok(new { tools = catalog.Tools, resources = catalog.Resources, prompts = catalog.Prompts });
        });
    }
}
