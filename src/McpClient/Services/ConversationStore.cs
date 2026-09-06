using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace McpClient.Services;

/// <summary>In-memory chat history, partitioned per signed-in user.</summary>
public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _conversations = new();

    private static string Key(string userId, string conversationId) => $"{userId}::{conversationId}";

    /// <summary>Gets the mutable history for a user/conversation pair, creating it if necessary.</summary>
    public List<ChatMessage> Get(string userId, string conversationId) =>
        _conversations.GetOrAdd(Key(userId, conversationId), _ => []);

    /// <summary>Removes a user's conversation history if it exists.</summary>
    public void Clear(string userId, string conversationId) =>
        _conversations.TryRemove(Key(userId, conversationId), out _);
}
