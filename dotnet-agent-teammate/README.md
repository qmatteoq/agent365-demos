# Microsoft Learn agent — .NET, Agent Framework, AI Teammate

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp). It answers questions
about Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365, and cites the
documentation it used.

Functionally identical to [`dotnet-agent-teams`](../dotnet-agent-teams) — same stack, same hosting,
same system prompt. The difference is entirely in **how it is onboarded to Agent 365**: this one is an
**AI Teammate**, so it acts under its **own identity** (the Agentic User) rather than on behalf of the
signed-in user.

That single change removes the Azure Bot registration and the whole on-behalf-of token chain.

| | |
| --- | --- |
| Language | .NET 10 |
| Agent framework | Microsoft Agent Framework (`Microsoft.Agents.AI.OpenAI`) |
| Hosting | Microsoft 365 Agents SDK (`Microsoft.Agents.Hosting.AspNetCore`) |
| Surface | Teams, Microsoft 365 Copilot |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP + Work IQ Mail and Calendar |
| Port | 3980 |

## How it fits together

```text
Teams / M365 Copilot
        │  Bot Framework activity
        ▼
ASP.NET Core host ──► LearnAgent : AgentApplication   Agent/LearnAgent.cs
                            │
                            ▼
                     AIAgent (Agent Framework)        Program.cs
                            ├──► Azure OpenAI  gpt-4.1
                            ├──► Microsoft Learn MCP           (3 tools)
                            └──► Work IQ Mail + Calendar MCP   (38 tools)
                                 Agent365/WorkIqToolProvider.cs
```

`Agent/ConversationSessionStore.cs` keeps one `AgentSession` per conversation, so chats are
multi-turn. Memory is in-process, so restarting the agent clears every conversation.

## Why there is no Azure Bot registration

This is the biggest structural difference from `dotnet-agent-teams`, and it is deliberate.

An AI Teammate's messaging endpoint is registered **on the blueprint**, not on an Azure Bot resource.
`a365 setup all --aiteammate --m365` calls the MCP Platform `createAgentBlueprint` endpoint, which
proxies Teams Graph and sets the bot `callbackUri` — the same value the Teams Developer Portal shows
as **Notification URL**.

Because there is no separate bot channel app, the identity constraints that apply to a custom engine
agent do not apply here:

| `dotnet-agent-teams` | This agent |
| --- | --- |
| Bot channel app + blueprint, kept strictly separate | Blueprint only |
| Blueprint cannot sign channel replies (`AADSTS82001`) | Not applicable — no channel app to sign as |
| `a365 setup all` overwrites the bot credentials, which must be restored | No bot credentials to overwrite |
| Observability exported on behalf of the human | Exported as the Agentic User |

There is only one identity to configure:

| Identity | Placeholder | Job |
| --- | --- | --- |
| Blueprint | `<blueprint-id>` | The agent's app registration and its Agent 365 registration |
| Agent instance (AUID) | `<agent-instance-id>` | The provisioned instance a turn actually runs as; the id spans are exported under |

## Configuration

| Setting | Purpose |
| --- | --- |
| `AzureOpenAI:Endpoint` / `Deployment` / `TenantId` | The model the agent reasons with |
| `LearnMcp:Endpoint` | Microsoft Learn MCP server |
| `Agent365Observability:AgentBlueprintId` | Stamped on spans; blank means reporting shows per-instance rows only |
| `AgentApplication:AgenticAuthHandlerName` | Name of the agentic auth handler — `agentic` |
| `AgentApplication:UserAuthorization:Handlers:agentic` | `AgenticUserAuthorization`, written by `a365 setup` |
| `Connections:ServiceConnection` | Blueprint credentials used to reply to the channel |

`AzureOpenAI:TenantId` pins `DefaultAzureCredential` to the tenant that owns the resource. Without it
a token from another tenant produces `HTTP 400 – Tenant provided in token does not match resource
token`.

> `appsettings.json` is the most fragile file here. Any `a365 setup` command rewrites it, and it will
> stamp the client secret back in as plaintext — check it before committing. Its two comment keys must
> also stay distinct (`"//"` and `"//connections"`); duplicate root keys crash the host at startup.

**No secret belongs in `appsettings.json`.** Set it once:

```powershell
dotnet user-secrets set "Connections:ServiceConnection:Settings:ClientSecret" "<blueprint secret>"
```

## Running it

### Prerequisites

- .NET 10 SDK
- The **Cognitive Services OpenAI User** role on the Azure OpenAI resource. Authentication uses
  `DefaultAzureCredential`, so `az login` is enough — there is no API key anywhere.
- The blueprint client secret in user secrets (above)
- `devtunnel`, to reach the agent from Teams
- Your own Agent 365 registration (see [Agent 365](#agent-365) below)

### Start it

```powershell
az login --tenant <tenant-id>
devtunnel host <tunnel-name>   # separate terminal
dotnet run
```

It listens on `http://localhost:3980`, with the channel endpoint at `/api/messages` and a plain
liveness string on `GET /`.

Unlike the other four agents this one has **no `.vscode` folder**, so there is no <kbd>F5</kbd>
profile — start it from a terminal as above. A *named* tunnel keeps the same public url across
restarts, which is what keeps the blueprint's registered messaging endpoint valid. The url is not
derived from the tunnel name and is only printed while the tunnel is hosted, so read it rather than
guessing it. To re-point the endpoint later:

```powershell
a365 setup blueprint --update-endpoint <url> --m365
```

`--m365` is required; without it the Teams Graph re-registration is skipped silently.

**Port 3980, not 3978 or 3979**, so all three Teams agents can run at once.

### It runs as Production, not Development

`Properties/launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Production`. This is deliberate and is
the opposite of the other agents in this repo.

The A365 Tooling SDK chooses its token provider purely from the environment name: it picks
`DevMcpTokenProvider`, which demands a hand-pasted `BEARER_TOKEN` environment variable, when
`ASPNETCORE_ENVIRONMENT` is exactly `Development`. Exercising the **real** agentic Work IQ path
locally therefore means running as Production.

Two things break if you do that naively, and both are already handled in `Program.cs`:

- **User secrets stop loading.** `WebApplication.CreateBuilder` registers the user-secrets provider
  *only* in Development. Without them `Connections:ServiceConnection:Settings:ClientSecret` is empty
  and every turn dies with `Failed to create authentication provider for connection name ''`. The
  failure is easy to misread: the turn still runs and the model still answers, and only the *reply*
  fails, so from Teams the agent just goes silent. `Program.cs` calls `AddUserSecrets` explicitly.
- **`ManagedIdentityCredential` comes back into the credential chain** and throws a fatal error that
  aborts the chain before the Azure CLI credential is reached.

Because the environment name was doing three unrelated jobs, a separate `A365_LOCAL_RUN` signal
carries the "am I on a laptop?" meaning. It defaults to local; set `A365_LOCAL_RUN=false` only when
genuinely cloud-hosted. It controls the credential choice and whether the console exporter is added —
it **never** disables the Agent 365 exporter.

### Commands

| Command | Effect |
| --- | --- |
| `/reset` | Forget the conversation so far |

## Agent 365

### Registering and publishing it

```powershell
a365 setup all --aiteammate --m365
a365 publish --aiteammate
```

Then upload `manifest/manifest.zip` at **admin.microsoft.com → Agents → All agents → Upload custom
agent**, and have a tenant admin approve an instance from
`https://admin.cloud.microsoft/#/agents/all/requested`. Nothing appears in the Agent Registry until
that upload happens — publishing locally is not enough. Approval is asynchronous and can take minutes
to hours.

Four things about the CLI worth knowing before you run it:

- **Omit `--authmode`.** `s2s` and `both` are rejected alongside `--aiteammate`; `obo` is accepted but
  warns that it is superfluous, since it is the default for an AI Teammate.
- **`a365 publish` re-derives `name.short` and `name.full`** from the CLI agent name on *every* run,
  overwriting hand edits. `description` survives. `name.short` is capped at 30 characters, so
  `--agent-name` must be 20 characters or fewer.
- **Publishing validates almost nothing.** It checks `name.short` and warns, but will let a
  101-character `description.short` through — the Admin Center then rejects it at a cap of **80**. A
  successful publish does not mean a valid package; verify the strings *inside* the zip.
- **The CLI owns `manifest.json`.** It generates and stamps it, so it must not be hand-written.
  Apostrophes in it are `\u0027`-escaped, so a literal `'` will not match on a find-and-replace.

The AI Teammate manifest has **no `bots` array and no `copilotAgents.customEngineAgents`** — only
`agenticUserTemplates`. Checklists written for custom engine agents do not apply.

When hunting traces in Defender, filter on the **agent instance id (AUID)**, not the blueprint id.

### How observability is instrumented

This is the one agent in the repo that matches a Microsoft scenario exactly:
[**Agent 365-enabled using OBO**](https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup#agent-365-enabled-using-obo).
Its turns *do* carry agentic identity, which is the criterion the docs select on, and why the built-in
`AgenticTokenCache` is the right choice here and the wrong one for a custom engine agent.

Wired in `Program.cs` and `Agent/LearnAgent.cs`:

- **`builder.UseMicrosoftOpenTelemetry(...)`** initialises the distro. `o.Exporters` is
  `ExportTarget.Agent365` unconditionally; `A365_LOCAL_RUN` only adds `ExportTarget.Console` on top.
  Traces reach Agent 365 during local testing exactly as they would in production.
- **`o.Instrumentation.EnableMetrics = false`.** The console metric exporter dumps every histogram
  bucket on a timer and drowns out the spans.
- **`o.Agent365.TokenResolver`** reads from an `AgenticTokenCache` created up front. The documentation
  says no custom resolver is needed on this path, but in `Microsoft.OpenTelemetry` 1.0.7
  `UseMicrosoftOpenTelemetry` does **not** register `IExporterTokenCache<AgenticTokenStruct>` itself —
  without registering it manually the host fails to start. The built-in cache still does the token
  work; the resolver is a thin read.
- **`UseS2SEndpoint` stays at its default.** The agentic-user path posts to `/observability/`; the
  `/observabilityService/` route is for application tokens, and the two do not accept each other's
  tokens.
- **`BaggageBackfillProcessor` is registered before the distro**, and the order is load-bearing. See
  [why the inference span needs it](#why-the-inference-span-needs-a-backfill-processor).

Per turn, in `LearnAgent.ResearchAsync`:

- The agent id is `turnContext.Activity.GetAgenticInstanceId()` — the **agentic instance id from the
  activity**, not a value decoded from a user token, and not the blueprint id.
- The turn runs inside a **`BaggageBuilder`** scope. Spans emitted outside one are dropped by the
  exporter as *"Partitioned into 0 identity groups"*. The chain starts with
  **`.FromTurnContext(turnContext)`**, which supplies the caller (`user.id`, `user.name`), the
  channel, the conversation and the agentic user, then sets tenant, agent id, agent name, blueprint
  and session explicitly. Order matters: `FromTurnContext` also writes `gen_ai.agent.id` from
  `Recipient.AgenticAppId`, so the explicit values come afterwards to win — `BaggageBuilder` keeps a
  single dictionary and the last write for a key survives.
- Baggage is what reaches the **child** spans. `CallerDetails` on the `InvokeAgentScope` only
  decorates the parent, so without the baggage the tool and model spans arrive anonymous.
- The exporter's token is registered through
  `AgenticTokenStruct(userAuthorization, turnContext, authHandlerName)`, because the exporter flushes
  on a background thread that has no turn context of its own.
- **`InvokeAgentScope`** wraps the run, so the inference and tool spans nest underneath it.
- If either the agent id or tenant id is missing, the turn runs with **no** observability rather than
  inventing an identity — spans that can never be authenticated are worse than none.

A healthy turn logs an HTTP 200 to
`…/observability/tenants/<tenant-id>/otlp/agents/<agent-instance-id>/traces` and produces
`invoke_agent`, `chat gpt-4.1` and `execute_tool …` spans. Traces take roughly 15–90 minutes to
surface in Advanced Hunting.

Both the parent `invoke_agent` span and its children carry the caller *and* the agent:

```text
user.id                   the human who asked
user.name                 the human who asked
microsoft.agent.user.id   the Agentic User the teammate runs as
microsoft.channel.name    msteams
```

That is what you want for a teammate: the agent acts under its own identity, but the turn is still
attributable to the person who started it.

#### Why the inference span needs a backfill processor

The SDK's `ActivityProcessor` copies baggage onto spans in **`OnStart`**, and only for spans that
already carry a `gen_ai.operation.name` tag. That holds for the scopes the A365 SDK creates itself,
which set the tag as they start.

It does not hold for the model call. `Microsoft.Extensions.AI` creates that span with

```csharp
activity = _activitySource.StartActivity("chat " + model, ActivityKind.Client);
```

and sets its tags **afterwards**. At `OnStart` there is nothing to match on, no baggage is copied, and
the exporter later drops the span:

```text
[Agent365Exporter] 1 spans skipped due to missing tenant or agent ID
```

The prompt, the system instructions and the completion go with it — so Defender records that a turn
happened and which tools ran, but never what the model was asked or what it said.

`Agent365/BaggageBackfillProcessor.cs` runs the same copy at **`OnEnd`**, when the tag exists. `OnEnd`
is raised synchronously on the thread that stops the activity, so `Baggage.Current` is still the
turn's baggage. It is deliberately narrow: only the SDK's own allowlisted operations, skipped entirely
if the span was already enriched (tested on `microsoft.tenant.id`, the field the exporter partitions
on), never overwriting a tag the instrumentation set itself, and identity keys only — message content
belongs to the span, not to the turn.

Registration order is what makes it work. Processors' `OnEnd` runs in registration order, so it is
added **before** `UseMicrosoftOpenTelemetry` to sit ahead of the export processor.

Measured on the same turn, before and after:

| | Before | After |
| --- | --- | --- |
| Exporter log | `1 spans skipped` | no skips |
| Export chunk | 1 span, 2,247 bytes | 2 spans, 99,255 bytes |

> This is a **workaround for an SDK timing quirk, not a supported extension point** — the distro
> exposes no processor hook. If a future SDK version enriches at `OnEnd` too, the guard makes this a
> no-op rather than a conflict. `MaxPayloadBytes` defaults to 900,000 and the largest observed chunk
> is ~155 KB, so the extra content has ample headroom.

### How Work IQ is wired

`ToolingManifest.json` declares `mcp_MailTools` and `mcp_CalendarTools`, each with its own url,
audience and scope. A healthy turn logs 22 mail tools and 16 calendar tools — 38 in total.

**The A365 Tooling SDK is deliberately not used.** Every published version of
`Microsoft.Agents.A365.Tooling` up to 1.1.14-preview depends on
`ModelContextProtocol.Core 0.2.0-preview.3` and calls `ModelContextProtocol.Client.IMcpClient`. This
agent needs `ModelContextProtocol` 1.3.0 for its Learn client, and 1.3.0 removed that type, so NuGet
unifies on 1.3.0 and the SDK throws at runtime:

```text
System.TypeLoadException: Could not load type 'ModelContextProtocol.Client.IMcpClient'
from assembly 'ModelContextProtocol.Core, Version=1.3.0.0'
```

No version upgrade fixes this. `Agent365/WorkIqToolProvider.cs` reads the url, audience and scope
already present in `ToolingManifest.json` and connects to each server directly with the 1.3.0 client
API. Each server is contacted independently and a failure is logged and skipped, so a server that is
down costs only its own tools and the agent still answers from Learn.

**The token comes from the agentic handler.** Two plausible routes are dead ends:

| Route | Result |
| --- | --- |
| `IConnections.GetTokenProvider(...).GetAccessTokenAsync(...)` | Resolves to `MsalAuth`, which only ever calls `AcquireTokenForClient`. Entra refuses that outright for an AI Teammate: **`AADSTS82001: Agentic application '…' is not permitted to request app-only tokens for resource '…'`**. No scope value rescues it. |
| A granular scope on that same call | Rejected even earlier: **`AADSTS1002012: … must have a scope value with /.default suffixed`** |

The route that works is
`UserAuthorization.ExchangeTurnTokenAsync(turnContext, "agentic", null, [scope], ct)` — the `agentic`
handler that `a365 setup` already wrote into `appsettings.json`. This is exactly what the SDK does
internally: `AgenticMcpTokenProvider` calls `AgenticAuthenticationService.GetAgenticUserTokenAsync`,
a thin wrapper over that same method. It reaches a three-hop chain — blueprint →
`api://AzureAdTokenExchange/.default` with `WithFmiPath(instanceId)` → an instance token → a final
call with `grant_type=user_fic`. That last hop is **delegated**, which is why the granular
`<audience>/Tools.ListInvoke.All` scope is correct there and illegal on the client-credential path.

The scope shape mirrors the SDK's own `Utility.ResolveTokenScopeForServer`: `<audience>/<scope>` for a
per-audience (V2) server, falling back to `<audience>/.default` only when the manifest carries no
scope.

## Known gaps

- **`POST /api/messages` accepts unauthenticated activities.** A well-formed activity with no
  `Authorization` header returns 202 and reaches the model; it only fails at `ReplyToActivity`, so a
  spoofer cannot get a reply but *can* burn model spend. `TokenValidation.Enabled: true` in
  `appsettings.json` is **inert** — that literal appears in no installed SDK assembly. The official
  wiring is `AddAgentAspNetAuthentication(builder.Configuration)` plus `UseAuthentication` /
  `UseAuthorization` and `.RequireAuthorization()`, but that extension method ships in no
  `Microsoft.Agents.*` package — it is a sample-local `AspNetExtensions.cs` you copy in.
  `dotnet-agent-teams` has the same gap.
- **The email-notification path has never been exercised.**
- **`ResolveCallerUpnAsync` is unimplemented**, so the "User principal name" column stays blank unless
  `From.Id` happens to contain an `@`.
