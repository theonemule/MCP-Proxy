using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AIGovernanceGateway.Data;
using AIGovernanceGateway.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIGovernanceGateway.Admin;

/// <summary>Credentials for local username/password authentication.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Input accepted by the external-token exchange endpoint.</summary>
public sealed record TokenExchangeRequest(string? Token, string? Subject, string? Username);

/// <summary>Issues internally signed bearer tokens for local logins and OIDC token exchanges.</summary>
public static class AuthEndpoints
{
    /// <summary>Maps anonymous login, session, OIDC configuration, and token-exchange endpoints.</summary>
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/config", (IOptions<AuthOptions> authOptions) => Results.Ok(new
        {
            enabled = authOptions.Value.Enabled,
            mode = authOptions.Value.Mode.ToString(),
            oidcConfigured = authOptions.Value.Mode == AuthMode.Oidc && !string.IsNullOrWhiteSpace(authOptions.Value.Authority)
        })).AllowAnonymous();

        app.MapGet("/account/login", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = LocalRedirect(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        app.MapPost("/account/logout", () =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        app.MapGet("/auth/session", async (
            HttpContext context,
            GovernanceDbContext db,
            JwtTokenService tokens,
            IOptions<AuthOptions> authOptions) =>
        {
            var principal = context.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return Results.Ok(new { authenticated = false });
            }

            var subject = principal.FindFirst("sub")?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? principal.FindFirst("preferred_username")?.Value;

            if (string.IsNullOrEmpty(subject))
            {
                return Results.Ok(new { authenticated = false });
            }

            var user = await db.Users.FirstOrDefaultAsync(x => (x.ExternalSubject == subject || x.Username == subject) && x.Enabled);
            if (user is null)
            {
                var username = principal.FindFirst("preferred_username")?.Value
                    ?? principal.FindFirst("name")?.Value
                    ?? principal.FindFirst(ClaimTypes.Name)?.Value
                    ?? subject;

                user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    ExternalSubject = subject,
                    Enabled = true
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }

            var accessToken = tokens.IssueUserToken(user.Id, user.Username, principal.Claims);
            return Results.Ok(new
            {
                authenticated = true,
                username = user.Username,
                token = accessToken
            });
        }).AllowAnonymous();

        app.MapPost("/auth/login", async (LoginRequest request, GovernanceDbContext db, JwtTokenService tokens) =>
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Username == request.Username);
            if (user is null || !user.Enabled || user.PasswordHash is null ||
                !PasswordHasher.Verify(request.Password, user.PasswordHash))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { accessToken = tokens.IssueUserToken(user.Id, user.Username) });
        });

        app.MapPost("/auth/token-exchange", ExchangeTokenAsync);
        app.MapPost("/auth/exchange", ExchangeTokenAsync);
    }

    private static string LocalRedirect(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";

    private static async Task<IResult> ExchangeTokenAsync(
        HttpContext context,
        TokenExchangeRequest? request,
        GovernanceDbContext db,
        JwtTokenService tokens,
        IOptions<AuthOptions> authOptions)
    {
        ClaimsPrincipal? principal = context.User?.Identity?.IsAuthenticated == true ? context.User : null;

        var rawToken = request?.Token;
        if (string.IsNullOrWhiteSpace(rawToken) && context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var headerVal = authHeader.ToString();
            if (headerVal.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                rawToken = headerVal["Bearer ".Length..].Trim();
            }
        }

        if (principal is null && !string.IsNullOrWhiteSpace(rawToken))
        {
            var handler = new JwtSecurityTokenHandler();
            if (handler.CanReadToken(rawToken))
            {
                try
                {
                    var jwt = handler.ReadJwtToken(rawToken);
                    var identity = new ClaimsIdentity(jwt.Claims, "TokenExchange");
                    principal = new ClaimsPrincipal(identity);
                }
                catch
                {
                    // Invalid token structure
                }
            }
        }

        var subject = principal?.FindFirst("sub")?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal?.FindFirst("preferred_username")?.Value
            ?? request?.Subject
            ?? request?.Username;

        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.BadRequest(new { error = "Unable to determine identity/subject from token or request." });
        }

        var user = await db.Users.FirstOrDefaultAsync(x => x.ExternalSubject == subject || x.Username == subject);
        if (user is null)
        {
            // Auto-provision local User record for OIDC callers on exchange
            user = new User
            {
                Id = Guid.NewGuid(),
                Username = request?.Username ?? subject,
                ExternalSubject = subject,
                Enabled = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        else if (!user.Enabled)
        {
            return Results.Unauthorized();
        }

        var claimsToForward = principal?.Claims ?? [];
        var accessToken = tokens.IssueUserToken(user.Id, user.Username, claimsToForward);

        return Results.Ok(new
        {
            accessToken,
            tokenType = "Bearer",
            expiresIn = authOptions.Value.TokenLifetimeMinutes * 60,
            userId = user.Id,
            username = user.Username
        });
    }
}