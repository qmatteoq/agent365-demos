using Microsoft.Agents.A365.Observability.Hosting.Caching;

namespace LearnTeamsAgent.Observability;

/// <summary>
/// Registers the pieces the Agent 365 span exporter needs to authenticate.
///
/// The exporter reads its token from <see cref="IExporterTokenCache{T}"/>. That cache is filled
/// by <see cref="ObservabilityTokenService"/>, which runs in the background and keeps a valid
/// service-to-service token available for the lifetime of the process.
/// </summary>
internal static class ObservabilityServiceExtensions
{
    public static IServiceCollection AddAgent365Observability(this IServiceCollection services)
    {
        services.AddSingleton<IExporterTokenCache<string>, ServiceTokenCache>();

        // The agent should still run when observability has not been configured, so start the
        // token service only when it has something to authenticate with. Without this guard the
        // service would loop on failures and bury the real startup output in warnings.
        services.AddSingleton<ObservabilityTokenService>();
        services.AddHostedService(sp =>
        {
            var obs = sp.GetRequiredService<IConfiguration>().GetSection("Agent365Observability");
            var logger = sp.GetRequiredService<ILogger<ObservabilityTokenService>>();

            var useManagedIdentity = obs.GetValue("UseManagedIdentity", true);

            var hasIdentity = IsConfigured(obs["TenantId"])
                           && IsConfigured(obs["AgentId"])
                           && IsConfigured(obs["ClientId"]);

            var canAuthenticate = hasIdentity
                               && (useManagedIdentity || IsConfigured(obs["ClientSecret"]));

            return new OptionalHostedService(
                canAuthenticate ? sp.GetRequiredService<ObservabilityTokenService>() : null,
                logger,
                canAuthenticate
                    ? null
                    : "Agent 365 observability is not configured, so traces will not be exported. " +
                      "Run 'a365 setup all' to provision the agent, and supply Agent365Observability:ClientSecret " +
                      "when running outside Azure.");
        });

        return services;
    }

    // `a365 setup` writes placeholder values in the <<NAME>> form when a value is still unknown,
    // so treat those as absent rather than trying to authenticate with them.
    private static bool IsConfigured(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("<<", StringComparison.Ordinal);

    /// <summary>
    /// Starts the wrapped service only when it was supplied, and explains the omission otherwise.
    /// </summary>
    private sealed class OptionalHostedService(IHostedService? inner, ILogger logger, string? skipReason) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (inner is not null)
            {
                return inner.StartAsync(cancellationToken);
            }

            if (skipReason is not null)
            {
                logger.LogWarning("{SkipReason}", skipReason);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }
}
