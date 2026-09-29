using AIGovernanceGateway.Data;

namespace AIGovernanceGateway.Security;

/// <summary>Gates a route group behind an admin scope, checked against the caller's effective roles.</summary>
public static class AdminScopeRouting
{
    /// <summary>Adds an endpoint filter requiring the specified effective administrative scope.</summary>
    /// <param name="group">Route group to protect.</param>
    /// <param name="scope">User or server administration scope required by the group.</param>
    /// <returns>The same route group for fluent endpoint mapping.</returns>
    public static RouteGroupBuilder RequireAdminScope(this RouteGroupBuilder group, AdminScope scope)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var permissions = context.HttpContext.RequestServices.GetRequiredService<IPermissionService>();
            var user = context.HttpContext.User;
            if (user.Identity?.IsAuthenticated != true ||
                !await permissions.HasAdminScopeAsync(user, scope, context.HttpContext.RequestAborted))
            {
                return Results.Forbid();
            }

            return await next(context);
        });
        return group;
    }
}