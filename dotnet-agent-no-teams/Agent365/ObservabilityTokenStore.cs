using System.Collections.Concurrent;

namespace LearnMcpAgent.Agent365;

/// <summary>
/// Holds the Observability API token for each (agentId, tenantId) pair.
/// The A365 exporter runs on a background flush loop with no user context, so the token has to be
/// deposited here by the request path (which does have the signed-in user's assertion) and read
/// back by the exporter's TokenResolver.
/// </summary>
public sealed class ObservabilityTokenStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string agentId, string tenantId, string token) =>
        _tokens[Key(agentId, tenantId)] = token;

    public string? Get(string agentId, string tenantId) =>
        _tokens.TryGetValue(Key(agentId, tenantId), out var token) ? token : null;

    private static string Key(string agentId, string tenantId) => $"{agentId}|{tenantId}";
}
