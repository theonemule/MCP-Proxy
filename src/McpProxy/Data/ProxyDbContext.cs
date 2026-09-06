using Microsoft.EntityFrameworkCore;

namespace McpProxy.Data;

/// <summary>Entity Framework Core context for proxy identity, RBAC, and server metadata.</summary>
public sealed class ProxyDbContext(DbContextOptions<ProxyDbContext> options) : DbContext(options)
{
    /// <summary>Roles and their administrative flags.</summary>
    public DbSet<Role> Roles => Set<Role>();
    /// <summary>Capability grants assigned to roles.</summary>
    public DbSet<Permission> Permissions => Set<Permission>();
    /// <summary>Local and externally linked users.</summary>
    public DbSet<User> Users => Set<User>();
    /// <summary>Machine credentials authenticated by API key.</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    /// <summary>Claim-to-role mappings for external identities.</summary>
    public DbSet<ClaimRoleMapping> ClaimRoleMappings => Set<ClaimRoleMapping>();
    /// <summary>Registered downstream MCP servers.</summary>
    public DbSet<McpServer> Servers => Set<McpServer>();

    /// <summary>Configures indexes, relationships, composite keys, and cascade behavior.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Role>(role =>
        {
            role.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<Permission>(permission =>
        {
            permission.HasOne(x => x.Role).WithMany(x => x.Permissions)
                .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            permission.HasOne(x => x.Server).WithMany(x => x.Permissions)
                .HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(x => x.Username).IsUnique();
            user.HasIndex(x => x.ExternalSubject);
        });

        modelBuilder.Entity<UserRole>(userRole =>
        {
            userRole.HasKey(x => new { x.UserId, x.RoleId });
            userRole.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId);
            userRole.HasOne(x => x.Role).WithMany(x => x.UserRoles).HasForeignKey(x => x.RoleId);
        });

        modelBuilder.Entity<ApiKey>(apiKey =>
        {
            apiKey.HasIndex(x => x.KeyPrefix).IsUnique();
        });

        modelBuilder.Entity<ApiKeyRole>(apiKeyRole =>
        {
            apiKeyRole.HasKey(x => new { x.ApiKeyId, x.RoleId });
            apiKeyRole.HasOne(x => x.ApiKey).WithMany(x => x.ApiKeyRoles).HasForeignKey(x => x.ApiKeyId);
            apiKeyRole.HasOne(x => x.Role).WithMany(x => x.ApiKeyRoles).HasForeignKey(x => x.RoleId);
        });

        modelBuilder.Entity<ClaimRoleMapping>(mapping =>
        {
            mapping.HasOne(x => x.Role).WithMany(x => x.ClaimMappings).HasForeignKey(x => x.RoleId);
            mapping.HasIndex(x => new { x.ClaimType, x.ClaimValue });
        });

        modelBuilder.Entity<McpServer>(server =>
        {
            server.HasIndex(x => x.NamespacePrefix).IsUnique();
        });
    }
}
