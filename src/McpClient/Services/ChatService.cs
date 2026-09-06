using System.Text.Json;
using System.Text.RegularExpressions;
using McpClient.Configuration;
using McpClient.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace McpClient.Services;

/// <summary>Coordinates LLM responses, MCP tool calls, conversation history, and diagnostics.</summary>
public sealed partial class ChatService(
    LlmClientFactory llmClientFactory,
    LlmSettingsStore llmSettings,
    McpSessionFactory sessionFactory,
    ConversationStore conversations,
    ILogger<ChatService> logger)
{
    /// <summary>Sends one user message through the configured LLM and available MCP tools.</summary>
    /// <param name="userId">Stable authenticated-user key for conversation isolation.</param>
    /// <param name="conversationId">Conversation history identifier.</param>
    /// <param name="message">User message to send.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Assistant text, tool-call records, and connection warnings.</returns>
    public async Task<ChatReply> SendAsync(
        string userId,
        string conversationId,
        string message,
        CancellationToken cancellationToken)
    {
        var llm = llmSettings.Get();
        var chatClient = llmClientFactory.Create(llm);

        await using var connections = await sessionFactory.ConnectAllAsync(cancellationToken);

        var warnings = connections.Failures
            .Select(f => $"MCP server '{f.Server}' unavailable: {f.Error}")
            .ToList();

        var (tools, toolOwners) = await CollectToolsAsync(connections, warnings, cancellationToken);

        var history = conversations.Get(userId, conversationId);
        lock (history)
        {
            if (history.Count == 0 && !string.IsNullOrWhiteSpace(llm.SystemPrompt))
            {
                history.Add(new ChatMessage(ChatRole.System, llm.SystemPrompt));
            }

            history.Add(new ChatMessage(ChatRole.User, message));
        }

        List<ChatMessage> snapshot;
        lock (history)
        {
            snapshot = [.. history];
        }

        var options = new ChatOptions
        {
            ModelId = llm.Model,
            Temperature = llm.Temperature,
            MaxOutputTokens = llm.MaxOutputTokens,
            Tools = tools.Count > 0 ? [.. tools] : null,
        };

        ChatResponse response;
        try
        {
            response = await chatClient.GetResponseAsync(snapshot, options, cancellationToken);
        }
        catch (Exception ex)
        {
            var activeServers = connections.Sessions.Select(s => $"{s.Options.Name} ({s.Options.Endpoint})").ToList();
            logger.LogError(ex,
                "LLM call failed. Endpoint={Endpoint}, Model={Model}, ApiKeyStatus={ApiKeyStatus}, ActiveServers={ActiveServers}",
                llm.Endpoint, llm.Model, MaskApiKey(llm.ApiKey), string.Join(", ", activeServers));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("LLM Request Failed!");
            sb.AppendLine();
            sb.AppendLine("=== DIAGNOSTIC CONTEXT ===");
            sb.AppendLine($"• LLM Endpoint:       {llm.Endpoint}");
            sb.AppendLine($"• Model / Deployment: {llm.Model}");
            sb.AppendLine($"• API Key Status:     {MaskApiKey(llm.ApiKey)}");
            sb.AppendLine($"• Max Tool Iterations:{llm.MaxToolIterations}");
            sb.AppendLine($"• History Messages:   {snapshot.Count}");
            sb.AppendLine($"• Active MCP Servers: {(activeServers.Count > 0 ? string.Join(", ", activeServers) : "None")}");
            sb.AppendLine($"• Tools Registered:   {tools.Count}");

            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== SERVER WARNINGS ===");
                foreach (var w in warnings)
                {
                    sb.AppendLine($"• {w}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== ERROR DETAILS ===");
            sb.AppendLine(ex.Message);

            if (ex.InnerException is not null)
            {
                sb.AppendLine();
                sb.AppendLine("=== INNER EXCEPTION ===");
                sb.AppendLine(ex.InnerException.Message);
            }

            throw new InvalidOperationException(sb.ToString(), ex);
        }

        lock (history)
        {
            history.AddRange(response.Messages);
        }

        return new ChatReply(
            conversationId,
            response.Text,
            ExtractToolCalls(response.Messages, toolOwners),
            warnings);
    }

    /// <summary>Connects to each enabled server and reports its discoverable capabilities.</summary>
    public async Task<IReadOnlyList<ServerCapabilityReport>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var reports = new List<ServerCapabilityReport>();

        foreach (var server in sessionFactory.EnabledServers)
        {
            try
            {
                await using var session = await sessionFactory.ConnectAsync(server, cancellationToken);
                var client = session.Client;

                var tools = await SafeListAsync(
                    async () => (await client.ListToolsAsync(cancellationToken: cancellationToken))
                        .Select(t => new ToolInfo(t.Name, t.Title, t.Description)).ToList());

                var prompts = await SafeListAsync(
                    async () => (await client.ListPromptsAsync(cancellationToken: cancellationToken))
                        .Select(p => new PromptInfo(
                            p.Name,
                            p.Description,
                            p.ProtocolPrompt.Arguments?.Select(argument => new PromptArgumentInfo(
                                argument.Name,
                                argument.Description,
                                argument.Required ?? false)).ToList() ?? []))
                        .ToList());

                var resources = await SafeListAsync(
                    async () => (await client.ListResourcesAsync(cancellationToken: cancellationToken))
                        .Select(r => new ResourceInfo(r.Uri, r.Name, r.Description, r.MimeType)).ToList());

                var templates = await SafeListAsync(
                    async () => (await client.ListResourceTemplatesAsync(cancellationToken: cancellationToken))
                        .Select(r => new ResourceInfo(r.UriTemplate, r.Name, r.Description, r.MimeType)).ToList());

                reports.Add(new ServerCapabilityReport(
                    server.Name,
                    server.Endpoint,
                    Connected: true,
                    Error: null,
                    client.ServerInfo?.Name,
                    client.ServerInfo?.Version,
                    client.ServerInstructions,
                    server.ForwardToken.ToString(),
                    tools, prompts, resources, templates));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Discovery failed for MCP server '{Server}'.", server.Name);
                reports.Add(new ServerCapabilityReport(
                    server.Name, server.Endpoint, Connected: false, Error: ex.Message,
                    null, null, null, server.ForwardToken.ToString(),
                    [], [], [], []));
            }
        }

        return reports;
    }

    private static async Task<IReadOnlyList<T>> SafeListAsync<T>(Func<Task<List<T>>> list)
    {
        try
        {
            return await list();
        }
        catch
        {
            // Capability is not supported by this server.
            return [];
        }
    }

    private async Task<(List<AITool> Tools, Dictionary<string, (string Server, string Tool)> Owners)> CollectToolsAsync(
        McpConnectionSet connections,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var tools = new List<AITool>();
        var owners = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        foreach (var session in connections.Sessions)
        {
            IList<McpClientTool> serverTools;
            try
            {
                serverTools = await session.Client.ListToolsAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not list tools for MCP server '{Server}'.", session.Options.Name);
                warnings.Add($"Could not list tools for '{session.Options.Name}': {ex.Message}");
                continue;
            }

            var prefix = SanitizeName(session.Options.Name);
            foreach (var tool in serverTools)
            {
                var qualified = $"{prefix}_{SanitizeName(tool.Name)}";
                owners[qualified] = (session.Options.Name, tool.Name);
                tools.Add(tool.WithName(qualified));
            }
        }

        return (tools, owners);
    }

    private static IReadOnlyList<ToolCallRecord> ExtractToolCalls(
        IEnumerable<ChatMessage> messages,
        Dictionary<string, (string Server, string Tool)> owners)
    {
        var calls = new Dictionary<string, (string Name, string Args)>(StringComparer.Ordinal);
        var records = new List<ToolCallRecord>();

        foreach (var content in messages.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case FunctionCallContent call:
                    calls[call.CallId] = (call.Name, Serialize(call.Arguments));
                    break;

                case FunctionResultContent result:
                    calls.TryGetValue(result.CallId, out var call2);
                    var name = call2.Name ?? result.CallId;
                    var (server, tool) = owners.TryGetValue(name, out var owner)
                        ? owner
                        : ("(unknown)", name);
                    records.Add(new ToolCallRecord(
                        server,
                        tool,
                        call2.Args ?? "{}",
                        Serialize(result.Result),
                        result.Exception is not null));
                    break;
            }
        }

        return records;
    }

    private static string MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "NOT CONFIGURED (Empty)";
        if (apiKey.Length <= 8) return $"Configured ({apiKey[..Math.Min(2, apiKey.Length)]}***, Length: {apiKey.Length})";
        return $"Configured ({apiKey[..4]}...{apiKey[^4..]}, Length: {apiKey.Length})";
    }

    private static string Serialize(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        try
        {
            return value as string ?? JsonSerializer.Serialize(value, SerializerOptions);
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static string SanitizeName(string value)
    {
        var sanitized = InvalidNameChars().Replace(value, "_");
        return string.IsNullOrEmpty(sanitized) ? "server" : sanitized;
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex InvalidNameChars();
}
