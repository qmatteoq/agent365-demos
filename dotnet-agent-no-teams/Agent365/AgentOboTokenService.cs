using System.Collections.Concurrent;
using System.Text.Json;

namespace LearnMcpAgent.Agent365;

/// <summary>
/// Configuration for the Agent 365 on-behalf-of token chain.
/// </summary>
public sealed class A365Config
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Agent blueprint app (client) id — the parent application.</summary>
    public string BlueprintClientId { get; set; } = string.Empty;

    /// <summary>Client secret of the blueprint app. Supplied via user-secrets, never appsettings.</summary>
    public string BlueprintClientSecret { get; set; } = string.Empty;

    /// <summary>Agent identity app id — the child identity the blueprint impersonates.</summary>
    public string AgentIdentityClientId { get; set; } = string.Empty;

    public string AgentName { get; set; } = string.Empty;

    /// <summary>Scope the web client requests so the resulting user token targets the blueprint.</summary>
    public string AgentUserScope => $"api://{BlueprintClientId}/access_agent_as_user";
}

/// <summary>
/// Implements the Entra "agent on-behalf-of" flow:
///   Hop 1  blueprint + client secret + fmi_path=&lt;agent identity&gt;  -> T1 (token exchange assertion)
///   Hop 2  agent identity + T1 (client_assertion) + user token (assertion) -> downstream resource token
/// See https://learn.microsoft.com/entra/agent-id/agent-on-behalf-of-oauth-flow
/// </summary>
public sealed class AgentOboTokenService(
    IHttpClientFactory httpClientFactory,
    A365Config options,
    ILogger<AgentOboTokenService> logger)
{
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();

    /// <summary>
    /// Exchanges the signed-in user's token for a downstream resource token issued to the agent identity.
    /// </summary>
    /// <param name="userAssertion">User access token whose audience is the blueprint app.</param>
    /// <param name="resourceScope">Target scope, e.g. <c>api://9b975845-.../.default</c>.</param>
    public async Task<string?> GetAgentTokenAsync(
        string userAssertion,
        string resourceScope,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{resourceScope}|{HashAssertion(userAssertion)}";
        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpiring)
        {
            return cached.Token;
        }

        try
        {
            var t1 = await AcquireExchangeTokenAsync(cancellationToken).ConfigureAwait(false);
            if (t1 is null) return null;

            var resourceToken = await AcquireResourceTokenAsync(t1, userAssertion, resourceScope, cancellationToken)
                .ConfigureAwait(false);
            if (resourceToken is null) return null;

            _cache[cacheKey] = resourceToken;
            return resourceToken.Token;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent OBO token acquisition failed for scope {Scope}.", resourceScope);
            return null;
        }
    }

    // Hop 1 — the blueprint authenticates with its own credential and asks for a token
    // exchange assertion scoped to the child agent identity via fmi_path.
    private async Task<CachedToken?> AcquireExchangeTokenAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "__t1";
        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpiring)
        {
            return cached;
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.BlueprintClientId,
            ["client_secret"] = options.BlueprintClientSecret,
            ["scope"] = TokenExchangeScope,
            ["fmi_path"] = options.AgentIdentityClientId,
            ["grant_type"] = "client_credentials",
        };

        var token = await PostTokenRequestAsync(form, "hop 1 (token exchange)", cancellationToken)
            .ConfigureAwait(false);
        if (token is not null)
        {
            _cache[cacheKey] = token;
        }
        return token;
    }

    // Hop 2 — the agent identity performs the OBO exchange, presenting T1 as its client
    // assertion and the signed-in user's token as the user assertion.
    private Task<CachedToken?> AcquireResourceTokenAsync(
        CachedToken exchangeToken,
        string userAssertion,
        string resourceScope,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.AgentIdentityClientId,
            ["scope"] = resourceScope,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = exchangeToken.Token,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = userAssertion,
            ["requested_token_use"] = "on_behalf_of",
        };

        return PostTokenRequestAsync(form, $"hop 2 ({resourceScope})", cancellationToken);
    }

    private async Task<CachedToken?> PostTokenRequestAsync(
        Dictionary<string, string> form,
        string stage,
        CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(nameof(AgentOboTokenService));
        var url = $"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/token";

        using var response = await http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Agent OBO {Stage} failed: {Status} {Body}", stage, (int)response.StatusCode, body);
            return null;
        }

        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrEmpty(accessToken)) return null;

        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        return new CachedToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static string HashAssertion(string assertion) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(assertion)))[..16];

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresOn)
    {
        // Refresh a few minutes early so a request never races the expiry.
        public bool IsExpiring => DateTimeOffset.UtcNow >= ExpiresOn.AddMinutes(-5);
    }
}
