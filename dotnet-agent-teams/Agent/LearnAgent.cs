using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.A365.Observability.Hosting.Extensions;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using LearnTeamsAgent.Agent365;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using ObsRequest = Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts.Request;

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
    private readonly WorkIqToolProvider _workIqTools;
    private readonly LearnMcpTools _learnTools;
    private readonly IConfiguration _configuration;
    private readonly string? _agenticAuthHandlerName;
    private readonly string? _oboAuthHandlerName;

    public LearnAgent(
        AgentApplicationOptions options,
        AIAgent agent,
        ConversationSessionStore sessions,
        WorkIqToolProvider workIqTools,
        LearnMcpTools learnTools,
        IConfiguration configuration,
        ILogger<LearnAgent> logger) : base(options)
    {
        _agent = agent;
        _sessions = sessions;
        _logger = logger;
        _workIqTools = workIqTools;
        _learnTools = learnTools;
        _configuration = configuration;

        // The handler names come from AgentApplication:UserAuthorization:Handlers in appsettings.json.
        _agenticAuthHandlerName = configuration["AgentApplication:AgenticAuthHandlerName"] ?? "agentic";
        _oboAuthHandlerName = configuration["AgentApplication:OboAuthHandlerName"];

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);
        OnMessage("/reset", ResetAsync);
        OnMessage("/signout", SignOutAsync);

        // Sign-in is attached to THIS route only, rather than enabled globally. Global
        // AutoSignIn fires on every activity - including the conversationUpdate raised when the
        // app is installed - so the user was prompted (and saw a failure) before they had even
        // asked anything. Only a real question needs the Microsoft 365 tools.
        if (!string.IsNullOrEmpty(_oboAuthHandlerName))
        {
            OnActivity(
                ActivityTypes.Message,
                OnMessageAsync,
                rank: RouteRank.Last,
                autoSignInHandlers: [_oboAuthHandlerName]);
        }
        else
        {
            OnActivity(ActivityTypes.Message, OnMessageAsync, rank: RouteRank.Last);
        }

        // Sign-in is silent when Teams SSO succeeds, so a failure would otherwise surface only
        // as missing WorkIQ tools. Surface it to the user and the log instead.
        UserAuthorization.OnUserSignInFailure(async (turnContext, turnState, handlerName, response, initiatingActivity, ct) =>
        {
            _logger.LogWarning(
                "Sign-in failed for handler {Handler}: {Cause} {Error}",
                handlerName, response.Cause, response.Error?.Message);

            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    "I couldn't sign you in, so my Microsoft 365 tools (mail, calendar, Teams) aren't available. " +
                    "I can still research Microsoft Learn for you."),
                ct);
        });
    }

    private async Task SignOutAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        await UserAuthorization.SignOutUserAsync(turnContext, turnState, cancellationToken: cancellationToken);
        await turnContext.SendActivityAsync(
            MessageFactory.Text("Signed out. Your Microsoft 365 tools will reconnect next time you ask something."),
            cancellationToken);
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

        // A365 Observability — best-effort instrumentation (verify against official sample)
        var (agentId, tenantId) = ResolveObservabilityIdentity(turnContext);
        var hasObservabilityIdentity = !string.IsNullOrEmpty(agentId) && !string.IsNullOrEmpty(tenantId);

        // Baggage flows the tenant and agent id onto every child span the AI SDK emits. Without it
        // the exporter cannot group the spans and silently drops them.
        //
        // FromTurnContext adds what identifies the *human* on this turn - user.id and user.name off
        // Activity.From - plus microsoft.channel.name, which certification requires alongside the
        // tenant and conversation ids. Setting those by hand left every child span with no caller,
        // so only the parent InvokeAgent span (which carries CallerDetails) knew who was asking.
        //
        // Order matters. FromTurnContext also writes gen_ai.agent.id from Recipient.AgenticAppId,
        // which is null on a non-agentic Teams turn, so the explicit values have to come after it
        // to win - BaggageBuilder keeps one dictionary and the last Set for a key survives.
        using IDisposable? baggageScope = hasObservabilityIdentity
            ? new BaggageBuilder()
                .FromTurnContext(turnContext)
                .TenantId(tenantId!)
                .AgentId(agentId!)
                .ConversationId(turnContext.Activity.Conversation?.Id ?? string.Empty)
                .Build()
            : null;

        // InvokeAgentScope emits the parent "InvokeAgent" record. Without it Advanced Hunting shows
        // only orphan inference rows and never renders the agent turn.
        using var invokeScope = hasObservabilityIdentity
            ? StartInvokeAgentScope(turnContext, agentId!, tenantId!, question)
            : null;

        try
        {
            var session = await _sessions.GetOrCreateAsync(turnContext.Activity.Conversation.Id, cancellationToken);

            // A365 WorkIQ — added by add-workiq-tools skill
            // WorkIQ tools are resolved per turn because each one is called with a token exchanged
            // for the current user, so the agent can only touch mail, calendar and Teams data that
            // the user can already see themselves.
            var runOptions = await BuildRunOptionsAsync(turnContext, agentId, cancellationToken)
                .ConfigureAwait(false);

            var response = await _agent.RunAsync(question, session, runOptions, cancellationToken);

            var answer = response.Text;
            if (string.IsNullOrWhiteSpace(answer))
            {
                answer = "I couldn't find an answer to that in the Microsoft Learn documentation.";
            }

            invokeScope?.RecordOutputMessages([answer]);

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

    // A365 Observability — best-effort instrumentation (verify against official sample)
    /// <summary>
    /// Works out which Agent 365 identity this turn belongs to. Teams agentic turns carry the
    /// identity on the activity itself. For every other channel the agent reports under the
    /// identity that <c>a365 setup</c> provisioned for it, which is also the id pinned onto the
    /// <see cref="AIAgent"/>, so the parent and child spans agree.
    /// </summary>
    private (string? AgentId, string? TenantId) ResolveObservabilityIdentity(ITurnContext turnContext)
    {
        var agentId = turnContext.Activity.IsAgenticRequest()
            ? turnContext.Activity.GetAgenticInstanceId()
            : null;

        if (string.IsNullOrEmpty(agentId))
        {
            agentId = _configuration["Agent365Observability:AgentId"];
        }

        var tenantId = turnContext.Activity.Conversation?.TenantId
                    ?? turnContext.Activity.Recipient?.TenantId
                    ?? _configuration["Agent365Observability:TenantId"];

        return (agentId, tenantId);
    }

    // A365 Observability — best-effort instrumentation (verify against official sample)
    private InvokeAgentScope StartInvokeAgentScope(
        ITurnContext turnContext,
        string agentId,
        string tenantId,
        string question)
    {
        var observability = _configuration.GetSection("Agent365Observability");
        var blueprintId = observability["AgentBlueprintId"] ?? string.Empty;

        if (string.IsNullOrEmpty(blueprintId))
        {
            _logger.LogWarning(
                "Agent365Observability:AgentBlueprintId is empty - Defender will show per-instance " +
                "activity only, with no roll-up to the agent blueprint.");
        }

        var agentDetails = new AgentDetails(
            agentId: agentId,
            agentName: observability["AgentName"] ?? "LearnTeamsAgent",
            agentDescription: observability["AgentDescription"] ?? string.Empty,
            agentBlueprintId: blueprintId,
            tenantId: tenantId);

        var from = turnContext.Activity.From;
        var callerDetails = new CallerDetails(
            userDetails: new UserDetails(
                userId: from?.AadObjectId ?? from?.Id ?? "unknown",
                userName: from?.Name ?? "unknown"));

        var conversationId = turnContext.Activity.Conversation?.Id ?? "unknown";
        var request = new ObsRequest(
            content: question,
            sessionId: conversationId,
            channel: new Channel(turnContext.Activity.ChannelId ?? "msteams"),
            conversationId: conversationId);

        // The endpoint is trace metadata only. It is built from the blueprint GUID under the
        // reserved .invalid TLD, because the agent's display name is free text and could contain
        // characters that are not valid in a host name.
        var endpoint = string.IsNullOrEmpty(blueprintId)
            ? new Uri("https://agent.invalid/")
            : new Uri($"https://{blueprintId}.agent.invalid/");

        var scope = InvokeAgentScope.Start(
            request,
            new InvokeAgentScopeDetails(endpoint),
            agentDetails,
            callerDetails);

        scope.RecordInputMessages([question]);
        return scope;
    }

    // A365 WorkIQ — added by add-workiq-tools skill
    /// <summary>
    /// Builds the run options for this turn: the Microsoft Learn tools, which are the same for
    /// everyone, plus the WorkIQ tools resolved for the signed-in user. When WorkIQ is unavailable
    /// - no user token, or consent not yet granted - the agent still answers from Microsoft Learn.
    /// </summary>
    private async Task<ChatClientAgentRunOptions> BuildRunOptionsAsync(
        ITurnContext turnContext,
        string? agentId,
        CancellationToken cancellationToken)
    {
        var tools = new List<AITool>(_learnTools.Tools);

        var handlerName = turnContext.Activity.IsAgenticRequest()
            ? _agenticAuthHandlerName
            : _oboAuthHandlerName ?? _agenticAuthHandlerName;

        if (!string.IsNullOrEmpty(handlerName))
        {
            try
            {
                // The WorkIQ servers require a delegated token carrying Tools.ListInvoke.All, so the
                // turn's user token is the starting point for every exchange the provider performs.
                var userAssertion = await UserAuthorization
                    .GetTurnTokenAsync(turnContext, handlerName, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(userAssertion))
                {
                    _logger.LogInformation("No user token this turn; answering from Microsoft Learn only.");
                }
                else
                {
                    var workIqTools = await _workIqTools
                        .GetToolsAsync(userAssertion, cancellationToken)
                        .ConfigureAwait(false);

                    if (workIqTools.Count > 0)
                    {
                        tools.AddRange(workIqTools);
                        _logger.LogInformation("Added {Count} WorkIQ tools for this turn.", workIqTools.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WorkIQ tools are unavailable for this turn; answering from Microsoft Learn only.");
            }
        }

        // The run options replace the agent's own ChatOptions, so the instructions have to be
        // repeated here or the agent would lose them for this turn.
        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Instructions = AgentDefaults.Instructions,
            Tools = tools,
        });
    }
}
