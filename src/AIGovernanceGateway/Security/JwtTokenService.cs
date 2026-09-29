using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AIGovernanceGateway.Security;

/// <summary>Issues internally signed bearer tokens for local user logins and token exchange.</summary>
public sealed class JwtTokenService(SymmetricSecurityKey signingKey, IOptions<AuthOptions> options)
{
    /// <summary>Issues a signed JWT for a local user and merges non-reserved additional claims.</summary>
    /// <param name="userId">Stable local user identifier.</param>
    /// <param name="username">Subject value placed in the token.</param>
    /// <param name="additionalClaims">Optional claims to copy unless they would override identity claims.</param>
    /// <returns>A compact serialized JWT.</returns>
    public string IssueUserToken(Guid userId, string username, IEnumerable<Claim>? additionalClaims = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, username),
            new(GatewayClaimTypes.PrincipalKind, "user"),
            new(GatewayClaimTypes.PrincipalId, userId.ToString())
        };

        if (additionalClaims is not null)
        {
            foreach (var claim in additionalClaims)
            {
                if (claim.Type != JwtRegisteredClaimNames.Sub &&
                    claim.Type != GatewayClaimTypes.PrincipalKind &&
                    claim.Type != GatewayClaimTypes.PrincipalId)
                {
                    claims.Add(claim);
                }
            }
        }

        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(options.Value.TokenLifetimeMinutes),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}