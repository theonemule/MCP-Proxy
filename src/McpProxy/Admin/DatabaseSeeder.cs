using McpProxy.Data;
using McpProxy.Security;
using McpProxy.Models;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Admin;

/// <summary>Creates the database schema and, on a fresh database, an administrator role and user from configuration.</summary>
public static class DatabaseSeeder
{
    /// <summary>Creates the schema and seeds the configured global administrator on an empty database.</summary>
    /// <remarks>Seeding is intentionally skipped when either users or roles already exist.</remarks>
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        await db.Database.EnsureCreatedAsync();
        await ModelSchemaUpgrade.EnsureAsync(db);

        if (await db.Users.AnyAsync() || await db.Roles.AnyAsync())
        {
            return;
        }

        var adminUsername = configuration["Bootstrap:AdminUsername"];
        var adminPassword = configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrWhiteSpace(adminPassword))
        {
            return;
        }

        var adminRole = new Role { Name = "Administrator", IsGlobalAdmin = true, Description = "Full administrative access." };
        var adminUser = new User { Username = adminUsername, PasswordHash = PasswordHasher.Hash(adminPassword) };
        db.Roles.Add(adminRole);
        db.Users.Add(adminUser);
        db.Add(new UserRole { UserId = adminUser.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();
    }
}
