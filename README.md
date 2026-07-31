# agent365-demos

Five demo agents showing how to onboard an existing agent to **Microsoft Agent 365** and instrument
it for **observability** and **Work IQ** tool access.

Every agent does the same job — it is a research assistant for the Microsoft ecosystem, grounding its
answers in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp)
(`https://learn.microsoft.com/api/mcp`, streamable HTTP). The agent logic is deliberately
uninteresting and near-identical across all five. What changes is the **stack**, the **hosting
surface**, and — the point of the repo — the **Agent 365 shape** that follows from them.

Between them they cover the three token paths an agent can use to export telemetry to Agent 365, in
both .NET and Python.

## Agents

| Folder | Stack | Hosting | Agent 365 shape |
| --- | --- | --- | --- |
| [`dotnet-agent-no-teams`](./dotnet-agent-no-teams) | Agent Framework (.NET) + Azure OpenAI | Blazor Server web app | System agent, on-behalf-of |
| [`dotnet-agent-teams`](./dotnet-agent-teams) | M365 Agents SDK (.NET) + Agent Framework | Teams / M365 Copilot | Custom engine agent, on-behalf-of |
| [`dotnet-agent-teammate`](./dotnet-agent-teammate) | M365 Agents SDK (.NET) + Agent Framework | Teams / M365 Copilot | **AI Teammate**, agentic user |
| [`python-agent-no-teams`](./python-agent-no-teams) | LangChain (Python) + Azure OpenAI | FastAPI web app | System agent, on-behalf-of |
| [`python-agent-teams`](./python-agent-teams) | M365 Agents SDK (Python) + LangChain | Teams / M365 Copilot | Custom engine agent, on-behalf-of |

`dotnet-agent-teams` and `dotnet-agent-teammate` are the *same* agent onboarded two different ways —
one acting on behalf of the signed-in user, one acting under its own Agentic User identity.
Comparing them is the clearest way to see what the AI Teammate shape actually changes.

## What you need before running any of this

None of the Agent 365 identifiers in this repo will work in your tenant. Blueprints, agent
identities, bot registrations and Teams app ids are all tenant-scoped, so **you must register your
own** before an agent will start. Configuration files carry placeholders; each agent's README lists
exactly which values it needs and where they go.

The registration itself is done with the Agent 365 CLI:

```powershell
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli
a365 --version
```

Then, from the agent's own folder:

```powershell
# system agent / custom engine agent
a365 setup all --authmode obo

# AI Teammate
a365 setup all --aiteammate --m365
```

This creates the agent identity blueprint, the Entra registrations, and — if a
`ToolingManifest.json` is present — grants the Work IQ permissions. See each agent's README for the
values it writes and the ones you must supply yourself.

> `a365 setup all` rewrites configuration files it owns. On the two agents that also have an Azure
> Bot registration (`dotnet-agent-teams`, `python-agent-teams`) it overwrites the **bot channel
> credentials** with the blueprint's, which will break channel authentication on the next restart.
> Restore the bot app's own client id and secret afterwards, and keep the two identities separate —
> Entra bars an agentic application from requesting client-credentials tokens (`AADSTS82001`), so a
> blueprint can never sign Bot Framework replies.

## Agent 365 capabilities per agent

| Capability | `dotnet-agent-no-teams` | `dotnet-agent-teams` | `dotnet-agent-teammate` | `python-agent-no-teams` | `python-agent-teams` |
| --- | --- | --- | --- | --- | --- |
| Agent identity and blueprint | Yes | Yes | Yes, an AI Teammate | Yes | Yes |
| Observability | On-behalf-of | On-behalf-of | Agentic user | On-behalf-of | On-behalf-of |
| Work IQ tools | Mail, calendar, Teams | Mail, calendar, Teams | Mail, calendar | No — see below | No — see below |
| User authentication | Separate web sign-in app | Teams SSO | None — own identity | Separate web sign-in app | Teams SSO |

## Observability

### The one rule that decides everything

The exporter posts spans to:

```text
POST /observability/tenants/{tenantId}/otlp/agents/{agentId}/traces
```

The service authorises by comparing the **`azp` claim of the token** against the **`{agentId}` in the
URL**. They must be identical. If they differ you get `403`; the exporter logs a failed batch and
carries on, so the agent looks healthy while emitting nothing.

Everything below is just three different ways of satisfying that one rule.

### Three token paths

| Hosting shape | How the token is acquired | `azp` / export id | Used by |
| --- | --- | --- | --- |
| Web app | Two-hop federated chain: the blueprint proves it owns the agent identity via `fmi_path`, then the agent identity performs an on-behalf-of exchange using the signed-in user's token | the **agent identity** | `dotnet-agent-no-teams`, `python-agent-no-teams` |
| Teams custom engine agent | One call. An Azure Bot OAuth connection is scoped to the observability API and the Bot Framework Token Service performs the exchange | the **bot app** registration | `dotnet-agent-teams`, `python-agent-teams` |
| AI Teammate | The SDK's built-in `AgenticTokenCache` exchanges the turn token for one belonging to the agent's own Agentic User | the **agentic instance** | `dotnet-agent-teammate` |

All three post to the delegated route (`/observability/`). The service-to-service route
(`/observabilityService/`) is for app-only tokens and the two do not accept each other's tokens, so
leave `UseS2SEndpoint` / `use_s2s_endpoint` at its default of `false` on every agent here.

### How this maps onto Microsoft's documented scenarios

The
[observability authentication guide](https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup)
defines four scenarios, selected on two questions: does the turn carry **agentic identity**
(`agenticAppId` / `agenticUserId`), and is the token delegated or app-only?

| Agent | Documented scenario |
| --- | --- |
| `dotnet-agent-teammate` | **Agent 365-enabled using OBO** |
| `dotnet-agent-teams`, `python-agent-teams` | **Custom engine using OBO** |
| `dotnet-agent-no-teams`, `python-agent-no-teams` | none of the four — see below |

> **"Agent 365-enabled" does not mean "registered with Agent 365".** Every agent here is registered
> and holds a blueprint. The term describes how the turn *arrives*: an Agent 365-enabled agent is
> invoked as the agentic app and its activity carries `agenticAppId`. A Teams custom engine agent is
> invoked through its own bot registration and carries none, which is why it authenticates as the bot
> app instead.
>
> That is by design rather than a limitation. The
> [get-started guide](https://learn.microsoft.com/microsoft-agent-365/developer/get-started#adding-agent-365-capabilities-incrementally)
> states that registration may be based on *"your existing Microsoft Entra application registration
> **or** a blueprint"*, and that *"Microsoft 365 custom engine agents are already discoverable today
> using their existing Microsoft Entra application registration."* For that agent type the app
> registration **is** the identity the platform knows it by. Traces exported under it are attributed
> in Microsoft Admin Center to the registered agent identity, not to the bot app.
>
> Moving an agent onto the agentic path is an **agent type** change, not an instrumentation change —
> the same page notes that *"AI teammate for Microsoft 365 custom engine agents requires an agent
> identity blueprint"*. `dotnet-agent-teammate` is that upgrade applied to `dotnet-agent-teams`.

The two **web apps** fall outside the taxonomy: all four documented scenarios assume an Agents SDK /
Bot Framework agent, and the closest one requires an Azure Bot OAuth connection, which a plain web
app does not have. They satisfy the `azp` rule from the other side, with the agent identity as both
the token's client and the export id.

### The observability API scope

One identifier in this repo is **not** tenant-specific and should be used as-is: the Agent 365
Observability API, `9b975845-388f-4429-889e-eab1ef63949c`. It is a Microsoft first-party resource
with the same id in every tenant.

| Path | Scope to request |
| --- | --- |
| Azure Bot OAuth connection | `api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite` |
| Web app federated chain | `api://9b975845-388f-4429-889e-eab1ef63949c/.default` |

An OAuth connection left on the default `api://botid-<client-id>/defaultScopes` produces
`401 InvalidAudience`.

### Emitting a span is not the same as exporting it

All three .NET agents register a small `BaggageBackfillProcessor` before
`UseMicrosoftOpenTelemetry`. Without it the **model call never reaches Agent 365**, and the loss is
silent apart from one line:

```text
[Agent365Exporter] 1 spans skipped due to missing tenant or agent ID
```

The distro copies identity from baggage onto spans in `OnStart`, and only for spans that already
carry a `gen_ai.operation.name` tag. Scopes the A365 SDK creates itself set that tag as they start,
so `invoke_agent` and `execute_tool` are enriched correctly. `Microsoft.Extensions.AI` does not: it
creates its `chat` span with `StartActivity("chat " + model, ActivityKind.Client)` and sets the tags
afterwards, so the enrichment misses it and the exporter drops it — taking the prompt, the system
instructions and the completion with it. The console exporter still prints the span, which is what
makes the loss easy to miss.

The processor re-runs the same copy at `OnEnd`, when the tag exists. On one measured turn the export
went from 1 span / 2,247 bytes to 2 spans / 99,255 bytes.

> This is a workaround for an SDK timing quirk, not a supported extension point — the distro exposes
> no processor hook. Whether the Python distro has the same gap has not been checked.

### Keeping identity consistent across a turn

Auto-instrumented spans (LangChain, the LLM client) read `gen_ai.agent.id` from **baggage**, while
the SDK's own scopes take it from the invoke scope. If those two disagree, a single turn splits into
two identity groups and only one of them authenticates. Build both from the same value.

## Work IQ

Tools are resolved **per turn**, not at startup, because the token only exists inside a turn.

The Work IQ servers require a **delegated** token carrying `Tools.ListInvoke.All`, and reject
anything else, including an app-only token minted by the agent identity:

```text
403  Access denied: Scope 'Tools.ListInvoke.All' is not present in the request.
```

So the token must start from a signed-in user (or, for an AI Teammate, from its own Agentic User).
Each agent's README documents its own chain.

Servers are selected with the CLI, which writes `ToolingManifest.json`:

```powershell
a365 develop list-available
a365 develop add-mcp-servers "mcp_MailTools" "mcp_CalendarTools" "mcp_TeamsTools"
```

The three .NET agents read that manifest and connect to each server directly rather than calling the
tooling gateway's discovery endpoint, contacting each server independently so that one unavailable
server costs only its own tools.

### Why the Python agents have no Work IQ tools

Work IQ is wired in through a framework-specific adapter package, and Microsoft does not publish one
for Python + LangChain. `microsoft-agents-a365-tooling-extensions-agentframework`, `-openai`,
`-googleadk`, `-semantickernel` and `-azureaifoundry` all exist on PyPI, and Node.js has a LangChain
adapter, but `microsoft-agents-a365-tooling-extensions-langchain` does not.

Nothing about Work IQ itself blocks these agents — its servers are ordinary streamable HTTP MCP
servers, the same transport the agents already use for Microsoft Learn, and the framework-agnostic
`microsoft-agents-a365-tooling` package exposes their urls and scopes. Only the glue is missing, so
these demos leave it out rather than ship unsupported code.

## Running an agent

Open **the agent's own folder** in VS Code, not the repository root, and press <kbd>F5</kbd>. Four of
the five carry their own `.vscode/launch.json` and `.vscode/tasks.json`; `dotnet-agent-teammate` is
started from a terminal.

| Agent | What F5 does | Reachable at |
| --- | --- | --- |
| `dotnet-agent-no-teams` | Builds and starts the app, then opens the browser | <https://localhost:7199> |
| `dotnet-agent-teams` | Builds, brings the dev tunnel up, starts the agent on port 3978 | Teams |
| `dotnet-agent-teammate` | No F5 — `dotnet run` plus `devtunnel host <name>` in two terminals, port 3980 | Teams |
| `python-agent-no-teams` | Syncs dependencies, starts the app, opens the browser | <http://localhost:8000> |
| `python-agent-teams` | Syncs dependencies, brings the dev tunnel up, starts the agent on port 3979 | Teams |

The three Teams-hosted agents use different ports so they can run simultaneously.

No agent has a switch that disables the Agent 365 exporter for local runs. A local run exports
exactly like a production run — that is the point of the demo. Where a console exporter exists it is
added *alongside* the Agent 365 one, never instead of it.

### Environment names matter

`dotnet-agent-no-teams` and `dotnet-agent-teams` run as `Development`, which is what loads **user
secrets**. `dotnet-agent-teammate` runs as **`Production`**, because the A365 Tooling SDK selects its
agentic token provider on the environment name and falls back to a dev provider in Development — so
it calls `AddUserSecrets` explicitly instead.

### Dev tunnels

Teams cannot reach `localhost`, so each Teams-hosted agent is exposed through a **named** dev tunnel.
Named rather than anonymous matters: a named tunnel keeps the same public url across restarts, and
that url is registered as the agent's messaging endpoint. An anonymous tunnel issues a new url per
run and silently breaks the channel.

```powershell
devtunnel create <tunnel-name> --allow-anonymous
devtunnel port create <tunnel-name> --port-number 3978
devtunnel show <tunnel-name>
```

Tunnel urls are **not** derived from the tunnel name — `devtunnel show` prints the real one. Register
it as `<url>/api/messages`: on the Azure Bot for the two custom engine agents, or with
`a365 setup blueprint --update-endpoint <url> --m365` for the AI Teammate, which has no Azure Bot.

VS Code will not start a second copy of its own tunnel task, but it cannot see a tunnel you started
from a terminal — close that one first to avoid two relays forwarding the same port.
