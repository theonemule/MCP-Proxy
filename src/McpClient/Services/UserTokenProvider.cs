using Microsoft.AspNetCore.Authentication;
using McpClient.Configuration;

namespace McpClient.Services;

/// <summary>Resolves the tokens obtained during OIDC sign-in for the current user.</summary>
public sealed class UserTokenProvider(IHttpContextAccessor accessor)
{
    /// <summary>Returns the requested OIDC token for the current authenticated request.</summary>
    public async Task<string?> GetTokenAsync(ForwardedToken kind)
    {
        if (kind == ForwardedToken.None)
        {
            return null;
        }

        var context = accessor.HttpContext;
        if (context?.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var name = kind == ForwardedToken.IdToken ? "id_token" : "access_token";
        return await context.GetTokenAsync(name);
    }
}
