using System.ClientModel;
using McpClient.Configuration;
using Microsoft.Extensions.AI;
using OpenAI;

namespace McpClient.Services;

/// <summary>Builds an <see cref="IChatClient"/> from the currently selected hosted or gateway profile.</summary>
public sealed class LlmClientFactory(ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Builds a configured chat client. The OpenAI SDK places the configured credential in
    /// Authorization: Bearer. In gateway mode that value may be an aigw_ key or OAuth/OIDC token.
    /// <paramref name="gatewayAccessToken"/> is used when the gateway profile has no explicit value.
    /// </summary>
    public IChatClient Create(LlmOptions settings, string? gatewayAccessToken = null)
    {
        var connection = settings.ActiveConnection;

        if (string.IsNullOrWhiteSpace(connection.Endpoint))
        {
            throw new InvalidOperationException($"{settings.Source} LLM Endpoint is not configured.");
        }

        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{settings.Source} LLM Endpoint '{connection.Endpoint}' is not a valid HTTP/HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(connection.Model))
        {
            throw new InvalidOperationException($"{settings.Source} Model is not configured.");
        }

        var credential = connection.ApiKey;
        if (settings.Source == LlmSource.Gateway && string.IsNullOrWhiteSpace(credential))
        {
            credential = gatewayAccessToken;
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException(
                settings.Source == LlmSource.Gateway
                    ? "Gateway authentication is not configured. Sign in with a gateway-scoped access token or set a gateway API key / OAuth access token."
                    : "Hosted model API key is not configured.");
        }

        var openAi = new OpenAIClient(
            new ApiKeyCredential(credential),
            new OpenAIClientOptions { Endpoint = endpointUri });

        IChatClient inner = openAi.GetChatClient(connection.Model).AsIChatClient();
        return new ChatClientBuilder(inner)
            .UseFunctionInvocation(loggerFactory, configure: c => c.MaximumIterationsPerRequest = settings.MaxToolIterations)
            .UseLogging(loggerFactory)
            .Build();
    }
}