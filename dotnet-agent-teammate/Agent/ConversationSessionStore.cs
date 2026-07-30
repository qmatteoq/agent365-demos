using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace LearnTeammateAgent.Agent;

/// <summary>
/// Keeps one <see cref="AgentSession"/> per Teams conversation so the agent remembers
/// the earlier turns of a chat. Sessions live in memory only - restarting the agent
/// resets every conversation.
/// </summary>
public sealed class ConversationSessionStore
{
    private readonly ConcurrentDictionary<string, Task<AgentSession>> _sessions = new();

    /// <remarks>
    /// The <see cref="Task{TResult}"/> - rather than the session itself - is cached so that two
    /// activities arriving on the same conversation at once still share a single session.
    /// </remarks>
    public Task<AgentSession> GetOrCreateAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken) =>
        _sessions.GetOrAdd(conversationId, _ => agent.CreateSessionAsync(cancellationToken).AsTask());

    public void Reset(string conversationId) => _sessions.TryRemove(conversationId, out _);
}
