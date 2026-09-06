using System.ClientModel;
using McpClient.Configuration;
using Microsoft.Extensions.AI;
using OpenAI;

namespace McpClient.Services;

/// <summary>Builds an <see cref="IChatClient"/> from the LLM settings in effect right now, so changes made on the Settings page apply to the next chat turn without a restart.</summary>
public sealed class LlmClientFactory(ILoggerFactory loggerFactory)
{
    /// <summary>Builds a configured chat client and validates endpoint/model settings.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the endpoint or model is invalid.</exception>
    public IChatClient Create(LlmOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new InvalidOperationException("LLM Endpoint is not configured. Please set a valid Endpoint in Settings.");
        }
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"LLM Endpoint '{settings.Endpoint}' is not a valid absolute URL. Please check your Settings.");
        }
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("LLM Model / Deployment Name is not configured. Please set the Model in Settings.");
        }

        var openAi = new OpenAIClient(
            new ApiKeyCredential(string.IsNullOrEmpty(settings.ApiKey) ? "not-configured" : settings.ApiKey),
            new OpenAIClientOptions { Endpoint = endpointUri });

        IChatClient inner = openAi.GetChatClient(settings.Model).AsIChatClient();
        return new ChatClientBuilder(inner)
            .UseFunctionInvocation(loggerFactory, configure: c => c.MaximumIterationsPerRequest = settings.MaxToolIterations)
            .UseLogging(loggerFactory)
            .Build();
    }
}
