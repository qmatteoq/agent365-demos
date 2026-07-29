using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace LearnMcpAgent.Agent365;

/// <summary>Microsoft Learn MCP tools, discovered once at startup — they need no user context.</summary>
public sealed record LearnMcpTools(IList<McpClientTool> Tools);

/// <summary>
/// Builds the agent for a signed-in user. WorkIQ tools are resolved per user, and
/// Microsoft.Extensions.AI requires tools to be supplied when the agent is created, so the agent
/// itself is built per session rather than registered as a singleton.
/// </summary>
public sealed class LearnAgentFactory(
    IChatClient chatClient,
    LearnMcpTools learnMcpTools,
    WorkIqToolProvider workIqToolProvider,
    A365Config a365Config,
    ILoggerFactory loggerFactory)
{
    private const string Instructions =
        "You are a Microsoft ecosystem research assistant. You specialise in answering questions about " +
        "Microsoft products and technologies - Azure, Microsoft 365, Power Platform, .NET, Windows, " +
        "Microsoft Entra, Copilot, Dynamics 365 and related services.\n" +
        "Always use the Microsoft Learn tools to search and fetch authoritative documentation before " +
        "answering a product question, even when you believe you already know the answer. Ground every " +
        "factual statement in the content you retrieved and cite the source URLs at the end of your answer.\n" +
        "You may also have Microsoft 365 tools available for the signed-in user's mail, calendar and Teams. " +
        "Use them when the question concerns the user's own work - for example to summarise recent mail on a " +
        "topic, check their calendar, or find a Teams conversation - and combine them with the documentation " +
        "when that produces a better answer.\n" +
        "If the documentation does not cover the question, say so explicitly instead of guessing. " +
        "Keep answers clear, concise and structured.";

    /// <summary>
    /// Creates an agent for the current user. Pass the user's access token for the blueprint app to
    /// enable WorkIQ tools; without it the agent still works with the Microsoft Learn tools only.
    /// The reporter is notified before each tool call so the UI can show live activity.
    /// </summary>
    public async Task<AIAgent> CreateAsync(
        string? userAssertion,
        ToolActivityReporter reporter,
        CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>();
        tools.AddRange(Wrap(learnMcpTools.Tools, "Microsoft Learn", reporter));

        // A365 WorkIQ — added by add-workiq-tools skill
        if (!string.IsNullOrEmpty(userAssertion))
        {
            var workIqToolSets = await workIqToolProvider.GetToolsAsync(userAssertion, cancellationToken)
                .ConfigureAwait(false);

            foreach (var toolSet in workIqToolSets)
            {
                tools.AddRange(Wrap(toolSet.Tools, toolSet.Source, reporter));
            }
        }

        var options = new ChatClientAgentOptions
        {
            Name = a365Config.AgentName,
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                Tools = tools,
            },
        };

        // Pin the agent id to the A365 agent identity. Left unset, the SDK generates a fresh GUID per
        // agent and the observability exporter produces orphan identity groups it cannot authenticate.
        if (!string.IsNullOrEmpty(a365Config.AgentIdentityClientId))
        {
            options.Id = a365Config.AgentIdentityClientId;
        }

        return new ChatClientAgent(chatClient, options, loggerFactory);
    }

    private static IEnumerable<AITool> Wrap(
        IEnumerable<McpClientTool> tools,
        string source,
        ToolActivityReporter reporter) =>
        tools.Select(tool => new ReportingAIFunction(tool, source, reporter));
}
