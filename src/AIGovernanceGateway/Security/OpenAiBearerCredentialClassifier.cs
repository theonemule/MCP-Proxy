namespace AIGovernanceGateway.Security;

/// <summary>Credential kinds accepted in OpenAI's Authorization: Bearer slot.</summary>
public enum OpenAiBearerCredentialKind
{
    /// <summary>No usable Bearer credential was supplied.</summary>
    None,
    /// <summary>A gateway-issued aigw_ API key.</summary>
    GatewayApiKey,
    /// <summary>A bearer token validated by the proxy's JWT/OIDC authentication scheme.</summary>
    BearerToken
}

/// <summary>
/// Classifies the opaque bearer value carried by OpenAI-compatible clients without changing
/// the OpenAI wire contract. Gateway keys use API-key auth; all other bearer values use JWT/OIDC.
/// </summary>
public static class OpenAiBearerCredentialClassifier
{
    /// <summary>Classifies an HTTP Authorization header used by the OpenAI-compatible surface.</summary>
    public static OpenAiBearerCredentialKind Classify(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiBearerCredentialKind.None;
        }

        var credential = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(credential))
        {
            return OpenAiBearerCredentialKind.None;
        }

        return ApiKeyGenerator.TryGetBearerApiKey(authorization, out _)
            ? OpenAiBearerCredentialKind.GatewayApiKey
            : OpenAiBearerCredentialKind.BearerToken;
    }
}