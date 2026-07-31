using System.Collections.Concurrent;

namespace LearnTeamsAgent.Agent365;

/// <summary>
/// Holds the Observability API token for each (agentId, tenantId) pair.
/// The A365 exporter runs on a background flush loop with no user context, so the token has to be
/// deposited here by the turn (which does have the signed-in user's assertion) and read back by the
/// exporter's TokenResolver.
/// </summary>
/// <remarks>
/// This is the same store the non-Teams agent in this repository uses. It exists because the
/// exporter's TokenResolver is a synchronous lookup on a background thread: it cannot run the
/// three-hop exchange itself, and it has no ITurnContext to run it from.
/// </remarks>
public sealed class ObservabilityTokenStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string agentId, string tenantId, string token) =>
        _tokens[Key(agentId, tenantId)] = token;

    public string? Get(string agentId, string tenantId) =>
        _tokens.TryGetValue(Key(agentId, tenantId), out var token) ? token : null;

    private static string Key(string agentId, string tenantId) => $"{agentId}|{tenantId}";
}
