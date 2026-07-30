using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Identity.Client;

namespace LearnTeamsAgent.Observability;

/// <summary>
/// Acquires the Agent 365 Observability API token used by the span exporter.
///
/// The observability backend binds the caller to the agent being written to: the token's
/// principal has to be the agent identity that appears in the export route. A delegated
/// on-behalf-of token cannot satisfy that, because its principal is the signed-in human, so
/// the backend answers 403. The token therefore has to be minted service-to-service, through
/// the federated identity chain, which yields a token whose subject is the agent:
///
///   Hop 1+2  blueprint credentials + FmiPath(agentId)  ->  assertion for the agent identity
///   Hop 3    agent identity presents that assertion     ->  Observability API token
///
/// The resulting token carries the Agent365.Observability.OtelWrite role and an azp/oid equal
/// to the agent id, which is what the export route expects.
///
/// <c>Agent365Observability:UseManagedIdentity</c> picks how the blueprint authenticates:
/// managed identity when hosted on Azure, a client secret when running locally. Managed
/// identity is attempted first when enabled and falls back to the secret, so the same
/// configuration works in both places.
/// </summary>
internal sealed class ObservabilityTokenService : BackgroundService
{
    private static readonly string[] FmiScopes = ["api://AzureADTokenExchange/.default"];
    private static readonly string[] ObservabilityScopes = ["api://9b975845-388f-4429-889e-eab1ef63949c/.default"];

    // Observability tokens live for roughly an hour; refresh early enough that a slow or failed
    // attempt still has time to retry before the cached token expires.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly IExporterTokenCache<string> _tokenCache;
    private readonly ILogger<ObservabilityTokenService> _logger;
    private readonly string _tenantId;
    private readonly string _agentId;
    private readonly string _blueprintClientId;
    private readonly string _blueprintClientSecret;
    private readonly bool _useManagedIdentity;

    public ObservabilityTokenService(
        IExporterTokenCache<string> tokenCache,
        ILogger<ObservabilityTokenService> logger,
        IConfiguration configuration)
    {
        _tokenCache = tokenCache;
        _logger = logger;

        var obs = configuration.GetSection("Agent365Observability");
        _tenantId = obs["TenantId"] ?? string.Empty;
        _agentId = obs["AgentId"] ?? string.Empty;
        _blueprintClientId = obs["ClientId"] ?? string.Empty;
        _blueprintClientSecret = obs["ClientSecret"] ?? string.Empty;
        _useManagedIdentity = obs.GetValue("UseManagedIdentity", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Agent 365 observability token service started for agent {AgentId} (managed identity: {UseManagedIdentity}).",
            _agentId,
            _useManagedIdentity);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Retry sooner after a failure than the normal refresh cadence, otherwise a single
            // transient error leaves the exporter without a token for the best part of an hour.
            var delay = RefreshInterval;

            try
            {
                await AcquireAndRegisterTokenAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                delay = RetryInterval;
                _logger.LogWarning(
                    ex,
                    "Could not acquire the Agent 365 observability token. Spans will not be exported until this succeeds. Retrying in {Delay}.",
                    delay);
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Agent 365 observability token service stopped.");
    }

    private async Task AcquireAndRegisterTokenAsync(CancellationToken cancellationToken)
    {
        var authority = new Uri($"https://login.microsoftonline.com/{_tenantId}");

        var assertion = _useManagedIdentity
            ? await AcquireAgentAssertionViaManagedIdentityAsync(authority, cancellationToken).ConfigureAwait(false)
            : await AcquireAgentAssertionViaClientSecretAsync(authority, cancellationToken).ConfigureAwait(false);

        // Hop 3: the agent identity authenticates with the assertion minted for it above, so the
        // Observability API sees the agent rather than the blueprint as the caller.
        var observabilityToken = await ConfidentialClientApplicationBuilder
            .Create(_agentId)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(assertion))
            .WithAuthority(authority)
            .Build()
            .AcquireTokenForClient(ObservabilityScopes)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        _tokenCache.RegisterObservability(_agentId, _tenantId, observabilityToken.AccessToken, ObservabilityScopes);

        _logger.LogInformation(
            "Registered an Agent 365 observability token for agent {AgentId}, valid until {ExpiresOn:u}.",
            _agentId,
            observabilityToken.ExpiresOn);
    }

    private async Task<string> AcquireAgentAssertionViaManagedIdentityAsync(Uri authority, CancellationToken cancellationToken)
    {
        try
        {
            var credential = await new ManagedIdentityCredential(new ManagedIdentityCredentialOptions())
                .GetTokenAsync(new TokenRequestContext(["api://AzureADTokenExchange"]), cancellationToken)
                .ConfigureAwait(false);

            return await AcquireAgentAssertionAsync(
                authority,
                builder => builder.WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(credential.Token)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            // Managed identity only exists on Azure infrastructure. Falling back keeps a machine
            // without an assigned identity working, provided a blueprint secret is configured.
            _logger.LogWarning(ex, "Managed identity is unavailable; falling back to the blueprint client secret.");
            return await AcquireAgentAssertionViaClientSecretAsync(authority, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<string> AcquireAgentAssertionViaClientSecretAsync(Uri authority, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_blueprintClientSecret))
        {
            throw new InvalidOperationException(
                "Agent365Observability:ClientSecret is not configured, so the blueprint cannot authenticate. " +
                "Set it in user secrets for local runs, or enable Agent365Observability:UseManagedIdentity when hosted on Azure.");
        }

        return AcquireAgentAssertionAsync(
            authority,
            builder => builder.WithClientSecret(_blueprintClientSecret),
            cancellationToken);
    }

    // Hops 1 and 2: the blueprint authenticates and asks for a token scoped to the agent through
    // the FMI path, which is what lets the agent identity speak for itself in hop 3.
    private async Task<string> AcquireAgentAssertionAsync(
        Uri authority,
        Func<ConfidentialClientApplicationBuilder, ConfidentialClientApplicationBuilder> configureCredential,
        CancellationToken cancellationToken)
    {
        var builder = ConfidentialClientApplicationBuilder.Create(_blueprintClientId);
        var application = configureCredential(builder).WithAuthority(authority).Build();

        var result = await application
            .AcquireTokenForClient(FmiScopes)
            .WithFmiPath(_agentId)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.AccessToken;
    }
}
