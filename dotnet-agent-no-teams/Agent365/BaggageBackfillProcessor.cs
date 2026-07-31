using System.Diagnostics;
using OpenTelemetry;

namespace LearnMcpAgent.Agent365;

/// <summary>
/// Copies the Agent 365 identity from baggage onto gen_ai spans the SDK's own processor cannot reach,
/// so the model call is exported instead of being dropped.
/// </summary>
/// <remarks>
/// The SDK's ActivityProcessor enriches spans in <c>OnStart</c>, and only when the span already
/// carries <c>gen_ai.operation.name</c>. That holds for the scopes the A365 SDK creates itself
/// (invoke_agent, execute_tool), which set the tag as they start.
///
/// It does not hold for the inference span. Microsoft.Extensions.AI creates it with
/// <c>_activitySource.StartActivity("chat " + model, ActivityKind.Client)</c> - verified by
/// decompiling OpenTelemetryChatClient - and only sets its tags afterwards. At OnStart there is
/// therefore nothing to match on, no baggage is copied, and the Agent 365 exporter later drops the
/// span with "1 spans skipped due to missing tenant or agent ID". The prompt, the system
/// instructions and the completion go with it.
///
/// Running the same copy at OnEnd fixes it: the tag exists by then, and OnEnd is raised
/// synchronously on the thread that stops the activity, so Baggage.Current is still the turn's
/// baggage. Registration order matters - see Program.cs.
/// </remarks>
internal sealed class BaggageBackfillProcessor : BaseProcessor<Activity>
{
    /// <summary>Mirrors the SDK's own allowlist (OpenTelemetryConstants.GenAiOperationNames).</summary>
    private static readonly HashSet<string> GenAiOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoke_agent", "execute_tool", "output_messages", "apply_guardrail", "chat",
    };

    /// <summary>
    /// Identity only. The SDK's list also carries message-content keys, but those belong to the
    /// span rather than to the turn, so copying them from baggage could only overwrite the truth.
    /// </summary>
    private static readonly string[] IdentityKeys =
    [
        "gen_ai.agent.id",
        "gen_ai.agent.name",
        "gen_ai.agent.description",
        "gen_ai.conversation.id",
        "microsoft.tenant.id",
        "microsoft.a365.agent.blueprint.id",
        "microsoft.a365.agent.platform.id",
        "microsoft.agent.user.id",
        "microsoft.agent.user.email",
        "microsoft.session.id",
        "microsoft.channel.name",
        "microsoft.channel.link",
        "microsoft.conversation.item.link",
        "user.id",
        "user.name",
        "user.email",
    ];

    public override void OnEnd(Activity activity)
    {
        if (activity?.GetTagItem("gen_ai.operation.name") is not string operation
            || !GenAiOperations.Contains(operation))
        {
            return;
        }

        // Already enriched at OnStart by the SDK - leave it alone. Tenant id is the field the
        // exporter partitions on, so it is the honest test for "did enrichment happen".
        if (activity.GetTagItem("microsoft.tenant.id") is not null)
        {
            return;
        }

        foreach (var key in IdentityKeys)
        {
            // Never overwrite a value the instrumentation set itself; it knows better than baggage.
            if (activity.GetTagItem(key) is not null)
            {
                continue;
            }

            var value = Baggage.Current.GetBaggage(key);
            if (!string.IsNullOrEmpty(value))
            {
                activity.SetTag(key, value);
            }
        }
    }
}

