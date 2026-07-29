# agent365-demos

A collection of demo agents used to showcase **Agent 365** onboarding.

Every agent shares the same functional goal: it is a research assistant for the Microsoft ecosystem,
grounding its answers in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/api/mcp). What changes between them is the
technology used to build the agent.

## Agents

| Folder | Stack | Hosting |
| --- | --- | --- |
| [`dotnet-agent-no-teams`](./dotnet-agent-no-teams) | Microsoft Agent Framework (.NET) + Azure OpenAI | Blazor Server web app, no Teams |

## Branching model

- **`main`** — the Agent 365 *instrumented* version of each agent (identity, blueprint, observability).
- **`plain/<agent-folder>`** — a snapshot of the same agent *before* Agent 365 onboarding.

This makes the onboarding work visible as a diff:

```powershell
git diff plain/dotnet-agent-no-teams..main -- dotnet-agent-no-teams
```

Switch to a `plain/*` branch to demo the "before" state, switch back to `main` for the "after".
