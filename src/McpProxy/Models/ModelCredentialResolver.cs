using McpProxy.Data;

namespace McpProxy.Models;

/// <summary>Resolves model-provider secrets without persisting plaintext credentials.</summary>
public static class ModelCredentialResolver
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Origin", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host",
        "X-Forwarded-Proto", "Content-Length", "Transfer-Encoding", "Connection"
    };

    /// <summary>Resolves the configured static downstream credential header.</summary>
    public static IReadOnlyDictionary<string, string> ResolveHeaders(ModelProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.CredentialReference))
        {
            return new Dictionary<string, string>();
        }

        if (ForbiddenHeaders.Contains(provider.CredentialHeader))
        {
            throw new InvalidOperationException(
                $"Model provider '{provider.Name}' may not use reserved header '{provider.CredentialHeader}' for credentials.");
        }

        var value = ResolveSecret(provider.CredentialReference);
        return new Dictionary<string, string>
        {
            [provider.CredentialHeader] = provider.CredentialPrefix + value
        };
    }

    /// <summary>Resolves a Bedrock bearer API key when configured, otherwise returns null so SigV4 can be used.</summary>
    public static IReadOnlyDictionary<string, string>? ResolveBedrockBearerHeaders(ModelProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.CredentialReference))
        {
            return ResolveHeaders(provider);
        }

        var bearerToken = Environment.GetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK");
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + bearerToken
        };
    }

    /// <summary>Resolves AWS credentials and region from explicit env references or standard AWS environment names.</summary>
    public static AwsCredentialSet ResolveAwsCredentials(ModelProvider provider)
    {
        var accessKey = ResolveOptionalSecret(provider.AwsAccessKeyReference)
            ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretKey = ResolveOptionalSecret(provider.AwsSecretKeyReference)
            ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        var sessionToken = ResolveOptionalSecret(provider.AwsSessionTokenReference)
            ?? Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");
        var region = provider.AwsRegion
            ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                $"AWS credentials for model provider '{provider.Name}' are not configured. " +
                "Set the provider env references or AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY.");
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            throw new InvalidOperationException(
                $"AWS region for model provider '{provider.Name}' is not configured.");
        }

        return new AwsCredentialSet(accessKey, secretKey, sessionToken, region);
    }

    /// <summary>Resolves one required secret reference.</summary>
    public static string ResolveSecret(string reference)
    {
        var value = ResolveOptionalSecret(reference);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Credential reference '{reference}' did not resolve to a value.");
        }

        return value;
    }

    private static string? ResolveOptionalSecret(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        const string environmentPrefix = "env:";
        if (!reference.StartsWith(environmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Model-provider secret references must use env:NAME.");
        }

        var variableName = reference[environmentPrefix.Length..];
        return Environment.GetEnvironmentVariable(variableName);
    }
}

/// <summary>Resolved AWS credential material used only for request signing in memory.</summary>
public sealed record AwsCredentialSet(string AccessKey, string SecretKey, string? SessionToken, string Region);
