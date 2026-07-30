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
| `a365/dotnet-agent-no-teams` | `dotnet-agent-no-teams`, after onboarding (merged into `main`) |
| `plain/teams-agent` | `dotnet-agent-teams`, before onboarding |
| `a365/teams-agent` | `dotnet-agent-teams`, after onboarding (merged into `main`) |

This makes the onboarding work visible as a diff:

```powershell
git diff plain/dotnet-agent-no-teams..main -- dotnet-agent-no-teams
git diff plain/teams-agent..main -- dotnet-agent-teams
```

Switch to a `plain/*` branch to demo the "before" state, switch back to `main` for the "after".

## Agent 365 capabilities per agent

| Capability | `dotnet-agent-no-teams` | `dotnet-agent-teams` |
| --- | --- | --- |
| Agent identity and blueprint | Yes | Yes |
| Observability instrumentation | Yes | Yes |
| WorkIQ mail / calendar / Teams tools | Yes | Wired, see the note below |
| User authentication | Not applicable, no user context | On-Behalf-Of through Teams SSO |

### Known issue: WorkIQ tools on the Teams agent

`dotnet-agent-teams` has WorkIQ fully wired, including the On-Behalf-Of sign-in that the classic
Teams bot channel requires, but the tools do not load at runtime. The tool discovery call returns
HTTP 500 from the service:

```text
GET https://agent365.svc.cloud.microsoft/agents/v2/{agentId}/mcpServers  ->  500
```

The same route returns 500 for an agent id that does not exist, where a 404 would be expected, and
other `/agents/v2/` routes are healthy. That points at the route itself rather than at this agent,
its identity, or its configuration. Every published version of `Microsoft.Agents.A365.Tooling`
hardcodes that path, so there is no version to pin to and no setting to change.

The agent handles this by falling back to Microsoft Learn only, so it stays usable for demos. No
code change should be needed once the service is fixed.
