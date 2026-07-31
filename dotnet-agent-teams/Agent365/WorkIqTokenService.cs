using System.Collections.Concurrent;
using System.Text.Json;

namespace LearnTeamsAgent.Agent365;

/// <summary>
/// Mints the per-audience delegated tokens the WorkIQ MCP servers require.
/// </summary>
/// <remarks>
/// The servers reject anything that does not carry the delegated <c>Tools.ListInvoke.All</c>
/// scope - an app-only token is answered with
/// <c>403 "Scope 'Tools.ListInvoke.All' is not present in the request"</c> - so the chain starts
/// from the signed-in user's Teams token and exchanges it on-behalf-of, three hops:
/// <list type="number">
/// <item>The bot channel app exchanges the user's Teams token for the blueprint's
/// <c>access_agent_as_user</c> scope. This is needed because the Azure Bot OAuth connection issues
/// a token whose audience is the channel app, and the final exchange only accepts an assertion
/// issued to the blueprint family.</item>
/// <item>The blueprint proves it owns the agent identity by requesting a token-exchange assertion
/// with <c>fmi_path</c> set to the agent identity.</item>
/// <item>The agent identity performs the on-behalf-of exchange for the WorkIQ audience, presenting
/// that assertion as its client credential.</item>
/// </list>
/// The token therefore belongs to the governed Agent 365 identity acting for the user, which is
/// what tool calls have to be attributed to. Exchanging with the channel app directly would also
/// satisfy the scope check - it holds the same consented permissions - but the resulting token
/// would not be tied to the agent identity, so it is deliberately not used.
/// This is the same chain the non-Teams agent in this repository uses.
/// </remarks>
public sealed class WorkIqTokenService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<WorkIqTokenService> logger)
{
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";
    private const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();

    private string TenantId => configuration["Agent365Observability:TenantId"] ?? string.Empty;
    private string BotClientId => configuration["Connections:BotConnection:Settings:ClientId"] ?? string.Empty;
    private string BotClientSecret => configuration["Connections:BotConnection:Settings:ClientSecret"] ?? string.Empty;
    private string BlueprintClientId => configuration["Agent365Observability:AgentBlueprintId"] ?? string.Empty;
    private string BlueprintClientSecret => configuration["Agent365Observability:ClientSecret"] ?? string.Empty;
    private string AgentIdentityClientId => configuration["Agent365Observability:AgentId"] ?? string.Empty;

    /// <summary>Scope the blueprint exposes so a child agent identity can act for the user.</summary>
    private string BlueprintUserScope => $"api://{BlueprintClientId}/access_agent_as_user";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(TenantId)
        && !string.IsNullOrEmpty(BotClientId)
        && !string.IsNullOrEmpty(BotClientSecret);

    /// <summary>
    /// Returns a token for <paramref name="audience"/> carrying the user's delegated
    /// <c>Tools.ListInvoke.All</c> scope, or null when the chain cannot produce one.
    /// </summary>
    /// <param name="userAssertion">The Teams token for the signed-in user, audience = bot app.</param>
    /// <param name="audience">WorkIQ server audience app id from <c>ToolingManifest.json</c>.</param>
    public async Task<string?> GetToolTokenAsync(
        string userAssertion,
        string audience,
        CancellationToken cancellationToken = default)
        => await GetTokenForScopeAsync(userAssertion, $"{audience}/.default", cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Returns a token for an explicit scope, minted through the same three-hop chain, or null when
    /// the chain cannot produce one.
    /// </summary>
    /// <remarks>
    /// Used for the Observability API, whose scope is a named permission rather than
    /// <c>/.default</c>. The distinction that matters is not the scope but the client: the token
    /// comes back with <c>azp</c> = the agent identity and the user as its subject, which is what
    /// the export route requires. A plain on-behalf-of exchange through the bot app returns
    /// <c>azp</c> = the bot app and is rejected with HTTP 403.
    /// </remarks>
    public async Task<string?> GetTokenForScopeAsync(
        string userAssertion,
        string resourceScope,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{resourceScope}|{HashAssertion(userAssertion)}";

        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpiring)
        {
            return cached.Token;
        }

        var token = await AcquireViaAgentIdentityAsync(userAssertion, resourceScope, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return null;
        }

        _cache[cacheKey] = token;
        return token.Token;
    }

    // The governed Agent 365 identity acts for the user - see the remarks on this class.
    private async Task<CachedToken?> AcquireViaAgentIdentityAsync(
        string userAssertion,
        string resourceScope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(BlueprintClientId)
            || string.IsNullOrEmpty(BlueprintClientSecret)
            || string.IsNullOrEmpty(AgentIdentityClientId))
        {
            logger.LogInformation("Agent 365 blueprint or identity is not configured; WorkIQ tools are disabled.");
            return null;
        }

        // Hop 1 - re-target the user token from the bot app to the blueprint, because the
        // on-behalf-of exchange in hop 3 only accepts an assertion issued to the blueprint family.
        var blueprintUserToken = await ExchangeOnBehalfOfAsync(
            clientId: BotClientId,
            clientSecret: BotClientSecret,
            clientAssertion: null,
            userAssertion: userAssertion,
            scope: BlueprintUserScope,
            stage: "hop 1 (user token -> blueprint)",
            cancellationToken).ConfigureAwait(false);

        if (blueprintUserToken is null) return null;

        // Hop 2 - the blueprint proves it owns the agent identity through fmi_path.
        var exchangeToken = await AcquireExchangeAssertionAsync(cancellationToken).ConfigureAwait(false);
        if (exchangeToken is null) return null;

        // Hop 3 - the agent identity performs the final on-behalf-of exchange for the WorkIQ audience.
        return await ExchangeOnBehalfOfAsync(
            clientId: AgentIdentityClientId,
            clientSecret: null,
            clientAssertion: exchangeToken.Token,
            userAssertion: blueprintUserToken.Token,
            scope: resourceScope,
            stage: $"hop 3 ({resourceScope})",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hop 2: the blueprint authenticates with its own credential and asks for a token-exchange
    /// assertion scoped to the child agent identity. Cached and reused across audiences.
    /// </summary>
    private async Task<CachedToken?> AcquireExchangeAssertionAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "__fmi_assertion";
        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpiring)
        {
            return cached;
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = BlueprintClientId,
            ["client_secret"] = BlueprintClientSecret,
            ["scope"] = TokenExchangeScope,
            ["fmi_path"] = AgentIdentityClientId,
            ["grant_type"] = "client_credentials",
        };

        var token = await PostTokenRequestAsync(form, "hop 2 (token exchange)", cancellationToken)
            .ConfigureAwait(false);

        if (token is not null)
        {
            _cache[cacheKey] = token;
        }

        return token;
    }

    private Task<CachedToken?> ExchangeOnBehalfOfAsync(
        string clientId,
        string? clientSecret,
        string? clientAssertion,
        string userAssertion,
        string scope,
        string stage,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = scope,
            ["grant_type"] = JwtBearerGrantType,
            ["assertion"] = userAssertion,
            ["requested_token_use"] = "on_behalf_of",
        };

        if (!string.IsNullOrEmpty(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }

        if (!string.IsNullOrEmpty(clientAssertion))
        {
            form["client_assertion_type"] = JwtBearerAssertionType;
            form["client_assertion"] = clientAssertion;
        }

        return PostTokenRequestAsync(form, stage, cancellationToken);
    }

    private async Task<CachedToken?> PostTokenRequestAsync(
        Dictionary<string, string> form,
        string stage,
        CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(nameof(WorkIqTokenService));
        var url = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";

        using var response = await http
            .PostAsync(url, new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("WorkIQ token {Stage} failed: {Status} {Body}", stage, (int)response.StatusCode, body);
            return null;
        }

        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

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
