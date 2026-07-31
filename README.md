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
| [`dotnet-agent-teammate`](./dotnet-agent-teammate) | Microsoft 365 Agents SDK (.NET) + Agent Framework + Azure OpenAI | **AI Teammate** in Microsoft Teams / M365 Copilot |
| [`python-agent-no-teams`](./python-agent-no-teams) | LangChain (Python) + Azure OpenAI | FastAPI web app, no Teams |
| [`python-agent-teams`](./python-agent-teams) | Microsoft 365 Agents SDK (Python) + LangChain + Azure OpenAI | Custom engine agent in Microsoft Teams / M365 Copilot |

`dotnet-agent-teams` and `dotnet-agent-teammate` are deliberately the *same* agent, onboarded two
different ways: the first as a system agent acting **on behalf of the signed-in user**, the second
as an AI Teammate acting under **its own Agentic User identity**. Comparing them is the point of
having both.

## Branching model

- **`main`** — the Agent 365 *instrumented* version of each agent (identity, blueprint, observability).
- **`plain/<name>`** — a snapshot of the same agent *before* Agent 365 onboarding.

| Branch | Contents |
| --- | --- |
| `plain/dotnet-agent-no-teams` | `dotnet-agent-no-teams`, before onboarding |
| `a365/dotnet-agent-no-teams` | `dotnet-agent-no-teams`, after onboarding (merged into `main`) |
| `plain/teams-agent` | `dotnet-agent-teams`, before onboarding |
| `a365/teams-agent` | `dotnet-agent-teams`, after onboarding (merged into `main`) |
| `plain/python-agent-no-teams` | `python-agent-no-teams`, before onboarding |
| `a365/python-agent-no-teams` | `python-agent-no-teams`, after onboarding (merged into `main`) |
| `plain/python-agent-teams` | `python-agent-teams`, before onboarding |
| `a365/python-agent-teams` | `python-agent-teams`, after onboarding (merged into `main`) |
| `plain/dotnet-agent-teammate` | `dotnet-agent-teammate`, before onboarding |
| `a365/dotnet-agent-teammate` | `dotnet-agent-teammate`, after onboarding (merged into `main`) |

This makes the onboarding work visible as a diff:

```powershell
git diff plain/dotnet-agent-no-teams..main -- dotnet-agent-no-teams
git diff plain/teams-agent..main -- dotnet-agent-teams
git diff plain/dotnet-agent-teammate..main -- dotnet-agent-teammate
git diff plain/python-agent-no-teams..main -- python-agent-no-teams
```

Switch to a `plain/*` branch to demo the "before" state, switch back to `main` for the "after".

## Running an agent

Open **the agent's own folder** in VS Code, not the repository root, and press <kbd>F5</kbd>. Four of
the five agents carry their own `.vscode/launch.json` and `.vscode/tasks.json`, which build them,
start whatever they depend on, and attach the debugger. `dotnet-agent-teammate` is the exception —
it has no `.vscode` folder and is started from a terminal.

| Agent | What F5 does | Reachable at |
| --- | --- | --- |
| `dotnet-agent-no-teams` | Builds and starts the app, then opens the browser | <https://localhost:7199> |
| `dotnet-agent-teams` | Builds, brings the dev tunnel up, then starts the agent on port 3978 | Teams, once the agent is listening |
| `dotnet-agent-teammate` | No F5 — run `dotnet run` and `devtunnel host dotnet-teammate-tunnel` in two terminals (port 3980) | Teams, once the agent is listening |
| `python-agent-no-teams` | Syncs dependencies, starts the app, then opens the browser | <http://localhost:8000> |
| `python-agent-teams` | Syncs dependencies, brings the dev tunnel up, then starts the agent on port 3979 | Teams, once the agent is listening |

The three Teams-hosted agents use three different ports — 3978, 3979 and 3980 — so they can all run
at the same time.

`dotnet-agent-no-teams` and `dotnet-agent-teams` run with `ASPNETCORE_ENVIRONMENT=Development`,
which is what makes **user secrets** load. Neither can authenticate without them, so if a fresh
clone fails at startup, check that they are set:

```powershell
dotnet user-secrets list
```

`dotnet-agent-teammate` runs as **`Production`** instead, because the A365 Tooling SDK selects its
agentic token provider on the environment name and falls back to a dev provider in Development.
`WebApplication.CreateBuilder` only registers user secrets in Development, so that agent calls
`AddUserSecrets` explicitly — see its own README for why removing that call makes the agent go
silent rather than throw.

No agent has an `appsettings.Development.json` or `.env` switch disabling the Agent 365 exporter, so
a local run exports traces to Agent 365 exactly like a production run. That is deliberate: it is the
point of the demo. Where a console exporter exists, it is added *alongside* the Agent 365 one, never
instead of it.

The Python agents read their configuration from `.env`; see
[`python-agent-no-teams/README.md`](./python-agent-no-teams/README.md).

### The Teams agents' dev tunnels

Teams cannot reach `localhost`, so each Teams-hosted agent is exposed through a **named** dev
tunnel. Named rather than anonymous matters: a named tunnel keeps the same public url every time,
and that url is registered as the agent's messaging endpoint. An anonymous tunnel would issue a new
url per run and silently break the channel.

Tunnel urls are **not** derived from the tunnel name and are only printed while hosting — read them,
do not guess them.

| Agent | Tunnel | Port |
| --- | --- | --- |
| `dotnet-agent-teams` | `dotnet-agent-teams-tunnel` | 3978 |
| `python-agent-teams` | `python-agent-teams-tunnel` | 3979 |
| `dotnet-agent-teammate` | `dotnet-teammate-tunnel` | 3980 |

The tunnels already exist in this environment. On a new machine, create one like this:

```powershell
devtunnel create dotnet-agent-teams-tunnel --allow-anonymous
devtunnel port create dotnet-agent-teams-tunnel --port-number 3978
devtunnel show dotnet-agent-teams-tunnel
```

`devtunnel show` prints the public url. If it differs from the one registered for the agent, update
the messaging endpoint to `<url>/api/messages` — on the Azure Bot for `dotnet-agent-teams` and
`python-agent-teams`, or with `a365 setup blueprint --update-endpoint <url> --m365` for
`dotnet-agent-teammate`, which has no Azure Bot.

Host a tunnel yourself with `devtunnel host <name>` if you want it up without running the agent. VS
Code will not start a second copy of its own tunnel task, but it cannot see a tunnel you started
from a terminal, so close that one first to avoid two relays forwarding the same port.

## Agent 365 capabilities per agent

| Capability | `dotnet-agent-no-teams` | `dotnet-agent-teams` | `dotnet-agent-teammate` | `python-agent-no-teams` | `python-agent-teams` |
| --- | --- | --- | --- | --- | --- |
| Agent identity and blueprint | Yes | Yes | Yes, an AI Teammate | Yes | Yes |
| Observability instrumentation | Yes, exported on-behalf-of | Yes, exported on-behalf-of | Yes, exported as the agentic user | Yes, exported on-behalf-of | Yes, exported on-behalf-of |
| WorkIQ tools | Yes — mail, calendar, Teams | Yes — mail, calendar, Teams; see the note below | Yes — mail and calendar | No, see the note below | No, see the note below |
| User authentication | Sign-in through a separate web client app | On-Behalf-Of through Teams SSO | None — the agent has its own identity | Sign-in through a separate web client app | On-Behalf-Of through Teams SSO |

### Why the Python agents have no WorkIQ tools

WorkIQ is wired into an agent through a framework-specific adapter package, and Microsoft does not
publish one for Python and LangChain. Every other combination has one -
`microsoft-agents-a365-tooling-extensions-agentframework`, `-openai`, `-googleadk`,
`-semantickernel` and `-azureaifoundry` all exist on PyPI, and Node.js has a LangChain adapter, but
`microsoft-agents-a365-tooling-extensions-langchain` does not exist. This applies to both Python
agents in this repo, whatever their hosting.

Nothing about WorkIQ itself blocks these agents. Its servers are ordinary streamable HTTP MCP servers,
which is the transport the agents already use for Microsoft Learn, and the framework-agnostic
`microsoft-agents-a365-tooling` package exposes their URLs and scopes. What is missing is the glue,
including the per-server token: the SDK acquires it through an M365 Agents SDK `TurnContext`, which a
plain web app does not have. `python-agent-no-teams` already works around the same gap for
observability with its own on-behalf-of chain, so the adapter is writable - it is simply unsupported
code, so these demos leave it out.

### Emitting a span is not the same as exporting it

All three .NET agents register a small `BaggageBackfillProcessor` before
`UseMicrosoftOpenTelemetry`, and without it the **model call never reaches Agent 365**.

The SDK copies identity from baggage onto spans in `OnStart`, and only for spans that already carry
a `gen_ai.operation.name` tag. The scopes the A365 SDK creates itself set that tag as they start, so
`invoke_agent` and `execute_tool` are fine. Microsoft.Extensions.AI does not: it creates its `chat`
span with `StartActivity("chat " + model, ActivityKind.Client)` and sets the tags afterwards
(verified by decompiling `OpenTelemetryChatClient`). The enrichment therefore misses it and the
exporter drops it:

```
[Agent365Exporter] 1 spans skipped due to missing tenant or agent ID
```

The prompt, the system instructions and the completion go with it — Defender records that a turn
happened and which tools ran, but not what the model was asked or what it answered. The console
exporter still shows the span, which is what makes this easy to miss.

The processor re-runs the same copy at `OnEnd`, when the tag exists. On one measured turn the export
went from *1 span / 2,247 bytes* to *2 spans / 99,255 bytes*.

> This is a workaround for an SDK timing quirk, not a supported extension point — the distro exposes
> no processor hook. Whether the Python SDK has the same gap has **not** been checked.

### Three ways to authenticate the observability exporter

The exporter's token must have the *agent identity* as its principal, but how the agent gets there
depends on whether a human is signed in, and on whether the agent has an identity of its own:

| | Chain | Used by |
| --- | --- | --- |
| Web-hosted | On-behalf-of: the user's token is the assertion, so the token represents *the agent acting for the user* | `dotnet-agent-no-teams`, `python-agent-no-teams` |
| Teams-hosted system agent | On-behalf-of, three hops: bot app re-targets the Teams SSO token to the blueprint, blueprint proves ownership via `fmi_path`, agent identity performs the final exchange | `dotnet-agent-teams`, `python-agent-teams` |
| AI Teammate | Agentic user: the SDK exchanges the turn token for a token belonging to the agent's own Agentic User | `dotnet-agent-teammate` |

> ⚠️ **Both Teams-hosted system agents moved from S2S to OBO, and the export route explains why the
> obvious OBO wiring fails.** The auth mode is decided by whether a human is in the loop at runtime,
> not by where the agent is hosted — a Teams agent has a user on every turn, so it belongs on OBO. But
> the distro's `AgenticTokenCache` does a *plain* on-behalf-of exchange through the bot channel app,
> and the export route authorises on the token's `azp`: it must equal the agent id in the URL. Probed
> on the live endpoint with one token against three ids — agent identity **403**, blueprint **403**,
> bot app **415** (authorised, wrong content type). The working chain therefore ends in an exchange
> performed *by the agent identity for the user*, giving `azp` = agent identity and the human as
> subject. See [`dotnet-agent-teams/README.md`](./dotnet-agent-teams/README.md).

In the web-hosted case a plain delegated user token is rejected by the export route, because its
principal is the human rather than the agent.

The AI Teammate is different again: it *has* an identity of its own, so there is nothing to act on
behalf of. It cannot use the service-to-service shape either — Entra bars agentic applications from
requesting app-only tokens at all (**`AADSTS82001`**). Its tokens come from
`UserAuthorization.ExchangeTurnTokenAsync(turnContext, "agentic", …)`, which runs a three-hop chain
ending in a `grant_type=user_fic` exchange. That last hop is *delegated*, for the Agentic User
rather than for a human. See
[`dotnet-agent-teammate/README.md`](./dotnet-agent-teammate/README.md) for the full chain.

The AI Teammate is different again: it *has* an identity of its own, so there is nothing to act on
behalf of. It cannot use the service-to-service shape either — Entra bars agentic applications from
requesting app-only tokens at all (**`AADSTS82001`**). Its tokens come from
`UserAuthorization.ExchangeTurnTokenAsync(turnContext, "agentic", …)`, which runs a three-hop chain
ending in a `grant_type=user_fic` exchange. That last hop is *delegated*, for the Agentic User
rather than for a human. See
[`dotnet-agent-teammate/README.md`](./dotnet-agent-teammate/README.md) for the full chain.

> ⚠️ On the two Teams-hosted **system** agents, `a365 setup all` overwrites the bot channel
> credentials with the blueprint's and must be undone afterwards. Entra bars agentic applications
> from client-credentials tokens (AADSTS82001), so a blueprint cannot sign Bot Framework replies,
> and it is also the wrong audience for validating the inbound Teams token. Each agent's README
> documents the repair. `dotnet-agent-teammate` is immune: it has no Azure Bot and no bot channel
> app, because the messaging endpoint is registered on the blueprint itself.

### How the Teams-hosted system agents authenticate

They use two different identities, for two different jobs:

- **The signed-in user**, through On-Behalf-Of, whenever they read that user's data. WorkIQ tools
  run this way so the agent can only see mail, calendar and Teams content the user could open
  themselves.
- **The agent's own identity**, for writing observability traces. The observability service
  authorises on the token's `azp`, which must equal the agent id in the export route — a token whose
  client is the bot channel app answers `403`. The token is therefore minted through the same
  federated identity chain the WorkIQ tools use
  ([`Agent365/WorkIqTokenService.cs`](./dotnet-agent-teams/Agent365/WorkIqTokenService.cs)), which
  ends in an exchange performed by the agent identity *for* the user: `azp` is the agent, the subject
  is the human.

Traces reach the service over the delegated route (`/observability/`). Delegated and
service-to-service traces use different routes and do not accept each other's tokens, so the token
and the route have to agree.

The AI Teammate collapses this distinction: both jobs use its own identity.

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
servers themselves are healthy. All three WorkIQ-enabled agents therefore read that manifest and
connect to each server directly:

- [`dotnet-agent-no-teams/Agent365/WorkIqToolProvider.cs`](./dotnet-agent-no-teams/Agent365/WorkIqToolProvider.cs)
- [`dotnet-agent-teams/Agent365/WorkIqToolProvider.cs`](./dotnet-agent-teams/Agent365/WorkIqToolProvider.cs)
- [`dotnet-agent-teammate/Agent365/WorkIqToolProvider.cs`](./dotnet-agent-teammate/Agent365/WorkIqToolProvider.cs)

Each server is contacted independently and a failure is logged and skipped, so a server that is
down costs only its own tools. Once the gateway is fixed, these providers can be replaced by
`IMcpToolRegistrationService.GetMcpToolsAsync` again with no other change.

On `dotnet-agent-teammate` there is a second, independent reason the SDK path cannot be used at
all: every version of `Microsoft.Agents.A365.Tooling` pins `ModelContextProtocol.Core 0.2.0-preview.3`
and calls `IMcpClient`, which was removed in the 1.3.0 the agent needs. Loading the extension throws
`TypeLoadException` before discovery is ever attempted.

### The token the WorkIQ servers expect

The servers check for the delegated `Tools.ListInvoke.All` scope and reject anything else, including
an app-only token minted by the agent identity:

```text
403  Access denied: Scope 'Tools.ListInvoke.All' is not present in the request.
```

In the two On-Behalf-Of agents the token therefore has to start from the signed-in user. In the
Teams-hosted one that takes three hops, in
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

`dotnet-agent-teammate` satisfies the same scope check from the other direction. It has no user to
act for, so its token is delegated for its own **Agentic User**, obtained with a single call to
`UserAuthorization.ExchangeTurnTokenAsync(turnContext, "agentic", null, ["{audience}/Tools.ListInvoke.All"], ct)`.
Two routes that look plausible are dead ends there, and both are documented in that agent's README
so they are not retried: an app-only token fails with `AADSTS82001`, and asking for a granular scope
on a client-credentials flow fails with `AADSTS1002012`.

Either way the tools are resolved **per turn** rather than at startup, because the token only exists
inside a turn.
