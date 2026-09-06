using System.Security.Claims;
using McpProxy.Data;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Tests;

public class PermissionServiceTests
{
    private static ProxyDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProxyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProxyDbContext(options);
    }

    private static ClaimsPrincipal PrincipalFor(string kind, Guid id, params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new(ProxyClaimTypes.PrincipalKind, kind),
            new(ProxyClaimTypes.PrincipalId, id.ToString())
        };
        claims.AddRange(extraClaims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task Server_wide_permission_grants_access_to_any_tool_on_that_server()
    {
        await using var db = CreateDb();
        var server = new McpServer { Name = "S", NamespacePrefix = "s", Endpoint = "https://example/mcp" };
        var role = new Role { Name = "R" };
        var user = new User { Username = "u", PasswordHash = "x" };
        db.AddRange(server, role, user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Add(new Permission { RoleId = role.Id, ServerId = server.Id, Kind = CapabilityKind.Server });
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        var principal = PrincipalFor("user", user.Id);

        Assert.True(await permissions.CanAccessAsync(principal, server.Id, CapabilityKind.Tool, "anything", default));
    }

    [Fact]
    public async Task Granular_permission_only_grants_the_named_item()
    {
        await using var db = CreateDb();
        var server = new McpServer { Name = "S", NamespacePrefix = "s", Endpoint = "https://example/mcp" };
        var role = new Role { Name = "R" };
        var apiKey = new ApiKey { Name = "k", KeyPrefix = "p", SecretHash = "h" };
        db.AddRange(server, role, apiKey);
        db.Add(new ApiKeyRole { ApiKeyId = apiKey.Id, RoleId = role.Id });
        db.Add(new Permission { RoleId = role.Id, ServerId = server.Id, Kind = CapabilityKind.Tool, ItemName = "allowed-tool" });
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        var principal = PrincipalFor("apikey", apiKey.Id);

        Assert.True(await permissions.CanAccessAsync(principal, server.Id, CapabilityKind.Tool, "allowed-tool", default));
        Assert.False(await permissions.CanAccessAsync(principal, server.Id, CapabilityKind.Tool, "other-tool", default));
    }

    [Fact]
    public async Task Claim_role_mapping_grants_roles_without_a_direct_assignment()
    {
        await using var db = CreateDb();
        var server = new McpServer { Name = "S", NamespacePrefix = "s", Endpoint = "https://example/mcp" };
        var role = new Role { Name = "R" };
        db.AddRange(server, role);
        db.Add(new Permission { RoleId = role.Id, ServerId = server.Id, Kind = CapabilityKind.Server });
        db.Add(new ClaimRoleMapping { ClaimType = "group", ClaimValue = "engineering", RoleId = role.Id });
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        var principal = PrincipalFor("user", Guid.NewGuid(), new Claim("group", "engineering"));

        Assert.True(await permissions.CanAccessAsync(principal, server.Id, CapabilityKind.Tool, "anything", default));
    }

    [Fact]
    public async Task Principal_with_no_roles_has_no_access()
    {
        await using var db = CreateDb();
        var server = new McpServer { Name = "S", NamespacePrefix = "s", Endpoint = "https://example/mcp" };
        db.Add(server);
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        var principal = PrincipalFor("user", Guid.NewGuid());

        Assert.False(await permissions.CanAccessAsync(principal, server.Id, CapabilityKind.Tool, "anything", default));
    }

    [Fact]
    public async Task Administrator_role_is_reported_as_administrator()
    {
        await using var db = CreateDb();
        var role = new Role { Name = "Admin", IsGlobalAdmin = true };
        var user = new User { Username = "u", PasswordHash = "x" };
        db.AddRange(role, user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        Assert.True(await permissions.IsGlobalAdminAsync(PrincipalFor("user", user.Id), default));
    }

    [Fact]
    public async Task Claim_mapped_global_admin_role_grants_admin_access_without_local_user_record()
    {
        await using var db = CreateDb();
        var role = new Role { Name = "Administrator", IsGlobalAdmin = true };
        db.Add(role);
        db.Add(new ClaimRoleMapping { ClaimType = "roles", ClaimValue = "global_admin", RoleId = role.Id });
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "mcp-proxy-admin@meblaize.onmicrosoft.com"),
            new Claim("roles", "global_admin")
        ], "Oidc"));

        Assert.True(await permissions.IsGlobalAdminAsync(principal, default));
        Assert.True(await permissions.HasAdminScopeAsync(principal, AdminScope.UserAdmin, default));
        Assert.True(await permissions.HasAdminScopeAsync(principal, AdminScope.ServerAdmin, default));
    }

    [Fact]
    public async Task Scoped_admin_role_grants_only_its_scope()
    {
        await using var db = CreateDb();
        var role = new Role { Name = "UserAdmins", IsUserAdmin = true };
        var user = new User { Username = "u", PasswordHash = "x" };
        db.AddRange(role, user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        var principal = PrincipalFor("user", user.Id);

        Assert.True(await permissions.HasAdminScopeAsync(principal, AdminScope.UserAdmin, default));
        Assert.False(await permissions.HasAdminScopeAsync(principal, AdminScope.ServerAdmin, default));
        Assert.False(await permissions.IsGlobalAdminAsync(principal, default));
    }

    [Fact]
    public async Task Unmapped_token_with_matching_claims_grants_access_via_claim_role_mappings()
    {
        await using var db = CreateDb();
        var server = new McpServer { Name = "S", NamespacePrefix = "s", Endpoint = "https://example/mcp" };
        var role = new Role { Name = "R" };
        db.AddRange(server, role);
        db.Add(new Permission { RoleId = role.Id, ServerId = server.Id, Kind = CapabilityKind.Server });
        db.Add(new ClaimRoleMapping { ClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", ClaimValue = "Developer", RoleId = role.Id });
        await db.SaveChangesAsync();

        var permissions = new PermissionService(db);
        // Principal has no PrincipalKind/PrincipalId claims (unmapped user from external IdP)
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "external-user-123"),
            new Claim("roles", "Developer") // Short claim name matches full URI claim mapping
        ], "Test"));

        Assert.True(await permissions.CanAccessAsync(principal, server.Id, CapabilityKind.Tool, "any-tool", default));
    }
}
