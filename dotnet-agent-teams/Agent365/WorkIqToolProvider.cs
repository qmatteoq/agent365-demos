using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace LearnTeamsAgent.Agent365;

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
/// directly on behalf of the signed-in user.
/// </summary>
/// <remarks>
/// This deliberately bypasses <c>IMcpToolRegistrationService.GetMcpToolsAsync</c> from the A365
/// Tooling SDK. That call first asks the tooling gateway which servers the agent may use, at
/// <c>/agents/v2/{agentId}/mcpServers</c>, and that route is failing service side: it returns 500
/// even for a syntactically valid but non-existent agent id, where a 404 would be expected. The
/// path is hardcoded in every published version of <c>Microsoft.Agents.A365.Tooling</c>, so there is
/// no SDK-level way around it.
///
/// The servers themselves are healthy. They are addressed directly from the local manifest, which
/// already carries the url, audience and scope for each one, so discovery is not needed. This is
/// the same approach the non-Teams agent in this repository uses.
/// </remarks>
public sealed class WorkIqToolProvider(
    WorkIqTokenService tokens,
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
        string userAssertion,
        CancellationToken cancellationToken = default)
    {
        if (Servers.Count == 0 || !tokens.IsConfigured)
        {
            return [];
        }

        var tools = new List<AITool>();

        foreach (var server in Servers)
        {
            try
            {
                // The audience is an app id rather than an identifier URI, so the resource is
                // expressed as "<appId>/.default". Requesting .default keeps the request valid for
                // whatever the tenant administrator consented to.
                var token = await tokens
                    .GetToolTokenAsync(userAssertion, server.Audience, cancellationToken)
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
