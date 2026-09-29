namespace AIGovernanceGateway.Security;

/// <summary>Selects how bearer-token user authentication is validated.</summary>
public enum AuthMode
{
    /// <summary>Validate bearer tokens issued by this proxy.</summary>
    Internal,
    /// <summary>Validate bearer tokens and interactive sign-in through an external OIDC issuer.</summary>
    Oidc
}

/// <summary>Binds the "Auth" configuration section.</summary>
public sealed class AuthOptions
{
    /// <summary>Whether authentication middleware and protected routes are enabled.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Bearer-token validation mode.</summary>
    public AuthMode Mode { get; set; } = AuthMode.Internal;

    /// <summary>Base64 symmetric key signing internally issued tokens. Generated at startup if unset.</summary>
    public string? SigningKey { get; set; }
    /// <summary>Lifetime in minutes for internally issued JWTs.</summary>
    public int TokenLifetimeMinutes { get; set; } = 60;

    /// <summary>OIDC authority metadata endpoint.</summary>
    public string? Authority { get; set; }
    /// <summary>OIDC API audience accepted by bearer authentication.</summary>
    public string? Audience { get; set; }
    /// <summary>OIDC confidential-client identifier.</summary>
    public string? ClientId { get; set; }
    /// <summary>OIDC client secret or an <c>env:</c> reference.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Whether the OIDC metadata endpoint must use HTTPS.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Whether the API-key authentication scheme is available.</summary>
    public bool ApiKeysEnabled { get; set; } = true;
    /// <summary>Header containing an API key.</summary>
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
}