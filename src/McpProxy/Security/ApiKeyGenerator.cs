using System.Security.Cryptography;

namespace McpProxy.Security;

/// <summary>
/// Generates and verifies API key secrets. The key returned to a caller at creation time is
/// never stored; only a prefix (for lookup) and a hash of the full key (for verification) persist.
/// </summary>
public static class ApiKeyGenerator
{
    /// <summary>Creates a new prefix.secret key and returns only its hash for persistence.</summary>
    public static (string Prefix, string PlaintextKey, string SecretHash) Generate()
    {
        var prefix = "mcp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var plaintextKey = $"{prefix}.{secret}";
        return (prefix, plaintextKey, Hash(secret));
    }

    /// <summary>Hashes a key secret with SHA-256 for database comparison.</summary>
    public static string Hash(string secret) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    /// <summary>Splits a presented key into its public prefix and secret component.</summary>
    public static bool TrySplit(string presentedKey, out string prefix, out string secret)
    {
        var parts = presentedKey.Split('.', 2);
        if (parts.Length == 2)
        {
            prefix = parts[0];
            secret = parts[1];
            return true;
        }

        prefix = "";
        secret = "";
        return false;
    }

    /// <summary>Extracts a gateway API key from the HTTP Bearer form used by OpenAI clients.</summary>
    public static bool TryGetBearerApiKey(string? authorization, out string apiKey)
    {
        apiKey = "";
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = authorization["Bearer ".Length..].Trim();
        if (!candidate.StartsWith("mcp_", StringComparison.Ordinal) ||
            !TrySplit(candidate, out _, out _))
        {
            return false;
        }

        apiKey = candidate;
        return true;
    }

    /// <summary>Compares a presented secret with a stored hash using constant-time comparison.</summary>
    public static bool Verify(string presentedSecret, string secretHash) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(Hash(presentedSecret)),
            Convert.FromBase64String(secretHash));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
