namespace McpClient.Models;

/// <summary>Incoming chat request from the client UI.</summary>
public sealed record ChatRequest(string? ConversationId, string Message);

/// <summary>One downstream tool call performed while answering a chat request.</summary>
public sealed record ToolCallRecord(string Server, string Tool, string Arguments, string Result, bool IsError);

/// <summary>Chat response containing the assistant text, tool calls, and non-fatal warnings.</summary>
public sealed record ChatReply(
    string ConversationId,
    string Reply,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<string> Warnings);

/// <summary>Summary of a discovered MCP tool.</summary>
public sealed record ToolInfo(string Name, string? Title, string? Description);

/// <summary>Argument metadata for an MCP prompt.</summary>
public sealed record PromptArgumentInfo(string Name, string? Description, bool Required);

/// <summary>Summary of a discovered MCP prompt.</summary>
public sealed record PromptInfo(
    string Name,
    string? Description,
    IReadOnlyList<PromptArgumentInfo> Arguments);

/// <summary>Summary of a discovered MCP resource.</summary>
public sealed record ResourceInfo(string Uri, string Name, string? Description, string? MimeType);

/// <summary>Request to read one resource from a configured server.</summary>
public sealed record ReadResourceRequest(string Uri);

/// <summary>Request to render one prompt with optional arguments.</summary>
public sealed record GetPromptRequest(string Name, Dictionary<string, object?>? Arguments);

/// <summary>Connection and capability report for one configured MCP server.</summary>
public sealed record ServerCapabilityReport(
    string Server,
    string Endpoint,
    bool Connected,
    string? Error,
    string? ServerName,
    string? ServerVersion,
    string? Instructions,
    string CredentialForwarded,
    IReadOnlyList<ToolInfo> Tools,
    IReadOnlyList<PromptInfo> Prompts,
    IReadOnlyList<ResourceInfo> Resources,
    IReadOnlyList<ResourceInfo> ResourceTemplates);
