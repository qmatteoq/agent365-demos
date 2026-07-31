# Microsoft Learn agent — .NET, Agent Framework, AI Teammate

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/api/mcp). It answers questions about
Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365, and cites the
documentation it used.

This is the fifth agent in this repo. Functionally it is the same agent as
[`dotnet-agent-teams`](../dotnet-agent-teams) — same stack, same hosting, same system prompt.
The difference is entirely in **how it is onboarded to Agent 365**: this one is an
**AI Teammate**, so it acts under its **own identity** (the Agentic User) rather than on behalf
of the signed-in user.

That single change removes the Azure Bot registration, removes the whole on-behalf-of token
chain, and is what finally made WorkIQ work — see [Agent 365](#agent-365) below.

| | |
|---|---|
| Language | .NET 10 |
| Agent framework | Microsoft Agent Framework (`Microsoft.Agents.AI.OpenAI`) |
| Hosting | Microsoft 365 Agents SDK (`Microsoft.Agents.Hosting.AspNetCore`) |
| Surface | Teams, Microsoft 365 Copilot |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP + WorkIQ Mail and Calendar |
| Port | 3980 |

## How it fits together

```
Teams / M365 Copilot
        │  Bot Framework activity
        ▼
ASP.NET Core host ──► LearnAgent : AgentApplication   Agent/LearnAgent.cs
                            │
                            ▼
                     AIAgent (Agent Framework)        Program.cs
                            ├──► Azure OpenAI  gpt-4.1
                            ├──► Microsoft Learn MCP           (3 tools)
                            └──► WorkIQ Mail + Calendar MCP    (38 tools)
                                 Agent365/WorkIqToolProvider.cs
```

`Agent/ConversationSessionStore.cs` keeps one `AgentSession` per conversation, so chats are
multi-turn. Memory is in-process, so restarting the agent clears every conversation.

## Running it

### Prerequisites

- .NET 10 SDK
- The **Cognitive Services OpenAI User** role on the Azure OpenAI resource. Authentication uses
  `DefaultAzureCredential`, so `az login` is enough — there is no API key anywhere.
- The blueprint client secret in user secrets (see below)
- `devtunnel`, to reach the agent from Teams

### Start it

```powershell
az login --tenant 57db880c-370a-428d-9139-2b346b4eb220
devtunnel host dotnet-teammate-tunnel   # separate terminal
dotnet run
```

It listens on `http://localhost:3980`, with the channel endpoint at `/api/messages` and a plain
liveness string on `GET /`. Then send it a question from Teams.

Unlike the other four agents, this one has **no `.vscode` folder**, so there is no <kbd>F5</kbd>
profile — start it from a terminal as above. The tunnel is named `dotnet-teammate-tunnel`; a
*named* tunnel keeps the same public url across restarts, which is what keeps the blueprint's
registered messaging endpoint valid. The url is not derived from the tunnel name and is only
printed while the tunnel is being hosted, so read it rather than guessing it.

**Port 3980, not 3978 or 3979.** `dotnet-agent-teams` uses 3978 and `python-agent-teams` uses
3979, so all three can run at once.

### It runs as Production, not Development — and that has consequences

`Properties/launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Production`. This is deliberate and
is the opposite of the other agents in this repo.

The A365 Tooling SDK chooses its token provider purely from the environment name: it picks
`DevMcpTokenProvider`, which demands a hand-pasted `BEARER_TOKEN` environment variable, when
`ASPNETCORE_ENVIRONMENT` is exactly `Development`. Exercising the **real** agentic WorkIQ path
locally therefore means running as Production.

Two things break if you do that naively, and both are already handled in `Program.cs`:

- **User secrets stop loading.** `WebApplication.CreateBuilder` registers the user-secrets
  provider *only* in Development. Without them `Connections:ServiceConnection:Settings:ClientSecret`
  is empty and every turn dies with `Failed to create authentication provider for connection
  name ''`. The failure is easy to misread: the turn still runs and the model still answers, and
  only the *reply* fails, so from Teams the agent just goes silent. `Program.cs` calls
  `AddUserSecrets` explicitly to fix this.
- **`ManagedIdentityCredential` comes back into the credential chain** and throws a fatal error
  that aborts the chain before the Azure CLI credential is reached.

Because the environment name was doing three unrelated jobs, a separate `A365_LOCAL_RUN` signal
carries the "am I on a laptop?" meaning. It defaults to local; set `A365_LOCAL_RUN=false` only
when genuinely cloud-hosted. It controls the credential choice and whether the console exporter
is added — it **never** disables the Agent 365 exporter.

Set the secret once:

```powershell
dotnet user-secrets set "Connections:ServiceConnection:Settings:ClientSecret" "<blueprint secret>"
```

### Commands

| Command | Effect |
|---|---|
| `/reset` | Forget the conversation so far |

## Configuration

| Setting | Purpose |
|---|---|
| `AzureOpenAI:Endpoint` / `Deployment` / `TenantId` | The model the agent reasons with |
| `LearnMcp:Endpoint` | Microsoft Learn MCP server |
| `Agent365Observability:AgentBlueprintId` | Stamped on spans; blank means Defender shows per-instance rows only |
| `AgentApplication:AgenticAuthHandlerName` | Name of the agentic auth handler — `agentic` |
| `AgentApplication:UserAuthorization:Handlers:agentic` | `AgenticUserAuthorization`, written by `a365 setup` |
| `Connections:ServiceConnection` | Blueprint credentials used to reply to the channel |

`AzureOpenAI:TenantId` pins `DefaultAzureCredential` to the tenant that owns the resource.
Without it a token from another tenant produces
`HTTP 400 – Tenant provided in token does not match resource token`.

> ⚠️ `appsettings.json` is the most fragile file here. Any `a365 setup` command rewrites it, and
> it will stamp the client secret back in as plaintext. Check it before committing. Its two
> comment keys must also stay distinct (`"//"` and `"//connections"`) — duplicate root keys crash
> the host at startup.

## Why there is no Azure Bot registration

This is the biggest structural difference from `dotnet-agent-teams`, and it is deliberate.

An AI Teammate's messaging endpoint is registered **on the blueprint**, not on an Azure Bot
resource. `a365 setup all --aiteammate --m365` calls the MCP Platform `createAgentBlueprint`
endpoint, which proxies Teams Graph and sets the bot `callbackUri` — the same value the Teams
Developer Portal shows as **Notification URL**. When the endpoint changes later:

```powershell
a365 setup blueprint --update-endpoint <url> --m365
```

`--m365` is required; without it the Teams Graph re-registration is skipped silently.

Because there is no separate bot channel app, none of `dotnet-agent-teams`' identity gotchas
apply here:

| `dotnet-agent-teams` | This agent |
|---|---|
| Bot channel app + blueprint, kept strictly separate | Blueprint only |
| Blueprint can't sign channel replies (`AADSTS82001`) | Not applicable — no channel app to sign as |
| `a365 setup all` overwrites the bot credentials in place | No bot credentials to overwrite |
| Observability exported service-to-service | Agentic User identity |
| WorkIQ blocked on a service-side 500 | WorkIQ works |

## Agent 365

| | |
|---|---|
| Auth mode | `agentic-user` (AI Teammate) |
| Blueprint / app id | `c41ceef2-c7c9-4618-bde2-75a125ba7c1e` |
| Agent instance (AUID) | `3ab31153-adc6-4ab5-acda-c12eb9d05c55` |
| Tenant | `57db880c-370a-428d-9139-2b346b4eb220` |

When hunting traces in Defender, filter on the **AUID**, not the blueprint id.

### Publishing it

```powershell
a365 setup all --aiteammate --m365
a365 publish --aiteammate
```

Then upload `manifest/manifest.zip` at **admin.microsoft.com → Agents → All agents → Upload
custom agent**, and have a tenant admin approve an instance from
`https://admin.cloud.microsoft/#/agents/all/requested`. Nothing appears in the Agent Registry
until that upload happens — publishing locally is not enough. Approval is asynchronous and can
take minutes to hours.

Four things about the CLI that are easy to lose an afternoon to:

- **Omit `--authmode`.** `s2s` and `both` are rejected alongside `--aiteammate`; `obo` is accepted
  but warns that it is superfluous, since it is the default for an AI Teammate.
- **`a365 publish` re-derives `name.short` and `name.full`** from the CLI agent name on *every*
  run, overwriting hand edits. `description` survives. `name.short` is capped at 30 characters, so
  the `--agent-name` must be 20 characters or fewer — hence `dotnet-teammate` rather than this
  folder's name.
- **Publishing validates almost nothing.** It checks `name.short` and warns, but let a 101-character
  `description.short` through; only the Admin Center rejected it, at a cap of **80**. A successful
  publish does not mean a valid package. Verify the string *inside the zip*, not just the source
  file.
- **The CLI owns `manifest.json`.** It generates and stamps it, so it must not be hand-written.
  Apostrophes in it are `\u0027`-escaped, so a literal `'` will not match on a find-and-replace.

The AI Teammate manifest has **no `bots` array and no `copilotAgents.customEngineAgents`** — only
`agenticUserTemplates`. Checklists written for custom engine agents do not apply.

### How observability is instrumented

Wired in `Program.cs` and `Agent/LearnAgent.cs`:

- **`builder.UseMicrosoftOpenTelemetry(...)`** initialises the distro.
  `o.Exporters` is **`ExportTarget.Agent365` unconditionally**; `A365_LOCAL_RUN` only adds
  `ExportTarget.Console` on top. Traces reach Agent 365 during local testing exactly as they would
  in production, which is the entire point of the demo.
- **`o.Instrumentation.EnableMetrics = false`.** The console metric exporter dumps every histogram
  bucket on a timer and drowns out the spans.
- **`o.Agent365.TokenResolver`** reads from an `AgenticTokenCache` created up front. Contrary to
  the documentation, `UseMicrosoftOpenTelemetry` does **not** register
  `IExporterTokenCache<AgenticTokenStruct>` itself in `Microsoft.OpenTelemetry` 1.0.7 — without
  registering it manually the host fails to start.
- **`UseS2SEndpoint` is left at its default.** The agentic-user path posts to `/observability/`;
  the S2S route is for the FMI chain that `dotnet-agent-teams` uses, and the two routes do not
  accept each other's tokens.

Per turn, in `LearnAgent.ResearchAsync`:

- The agent id is `turnContext.Activity.GetAgenticInstanceId()` — the **agentic instance id from
  the activity**, not a value decoded from a user token, and not the blueprint id.
- The turn runs inside a **`BaggageBuilder`** scope carrying tenant and agent id. Spans emitted
  outside one are dropped by the exporter as *"Partitioned into 0 identity groups"*.
- The exporter's token is registered through `AgenticTokenStruct(userAuthorization, turnContext,
  authHandlerName)`, because the exporter flushes on a background thread that has no turn context
  of its own.
- **`InvokeAgentScope`** wraps the run, so the inference and tool spans nest underneath it.
- If either the agent id or tenant id is missing, the turn runs with **no** observability rather
  than inventing an identity — spans that can never be authenticated are worse than none.

A healthy turn logs an HTTP 200 to
`…/observability/tenants/<tenant>/otlp/agents/<auid>/traces` and produces
`invoke_agent`, `chat gpt-4.1` and `execute_tool …` spans. Traces take roughly 15–90 minutes to
surface in Advanced Hunting.

### How WorkIQ is wired

`ToolingManifest.json` declares `mcp_MailTools` and `mcp_CalendarTools`, each with its own url,
audience and scope. A healthy turn logs 22 mail tools and 16 calendar tools — 38 in total.

**The A365 Tooling SDK is deliberately not used.** Every published version of
`Microsoft.Agents.A365.Tooling` up to 1.1.14-preview depends on
`ModelContextProtocol.Core 0.2.0-preview.3` and calls `ModelContextProtocol.Client.IMcpClient`.
This agent needs `ModelContextProtocol` 1.3.0 for its Learn client, and 1.3.0 removed that type,
so NuGet unifies on 1.3.0 and the SDK throws at runtime:

```text
System.TypeLoadException: Could not load type 'ModelContextProtocol.Client.IMcpClient'
from assembly 'ModelContextProtocol.Core, Version=1.3.0.0'
```

No version upgrade fixes this. `Agent365/WorkIqToolProvider.cs` reads the url, audience and scope
already present in `ToolingManifest.json` and connects to each server directly with the 1.3.0
client API. Each server is contacted independently and a failure is logged and skipped, so a
server that is down costs only its own tools and the agent still answers from Learn.

**Getting the token was the hard part**, and two plausible routes are dead ends:

| Route | Result |
|---|---|
| `IConnections.GetTokenProvider(...).GetAccessTokenAsync(...)` | Resolves to `MsalAuth`, which only ever calls `AcquireTokenForClient`. Entra refuses that outright for an AI Teammate: **`AADSTS82001: Agentic application '…' is not permitted to request app-only tokens for resource '…'`**. No scope value rescues it. |
| A granular scope on that same call | Rejected even earlier: **`AADSTS1002012: … must have a scope value with /.default suffixed`** |

The route that works is `UserAuthorization.ExchangeTurnTokenAsync(turnContext, "agentic", null,
[scope], ct)` — the `agentic` handler that `a365 setup` already wrote into `appsettings.json`.
This is exactly what the SDK does internally: `AgenticMcpTokenProvider` calls
`AgenticAuthenticationService.GetAgenticUserTokenAsync`, which is a thin wrapper over that same
method. It reaches a three-hop chain — blueprint → `api://AzureAdTokenExchange/.default` with
`WithFmiPath(instanceId)` → an instance token → a final call with `grant_type=user_fic`. That last
hop is **delegated**, which is why the granular `<audience>/Tools.ListInvoke.All` scope is correct
there and illegal on the client-credential path.

The scope shape mirrors the SDK's own `Utility.ResolveTokenScopeForServer`: `<audience>/<scope>`
for a per-audience (V2) server, falling back to `<audience>/.default` only when the manifest
carries no scope.

## Known gaps

- **`POST /api/messages` accepts unauthenticated activities.** A well-formed activity with no
  `Authorization` header returns 202 and reaches the model; it only fails at `ReplyToActivity`,
  so a spoofer cannot get a reply but *can* burn model spend. `TokenValidation.Enabled: true` in
  `appsettings.json` is **inert** — that literal appears in no installed SDK assembly. The
  official wiring is `AddAgentAspNetAuthentication(builder.Configuration)` plus
  `UseAuthentication`/`UseAuthorization` and `.RequireAuthorization()`, but that extension method
  ships in no `Microsoft.Agents.*` package — it is a sample-local `AspNetExtensions.cs` you copy
  in. `dotnet-agent-teams` has the same gap.
- **The email-notification path has never been exercised.**
- **`ResolveCallerUpnAsync` is unimplemented**, so MAC's "User principal name" column stays blank
  unless `From.Id` happens to contain an `@`.
