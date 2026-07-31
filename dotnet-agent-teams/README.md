# Microsoft Learn agent — .NET, Agent Framework, Teams

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/api/mcp). It answers questions about
Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365, and cites the
documentation it used.

This is the second agent in this repo. Same agent core as
[`dotnet-agent-no-teams`](../dotnet-agent-no-teams), but hosted in **Microsoft Teams and Microsoft
365 Copilot** through the **Microsoft 365 Agents SDK** instead of a Blazor app — the .NET
counterpart of [`python-agent-teams`](../python-agent-teams).

It is onboarded to Agent 365 as a **system agent** with observability exported
**service-to-service**. The AI Teammate variant of the same agent is
[`dotnet-agent-teammate`](../dotnet-agent-teammate); comparing the two is the point of having both.

|  |  |
| --- | --- |
| Language | .NET 10 |
| Agent framework | Microsoft Agent Framework (`Microsoft.Agents.AI.OpenAI`) |
| Hosting | Microsoft 365 Agents SDK (`Microsoft.Agents.Hosting.AspNetCore`) |
| Surface | Teams, Microsoft 365 Copilot |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP + WorkIQ Mail, Calendar and Teams |
| Port | 3978 |

## How it fits together

```text
Teams / M365 Copilot
        │  Bot Framework activity (JWT signed)
        ▼
Azure Bot ──► https://<dev-tunnel>/api/messages
        ▼
ASP.NET Core host ──► LearnAgent : AgentApplication   Agent/LearnAgent.cs
                            │
                            ▼
                     AIAgent (Agent Framework)        Program.cs
                            ├──► Azure OpenAI  gpt-4.1
                            ├──► Microsoft Learn MCP        (3 tools)
                            └──► WorkIQ Mail/Calendar/Teams  Agent365/WorkIqToolProvider.cs
```

`Agent/ConversationSessionStore.cs` keeps one `AgentSession` per conversation, so chats are
multi-turn. Memory is in-process, so restarting the agent clears every conversation.

## Running it

### Prerequisites

- .NET 10 SDK
- The **Cognitive Services OpenAI User** role on the Azure OpenAI resource (`az login` is enough —
  no API key anywhere)
- `devtunnel`
- Two secrets in user secrets:

  ```powershell
  dotnet user-secrets set "Connections:BotConnection:Settings:ClientSecret" "<bot app secret>"
  dotnet user-secrets set "Agent365Observability:ClientSecret" "<blueprint secret>"
  ```

  Both are required. The agent runs as `Development`, which is what makes user secrets load at all.

### Start it

Open **this folder** in VS Code and press <kbd>F5</kbd>. That runs `dotnet build`, brings the named
dev tunnel up (waiting for `Ready to accept connections for tunnel:`), then launches the agent on
`http://localhost:3978` with the debugger attached.

By hand:

```powershell
az login --tenant 57db880c-370a-428d-9139-2b346b4eb220
devtunnel host dotnet-agent-teams-tunnel   # separate terminal
dotnet run
```

`GET /` returns a liveness string; the channel endpoint is `POST /api/messages`.

**Port 3978.** `python-agent-teams` uses 3979 and `dotnet-agent-teammate` uses 3980, so all three
can run at once.

### The dev tunnel

Teams cannot reach `localhost`, so the agent is exposed through a **named** dev tunnel. Named
matters: it keeps the same public url across restarts, and that url is registered as the Azure
Bot's messaging endpoint. An anonymous tunnel would issue a new url per run and silently break the
channel. The url is not derived from the tunnel name and is only printed while hosting — read it,
do not guess it.

It already exists in this environment. On a new machine, create it once:

```powershell
devtunnel create dotnet-agent-teams-tunnel --allow-anonymous
devtunnel port create dotnet-agent-teams-tunnel --port-number 3978
devtunnel show dotnet-agent-teams-tunnel
```

If the url differs from the one on the Azure Bot, update the bot's messaging endpoint to
`<url>/api/messages`.

### Testing it

`appPackage/manifest.json` carries `${{BOT_ID}}` / `${{TEAMS_APP_ID}}` placeholders, so it cannot be
zipped by hand. Build the package first:

```powershell
./build-app-package.ps1        # writes appPackage.zip
```

Then sideload `appPackage.zip` in Teams (**Apps → Manage your apps → Upload an app**). The bot id
defaults to the bot channel app and the Teams app id is fixed, so repeat uploads update the same app
instead of creating a new one.

Or skip Teams entirely and use the Microsoft 365 Agents Playground:

```powershell
npm install -g @microsoft/teams-app-test-tool
teamsapptester
```

The Playground connects to `http://127.0.0.1:3978/api/messages`. Note that WorkIQ tools will not
resolve there — they need a real Teams SSO token to exchange.

Because the manifest declares `copilotAgents.customEngineAgents` and includes `copilot` in the
bot's scopes, the same package also surfaces the agent inside Microsoft 365 Copilot.

## Identities

Three applications are involved, and conflating them is the fastest way to break this agent:

| Identity | App id | Job |
|---|---|---|
| Bot channel app | `0cf93255-7aee-4542-8df9-fc53bb8af150` | Validates the inbound Teams token, signs outbound replies, and is hop 1 of the WorkIQ chain |
| Blueprint | `f56c2c54-5fb4-4097-a73e-95970ea5b8f7` | Hop 1 of the observability chain, hop 2 of the WorkIQ chain |
| Agent identity | `a349a3ca-4c84-4165-be0a-8a0e5041b460` | The principal every outbound A365 token finally belongs to |

The bot channel app is a plain single-tenant Entra app, **not** a blueprint. Entra bars agentic
applications from client-credentials tokens (**`AADSTS82001`**), so a blueprint cannot sign
outbound Bot Framework replies at all. It is also the wrong audience: inbound Bot Framework tokens
carry `aud = <bot channel app>`, so pointing validation at the blueprint rejects every request.

> ⚠️ **`a365 setup all` overwrites the bot channel credentials with the blueprint's** and replaces
> the bot secret in place, so the original is unrecoverable. Reset it with
> `az ad app credential reset --id 0cf93255-…` and restore the connection settings afterwards. It
> breaks silently: the running process keeps the old values in memory, so it only fails on the next
> restart.

## Configuration

| Setting | Purpose |
|---|---|
| `AzureOpenAI:Endpoint` / `Deployment` / `TenantId` | The model the agent reasons with |
| `LearnMcp:Endpoint` | Microsoft Learn MCP server |
| `Connections:BotConnection` | Bot channel app — channel auth and WorkIQ hop 1 |
| `Connections:ServiceConnection` | Blueprint — governance, WorkIQ, observability |
| `Agent365Observability:*` | Identity and credentials for the exporter's token chain |

**No secret belongs in `appsettings.json`.** Use `dotnet user-secrets`.

## Agent 365

| | |
|---|---|
| Auth mode | `s2s` (service-to-service) |
| Blueprint | `f56c2c54-5fb4-4097-a73e-95970ea5b8f7` |
| Agent identity | `a349a3ca-4c84-4165-be0a-8a0e5041b460` |
| Bot channel app | `0cf93255-7aee-4542-8df9-fc53bb8af150` |

When hunting traces in Defender, filter on the **agent identity**, not the blueprint id.

### How observability is instrumented

Wired in `Program.cs` and `Observability/`:

- **`builder.Services.AddAgent365Observability()`** registers `ObservabilityTokenService` as a
  hosted service, but only when `Agent365Observability` is completely configured (tenant, agent id,
  client id, and either a managed identity or a secret). An incomplete config leaves it unstarted
  rather than failing at runtime.
- **`builder.UseMicrosoftOpenTelemetry(...)`** sets `o.Exporters` to
  `ExportTarget.Agent365 | ExportTarget.Console` in Development and `ExportTarget.Agent365` in
  Production — the Agent 365 export is **never** disabled.
- **`o.Agent365.UseS2SEndpoint = true`.** This is the key difference from the two OBO agents.
  Service-to-service traces go to a different route than delegated ones, and the routes do not
  accept each other's tokens. The distro leaves this off by default, so it must be set explicitly.
- **Infrastructure instrumentation is re-enabled explicitly** (`EnableAspNetCoreInstrumentation`,
  `EnableHttpClientInstrumentation`, `EnableAzureSdkInstrumentation`), because exporting to
  Agent 365 alone otherwise suppresses it.
- **The agent id is pinned** to `Agent365Observability:AgentId`. Left unset, the SDK generates a
  fresh GUID per agent and the exporter emits orphan identity groups.
- **`o.Agent365.TokenResolver`** reads from an `IExporterTokenCache<string>` (`ServiceTokenCache`)
  that `ObservabilityTokenService` refills every 50 minutes, retrying after a minute on failure.
  The exporter flushes on a background loop with no turn context, so the token has to be waiting
  for it.

**The token chain** (`Observability/ObservabilityTokenService.cs`) — MSAL, not raw HTTP:

1. **Blueprint** + secret (or managed identity), `WithFmiPath(<agent identity>)`, scope
   `api://AzureADTokenExchange/.default` → an assertion. If managed identity fails it falls back to
   the client secret.
2. **Agent identity** authenticates with that assertion as its client credential, scope
   `api://9b975845-388f-4429-889e-eab1ef63949c/.default` → the Observability API token.

A delegated *user* token is rejected outright by the export route, because its principal is the
human rather than the agent — so whatever the chain looks like, it has to end at the agent identity.

> ⚠️ **This agent's use of the S2S shape is under review, and the reason previously given for it was
> wrong.** The claim was that "a Teams agent has no interactive sign-in and therefore no user
> assertion to exchange". That is false here: hop 1 of this agent's own WorkIQ chain exchanges the
> user's Teams SSO token every turn, so a user assertion *is* available and the OBO shape
> (`AddAgenticTracingExporter`, no `UseS2SEndpoint`) was possible. Microsoft's own guidance picks the
> auth mode on whether a user is in the loop at runtime — for a Teams agent, that means OBO. Note
> also that `a365.config.json` records no `--authmode`, so the *registration* used the default
> delegated grants; only the exporter is S2S. Attribution is not the reason to switch: caller
> identity travels in baggage and survives the S2S route (see the instrumentation note above).

Per turn, in `Agent/LearnAgent.cs`:

- A **`BaggageBuilder`** scope flows identity onto every child span. Spans emitted outside one are
  dropped as *"Partitioned into 0 identity groups"*. It starts with
  **`.FromTurnContext(turnContext)`**, which adds `user.id` and `user.name` off `Activity.From`
  plus `microsoft.channel.name` — the last of which `BaggageBuilder`'s own documentation lists as a
  certification requirement alongside the tenant and conversation ids. The explicit
  `.TenantId()` / `.AgentId()` / `.ConversationId()` calls come **after** it deliberately:
  `FromTurnContext` also writes `gen_ai.agent.id` from `Recipient.AgenticAppId`, which is null on a
  non-agentic Teams turn, and the builder keeps one dictionary where the last write per key wins.
  `.AgentName()`, `.AgentBlueprintId()` and `.SessionId()` are chained too, from configuration —
  the activity carries none of them. It is easy to think `AgentDetails` on the `InvokeAgentScope`
  below covers this, since it names the same three; it does not. **`AgentDetails` decorates only
  the parent span.** Omit them here and every `execute_tool` and `chat` row arrives with a bare
  agent id and no blueprint — and the blueprint is what groups instances of the same agent
  together in reporting.
- An **`InvokeAgentScope`** wraps the run, with `RecordInputMessages` / `RecordOutputMessages`, and
  is given `CallerDetails` so the parent span names the human as well.
- **No manual `InferenceScope` or `ExecuteToolScope`.** The chat client is wrapped with
  `.UseFunctionInvocation()` and `.UseOpenTelemetry()`, which emits the `gen_ai` inference and tool
  spans as children automatically. Without that wrapping the agent answers but Defender shows a
  hollow parent span.
- **`BaggageBackfillProcessor` (registered in `Program.cs` before the distro) is what gets the
  inference span exported at all.** The SDK enriches spans from baggage in `OnStart`, and only when
  the span already carries `gen_ai.operation.name`. Microsoft.Extensions.AI creates its `chat` span
  with `StartActivity("chat " + model, ActivityKind.Client)` and sets the tags afterwards — verified
  by decompiling `OpenTelemetryChatClient` — so the enrichment misses it and the exporter drops it
  with *"1 spans skipped due to missing tenant or agent ID"*, taking the prompt and the completion
  with it. The processor re-runs the copy at `OnEnd`, when the tag exists. Registration order is
  load-bearing: `OnEnd` runs in registration order, so it must be added **before**
  `UseMicrosoftOpenTelemetry` to sit ahead of the export processor. See
  `dotnet-agent-teammate/README.md` for the full write-up and the before/after measurements.
  This is a workaround for an SDK timing quirk, not a supported extension point.

> **The export token does not carry the caller — baggage does.** It is tempting to assume the S2S
> route loses user attribution because its token belongs to the agent rather than a person. It does
> not. `AddServiceTracingExporter` and `AddAgenticTracingExporter` differ only in which token cache
> they use and in `UseS2SEndpoint`; the payload is built identically. Verified on a live turn: the
> `invoke_agent` span and its `execute_tool` children all carried `user.id` and `user.name`, and
> `POST /observabilityService/.../traces` returned **200**. Whether the portal *renders* that user
> for an S2S export is a separate question that only the portal can answer.

### How WorkIQ is wired

`ToolingManifest.json` declares `mcp_MailTools`, `mcp_CalendarTools` and `mcp_TeamsServer`, each
with its own url, audience and scope (`Tools.ListInvoke.All`).

**Tool discovery is bypassed because the route is broken service-side.**
`IMcpToolRegistrationService.GetMcpToolsAsync` first asks the gateway which servers the agent may
use:

```text
GET https://agent365.svc.cloud.microsoft/agents/v2/{agentId}/mcpServers  ->  500
```

It returns 500 even for a syntactically valid but non-existent agent id, where a 404 would be
expected, and other `/agents/v2/` routes are healthy — so the route itself is at fault, not this
agent. The path is hardcoded in every published version of `Microsoft.Agents.A365.Tooling`, so
there is no version to pin to.

Discovery turns out to be unnecessary: `ToolingManifest.json` already carries the url, audience and
scope for every server. `Agent365/WorkIqToolProvider.cs` therefore connects to each one directly
with `HttpClientTransport` + `McpClient` over streamable HTTP, retrying once on a transport drop
(the WorkIQ endpoints have been seen dropping TLS mid-handshake) and skipping any server that stays
unavailable. The SDK services are still registered so the extension stays wired, but nothing routes
through them.

**The token is a three-hop delegated chain** (`Agent365/WorkIqTokenService.cs`, raw HTTP). The
servers require the delegated `Tools.ListInvoke.All` scope and reject anything else, including an
app-only token:

```text
403  Access denied: Scope 'Tools.ListInvoke.All' is not present in the request.
```

1. **Bot channel app** exchanges the user's Teams token for `api://<blueprint>/access_agent_as_user`.
   Teams SSO issues a token whose audience is the channel app, and the last hop only accepts an
   assertion issued to the blueprint family.
2. **Blueprint** + secret + `fmi_path=<agent identity>` → a token-exchange assertion, proving it
   owns that identity.
3. **Agent identity** performs the final on-behalf-of exchange for `<audience>/.default`,
   presenting that assertion as its client credential.

The result belongs to the governed agent identity acting for the user, which is what tool calls
must be attributed to. The channel app holds the same consented permissions and could satisfy the
scope check on its own, but that token would not be tied to the agent identity, so it is
deliberately not used.

Tools are resolved **per turn**, not at startup, because each one is called with a token exchanged
for the current user — so the agent can only reach mail, calendar and Teams content that user could
open themselves.

Verified working on a live Teams turn: all three servers connect and expose 74 tools between them
(mail 22, calendar 16, Teams 36).

> Sign-in is attached to the message route only, not globally. `AutoSignIn` fires on every activity
> including the `conversationUpdate` raised at install time, which surfaces a sign-in prompt before
> the user has asked anything. Teams SSO failures are otherwise silent and show up only as missing
> WorkIQ tools, so the failure handler logs and tells the user.

## Known gaps

- **`POST /api/messages` accepts unauthenticated activities.** No authentication middleware is
  wired, so `TokenValidation` in `appsettings.json` is not enforced. The official fix is
  `AddAgentAspNetAuthentication(builder.Configuration)` plus `UseAuthentication`/`UseAuthorization`
  and `.RequireAuthorization()` — but that extension method ships in no `Microsoft.Agents.*`
  package; it is a sample-local `AspNetExtensions.cs` you copy in.
  `dotnet-agent-teammate` has the same gap.
- The bot app secret for `0cf93255-…` has been exposed and still wants rotating.
````