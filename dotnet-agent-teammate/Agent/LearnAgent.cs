using System.Text;
using System.Text.RegularExpressions;
using AgentNotification;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

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

    private readonly AIAgent _agent;
    private readonly ConversationSessionStore _sessions;
    private readonly ILogger<LearnAgent> _logger;

    public LearnAgent(
        AgentApplicationOptions options,
        AIAgent agent,
        ConversationSessionStore sessions,
        IConfiguration configuration,
        ILogger<LearnAgent> logger) : base(options)
    {
        _agent = agent;
        _sessions = sessions;
        _logger = logger;

        // Handler names come from configuration, not constants, so the same binary runs
        // unauthenticated in the Agents Playground and agentic in Teams.
        var agenticHandlerName = configuration.GetValue<string>("AgentApplication:AgenticAuthHandlerName");
        var oboHandlerName = configuration.GetValue<string>("AgentApplication:OboAuthHandlerName");

        string[] agenticHandlers = string.IsNullOrWhiteSpace(agenticHandlerName) ? [] : [agenticHandlerName];
        string[] oboHandlers = string.IsNullOrWhiteSpace(oboHandlerName) ? [] : [oboHandlerName];

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
    /// </summary>
    private async Task<string> ResearchAsync(
        ITurnContext turnContext,
        string conversationId,
        string question,
        CancellationToken cancellationToken)
    {
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingTask = KeepTypingAsync(turnContext, typingCts.Token);

        try
        {
            var session = await _sessions.GetOrCreateAsync(conversationId, cancellationToken);
            var response = await _agent.RunAsync(question, session, cancellationToken: cancellationToken);

            return string.IsNullOrWhiteSpace(response.Text)
                ? "I couldn't find an answer to that in the Microsoft Learn documentation."
                : response.Text;
        }
        finally
        {
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
