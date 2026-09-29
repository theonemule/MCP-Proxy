using System.Text.RegularExpressions;
using McpProxy.Data;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Admin;

/// <summary>Request to create or replace a model provider.</summary>
public sealed record CreateModelProviderRequest(
    string Name,
    string Slug,
    ModelProviderKind Kind,
    string? BaseEndpoint,
    string? ChatPath,
    bool Enabled,
    string? CredentialReference,
    string CredentialHeader = "Authorization",
    string CredentialPrefix = "Bearer ",
    string? AwsRegion = null,
    string? AwsAccessKeyReference = null,
    string? AwsSecretKeyReference = null,
    string? AwsSessionTokenReference = null);

/// <summary>Request to create or replace a public model route.</summary>
public sealed record CreateModelRouteRequest(
    Guid ProviderId,
    string PublicName,
    string DownstreamModel,
    bool Enabled = true);

/// <summary>Request to add or replace one weighted/failover target behind a public route.</summary>
public sealed record CreateModelRouteTargetRequest(
    Guid ModelRouteId,
    Guid ProviderId,
    string DownstreamModel,
    int Priority = 100,
    int Weight = 100,
    bool Enabled = true);

/// <summary>Request to grant a role access to a provider or model route.</summary>
public sealed record CreateModelPermissionRequest(
    Guid RoleId,
    ModelPermissionScope Scope,
    Guid? ProviderId,
    Guid? ModelRouteId);

/// <summary>Administrative endpoints for model providers, public model aliases, and their role grants.</summary>
public static partial class ModelAdminEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSlugRegex();

    /// <summary>Maps model-router administration into the existing /admin route group.</summary>
    public static void MapModelAdminEndpoints(this RouteGroupBuilder admin)
    {
        MapProviders(admin.MapGroup("/model-providers").RequireAdminScope(AdminScope.ServerAdmin));
        MapRoutes(admin.MapGroup("/model-routes").RequireAdminScope(AdminScope.ServerAdmin));
        MapRouteTargets(admin.MapGroup("/model-route-targets").RequireAdminScope(AdminScope.ServerAdmin));
        MapPermissions(admin.MapGroup("/model-permissions").RequireAdminScope(AdminScope.UserAdmin));
    }

    private static void MapProviders(RouteGroupBuilder providers)
    {
        providers.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.ModelProviders.AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.Slug,
                    x.Kind,
                    x.BaseEndpoint,
                    x.ChatPath,
                    x.Enabled,
                    x.CredentialReference,
                    x.CredentialHeader,
                    x.CredentialPrefix,
                    x.AwsRegion,
                    x.AwsAccessKeyReference,
                    x.AwsSecretKeyReference,
                    x.AwsSessionTokenReference
                })
                .ToListAsync()));

        providers.MapPost("/", async (CreateModelProviderRequest request, ProxyDbContext db) =>
        {
            var validation = await ValidateProviderAsync(request, null, db);
            if (validation is not null) return validation;

            var provider = new ModelProvider
            {
                Name = request.Name.Trim(),
                Slug = request.Slug.Trim(),
                Kind = request.Kind,
                BaseEndpoint = request.BaseEndpoint?.Trim() ?? "",
                ChatPath = string.IsNullOrWhiteSpace(request.ChatPath) ? null : request.ChatPath.Trim(),
                Enabled = request.Enabled,
                CredentialReference = NullIfWhiteSpace(request.CredentialReference),
                CredentialHeader = request.CredentialHeader,
                CredentialPrefix = request.CredentialPrefix,
                AwsRegion = NullIfWhiteSpace(request.AwsRegion),
                AwsAccessKeyReference = NullIfWhiteSpace(request.AwsAccessKeyReference),
                AwsSecretKeyReference = NullIfWhiteSpace(request.AwsSecretKeyReference),
                AwsSessionTokenReference = NullIfWhiteSpace(request.AwsSessionTokenReference)
            };
            db.ModelProviders.Add(provider);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/model-providers/{provider.Id}", new { provider.Id });
        });

        providers.MapPut("/{id:guid}", async (
            Guid id,
            CreateModelProviderRequest request,
            ProxyDbContext db) =>
        {
            var provider = await db.ModelProviders.SingleOrDefaultAsync(x => x.Id == id);
            if (provider is null) return Results.NotFound();

            var validation = await ValidateProviderAsync(request, id, db);
            if (validation is not null) return validation;

            provider.Name = request.Name.Trim();
            provider.Slug = request.Slug.Trim();
            provider.Kind = request.Kind;
            provider.BaseEndpoint = request.BaseEndpoint?.Trim() ?? "";
            provider.ChatPath = string.IsNullOrWhiteSpace(request.ChatPath) ? null : request.ChatPath.Trim();
            provider.Enabled = request.Enabled;
            provider.CredentialReference = NullIfWhiteSpace(request.CredentialReference);
            provider.CredentialHeader = request.CredentialHeader;
            provider.CredentialPrefix = request.CredentialPrefix;
            provider.AwsRegion = NullIfWhiteSpace(request.AwsRegion);
            provider.AwsAccessKeyReference = NullIfWhiteSpace(request.AwsAccessKeyReference);
            provider.AwsSecretKeyReference = NullIfWhiteSpace(request.AwsSecretKeyReference);
            provider.AwsSessionTokenReference = NullIfWhiteSpace(request.AwsSessionTokenReference);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        providers.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            if (!await db.ModelProviders.AnyAsync(x => x.Id == id))
            {
                return Results.NotFound();
            }

            var routeIds = await db.ModelRoutes.Where(x => x.ProviderId == id).Select(x => x.Id).ToListAsync();
            await db.ModelPermissions
                .Where(x => x.ProviderId == id || (x.ModelRouteId != null && routeIds.Contains(x.ModelRouteId.Value)))
                .ExecuteDeleteAsync();
            await db.ModelRouteTargets
                .Where(x => x.ProviderId == id || routeIds.Contains(x.ModelRouteId))
                .ExecuteDeleteAsync();
            await db.ModelRoutes.Where(x => x.ProviderId == id).ExecuteDeleteAsync();
            await db.ModelProviders.Where(x => x.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }

    private static void MapRoutes(RouteGroupBuilder routes)
    {
        routes.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.ModelRoutes.AsNoTracking()
                .OrderBy(x => x.PublicName)
                .Select(x => new
                {
                    x.Id,
                    x.ProviderId,
                    ProviderName = x.Provider.Name,
                    ProviderSlug = x.Provider.Slug,
                    ProviderKind = x.Provider.Kind,
                    x.PublicName,
                    x.DownstreamModel,
                    x.Enabled
                })
                .ToListAsync()));

        routes.MapPost("/", async (CreateModelRouteRequest request, ProxyDbContext db) =>
        {
            var validation = await ValidateRouteAsync(request, null, db);
            if (validation is not null) return validation;

            var route = new ModelRoute
            {
                ProviderId = request.ProviderId,
                PublicName = request.PublicName.Trim(),
                DownstreamModel = request.DownstreamModel.Trim(),
                Enabled = request.Enabled
            };
            db.ModelRoutes.Add(route);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/model-routes/{route.Id}", new { route.Id });
        });

        routes.MapPut("/{id:guid}", async (Guid id, CreateModelRouteRequest request, ProxyDbContext db) =>
        {
            var route = await db.ModelRoutes.SingleOrDefaultAsync(x => x.Id == id);
            if (route is null) return Results.NotFound();

            var validation = await ValidateRouteAsync(request, id, db);
            if (validation is not null) return validation;

            route.ProviderId = request.ProviderId;
            route.PublicName = request.PublicName.Trim();
            route.DownstreamModel = request.DownstreamModel.Trim();
            route.Enabled = request.Enabled;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        routes.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.ModelRoutes.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapRouteTargets(RouteGroupBuilder targets)
    {
        targets.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.ModelRouteTargets.AsNoTracking()
                .OrderBy(x => x.ModelRoute.PublicName)
                .ThenBy(x => x.Priority)
                .ThenBy(x => x.Provider.Name)
                .Select(x => new
                {
                    x.Id,
                    x.ModelRouteId,
                    ModelName = x.ModelRoute.PublicName,
                    x.ProviderId,
                    ProviderName = x.Provider.Name,
                    ProviderSlug = x.Provider.Slug,
                    ProviderKind = x.Provider.Kind,
                    x.DownstreamModel,
                    x.Priority,
                    x.Weight,
                    x.Enabled
                })
                .ToListAsync()));

        targets.MapPost("/", async (CreateModelRouteTargetRequest request, ProxyDbContext db) =>
        {
            var validation = await ValidateRouteTargetAsync(request, null, db);
            if (validation is not null) return validation;

            var target = new ModelRouteTarget
            {
                ModelRouteId = request.ModelRouteId,
                ProviderId = request.ProviderId,
                DownstreamModel = request.DownstreamModel.Trim(),
                Priority = request.Priority,
                Weight = request.Weight,
                Enabled = request.Enabled
            };
            db.ModelRouteTargets.Add(target);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/model-route-targets/{target.Id}", new { target.Id });
        });

        targets.MapPut("/{id:guid}", async (
            Guid id,
            CreateModelRouteTargetRequest request,
            ProxyDbContext db) =>
        {
            var target = await db.ModelRouteTargets.SingleOrDefaultAsync(x => x.Id == id);
            if (target is null) return Results.NotFound();

            var validation = await ValidateRouteTargetAsync(request, id, db);
            if (validation is not null) return validation;

            target.ModelRouteId = request.ModelRouteId;
            target.ProviderId = request.ProviderId;
            target.DownstreamModel = request.DownstreamModel.Trim();
            target.Priority = request.Priority;
            target.Weight = request.Weight;
            target.Enabled = request.Enabled;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        targets.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.ModelRouteTargets.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapPermissions(RouteGroupBuilder permissions)
    {
        permissions.MapGet("/", async (ProxyDbContext db) =>
            Results.Ok(await db.ModelPermissions.AsNoTracking()
                .OrderBy(x => x.Role.Name)
                .Select(x => new
                {
                    x.Id,
                    x.RoleId,
                    RoleName = x.Role.Name,
                    x.Scope,
                    x.ProviderId,
                    ProviderName = x.Provider == null ? null : x.Provider.Name,
                    x.ModelRouteId,
                    ModelName = x.ModelRoute == null ? null : x.ModelRoute.PublicName
                })
                .ToListAsync()));

        permissions.MapPost("/", async (CreateModelPermissionRequest request, ProxyDbContext db) =>
        {
            if (!await db.Roles.AnyAsync(x => x.Id == request.RoleId))
            {
                return Results.NotFound("Role not found.");
            }

            Guid? providerId = null;
            Guid? routeId = null;

            if (request.Scope == ModelPermissionScope.Provider)
            {
                if (request.ProviderId is null ||
                    !await db.ModelProviders.AnyAsync(x => x.Id == request.ProviderId.Value))
                {
                    return Results.BadRequest("ProviderId is required and must identify an existing provider.");
                }
                providerId = request.ProviderId;
            }
            else if (request.Scope == ModelPermissionScope.Route)
            {
                if (request.ModelRouteId is null ||
                    !await db.ModelRoutes.AnyAsync(x => x.Id == request.ModelRouteId.Value))
                {
                    return Results.BadRequest("ModelRouteId is required and must identify an existing model route.");
                }
                routeId = request.ModelRouteId;
            }
            else
            {
                return Results.BadRequest("Unsupported model permission scope.");
            }

            var duplicate = await db.ModelPermissions.AnyAsync(x =>
                x.RoleId == request.RoleId &&
                x.Scope == request.Scope &&
                x.ProviderId == providerId &&
                x.ModelRouteId == routeId);
            if (duplicate)
            {
                return Results.Conflict("That model permission already exists.");
            }

            var permission = new ModelPermission
            {
                RoleId = request.RoleId,
                Scope = request.Scope,
                ProviderId = providerId,
                ModelRouteId = routeId
            };
            db.ModelPermissions.Add(permission);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/model-permissions/{permission.Id}", new { permission.Id });
        });

        permissions.MapDelete("/{id:guid}", async (Guid id, ProxyDbContext db) =>
        {
            var affected = await db.ModelPermissions.Where(x => x.Id == id).ExecuteDeleteAsync();
            return affected > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static async Task<IResult?> ValidateProviderAsync(
        CreateModelProviderRequest request,
        Guid? existingId,
        ProxyDbContext db)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Slug) ||
            !SafeSlugRegex().IsMatch(request.Slug.Trim()))
        {
            return Results.BadRequest("Name and a URL-safe slug are required.");
        }

        if (await db.ModelProviders.AnyAsync(x => x.Slug == request.Slug.Trim() && x.Id != existingId))
        {
            return Results.Conflict("Provider slug already exists.");
        }

        if (request.Kind != ModelProviderKind.AwsBedrock)
        {
            if (!Uri.TryCreate(request.BaseEndpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest("A valid HTTP/HTTPS BaseEndpoint is required.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.BaseEndpoint) &&
                 (!Uri.TryCreate(request.BaseEndpoint, UriKind.Absolute, out var bedrockUri) ||
                  (bedrockUri.Scheme != Uri.UriSchemeHttp && bedrockUri.Scheme != Uri.UriSchemeHttps)))
        {
            return Results.BadRequest("BaseEndpoint must be HTTP/HTTPS when supplied.");
        }

        if (string.IsNullOrWhiteSpace(request.CredentialHeader))
        {
            return Results.BadRequest("CredentialHeader cannot be empty.");
        }

        return null;
    }

    private static async Task<IResult?> ValidateRouteAsync(
        CreateModelRouteRequest request,
        Guid? existingId,
        ProxyDbContext db)
    {
        if (!await db.ModelProviders.AnyAsync(x => x.Id == request.ProviderId))
        {
            return Results.BadRequest("ProviderId must identify an existing provider.");
        }

        if (string.IsNullOrWhiteSpace(request.PublicName) || string.IsNullOrWhiteSpace(request.DownstreamModel))
        {
            return Results.BadRequest("PublicName and DownstreamModel are required.");
        }

        if (await db.ModelRoutes.AnyAsync(x => x.PublicName == request.PublicName.Trim() && x.Id != existingId))
        {
            return Results.Conflict("Public model name already exists.");
        }

        return null;
    }

    private static async Task<IResult?> ValidateRouteTargetAsync(
        CreateModelRouteTargetRequest request,
        Guid? existingId,
        ProxyDbContext db)
    {
        if (!await db.ModelRoutes.AnyAsync(x => x.Id == request.ModelRouteId))
        {
            return Results.BadRequest("ModelRouteId must identify an existing public model route.");
        }

        if (!await db.ModelProviders.AnyAsync(x => x.Id == request.ProviderId))
        {
            return Results.BadRequest("ProviderId must identify an existing provider.");
        }

        if (string.IsNullOrWhiteSpace(request.DownstreamModel))
        {
            return Results.BadRequest("DownstreamModel is required.");
        }

        if (request.Priority is < 0 or > 100000)
        {
            return Results.BadRequest("Priority must be between 0 and 100000.");
        }

        if (request.Weight is < 1 or > 10000)
        {
            return Results.BadRequest("Weight must be between 1 and 10000.");
        }

        var downstream = request.DownstreamModel.Trim();
        if (await db.ModelRouteTargets.AnyAsync(x =>
                x.ModelRouteId == request.ModelRouteId &&
                x.ProviderId == request.ProviderId &&
                x.DownstreamModel == downstream &&
                x.Id != existingId))
        {
            return Results.Conflict("That provider/model target is already configured for this route.");
        }

        var route = await db.ModelRoutes.AsNoTracking()
            .SingleAsync(x => x.Id == request.ModelRouteId);
        if (route.ProviderId == request.ProviderId &&
            route.DownstreamModel == downstream)
        {
            return Results.Conflict("That provider/model pair is already the route's implicit primary target.");
        }

        return null;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
