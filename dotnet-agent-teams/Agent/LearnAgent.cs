using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace LearnTeamsAgent.Agent;

/// <summary>
/// Microsoft Learn research agent surfaced through the Microsoft 365 Agents SDK,
/// so the same code answers in Teams, Microsoft 365 Copilot and the Agents Playground.
/// </summary>
public class LearnAgent : AgentApplication
{
    private const string WelcomeText =
        "Hi! I'm the **Microsoft Learn agent**. Ask me anything about the Microsoft ecosystem - " +
        "Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot - and I'll research the answer " +
        "in the official Microsoft Learn documentation and cite my sources.\n\n" +
        "Try: *\"What are the authentication options for Azure Container Apps?\"*\n\n" +
        "Send `/reset` at any time to start a fresh conversation.";

    private readonly AIAgent _agent;
    private readonly ConversationSessionStore _sessions;
    private readonly ILogger<LearnAgent> _logger;

    public LearnAgent(
        AgentApplicationOptions options,
        AIAgent agent,
        ConversationSessionStore sessions,
        ILogger<LearnAgent> logger) : base(options)
    {
        _agent = agent;
        _sessions = sessions;
        _logger = logger;

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);
        OnMessage("/reset", ResetAsync);
        OnActivity(ActivityTypes.Message, OnMessageAsync, rank: RouteRank.Last);
    }

    private static async Task WelcomeAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        foreach (var member in turnContext.Activity.MembersAdded ?? [])
        {
            if (member.Id != turnContext.Activity.Recipient?.Id)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(WelcomeText), cancellationToken);
            }
        }
    }

    private async Task ResetAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        _sessions.Reset(turnContext.Activity.Conversation.Id);
        await turnContext.SendActivityAsync(
            MessageFactory.Text("Conversation cleared. What would you like to research?"),
            cancellationToken);
    }

    private async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var question = turnContext.Activity.Text?.Trim();

        if (string.IsNullOrEmpty(question))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Send me a question about the Microsoft ecosystem and I'll look it up on Microsoft Learn."),
                cancellationToken);
            return;
        }

        // Researching on Microsoft Learn takes a few seconds, so keep the channel from timing out.
        await turnContext.SendActivityAsync(new Activity { Type = ActivityTypes.Typing }, cancellationToken);

        try
        {
            var session = await _sessions.GetOrCreateAsync(turnContext.Activity.Conversation.Id, cancellationToken);
            var response = await _agent.RunAsync(question, session, cancellationToken: cancellationToken);

            var answer = response.Text;
            if (string.IsNullOrWhiteSpace(answer))
            {
                answer = "I couldn't find an answer to that in the Microsoft Learn documentation.";
            }

            await turnContext.SendActivityAsync(MessageFactory.Text(answer), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer question in conversation {ConversationId}.", turnContext.Activity.Conversation.Id);
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Sorry, something went wrong while researching that. Please try again."),
                cancellationToken);
        }
    }
}
