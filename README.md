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
| [`dotnet-agent-teams`](./dotnet-agent-teams) | Microsoft 365 Agents SDK (.NET) + Agent Framework + Azure OpenAI | Custom engine agent in Microsoft Teams / M365 Copilot |

## Branching model

- **`main`** — the Agent 365 *instrumented* version of each agent (identity, blueprint, observability).
- **`plain/<name>`** — a snapshot of the same agent *before* Agent 365 onboarding.

| Branch | Contents |
| --- | --- |
| `plain/dotnet-agent-no-teams` | `dotnet-agent-no-teams`, before onboarding |
| `plain/teams-agent` | `dotnet-agent-teams`, before onboarding |

This makes the onboarding work visible as a diff:

```powershell
git diff plain/dotnet-agent-no-teams..main -- dotnet-agent-no-teams
```

Switch to a `plain/*` branch to demo the "before" state, switch back to `main` for the "after".
