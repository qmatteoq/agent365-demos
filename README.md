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

## Running an agent

Open **the agent's own folder** in VS Code, not the repository root, and press <kbd>F5</kbd>. Each
agent carries its own `.vscode/launch.json` and `.vscode/tasks.json`, which build it, start whatever
it depends on, and attach the debugger.

| Agent | What F5 does | Reachable at |
| --- | --- | --- |
| `dotnet-agent-no-teams` | Builds and starts the app, then opens the browser | <https://localhost:7199> |
| `dotnet-agent-teams` | Builds, brings the dev tunnel up, then starts the agent on port 3978 | Teams, once the agent is listening |

Both run with `ASPNETCORE_ENVIRONMENT=Development`, which is what makes **user secrets** load.
Neither agent can authenticate without them, so if a fresh clone fails at startup, check that they
are set:

```powershell
dotnet user-secrets list
```

Neither agent has an `appsettings.Development.json` disabling the Agent 365 exporter, so an F5 run
exports traces to Agent 365 exactly like a production run. That is deliberate: it is the point of
the demo.

### The Teams agent's dev tunnel

Teams cannot reach `localhost`, so the agent is exposed through a **named** dev tunnel that the
launch task hosts automatically. Named rather than anonymous matters: a named tunnel keeps the same
public url every time, and that url is registered as the messaging endpoint of the Azure Bot. An
anonymous tunnel would issue a new url per run and silently break the channel.

The tunnel already exists in this environment. On a new machine, create it once:

```powershell
devtunnel create dotnet-agent-teams-tunnel --allow-anonymous
devtunnel port create dotnet-agent-teams-tunnel --port-number 3978
devtunnel show dotnet-agent-teams-tunnel
```

`devtunnel show` prints the public url. If it differs from the one registered on the Azure Bot,
update the bot's messaging endpoint to `<url>/api/messages`.

Host it yourself with `devtunnel host dotnet-agent-teams-tunnel` if you want it up without running
the agent. VS Code will not start a second copy of its own tunnel task, but it cannot see a tunnel
you started from a terminal, so close that one first to avoid two relays forwarding the same port.

## Agent 365 capabilities per agent

| Capability | `dotnet-agent-no-teams` | `dotnet-agent-teams` |
| --- | --- | --- |
| Agent identity and blueprint | Yes | Yes |
| Observability instrumentation | Yes | Yes, exported service-to-service |
| WorkIQ mail / calendar / Teams tools | Yes | Yes, see the note below |
| User authentication | Not applicable, no user context | On-Behalf-Of through Teams SSO |

### How the Teams agent authenticates

The agent uses two different identities, for two different jobs:

- **The signed-in user**, through On-Behalf-Of, whenever it reads that user's data. WorkIQ tools
  run this way so the agent can only see mail, calendar and Teams content the user could open
  themselves.
- **The agent's own identity**, for writing observability traces. The observability service binds
  the caller to the agent named in the export route, and a delegated token's principal is the
  human rather than the agent, so it answers `403`. The token is therefore minted through the
  federated identity chain in
  [`Observability/ObservabilityTokenService.cs`](./dotnet-agent-teams/Observability/ObservabilityTokenService.cs),
  which produces a token whose subject is the agent.

Traces reach the service over the service-to-service route. Delegated and service-to-service
traces use different routes and do not accept each other's tokens, so the two have to agree.

### Working around a broken tool discovery route

Both agents load their WorkIQ tools without asking the tooling gateway which servers they may use.
That discovery call fails service side:

```text
GET https://agent365.svc.cloud.microsoft/agents/v2/{agentId}/mcpServers  ->  500
```

The same route returns 500 for an agent id that does not exist, where a 404 would be expected, and
other `/agents/v2/` routes are healthy, which points at the route itself rather than at either
agent. Every published version of `Microsoft.Agents.A365.Tooling` hardcodes that path, so there is
no version to pin to and no setting to change.

Discovery turns out to be unnecessary. `ToolingManifest.json`, written by
`a365 develop add-mcp-servers`, already lists the url, audience and scope of every server, and the
servers themselves are healthy. Both agents therefore read that manifest and connect to each server
directly:

- [`dotnet-agent-no-teams/Agent365/WorkIqToolProvider.cs`](./dotnet-agent-no-teams/Agent365/WorkIqToolProvider.cs)
- [`dotnet-agent-teams/Agent365/WorkIqToolProvider.cs`](./dotnet-agent-teams/Agent365/WorkIqToolProvider.cs)

Each server is contacted independently and a failure is logged and skipped, so a server that is
down costs only its own tools. Once the gateway is fixed, these providers can be replaced by
`IMcpToolRegistrationService.GetMcpToolsAsync` again with no other change.

### The token the WorkIQ servers expect

The servers check for the delegated `Tools.ListInvoke.All` scope and reject anything else, including
an app-only token minted by the agent identity:

```text
403  Access denied: Scope 'Tools.ListInvoke.All' is not present in the request.
```

So the token has to start from the signed-in user. In the Teams agent that takes three hops, in
[`Agent365/WorkIqTokenService.cs`](./dotnet-agent-teams/Agent365/WorkIqTokenService.cs):

1. The bot channel app exchanges the user's Teams token for the blueprint's `access_agent_as_user`
   scope. Teams SSO issues a token whose audience is the channel app, and the last hop only accepts
   an assertion issued to the blueprint family.
2. The blueprint requests a token-exchange assertion with `fmi_path` set to the agent identity,
   proving it owns that identity.
3. The agent identity performs the final On-Behalf-Of exchange for the WorkIQ audience, presenting
   that assertion as its client credential.

The result is a token that belongs to the governed agent identity acting for the user, which is what
tool calls have to be attributed to. The channel app holds the same consented permissions and could
satisfy the scope check on its own, but that token would not be tied to the agent identity, so it is
deliberately not used.

The non-Teams agent runs the same chain minus the first hop, because its users sign in to the
blueprint scope directly.
