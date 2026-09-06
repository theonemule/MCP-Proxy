using System.Security.Claims;
using System.Text.Encodings.Web;
using McpProxy.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McpProxy.Security;

/// <summary>Options marker for the proxy's API-key authentication scheme.</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>Name used when registering and forwarding the API-key scheme.</summary>
    public const string SchemeName = "ApiKey";
}

/// <summary>
/// Authenticates requests carrying the configured API key header. The key is looked up by its
/// public prefix and verified against a stored hash; the plaintext secret is never persisted.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<AuthOptions> authOptions,
    ProxyDbContext db)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, loggerFactory, encoder)
{
    /// <summary>Validates the configured header, key status, expiry, and hashed secret.</summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerName = authOptions.Value.ApiKeyHeaderName;
        if (!Request.Headers.TryGetValue(headerName, out var presented) || presented.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (!ApiKeyGenerator.TrySplit(presented.ToString(), out var prefix, out var secret))
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var apiKey = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.KeyPrefix == prefix);
        if (apiKey is null || !apiKey.Enabled ||
            (apiKey.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow) ||
            !ApiKeyGenerator.Verify(secret, apiKey.SecretHash))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        await db.ApiKeys.Where(x => x.Id == apiKey.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, apiKey.Name),
            new Claim(ProxyClaimTypes.PrincipalKind, "apikey"),
            new Claim(ProxyClaimTypes.PrincipalId, apiKey.Id.ToString())
        };
        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyAuthenticationOptions.SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}

