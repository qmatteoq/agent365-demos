# Microsoft Learn agent — Python, LangChain, Teams

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp). It answers
questions about Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365,
and cites the documentation it used.

This is the fourth agent in this repo. It is the same idea as
[`python-agent-no-teams`](../python-agent-no-teams), but hosted in **Microsoft Teams and
Microsoft 365 Copilot** through the **Microsoft 365 Agents SDK** instead of a standalone web
app — the Python counterpart of [`dotnet-agent-teams`](../dotnet-agent-teams).

| | |
|---|---|
| Language | Python 3.12 |
| Agent framework | LangChain / LangGraph |
| Hosting | Microsoft 365 Agents SDK (`microsoft-agents-hosting-aiohttp`) |
| Surface | Teams, Microsoft 365 Copilot |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP server |

## How it fits together

```
Teams / M365 Copilot
        │  Bot Framework activity (JWT signed)
        ▼
Azure Bot  python-agent-teams-bot
        │  https://<dev-tunnel>/api/messages
        ▼
aiohttp host ──► AgentApplication (Agents SDK)  app/main.py
                        │
                        ▼
                 LearnAgent (LangChain)          app/agent.py
                        ├──► Azure OpenAI  gpt-4.1
                        └──► Microsoft Learn MCP  (3 tools)
```

`app/agent.py` contains no Teams or Agents SDK types at all. It is the same agent core as the
non-Teams Python agent, which keeps the meaningful difference between the two samples confined
to the hosting layer.

## Identities

Two separate applications are involved, and conflating them is the most common way to break
this setup:

| Identity | App id | Job |
|---|---|---|
| Bot channel app | `d1fbe2ae-6c95-492f-b34a-f14451b994f5` | Authenticates the Teams channel, signs outbound replies, and is the principal of the observability token — so it is also the id in the export route |
| Teams app | `d80a2cae-b655-487a-82be-8bf9271e1d8e` | Identifies the app in the Teams catalogue |

The bot channel app is a plain single-tenant Entra app, **not** an Agent 365 blueprint. Entra
bars agentic applications from requesting client-credentials tokens (`AADSTS82001`), so a
blueprint cannot authenticate outbound Bot Framework replies. When this agent is onboarded to
Agent 365, the blueprint is added alongside — it never replaces the channel app.

Both ids differ from the .NET Teams agent's, so the two demo agents can be installed and run
side by side.

## Configuration

Copy `.env.example` to `.env` and fill it in. `.env` is gitignored.

The Agents SDK reads its own configuration straight from the environment using a
double-underscore convention, which is why those keys do not appear in `app/config.py`:

```dotenv
CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID=<bot channel app id>
CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET=<secret>
CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID=<tenant>
```

Everything else (Azure OpenAI, the MCP endpoint, the port) is ordinary application
configuration bound by `pydantic-settings`.

Azure OpenAI is reached with `AzureCliCredential` locally — run `az login` first — or with a
managed identity when `AZURE_OPENAI_USE_MANAGED_IDENTITY=true` on Azure.

## Running it

Press <kbd>F5</kbd> in VS Code. That syncs dependencies, brings the dev tunnel up, and starts
the agent on the port the tunnel forwards to.

Or by hand:

```powershell
uv sync
devtunnel host python-agent-teams-tunnel      # separate terminal
.\.venv\Scripts\python.exe -m app.main
```

Then sideload `appPackage.zip` in Teams (**Apps → Manage your apps → Upload a custom app**) and
send it a question. Rebuild the package with `.\build-app-package.ps1` after editing the
manifest.

`GET /` is an unauthenticated liveness probe. `POST /api/messages` requires a valid Bot
Framework token.

### Commands

| Command | Effect |
|---|---|
| `/help` | What the agent can do |
| `/reset` | Forget the conversation so far |

Conversation memory is per Teams conversation and lives in memory, so restarting the agent
clears every conversation.

## Things worth knowing

**Port 3979, not 3978.** The .NET Teams agent uses 3978. This one is deliberately different so
both can run at once.

**The dev tunnel url is not derived from the tunnel name.** `python-agent-teams-tunnel` is
hosted at `https://dwbnlc5s-3979.euw.devtunnels.ms`. The url is only printed while the tunnel
is being hosted. A *named* tunnel keeps that url across restarts, which is why the Azure Bot's
messaging endpoint stays valid; an anonymous tunnel would hand out a new url every run and
silently break the channel.

**Use the x86_64 interpreter on Windows on ARM.** `langchain-openai` pulls in `tiktoken`, which
publishes no `win-arm64` wheel. A native ARM64 CPython therefore tries to build it from Rust
source and fails. `.venv` is created from `C:\Python312-x64\python.exe`, and `tasks.json` pins
the same interpreter so <kbd>F5</kbd> does not regress.

**JWT validation is scoped to the messaging endpoint.** The Agents SDK sample registers
`jwt_authorization_middleware` application-wide, which rejects every request that has no
`Authorization` header — including health probes. Here it is applied to `POST /api/messages`
only; that endpoint is still fully protected.

**The MCP handshake is lazy.** The Agents SDK only provides a running event loop once a turn
arrives, so `LearnAgent.start()` runs on the first message rather than at import. A lock makes
concurrent first turns wait for a single handshake instead of racing several.

## Agent 365

This agent is registered in Agent 365 and exports OpenTelemetry traces to Microsoft
Defender. Registration was done with `a365 setup all`; the generated ids live in
`a365.generated.config.json` and `.env` (both gitignored).

| | |
|---|---|
| Auth mode | `obo` (on-behalf-of the signed-in Teams user) |
| Blueprint | `b646f9c7-83ed-4f77-8baa-b1027797bc5d` |
| Agent identity | `69d5a4ee-3c5e-4e8e-ba1c-0c5298b6e70a` |
| Bot channel app | `d1fbe2ae-6c95-492f-b34a-f14451b994f5` |

A human drives every turn of this agent, so its traces are exported on behalf of that
human. Service-to-service export would work mechanically but produce traces with no
caller, which is the wrong shape for an interactive agent and would make it useless as a
reference for the OBO path.

### Two identities, and why they must not be merged

| Identity | Used for |
|---|---|
| Bot channel app | Signing outbound Bot Framework replies, validating the inbound Teams token, and — because this is a custom engine agent — being the principal of the observability token and the id in the export route |
| Blueprint | The Agent 365 registration. Not used at runtime on this path |

Keeping them separate is not a style choice. Entra bars agentic applications from
client-credentials tokens (**AADSTS82001**), so the blueprint cannot sign channel traffic
at all. It is also the wrong audience: inbound Bot Framework tokens carry
`aud = <bot channel app>`, and `app["agent_configuration"]` feeds the JWT middleware
that checks it — point that at the blueprint and every inbound request is rejected.

> ⚠️ **`a365 setup all` overwrites `CONNECTIONS__SERVICE_CONNECTION__SETTINGS__*` with
> the blueprint's client id and secret.** It replaces the bot secret in place, so the
> original is unrecoverable — reset it with
> `az ad app credential reset --id <bot app id>` and restore all three keys afterwards.
> The running process keeps the old values in memory, so the breakage only surfaces on the
> next restart.

The blueprint is deliberately **not** registered as an Agents SDK connection, and its client
secret is no longer needed at runtime at all.

### The observability token

There is no chain. `app/main.py` → `_publish_observability_token` makes one call:

```python
token = await AGENT_APP.auth.get_token(context, "OBO")
token_store.set_token(token.token, token.expiration)
```

The token comes back already scoped to the Observability API, because the **Azure Bot OAuth
connection** it is bound to is configured that way. The Bot Framework Token Service performs the
on-behalf-of exchange internally. No MSAL, no `fmi_path`, no blueprint secret — `app/a365/fmi.py`
was deleted.

The work is in the OAuth connection. This bot has two, but only one is used:

| Connection | Scopes | Used for |
|---|---|---|
| `oboConnectionProfile` | `api://9b975845-…/Agent365.Observability.OtelWrite` | the `OBO` handler — observability |
| `BotOAuth` | `api://botid-d1fbe2ae-…/defaultScopes` | left in place from the earlier wiring; nothing references it. The .NET sibling still needs its equivalent for WorkIQ |

Both share a `tokenExchangeUrl` of `api://botid-d1fbe2ae-…`, which keeps Teams SSO silent: the
Teams manifest declares one `webApplicationInfo.resource`, and the connection exchanges that single
SSO token for its own configured scope.

```bash
az bot authsetting create -g rg-agent365 -n python-agent-teams-bot \
  -c oboConnectionProfile --client-id <bot app id> --client-secret <bot app secret> \
  --service AadV2 --provider-scope-string \
    "api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite" \
  --parameters tenantID=<tenant> tokenExchangeUrl=api://botid-<bot app id>
```

The scope is a **named** scope, not `/.default`: a delegated token carries scopes, not roles. A
connection left on the default `api://botid-…/defaultScopes` produces **401 InvalidAudience**.

> ⚠️ **This is a *custom engine agent*, and getting that classification wrong costs an HTTP 403.**
> Microsoft's guidance selects the scenario on one testable criterion: whether the turn carries
> **agentic identity** (`agenticAppId` / `agenticUserId`) from the Agent 365 platform. This agent is
> reached through its own bot app registration via Teams, so it carries none. That is now directly
> observed rather than inferred — `app/main.py` logs it once per process:
>
> ```text
> Turn identity: agentic_app_id=None agentic_user_id=None -> custom engine agent
> ```
>
> So it is a
> [custom engine agent using OBO](https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup#custom-engine-using-obo),
> and the docs are explicit: *the `agentId` in the token cache must match the app registration's
> Client ID — not the activity's `agenticAppId`, which doesn't exist for custom engine agents.*
>
> The export route authorises on the token's `azp`, and it must equal the agent id in the URL.
> Probed live with a single token and a zero-length protobuf body (a valid empty OTLP request),
> varying only the agent id:
>
> | id in the route | response |
> |---|---|
> | agent identity | **403** |
> | blueprint | **403** |
> | bot channel app (the token's `azp`) | **415** — authorised, wrong content type |
>
> On this path nothing can make `azp` be the Agent 365 agent identity: the Token Service issues the
> token to the bot app. So the route carries the bot app's client id and the two agree.
>
> ✅ **And that does not orphan the traces.** Confirmed in MAC on the .NET sibling, which uses the
> identical wiring: traces appear attributed to the **agent identity's display name**, carrying
> `TargetAgentId` = the bot app id and `TargetAgentBlueprintId` = the blueprint. The service
> resolves the bot app id back to the registered agent, so the route id is a routing key rather
> than the reported identity.

> 🪤 **`AgentDetails` alone is not enough — baggage carries the id too.** Auto-instrumented
> LangChain and LLM spans read `gen_ai.agent.id` from **baggage**, not from the invoke scope.
> Setting `build_agent_details` to the new id while leaving `build_baggage_scope` on the old one
> split a single turn into **two identity groups**: one exported 200, the other returned
> **400 `TenantIdInvalid`** because no token was bound to it. Both helpers in
> `app/a365/observability.py` must use `settings.observability_agent_id`.

The exporter flushes on a background thread with no turn context, so it cannot fetch the token
itself. It is fetched in the message handler, while the turn is still live, and deposited in
`app/a365/token_store.py` for the exporter's resolver to read back.

**Previous implementations.** Two, in order:

1. **S2S** (`a365_use_s2s_endpoint=True`, a background refresh loop holding a client-credentials
   token). Exports returned 200, but the token's principal was the agent alone, with no user in it —
   the wrong shape for an agent that has a human on every turn. The auth mode is decided by whether
   a user is in the loop at runtime, not by where the agent is hosted. Preserved on the
   **`s2s/teams-agents`** branch.
2. **A hand-rolled three-hop FMI chain** in `app/a365/fmi.py` (bot app → blueprint with `fmi_path` →
   agent identity), built to force `azp` to the agent identity so the route could carry it. It
   worked and returned 200, but it is not the documented path for this scenario and needed the
   blueprint secret at runtime. It was written because `instrument-observability` has no
   custom-engine branch; see the root README. (For the record, MSAL Python 1.37 does support
   `fmi_path` on `acquire_token_for_client` natively — the sibling `python-agent-no-teams` README
   claims otherwise and is wrong.)

### Instrumentation notes

* `init_observability` runs in `app/main.py` **before** `app.agent` is imported,
  otherwise auto-instrumentation cannot patch LangChain and the Azure OpenAI client.
* `a365_use_s2s_endpoint=False` selects the delegated route. Its S2S counterpart takes
  application tokens only and refuses a delegated one, so the two are not interchangeable.
* The message route declares `auth_handlers=[OBO]`, which is what makes the SDK complete
  the Teams SSO sign-in before the handler runs. Sign-in is attached to that route alone —
  attaching it globally would prompt on the install-time `conversationUpdate` too.
* The handler name is spelled **uppercase**. It is an environment-variable segment
  (`AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__OBO__…`) and `os.environ` upper-cases
  every key on Windows, so the SDK only ever sees `OBO`.
* Every turn runs inside a `BaggageBuilder` scope. Spans emitted outside one are dropped
  by the exporter as *"Partitioned into 0 identity groups"*.
* `InvokeAgentScope` is used as a context manager. Entering it attaches the span to the
  OpenTelemetry context so the LangChain inference and tool spans nest underneath it;
  `start()` alone would leave them as orphans.
* Agent Framework and Semantic Kernel instrumentation are disabled on purpose. Their span
  enrichers register first and make the distro skip the LangChain enricher, which is the
  one that maps this agent's messages and conversation id into the shape Agent 365 expects.
* `A365_OBSERVABILITY_LOG_LEVEL` is a Node.js-only variable that the Python distro
  ignores, so `app/a365/observability.py` applies it to the distro's loggers itself. Set
  it to `debug|info|warn|error` to confirm spans left the process — a successful export
  is logged at DEBUG and only failures at ERROR.

### No WorkIQ

There is no Microsoft-published WorkIQ adapter for Python + LangChain
(`microsoft-agents-a365-tooling-extensions-langchain` does not exist on PyPI), so this
agent is observability-only. The .NET Teams agent covers the WorkIQ demo.
