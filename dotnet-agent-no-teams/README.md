# Microsoft Learn agent — .NET, Agent Framework, Blazor web app

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp). It answers questions
about Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365, and cites the
documentation it used.

The only .NET agent here with **no Teams hosting** — you chat with it in the browser. It is the .NET
counterpart of [`python-agent-no-teams`](../python-agent-no-teams).

| | |
| --- | --- |
| Language | .NET 10 |
| Agent framework | Microsoft Agent Framework (`Microsoft.Agents.AI.OpenAI`) |
| Hosting | Blazor Web App (Interactive Server) |
| Surface | Browser |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP + Work IQ Mail, Calendar and Teams |
| Ports | 5140 (http), 7199 (https) |

## How it fits together

```text
Browser
   │  sign-in (OpenID Connect)
   ▼
Blazor Web App ──► Home.razor            the chat page and the per-turn A365 wiring
                      │
                      ▼
               AIAgent (Agent Framework)  Agent365/LearnAgentFactory.cs
                      ├──► Azure OpenAI  gpt-4.1
                      ├──► Microsoft Learn MCP         (3 tools)
                      └──► Work IQ Mail/Calendar/Teams  Agent365/WorkIqToolProvider.cs
```

Conversation state lives in the Blazor circuit, so a page reload starts a new conversation.

## Identities

This agent needs **two** Entra app registrations, and they must stay separate:

| Identity | Placeholder | Job |
| --- | --- | --- |
| Web sign-in client | `<web-client-id>` | Signs the human in and requests the blueprint's scope |
| Agent blueprint | `<blueprint-id>` | Owns the agent identity; hop 1 of the token chain |
| A365 agent identity | `<agent-identity-id>` | The principal spans are exported as |

A blueprint cannot run interactive `/authorize` flows at all, so something else has to sign the user
in. That is the web client's only job; it requests `api://<blueprint-id>/access_agent_as_user`, and
the resulting user token is the assertion for everything downstream.

## Configuration

| Setting | Purpose |
| --- | --- |
| `AzureOpenAI:Endpoint` / `Deployment` / `TenantId` | The model the agent reasons with |
| `LearnMcp:Endpoint` | Microsoft Learn MCP server |
| `AzureAd:*` | The web sign-in client registration |
| `Agent365:BlueprintClientSecret` | **User secrets only** — never in `appsettings.json` |
| `Agent365Observability:AgentId` / `AgentBlueprintId` / `ClientId` | Identity stamped on exported spans |

`AzureOpenAI:TenantId` pins `DefaultAzureCredential` to the tenant that owns the resource. Without it
a token from another tenant produces `HTTP 400 – Tenant provided in token does not match resource
token`.

## Running it

### Prerequisites

- .NET 10 SDK
- The **Cognitive Services OpenAI User** role on the Azure OpenAI resource. Authentication uses
  `DefaultAzureCredential`, so `az login` is enough — there is no API key anywhere.
- Your own Agent 365 registration (see [Agent 365](#agent-365) below)
- The blueprint client secret in user secrets:

  ```powershell
  dotnet user-secrets set "Agent365:BlueprintClientSecret" "<secret>"
  ```

### Start it

Open **this folder** in VS Code and press <kbd>F5</kbd>, or:

```powershell
az login --tenant <tenant-id>
dotnet run
```

Then browse to **<http://localhost:5140>** (or <https://localhost:7199>).

> Use `localhost`, not `127.0.0.1`. Cookies are host-specific and the registered redirect URI uses
> `localhost`, so starting on `127.0.0.1` loses the session at the redirect.

`Program.cs` throws at startup if `AzureOpenAI:Endpoint` or `AzureOpenAI:Deployment` is missing, so a
misconfigured app fails immediately rather than at the first question.

You must sign in before you can chat — the agent's tokens are all derived from **your** token.
`RedirectToLogin.razor` sends unauthenticated visitors to Microsoft Identity Web's
`MicrosoftIdentity/Account/SignIn`.

## Agent 365

### Registering it

```powershell
a365 setup all --authmode obo
```

Then register the web sign-in client separately, expose
`api://<blueprint-id>/access_agent_as_user` on the blueprint, and grant the web client consent to it.
Put the generated ids into `appsettings.json` and the blueprint secret into user secrets.

When hunting traces in Defender, filter on the **agent identity**, not the blueprint id.

### The agent on-behalf-of chain

> **Where this sits in the documented scenarios.** Microsoft's
> [observability authentication guide](https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup)
> defines four scenarios, and **none of them covers this agent**. All four assume an Agents SDK /
> Bot Framework agent; the closest, *Custom engine using OBO*, requires an **Azure Bot OAuth
> connection**, and this is a plain Blazor web app with no Azure Bot. The chain below is therefore
> this repo's own, not a documented pattern.
>
> It still honours the invariant the docs are enforcing — *the id in the export route must equal the
> token's `azp`, or the service answers HTTP 403*. Here both are the **A365 agent identity**, because
> hop 2 is performed by that identity. The Teams-hosted agents reach the same invariant from the
> other side, with `azp` = the bot app. See the
> [root README](../README.md#how-this-maps-onto-microsofts-documented-scenarios).

Everything the agent calls — the observability API and each Work IQ server — is reached with a token
for *the agent acting for the user*, minted by `Agent365/AgentOboTokenService.cs`. A plain delegated
user token is rejected, because its principal is the human rather than the agent.

Two hops, both raw HTTP POSTs to `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`:

1. **Blueprint** + client secret + `fmi_path=<agent-identity-id>` → an assertion for
   `api://AzureADTokenExchange/.default`. This is the blueprint proving it owns the agent identity.
2. **Agent identity** + that assertion as `client_assertion` + the user's token as `assertion` → a
   token for the target resource.

Only the final scope changes per caller:

| Caller | Final scope |
| --- | --- |
| Observability exporter | `api://9b975845-388f-4429-889e-eab1ef63949c/.default` |
| Work IQ server | `<audience>/.default`, audience taken from `ToolingManifest.json` |

`9b975845-388f-4429-889e-eab1ef63949c` is the Agent 365 Observability API — a Microsoft first-party
app id, identical in every tenant.

Tokens are cached in a `ConcurrentDictionary` and refreshed five minutes before expiry, so a request
never races the expiry.

### How observability is instrumented

- **`builder.UseMicrosoftOpenTelemetry(...)`** in `Program.cs`. `o.Exporters` is
  `ExportTarget.Agent365 | ExportTarget.Console` in Development and `ExportTarget.Agent365` in
  Production — **the Agent 365 export is never disabled**, so an F5 run exports exactly like a
  production run. That is the point of the demo.
- **`o.Instrumentation.EnableMetrics = false`.** The console metric exporter dumps every histogram
  bucket on a timer and drowns out the spans.
- **`UseS2SEndpoint` is left at its default (false).** The OBO path posts to `/observability/`; the
  S2S route is a different route and does not accept this token shape.
- **`o.Agent365.TokenResolver`** reads from `Agent365/ObservabilityTokenStore.cs`, a small dictionary
  keyed on (agent id, tenant id). The exporter flushes on a background loop that has no signed-in
  user, so the request path — which does have the assertion — deposits the token there first.
- **The agent id is pinned** to the A365 agent identity. Left unset, the SDK generates a fresh GUID
  per agent and the exporter emits orphan identity groups it cannot authenticate.

Per turn, in `Home.razor`:

- A **`BaggageBuilder`** scope carries tenant, agent id, agent name, blueprint id, conversation id,
  session id, the signed-in user (`UserId` / `UserName` / `UserEmail`) and `ChannelName("web")`.
  Spans emitted outside one are dropped by the exporter as *"Partitioned into 0 identity groups"*.
  There is no `ITurnContext` here — this is a plain web app, not a Bot Framework turn — so
  `FromTurnContext` is unavailable and the caller is read straight off the signed-in principal.
  `ResolveCaller()` is shared with the `InvokeAgentScope` below so the parent span and its children
  can never disagree about who asked.
- An **`InvokeAgentScope`** wraps the run, with `RecordInputMessages` / `RecordOutputMessages`.
- There is **no manual `InferenceScope` or `ExecuteToolScope`**. The chat client is wrapped with
  `.UseFunctionInvocation()` and `.UseOpenTelemetry(cfg => cfg.EnableSensitiveData = true)` in
  `Program.cs`, which emits the `gen_ai` inference and tool spans automatically as children of the
  invoke scope. Without that wrapping the agent still answers, but Defender sees a parent span with
  no LLM children.
- **`BaggageBackfillProcessor` (registered in `Program.cs` before the distro) is what gets the
  inference span exported at all.** The SDK enriches spans from baggage in `OnStart`, and only when
  the span already carries `gen_ai.operation.name`. `Microsoft.Extensions.AI` creates its `chat` span
  with `StartActivity("chat " + model, ActivityKind.Client)` and sets the tags afterwards, so the
  enrichment misses it and the exporter drops it with *"1 spans skipped due to missing tenant or
  agent ID"*. Emitting a span is not the same as exporting it. Registration order is load-bearing;
  see [`dotnet-agent-teammate`](../dotnet-agent-teammate/README.md#why-the-inference-span-needs-a-backfill-processor)
  for the full write-up. This is a workaround for an SDK timing quirk, not a supported extension
  point.
- A web agent has no `ITurnContext`, so a conversation id is generated per Blazor circuit and reused
  for every turn, giving all spans in the session one `gen_ai.conversation.id`.

> `EnableSensitiveData = true` puts prompt and completion text on the span so it is readable in
> Defender. Turn it off when handling regulated data.

### How Work IQ is wired

`ToolingManifest.json` declares `mcp_MailTools`, `mcp_CalendarTools` and `mcp_TeamsServer`, each with
its own url, audience and scope.

`Agent365/WorkIqToolProvider.cs` connects to each server directly with `HttpClientTransport` +
`McpClient`, rather than through the A365 Tooling SDK. The reason is structural:
`IMcpToolRegistrationService.GetMcpToolsAsync` needs an `ITurnContext` and a `UserAuthorization`, and
**both only exist in a Bot Framework hosted agent**. This is a plain web app, so it has neither.

Each server is contacted independently and a failure is logged and skipped, so a server that is down
costs only its own tools and the agent still answers from Learn.
