using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace LearnTeammateAgent.Agent365;

/// <summary>One entry of <c>ToolingManifest.json</c>, written by <c>a365 develop add-mcp-servers</c>.</summary>
public sealed class WorkIqServer
{
    [JsonPropertyName("mcpServerName")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("mcpServerUniqueName")] public string UniqueName { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
    [JsonPropertyName("audience")] public string Audience { get; set; } = string.Empty;
}

/// <summary>
/// Loads the WorkIQ MCP servers declared in <c>ToolingManifest.json</c> and connects to them
/// directly under the agent's own identity.
/// </summary>
/// <remarks>
/// This replaces <c>IMcpToolRegistrationService.GetMcpToolsAsync</c> from the A365 Tooling SDK,
/// which cannot run in this project at all. Every published version of
/// <c>Microsoft.Agents.A365.Tooling</c> up to 1.1.14-preview depends on
/// <c>ModelContextProtocol.Core 0.2.0-preview.3</c> and calls <c>ModelContextProtocol.Client.IMcpClient</c>.
/// This agent needs <c>ModelContextProtocol 1.3.0</c> for its Microsoft Learn client, and 1.3.0
/// removed that type, so NuGet unifies on 1.3.0 and the SDK throws at runtime:
///
///   System.TypeLoadException: Could not load type 'ModelContextProtocol.Client.IMcpClient'
///   from assembly 'ModelContextProtocol.Core, Version=1.3.0.0'
///
/// The servers themselves are healthy and the local manifest already carries the url, audience and
/// scope for each one, so the gateway's discovery call is not needed. Connecting directly also
/// sidesteps <c>/agents/v2/{agentId}/mcpServers</c>, which returns 500 for the other Teams agent in
/// this repository. Both other .NET agents here take the same approach.
///
/// What differs from those agents is the token. They exchange a signed-in user's assertion; this
/// one is an AI Teammate, so the token has to be minted for the Agentic User.
///
/// The only route that works is <c>UserAuthorization.ExchangeTurnTokenAsync</c> against the
/// "agentic" handler. Decompiling the SDK confirms this is exactly what it does itself:
/// <c>AgenticMcpTokenProvider.GetTokenAsync</c> calls
/// <c>AgenticAuthenticationService.GetAgenticUserTokenAsync</c>, which is a thin wrapper over
/// <c>ExchangeTurnTokenAsync(turnContext, handlerName, null, scopes, ct)</c>.
///
/// Two other routes were tried and are dead ends, recorded here so they are not retried:
///
///   * <c>IConnections.GetTokenProvider(...).GetAccessTokenAsync(...)</c> resolves to
///     <c>MsalAuth</c>, which only ever calls <c>AcquireTokenForClient</c>. Entra refuses that for
///     an AI Teammate outright: "AADSTS82001: Agentic application '...' is not permitted to
///     request app-only tokens for resource '...'". No scope value can rescue it.
///   * A granular scope on that same client-credential flow is rejected even earlier with
///     "AADSTS1002012: ... must have a scope value with /.default suffixed".
///
/// The scope shape below matches the SDK's own <c>Utility.ResolveTokenScopeForServer</c>:
/// "&lt;audience&gt;/&lt;scope&gt;" for a per-audience (V2) server, falling back to
/// "&lt;audience&gt;/.default" when the manifest carries no scope.
/// </remarks>
public sealed class WorkIqToolProvider(
    IWebHostEnvironment environment,
    ILogger<WorkIqToolProvider> logger)
{
    private readonly Lazy<IReadOnlyList<WorkIqServer>> _servers = new(() => LoadManifest(environment, logger));

    public IReadOnlyList<WorkIqServer> Servers => _servers.Value;

    /// <summary>
    /// Connects to every configured WorkIQ server and returns their tools. A server that fails to
    /// authenticate or respond is skipped, so the agent keeps the tools that do work and still
    /// answers from Microsoft Learn when none of them do.
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(
        UserAuthorization userAuthorization,
        ITurnContext turnContext,
        string authHandlerName,
        CancellationToken cancellationToken = default)
    {
        if (Servers.Count == 0 || string.IsNullOrWhiteSpace(authHandlerName))
        {
            return [];
        }

        var tools = new List<AITool>();

        foreach (var server in Servers)
        {
            try
            {
                var scope = string.IsNullOrWhiteSpace(server.Scope)
                    ? $"{server.Audience}/.default"
                    : $"{server.Audience}/{server.Scope}";

                var token = await userAuthorization
                    .ExchangeTurnTokenAsync(turnContext, authHandlerName, null!, [scope], cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(token))
                {
                    logger.LogWarning("No token for WorkIQ server {Server}; skipping it.", server.Name);
                    continue;
                }

                var serverTools = await ListToolsWithRetryAsync(server, token, cancellationToken)
                    .ConfigureAwait(false);

                tools.AddRange(serverTools);

                logger.LogInformation("WorkIQ server {Server} exposed {Count} tools: {Tools}",
                    server.Name, serverTools.Count, string.Join(", ", serverTools.Select(t => t.Name)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WorkIQ server {Server} is unavailable; continuing without it.", server.Name);
            }
        }

        return tools;
    }

    /// <summary>
    /// Connects to one server and lists its tools, retrying once on a transport failure. These
    /// endpoints occasionally drop the TLS connection mid-handshake, which would otherwise cost the
    /// turn every tool that server provides.
    /// </summary>
    private async Task<IList<McpClientTool>> ListToolsWithRetryAsync(
        WorkIqServer server,
        string token,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(server.Url),
                    Name = server.Name,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {token}",
                    },
                });

                var client = await McpClient
                    .CreateAsync(transport, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return await client
                    .ListToolsAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 1 && ex is HttpRequestException or IOException)
            {
                logger.LogInformation("WorkIQ server {Server} dropped the connection; retrying once.", server.Name);
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<WorkIqServer> LoadManifest(IWebHostEnvironment environment, ILogger logger)
    {
        var path = Path.Combine(environment.ContentRootPath, "ToolingManifest.json");
        if (!File.Exists(path))
        {
            logger.LogInformation("No ToolingManifest.json found; WorkIQ tools are disabled.");
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize<Manifest>(stream);
            return manifest?.McpServers ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read ToolingManifest.json; WorkIQ tools are disabled.");
            return [];
        }
    }

    private sealed class Manifest
    {
        [JsonPropertyName("mcpServers")] public List<WorkIqServer> McpServers { get; set; } = [];
    }
}
