using Microsoft.Extensions.AI;

namespace LearnMcpAgent.Agent365;

/// <summary>A single tool invocation, surfaced to the UI while a turn is running.</summary>
/// <param name="Source">Friendly name of the MCP server the tool came from.</param>
/// <param name="ToolName">The tool the model chose to call.</param>
public sealed record ToolInvocation(string Source, string ToolName)
{
    public string Display => $"{Source} · {ToolName}";
}

/// <summary>
/// Raises an event every time the agent calls a tool. One instance lives per chat session so the
/// page can show what the agent is actually doing instead of a fixed placeholder.
/// </summary>
public sealed class ToolActivityReporter
{
    public event Func<ToolInvocation, Task>? ToolInvoked;

    public Task ReportAsync(ToolInvocation invocation) =>
        ToolInvoked?.Invoke(invocation) ?? Task.CompletedTask;
}

/// <summary>
/// Wraps a tool so its invocation is announced before the call runs. Function invocation happens
/// inside the chat client pipeline, so wrapping the tool itself is the only place that sees every
/// call regardless of streaming mode.
/// </summary>
internal sealed class ReportingAIFunction(
    AIFunction inner,
    string source,
    ToolActivityReporter reporter) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        await reporter.ReportAsync(new ToolInvocation(source, Name)).ConfigureAwait(false);
        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
