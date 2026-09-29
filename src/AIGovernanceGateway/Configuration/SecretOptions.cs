namespace AIGovernanceGateway.Configuration;

/// <summary>Resolves named secrets without coupling application code to a storage product.</summary>
public interface ISecretProvider
{
    /// <summary>Returns a secret value, or null when the key is not configured.</summary>
    string? GetSecret(string key);
}

/// <summary>Resolves secrets from the current process environment.</summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    /// <inheritdoc />
    public string? GetSecret(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return value;
    }
}

/// <summary>Resolves literal values and <c>env:VARIABLE_NAME</c> references.</summary>
public static class SecretReferenceResolver
{
    /// <summary>Resolves a configured value through the supplied provider when it is an environment reference.</summary>
    public static string? Resolve(string? value, ISecretProvider provider)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var key = value[4..];
        var secret = provider.GetSecret(key);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException($"Secret '{key}' is not configured.");
        }

        return secret;
    }
}

/// <summary>Declares the configured secret provider and known secret names.</summary>
public sealed class SecretOptions
{
    /// <summary>Provider identifier; currently <c>environment</c>.</summary>
    public string Provider { get; set; } = "environment";
    /// <summary>Optional list of secret names used by deployment tooling.</summary>
    public string[] Keys { get; set; } = [];
}