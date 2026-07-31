# Microsoft Learn agent — .NET, Agent Framework, Teams

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp). It answers questions
about Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365, and cites the
documentation it used.

Hosted in **Microsoft Teams and Microsoft 365 Copilot** through the **Microsoft 365 Agents SDK**, and
onboarded to Agent 365 as a **custom engine agent** exporting observability **on behalf of the
signed-in user**. The AI Teammate variant of the same agent is
[`dotnet-agent-teammate`](../dotnet-agent-teammate); comparing the two shows what the AI Teammate
shape changes.

|  |  |
| --- | --- |
| Language | .NET 10 |
| Agent framework | Microsoft Agent Framework (`Microsoft.Agents.AI.OpenAI`) |
| Hosting | Microsoft 365 Agents SDK (`Microsoft.Agents.Hosting.AspNetCore`) |
| Surface | Teams, Microsoft 365 Copilot |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP + Work IQ Mail, Calendar and Teams |
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
                            └──► Work IQ Mail/Calendar/Teams  Agent365/WorkIqToolProvider.cs
```

`Agent/ConversationSessionStore.cs` keeps one `AgentSession` per conversation, so chats are
multi-turn. Memory is in-process, so restarting the agent clears every conversation.

## Identities

Three applications are involved, and conflating them is the fastest way to break this agent.

| Identity | Placeholder | Job |
| --- | --- | --- |
| Bot channel app | `<bot-app-client-id>` | Validates the inbound Teams token, signs outbound replies, is the principal of the observability token, and is hop 1 of the Work IQ token chain |
| Blueprint | `<blueprint-id>` | Hop 2 of the Work IQ chain — proving it owns the agent identity |
| Agent identity | `<agent-identity-id>` | The principal the Work IQ token finally belongs to |

**The bot channel app must be a plain single-tenant Entra app, not a blueprint.** Entra bars agentic
applications from requesting client-credentials tokens (`AADSTS82001`), so a blueprint cannot sign
outbound Bot Framework replies at all. It is also the wrong audience: inbound Bot Framework tokens
carry `aud = <bot-app-client-id>`, so pointing token validation at the blueprint rejects every
request.

> **After running `a365 setup all`, restore the bot channel credentials.** The CLI overwrites them
> with the blueprint's and replaces the bot secret in place, so the original is unrecoverable — reset
> it with `az ad app credential reset --id <bot-app-client-id>` and put the bot app's own client id
> and secret back into `Connections:BotConnection`. This fails silently: the running process keeps
> the old values in memory, so it only breaks on the next restart.

## Configuration

| Setting | Purpose |
| --- | --- |
| `AzureOpenAI:Endpoint` / `Deployment` / `TenantId` | The model the agent reasons with |
| `LearnMcp:Endpoint` | Microsoft Learn MCP server |
| `Connections:BotConnection` | Bot channel app — channel auth, the observability token's client, and hop 1 of the Work IQ chain |
| `Connections:ServiceConnection` | The blueprint, as written by `a365 setup all`. `ConnectionsMap` routes all traffic to `BotConnection`, and the Work IQ chain reads its blueprint credentials from `Agent365Observability:*`, so nothing in this agent's own code uses it |
| `Agent365Observability:*` | Agent id, blueprint id and blueprint secret, used by the Work IQ token chain |

**No secret belongs in `appsettings.json`.** Use `dotnet user-secrets`.

## Running it

### Prerequisites

- .NET 10 SDK
- The **Cognitive Services OpenAI User** role on the Azure OpenAI resource (`az login` is enough — no
  API key anywhere)
- `devtunnel`
- Your own Agent 365 registration (see [Agent 365](#agent-365) below)
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
az login --tenant <tenant-id>
devtunnel host <tunnel-name>   # separate terminal
dotnet run
```

`GET /` returns a liveness string; the channel endpoint is `POST /api/messages`.

**Port 3978.** `python-agent-teams` uses 3979 and `dotnet-agent-teammate` uses 3980, so all three can
run at once.

### The dev tunnel

Teams cannot reach `localhost`, so the agent is exposed through a **named** dev tunnel. Named
matters: it keeps the same public url across restarts, and that url is registered as the Azure Bot's
messaging endpoint. An anonymous tunnel would issue a new url per run and silently break the channel.

```powershell
devtunnel create <tunnel-name> --allow-anonymous
devtunnel port create <tunnel-name> --port-number 3978
devtunnel show <tunnel-name>
```

The url is not derived from the tunnel name — `devtunnel show` prints the real one. Set the Azure
Bot's messaging endpoint to `<url>/api/messages`.

### Testing it

`appPackage/manifest.json` carries `${{BOT_ID}}` / `${{TEAMS_APP_ID}}` placeholders, so it cannot be
zipped by hand. Build the package first:

```powershell
./build-app-package.ps1        # writes appPackage.zip
```

Then sideload `appPackage.zip` in Teams (**Apps → Manage your apps → Upload an app**). Because the
bot id and Teams app id are fixed, repeat uploads update the same app instead of creating a new one.

Or skip Teams entirely and use the Microsoft 365 Agents Playground:

```powershell
npm install -g @microsoft/teams-app-test-tool
teamsapptester
```

The Playground connects to `http://127.0.0.1:3978/api/messages`. Work IQ tools will not resolve
there — they need a real Teams SSO token to exchange.

Because the manifest declares `copilotAgents.customEngineAgents` and includes `copilot` in the bot's
scopes, the same package also surfaces the agent inside Microsoft 365 Copilot.

## Agent 365

### Registering it

From this folder:

```powershell
a365 setup all --authmode obo
```

This creates the blueprint and agent identity and grants the Work IQ permissions declared in
`ToolingManifest.json`. Afterwards, restore the bot channel credentials as described under
[Identities](#identities).

You also need an **Azure Bot OAuth connection** for observability — see below.

### How observability is instrumented

This agent is a **custom engine agent**: it is reached through its own bot registration, so its
activities carry no agentic identity (`Recipient.AgenticAppId` is null). That places it in the
documented
[custom engine using OBO](https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup#custom-engine-using-obo)
scenario, where **the export id must be the bot app registration's client id**.

The export route authorises by comparing the token's `azp` against the agent id in the url, and on
this path the Bot Framework Token Service issues the token to the bot app — so both are the bot app's
client id, and they agree. Spans therefore carry the bot app id in `gen_ai.agent.id`. That does not
orphan them: Microsoft Admin Center resolves the route id back to the registered agent and reports
the traces under the **agent identity's** display name.

Wired in `Program.cs`, `Agent365/` and `Observability/`:

- **`builder.UseMicrosoftOpenTelemetry(...)`** sets `o.Exporters` to
  `ExportTarget.Agent365 | ExportTarget.Console` in Development and `ExportTarget.Agent365` in
  Production — the Agent 365 export is **never** disabled.
- **`o.Agent365.UseS2SEndpoint` stays at its default (`false`).** The delegated token is accepted by
  `/observability/`, not the `/observabilityService/` route an app-only token targets.
- **Infrastructure instrumentation is re-enabled explicitly** (`EnableAspNetCoreInstrumentation`,
  `EnableHttpClientInstrumentation`, `EnableAzureSdkInstrumentation`), because exporting to Agent 365
  alone otherwise suppresses it.
- **The agent id is pinned** to `Connections:BotConnection:Settings:ClientId`. Left unset, the SDK
  generates a fresh GUID per run and the exporter emits orphan identity groups it cannot
  authenticate.
- **`o.Agent365.TokenResolver`** reads from `Agent365/ObservabilityTokenStore.cs`. The exporter
  flushes on a background loop with no turn context, so the token cannot be minted there: each turn
  deposits one in the store and the resolver reads it back.

**The token chain is a single call.** `Agent/LearnAgent.cs` → `PublishObservabilityTokenAsync` calls
`UserAuthorization.GetTurnTokenAsync(turnContext, "observability")` and deposits the result. No MSAL,
no federated identity chain, no blueprint secret — the Bot Framework Token Service performs the
on-behalf-of exchange internally, against the Azure Bot OAuth connection named by the handler.

The work is in the **Azure Bot OAuth connection**, not in the code. The agent has two:

| Connection | Scope | Used for |
| --- | --- | --- |
| `BotOAuth` | `api://botid-<bot-app-client-id>/defaultScopes` | Work IQ — its token is the OBO *assertion*, so its audience must be this app |
| `oboConnectionProfile` | `api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite` | observability — the token is used directly |

Observability needs a **second** connection rather than a re-scoped `BotOAuth`, because Work IQ uses
`BotOAuth`'s `api://botid-…` token as its OBO assertion and an assertion must have the exchanging
client as its audience. Both share a `tokenExchangeUrl` of `api://botid-<bot-app-client-id>`, which
is what keeps Teams SSO silent for both: the Teams manifest declares one
`webApplicationInfo.resource`, and each connection exchanges that single SSO token for its own
configured scope.

Create the observability connection with:

```bash
az bot authsetting create \
  --resource-group <resource-group> \
  --name <azure-bot-name> \
  --setting-name oboConnectionProfile \
  --client-id <bot-app-client-id> \
  --client-secret <bot-app-secret> \
  --service "Aadv2" \
  --parameters clientId="<bot-app-client-id>" clientSecret="<bot-app-secret>" \
    tenantId="<tenant-id>" tokenExchangeUrl="api://botid-<bot-app-client-id>" \
  --provider-scope-string \
    "api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite"
```

Leaving the scope at the default `api://botid-<client-id>/defaultScopes` produces
`401 InvalidAudience`.

Per turn, in `Agent/LearnAgent.cs`:

- A **`BaggageBuilder`** scope flows identity onto every child span. Spans emitted outside one are
  dropped as *"Partitioned into 0 identity groups"*. It starts with
  **`.FromTurnContext(turnContext)`**, which adds `user.id` and `user.name` off `Activity.From` plus
  `microsoft.channel.name` — the last of which `BaggageBuilder`'s documentation lists as a
  certification requirement alongside the tenant and conversation ids. The explicit `.TenantId()` /
  `.AgentId()` / `.ConversationId()` calls come **after** it deliberately: `FromTurnContext` also
  writes `gen_ai.agent.id` from `Recipient.AgenticAppId`, which is null on a non-agentic Teams turn,
  and the builder keeps one dictionary where the last write per key wins. `.AgentName()`,
  `.AgentBlueprintId()` and `.SessionId()` are chained too, from configuration — the activity carries
  none of them.

  `AgentDetails` on the `InvokeAgentScope` below names the same three values but **decorates only the
  parent span**. Omit them from the baggage and every `execute_tool` and `chat` row arrives with a
  bare agent id and no blueprint — and the blueprint is what groups instances of the same agent
  together in reporting.
- An **`InvokeAgentScope`** wraps the run, with `RecordInputMessages` / `RecordOutputMessages`, and is
  given `CallerDetails` so the parent span names the human as well.
- **No manual `InferenceScope` or `ExecuteToolScope`.** The chat client is wrapped with
  `.UseFunctionInvocation()` and `.UseOpenTelemetry()`, which emits the `gen_ai` inference and tool
  spans as children automatically. Without that wrapping the agent answers but Defender shows a
  hollow parent span.
- **`BaggageBackfillProcessor`** (registered in `Program.cs` **before** the distro) is what gets the
  inference span exported at all. Registration order is load-bearing — `OnEnd` runs in registration
  order, so it must sit ahead of the export processor. See the
  [root README](../README.md#emitting-a-span-is-not-the-same-as-exporting-it) for the full
  explanation.

### How Work IQ is wired

`ToolingManifest.json` declares `mcp_MailTools`, `mcp_CalendarTools` and `mcp_TeamsServer`, each with
its own url, audience and scope (`Tools.ListInvoke.All`).

**Tool discovery is bypassed.** `IMcpToolRegistrationService.GetMcpToolsAsync` first asks the gateway
which servers the agent may use:

```text
GET https://agent365.svc.cloud.microsoft/agents/v2/{agentId}/mcpServers  ->  500
```

That route returns 500 even for a syntactically valid but non-existent agent id, where a 404 would be
expected, while other `/agents/v2/` routes are healthy — so the route itself is at fault. The path is
hardcoded in every published version of `Microsoft.Agents.A365.Tooling`, so there is no version to
pin to.

Discovery is unnecessary anyway: `ToolingManifest.json` already carries the url, audience and scope
for every server. `Agent365/WorkIqToolProvider.cs` connects to each one directly with
`HttpClientTransport` + `McpClient` over streamable HTTP, retrying once on a transport drop and
skipping any server that stays unavailable, so one bad server costs only its own tools. The SDK
services are still registered so the extension stays wired, but nothing routes through them. Once the
gateway is fixed, this can be replaced by `GetMcpToolsAsync` with no other change.

**The token is a three-hop delegated chain** (`Agent365/WorkIqTokenService.cs`, raw HTTP). The servers
require the delegated `Tools.ListInvoke.All` scope and reject anything else, including an app-only
token:

```text
403  Access denied: Scope 'Tools.ListInvoke.All' is not present in the request.
```

1. **Bot channel app** exchanges the user's Teams token for
   `api://<blueprint-id>/access_agent_as_user`. Teams SSO issues a token whose audience is the
   channel app, and the last hop only accepts an assertion issued to the blueprint family.
2. **Blueprint** + secret + `fmi_path=<agent-identity-id>` → a token-exchange assertion, proving it
   owns that identity.
3. **Agent identity** performs the final on-behalf-of exchange for `<audience>/.default`, presenting
   that assertion as its client credential.

The result belongs to the governed agent identity acting for the user, which is what tool calls must
be attributed to. The channel app holds the same consented permissions and could satisfy the scope
check on its own, but that token would not be tied to the agent identity, so it is deliberately not
used.

Tools are resolved **per turn**, not at startup, because each one is called with a token exchanged for
the current user — so the agent can only reach mail, calendar and Teams content that user could open
themselves. The three servers expose 74 tools between them (mail 22, calendar 16, Teams 36).

> Sign-in is attached to the **message route only**, not globally. `AutoSignIn` fires on every
> activity including the `conversationUpdate` raised at install time, which would surface a sign-in
> prompt before the user has asked anything. Teams SSO failures are otherwise silent and show up only
> as missing Work IQ tools, so the failure handler logs and tells the user.

## Known gaps

- **`POST /api/messages` accepts unauthenticated activities.** No authentication middleware is wired,
  so `TokenValidation` in `appsettings.json` is not enforced. The official fix is
  `AddAgentAspNetAuthentication(builder.Configuration)` plus `UseAuthentication` / `UseAuthorization`
  and `.RequireAuthorization()` — but that extension method ships in no `Microsoft.Agents.*` package;
  it is a sample-local `AspNetExtensions.cs` you copy in. `dotnet-agent-teammate` has the same gap.
