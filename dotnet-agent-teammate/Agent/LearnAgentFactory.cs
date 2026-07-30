using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace LearnTeammateAgent.Agent;

/// <summary>Microsoft Learn MCP tools, discovered once at startup - they need no user context.</summary>
public sealed record LearnMcpTools(IList<McpClientTool> Tools);

/// <summary>
/// Builds the agent for a resolved Agent 365 identity.
/// </summary>
/// <remarks>
/// The agent is built per agent id rather than registered as a single instance so that
/// <see cref="ChatClientAgentOptions.Id"/> can be pinned to the identity the observability
/// exporter authenticates with. Left unset, the SDK generates a fresh GUID and the exporter
/// produces orphan identity groups it cannot obtain a token for. In practice this caches one
/// entry per provisioned agentic instance.
/// </remarks>
public sealed class LearnAgentFactory(
    IChatClient chatClient,
    LearnMcpTools learnMcpTools,
    ILoggerFactory loggerFactory)
{
    private const string Instructions =
        "You are a Microsoft ecosystem research assistant running inside Microsoft Teams and Microsoft 365 Copilot. " +
        "You specialise in answering questions about Microsoft products and technologies - Azure, Microsoft 365, " +
        "Power Platform, .NET, Windows, Microsoft Entra, Copilot, Dynamics 365 and related services.\n" +
        "Always use the Microsoft Learn MCP tools to search and fetch authoritative documentation before " +
        "answering, even when you believe you already know the answer. Ground every factual statement in " +
        "the content you retrieved and cite the source URLs at the end of your answer.\n" +
        "If the documentation does not cover the question, say so explicitly instead of guessing. " +
        "Keep answers clear, concise and structured. Use short paragraphs and bullet lists, because long " +
        "answers are hard to read in a chat window.";

    private readonly ConcurrentDictionary<string, AIAgent> _agents = new();

    public AIAgent Get(string? agentId) =>
        _agents.GetOrAdd(agentId ?? string.Empty, id =>
        {
            var options = new ChatClientAgentOptions
            {
                Name = "LearnTeammateAgent",
                ChatOptions = new ChatOptions
                {
                    Instructions = Instructions,
                    Tools = learnMcpTools.Tools.Cast<AITool>().ToList(),
                },
            };

            if (!string.IsNullOrEmpty(id))
            {
                options.Id = id;
            }

            return new ChatClientAgent(chatClient, options, loggerFactory);
        });
}
