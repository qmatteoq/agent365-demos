using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;

namespace LearnMcpAgent.Agent365;

/// <summary>
/// One entry of <c>ToolingManifest.json</c>, written by <c>a365 develop add-mcp-servers</c>.
/// </summary>
public sealed class WorkIqServer
{
    [JsonPropertyName("mcpServerName")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("mcpServerUniqueName")] public string UniqueName { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
    [JsonPropertyName("audience")] public string Audience { get; set; } = string.Empty;
}

/// <summary>Tools exposed by one WorkIQ MCP server, with the label shown in the chat UI.</summary>
public sealed record WorkIqToolSet(string Source, IList<McpClientTool> Tools);

/// <summary>
/// Loads the WorkIQ MCP servers declared in <c>ToolingManifest.json</c> and connects to them
/// on behalf of the signed-in user.
/// </summary>
/// <remarks>
/// The A365 Tooling SDK (<c>IMcpToolRegistrationService.GetMcpToolsAsync</c>) requires an
/// <c>ITurnContext</c> and a <c>UserAuthorization</c> instance, both of which only exist in a
/// Bot Framework hosted agent. This app is a plain web agent, so it connects to the same MCP
/// endpoints directly and supplies the per-audience token itself using the agent on-behalf-of
/// chain in <see cref="AgentOboTokenService"/> — the identical token the SDK would obtain.
/// </remarks>
public sealed class WorkIqToolProvider(
    AgentOboTokenService oboTokens,
    IWebHostEnvironment environment,
    ILogger<WorkIqToolProvider> logger)
{
    private readonly Lazy<IReadOnlyList<WorkIqServer>> _servers = new(() => LoadManifest(environment, logger));

    public IReadOnlyList<WorkIqServer> Servers => _servers.Value;

    /// <summary>
    /// Connects to every configured WorkIQ server and returns their tools grouped by server. A
    /// server that fails to authenticate or respond is skipped so the agent still starts with the
    /// tools that do work.
    /// </summary>
    public async Task<IReadOnlyList<WorkIqToolSet>> GetToolsAsync(
        string userAssertion,
        CancellationToken cancellationToken = default)
    {
        var toolSets = new List<WorkIqToolSet>();

        foreach (var server in Servers)
        {
            try
            {
                // The audience is an app id, not an identifier URI, so the resource is expressed as
                // "<appId>/.default". Requesting .default keeps the request valid for whatever the
                // tenant administrator consented to via 'a365 setup permissions mcp'.
                var token = await oboTokens
                    .GetAgentTokenAsync(userAssertion, $"{server.Audience}/.default", cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(token))
                {
                    logger.LogWarning("No token for WorkIQ server {Server}; skipping.", server.Name);
                    continue;
                }

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

                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var serverTools = await client.ListToolsAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                toolSets.Add(new WorkIqToolSet(FriendlyName(server.Name), serverTools));
                logger.LogInformation("WorkIQ server {Server} exposed {Count} tools: {Tools}",
                    server.Name, serverTools.Count, string.Join(", ", serverTools.Select(t => t.Name)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WorkIQ server {Server} is unavailable; continuing without it.", server.Name);
            }
        }

        return toolSets;
    }

    // "mcp_MailTools" -> "Mail", "mcp_TeamsServer" -> "Teams" — used as the label in the chat UI.
    private static string FriendlyName(string serverName)
    {
        var name = serverName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)
            ? serverName[4..]
            : serverName;

        foreach (var suffix in (string[])["Tools", "Server", "RemoteServer"])
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name;
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
