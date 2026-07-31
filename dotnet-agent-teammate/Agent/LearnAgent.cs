using System.Text;
using System.Text.RegularExpressions;
using AgentNotification;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
// FromTurnContext lives in Hosting.Extensions, not alongside BaggageBuilder in Runtime.Common.
using Microsoft.Agents.A365.Observability.Hosting.Extensions;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;
using Microsoft.Agents.A365.Runtime.Utils;
// A365 WorkIQ - added by add-workiq-tools skill
using LearnTeammateAgent.Agent365;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using ObsRequest = Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts.Request;

namespace LearnTeammateAgent.Agent;

/// <summary>
/// Microsoft Learn research agent surfaced through the Microsoft 365 Agents SDK as an
/// Agent 365 AI Teammate, so the same code answers in Teams, Microsoft 365 Copilot,
/// the Agents Playground - and by email, under the agent's own identity.
/// </summary>
public class LearnAgent : AgentApplication
{
    private const string WelcomeText =
        "Hi! I'm the **Microsoft Learn agent**. Ask me anything about the Microsoft ecosystem - " +
        "Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot - and I'll research the answer " +
        "in the official Microsoft Learn documentation and cite my sources.\n\n" +
        "Try: *\"What are the authentication options for Azure Container Apps?\"*\n\n" +
        "Send `/reset` at any time to start a fresh conversation.";

    private const string HireText =
        "Thanks for hiring me! Send me a question about the Microsoft ecosystem - in chat or by " +
        "email - and I'll research it on Microsoft Learn and cite my sources.";

    private const string FarewellText =
        "Thanks for working with me. My notes are cleared - reinstall me any time.";

    /// <summary>Teams expires a typing indicator after about 5 seconds, so refresh inside that window.</summary>
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);

    private readonly LearnAgentFactory _agentFactory;
    private readonly ConversationSessionStore _sessions;
    private readonly IExporterTokenCache<AgenticTokenStruct>? _tokenCache;
    // A365 WorkIQ - added by add-workiq-tools skill, rewired onto the direct provider
    private readonly WorkIqToolProvider? _workIq;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LearnAgent> _logger;
    private readonly string? _agenticAuthHandlerName;
    private readonly string? _oboAuthHandlerName;

    public LearnAgent(
        AgentApplicationOptions options,
        LearnAgentFactory agentFactory,
        ConversationSessionStore sessions,
        IExporterTokenCache<AgenticTokenStruct> tokenCache,
        WorkIqToolProvider workIq,
        IConfiguration configuration,
        ILogger<LearnAgent> logger) : base(options)
    {
        _agentFactory = agentFactory;
        _sessions = sessions;
        _tokenCache = tokenCache;
        _workIq = workIq;
        _configuration = configuration;
        _logger = logger;

        // Handler names come from configuration, not constants, so the same binary runs
        // unauthenticated in the Agents Playground and agentic in Teams.
        _agenticAuthHandlerName = configuration.GetValue<string>("AgentApplication:AgenticAuthHandlerName");
        _oboAuthHandlerName = configuration.GetValue<string>("AgentApplication:OboAuthHandlerName");

        string[] agenticHandlers = string.IsNullOrWhiteSpace(_agenticAuthHandlerName) ? [] : [_agenticAuthHandlerName];
        string[] oboHandlers = string.IsNullOrWhiteSpace(_oboAuthHandlerName) ? [] : [_oboAuthHandlerName];

        // Without this, an exception escaping the turn pipeline bubbles out of ProcessAsync
        // and the channel sees an opaque 500 with no reply to the user.
        OnTurnError(OnTurnErrorAsync);

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);

        // Hire / fire lifecycle. Registered twice: agentic for Agent 365, plain for the Playground.
        OnActivity(ActivityTypes.InstallationUpdate, OnInstallationUpdateAsync,
            isAgenticOnly: true, autoSignInHandlers: agenticHandlers);
        OnActivity(ActivityTypes.InstallationUpdate, OnInstallationUpdateAsync,
            isAgenticOnly: false);

        // Email sent to the teammate's own mailbox arrives here rather than as a message activity.
        this.OnAgenticEmailNotification(OnEmailNotificationAsync, autoSignInHandlers: agenticHandlers);

        OnMessage("/reset", ResetAsync);

        // Ranked last so the more specific routes above win.
        OnActivity(ActivityTypes.Message, OnMessageAsync,
            isAgenticOnly: true, autoSignInHandlers: agenticHandlers, rank: RouteRank.Last);
        OnActivity(ActivityTypes.Message, OnMessageAsync,
            isAgenticOnly: false, autoSignInHandlers: oboHandlers, rank: RouteRank.Last);
    }

    private async Task OnTurnErrorAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled error on a {ActivityType} turn.", turnContext.Activity?.Type);

        try
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Sorry, I hit an unexpected error. Please try again."),
                cancellationToken);
        }
        catch (Exception sendFailure)
        {
            _logger.LogError(sendFailure, "Could not deliver the turn-error message to the channel.");
        }
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

    private async Task OnInstallationUpdateAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "InstallationUpdate - action {Action} by {User}.",
            turnContext.Activity.Action,
            turnContext.Activity.From?.Name);

        if (string.Equals(turnContext.Activity.Action, "add", StringComparison.OrdinalIgnoreCase))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(HireText), cancellationToken);
        }
        else if (string.Equals(turnContext.Activity.Action, "remove", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.Reset(turnContext.Activity.Conversation?.Id ?? string.Empty);
            await turnContext.SendActivityAsync(MessageFactory.Text(FarewellText), cancellationToken);
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

        var conversationId = turnContext.Activity.Conversation?.Id ?? string.Empty;

        try
        {
            var answer = await ResearchAsync(turnContext, conversationId, question, cancellationToken);
            await turnContext.SendActivityAsync(MessageFactory.Text(answer), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer question in conversation {ConversationId}.", conversationId);
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Sorry, something went wrong while researching that. Please try again."),
                cancellationToken);
        }
    }

    /// <summary>
    /// Handles email delivered to the teammate's own mailbox. The reply is sent back as an
    /// email response activity rather than a chat message.
    /// </summary>
    private async Task OnEmailNotificationAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        AgentNotificationActivity notification,
        CancellationToken cancellationToken)
    {
        if (notification.NotificationType != NotificationTypeEnum.EmailNotification)
        {
            _logger.LogWarning("Email route received {Type}; ignoring.", notification.NotificationType);
            return;
        }

        var email = notification.EmailNotification;
        var question = HtmlToPlainText(email?.HtmlBody);

        if (string.IsNullOrWhiteSpace(question))
        {
            _logger.LogInformation("Email {EmailId} had no readable body; nothing to answer.", email?.Id);
            return;
        }

        // Thread on the mail conversation so a reply chain keeps its context.
        var conversationId = email?.ConversationId
            ?? turnContext.Activity.Conversation?.Id
            ?? string.Empty;

        _logger.LogInformation("Researching email {EmailId} on thread {ConversationId}.", email?.Id, conversationId);

        try
        {
            var answer = await ResearchAsync(turnContext, conversationId, question, cancellationToken);
            var reply = turnContext.Activity.CreateEmailResponseActivity(PlainTextToHtml(answer));
            await turnContext.SendActivityAsync(reply, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer email {EmailId}.", email?.Id);

            var reply = turnContext.Activity.CreateEmailResponseActivity(
                PlainTextToHtml("Sorry, something went wrong while researching that. Please try again."));
            await turnContext.SendActivityAsync(reply, cancellationToken);
        }
    }

    /// <summary>
    /// Runs the question through the agent while keeping a typing indicator alive, since
    /// researching on Microsoft Learn routinely takes longer than the channel's timeout.
    /// The call is wrapped in the Agent 365 observability context so the turn is traceable.
    /// </summary>
    private async Task<string> ResearchAsync(
        ITurnContext turnContext,
        string conversationId,
        string question,
        CancellationToken cancellationToken)
    {
        // A365 Observability - best-effort instrumentation (verify against official sample)
        // A365 auth mode: agentic-user - the AI Teammate acts as its own identity, so the agent id
        // is the agentic instance id from the activity rather than anything decoded from a user token.
        var isAgentic = turnContext.Activity.IsAgenticRequest();
        var authHandlerName = isAgentic ? _agenticAuthHandlerName : _oboAuthHandlerName;

        string? resolvedAgentId = null;
        if (isAgentic)
        {
            resolvedAgentId = turnContext.Activity.GetAgenticInstanceId();
        }
        else if (!string.IsNullOrEmpty(authHandlerName))
        {
            try
            {
                var authToken = await UserAuthorization
                    .GetTurnTokenAsync(turnContext, authHandlerName, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(authToken))
                {
                    resolvedAgentId = Utility.ResolveAgentIdentity(turnContext, authToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve agent id from the auth token; observability skipped for this turn.");
            }
        }

        var resolvedTenantId = turnContext.Activity.Conversation?.TenantId
            ?? turnContext.Activity.Recipient?.TenantId;

        // Without a real (agent, tenant) pair the exporter cannot obtain a token, so it is better to
        // export nothing than to invent an identity and emit spans that can never be authenticated.
        var hasObservabilityIdentity = !string.IsNullOrEmpty(resolvedAgentId)
            && !string.IsNullOrEmpty(resolvedTenantId);

        if (!hasObservabilityIdentity)
        {
            _logger.LogDebug("No Agent 365 identity on this turn; running without observability.");
        }

        // FromTurnContext supplies what identifies the *human* on this turn - user.id and user.name
        // from Activity.From - plus microsoft.channel.name and the conversation id. Baggage is what
        // reaches the child inference and execute_tool spans; CallerDetails below only decorates the
        // parent invoke_agent span, so without this the tool calls are anonymous. BaggageBuilder's
        // own remarks list tenant, conversation and channel name as certification requirements.
        //
        // Order matters. FromTurnContext also writes gen_ai.agent.id from Recipient.AgenticAppId,
        // so the explicit values are chained afterwards to win - BaggageBuilder keeps one dictionary
        // and the last Set for a key survives. This agent resolves its own id from the agentic
        // instance id instead, which stays correct on the email turns too.
        var baggageConfig = _configuration.GetSection("Agent365Observability");

        using IDisposable? baggageScope = hasObservabilityIdentity
            ? new BaggageBuilder()
                .FromTurnContext(turnContext)
                .TenantId(resolvedTenantId!)
                .AgentId(resolvedAgentId!)
                // Not supplied by FromTurnContext - the activity carries no blueprint or session.
                .AgentName(baggageConfig["AgentName"] ?? "LearnTeammateAgent")
                .AgentBlueprintId(baggageConfig["AgentBlueprintId"] ?? string.Empty)
                .ConversationId(conversationId)
                .SessionId(conversationId)
                .Build()
            : null;

        if (hasObservabilityIdentity)
        {
            try
            {
                _tokenCache?.RegisterObservability(
                    resolvedAgentId!,
                    resolvedTenantId!,
                    new AgenticTokenStruct(
                        userAuthorization: UserAuthorization,
                        turnContext: turnContext,
                        authHandlerName: authHandlerName ?? string.Empty),
                    EnvironmentUtils.GetObservabilityAuthenticationScope());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register the observability token.");
            }
        }

        var invokeScope = hasObservabilityIdentity
            ? StartInvokeScope(turnContext, question, resolvedAgentId!, resolvedTenantId!)
            : null;

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingTask = KeepTypingAsync(turnContext, typingCts.Token);

        try
        {
            var agent = _agentFactory.Get(resolvedAgentId);
            var session = await _sessions.GetOrCreateAsync(agent, conversationId, cancellationToken);

            // A365 WorkIQ - added by add-workiq-tools skill.
            // Resolved per turn rather than at startup: the tools are reached with a delegated
            // token for the agent's own Agentic User, which only exists inside a turn.
            var runOptions = await BuildWorkIqRunOptionsAsync(
                turnContext, resolvedAgentId, authHandlerName, cancellationToken);

            var response = await agent.RunAsync(question, session, runOptions, cancellationToken);

            var answer = string.IsNullOrWhiteSpace(response.Text)
                ? "I couldn't find an answer to that in the Microsoft Learn documentation."
                : response.Text;

            invokeScope?.RecordOutputMessages([answer]);
            return answer;
        }
        finally
        {
            invokeScope?.Dispose();

            await typingCts.CancelAsync();
            try
            {
                await typingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected - the loop is cancelled as soon as the answer is ready.
            }
        }
    }

    /// <summary>
    /// Resolves the WorkIQ MCP tools for this turn and returns run options carrying them.
    /// Returns null when WorkIQ is unavailable, so the turn still runs on Microsoft Learn alone.
    /// </summary>
    /// <remarks>
    /// A365 WorkIQ - added by add-workiq-tools skill, then rewired onto <see cref="WorkIqToolProvider"/>.
    /// The SDK's <c>GetMcpToolsAsync</c> cannot run in this project: it is compiled against
    /// ModelContextProtocol.Core 0.2.0-preview.3 and calls <c>IMcpClient</c>, a type 1.3.0 removed,
    /// so it throws TypeLoadException at runtime. See WorkIqToolProvider for the full reasoning.
    /// Failure here stays non-fatal - the agent degrades to Learn-only rather than failing the turn.
    /// </remarks>
    private async Task<AgentRunOptions?> BuildWorkIqRunOptionsAsync(
        ITurnContext turnContext,
        string? resolvedAgentId,
        string? authHandlerName,
        CancellationToken cancellationToken)
    {
        if (_workIq is null || string.IsNullOrEmpty(resolvedAgentId) || string.IsNullOrEmpty(authHandlerName))
        {
            // No agent identity (or no agentic auth handler) means no token can be obtained for the
            // WorkIQ audiences, which is the normal state in the Agents Playground.
            return null;
        }

        try
        {
            var tools = await _workIq
                .GetToolsAsync(UserAuthorization, turnContext, authHandlerName, cancellationToken)
                .ConfigureAwait(false);

            if (tools is null || tools.Count == 0)
            {
                _logger.LogInformation("WorkIQ returned no tools for this turn.");
                return null;
            }

            _logger.LogInformation("Loaded {Count} WorkIQ tools for this turn.", tools.Count);

            return new ChatClientAgentRunOptions(_agentFactory.CreateChatOptions(tools));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load WorkIQ tools; continuing with Microsoft Learn only.");
            return null;
        }
    }

    /// <summary>
    /// Opens the InvokeAgent scope that the Defender portal uses as the parent record for a turn.
    /// Without it, Advanced Hunting shows only orphan inference and tool rows.
    /// </summary>
    private InvokeAgentScope StartInvokeScope(
        ITurnContext turnContext,
        string question,
        string resolvedAgentId,
        string resolvedTenantId)
    {
        // A365 Observability - best-effort instrumentation (verify against official sample)
        var obsConfig = _configuration.GetSection("Agent365Observability");

        // AgentId is the agentic instance and groups activity per installed teammate;
        // AgentBlueprintId rolls that activity up to the blueprint. The .NET recipient carries no
        // blueprint field, so the blueprint id has to come from config.
        var blueprintId = obsConfig["AgentBlueprintId"] ?? string.Empty;
        if (string.IsNullOrEmpty(blueprintId))
        {
            _logger.LogWarning(
                "Agent365Observability:AgentBlueprintId is empty - Defender will show per-instance " +
                "activity with no blueprint roll-up. Set it from a365.generated.config.json.");
        }

        var agentDetails = new AgentDetails(
            agentId: resolvedAgentId,
            agentName: obsConfig["AgentName"] ?? "LearnTeammateAgent",
            agentDescription: obsConfig["AgentDescription"] ?? string.Empty,
            agentBlueprintId: blueprintId,
            tenantId: resolvedTenantId);

        var from = turnContext.Activity?.From;
        var callerDetails = new CallerDetails(
            userDetails: new UserDetails(
                userId: from?.AadObjectId ?? from?.Id ?? "unknown",
                userName: from?.Name ?? "unknown",
                // A 1:1 Teams chat carries an MRI rather than a UPN; email and mention turns do
                // carry one, so only set the tag when the value really is an address.
                userEmail: from?.Id is { } id && id.Contains('@') ? id : string.Empty));

        var conversationId = turnContext.Activity?.Conversation?.Id ?? "unknown";

        var scopeRequest = new ObsRequest(
            content: question,
            sessionId: conversationId,
            channel: new Channel(turnContext.Activity?.ChannelId ?? "msteams"),
            conversationId: conversationId);

        // Metadata only. Built from the blueprint GUID under the reserved .invalid TLD, because
        // slugifying a free-form display name risks UriFormatException.
        var endpointUri = !string.IsNullOrEmpty(blueprintId)
            ? new Uri($"https://{blueprintId}.agent.invalid/")
            : new Uri("https://agent.invalid/");

        var scope = InvokeAgentScope.Start(
            request: scopeRequest,
            scopeDetails: new InvokeAgentScopeDetails(endpoint: endpointUri),
            agentDetails: agentDetails,
            callerDetails: callerDetails);

        scope.RecordInputMessages([question]);
        return scope;
    }

    private async Task KeepTypingAsync(ITurnContext turnContext, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await turnContext.SendActivityAsync(new Activity { Type = ActivityTypes.Typing }, cancellationToken);
                await Task.Delay(TypingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A channel that rejects typing indicators must not fail the turn.
                _logger.LogDebug(ex, "Typing indicator failed; continuing without it.");
                return;
            }
        }
    }

    /// <summary>Reduces an HTML mail body to the plain text the agent should reason over.</summary>
    private static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<br[^>]*>|</p>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \\t]+", " ");
        text = Regex.Replace(text, "(\\s*\\n){3,}", "\n\n");

        return text.Trim();
    }

    /// <summary>Wraps the agent's plain-text answer as the HTML body of an email reply.</summary>
    private static string PlainTextToHtml(string text)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(text ?? string.Empty);
        var builder = new StringBuilder("<div>");
        builder.Append(encoded.Replace("\r\n", "\n").Replace("\n", "<br />"));
        builder.Append("</div>");
        return builder.ToString();
    }
}
