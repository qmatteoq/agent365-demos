# Microsoft Learn agent — Python, LangChain, Teams

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp). It answers questions
about Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365, and cites the
documentation it used.

Same idea as [`python-agent-no-teams`](../python-agent-no-teams), but hosted in **Microsoft Teams and
Microsoft 365 Copilot** through the **Microsoft 365 Agents SDK** instead of a standalone web app —
the Python counterpart of [`dotnet-agent-teams`](../dotnet-agent-teams).

| | |
| --- | --- |
| Language | Python 3.12 |
| Agent framework | LangChain / LangGraph |
| Hosting | Microsoft 365 Agents SDK (`microsoft-agents-hosting-aiohttp`) |
| Surface | Teams, Microsoft 365 Copilot |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP server |
| Port | 3979 |

## How it fits together

```text
Teams / M365 Copilot
        │  Bot Framework activity (JWT signed)
        ▼
Azure Bot
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
non-Teams Python agent, which keeps the meaningful difference between the two samples confined to the
hosting layer.

## Identities

| Identity | Placeholder | Job |
| --- | --- | --- |
| Bot channel app | `<bot-app-client-id>` | Authenticates the Teams channel, signs outbound replies, and is the principal of the observability token — so it is also the id in the export route |
| Teams app | `<teams-app-id>` | Identifies the app in the Teams catalogue |
| Blueprint | `<blueprint-id>` | The Agent 365 registration. Not used at runtime on this path |

**The bot channel app must be a plain single-tenant Entra app, not a blueprint.** Entra bars agentic
applications from requesting client-credentials tokens (`AADSTS82001`), so a blueprint cannot
authenticate outbound Bot Framework replies. It is also the wrong audience: inbound Bot Framework
tokens carry `aud = <bot-app-client-id>`, and `app["agent_configuration"]` feeds the JWT middleware
that checks it — point that at the blueprint and every inbound request is rejected. When the agent is
onboarded to Agent 365 the blueprint is added *alongside* the channel app; it never replaces it.

> **After running `a365 setup all`, restore the bot channel credentials.** The CLI overwrites
> `CONNECTIONS__SERVICE_CONNECTION__SETTINGS__*` with the blueprint's client id and secret, and
> replaces the bot secret in place so the original is unrecoverable. Reset it with
> `az ad app credential reset --id <bot-app-client-id>` and restore all three keys. The running
> process keeps the old values in memory, so the breakage only surfaces on the next restart.

Use ids that differ from the .NET Teams agent's so both demo agents can be installed and run side by
side.

## Configuration

Copy `.env.example` to `.env` and fill it in. `.env` is gitignored.

The Agents SDK reads its own configuration straight from the environment using a double-underscore
convention, which is why those keys do not appear in `app/config.py`:

```dotenv
CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID=<bot-app-client-id>
CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET=<bot-app-secret>
CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID=<tenant-id>
```

Everything else (Azure OpenAI, the MCP endpoint, the port) is ordinary application configuration
bound by `pydantic-settings`.

Azure OpenAI is reached with `AzureCliCredential` locally — run `az login` first — or with a managed
identity when `AZURE_OPENAI_USE_MANAGED_IDENTITY=true` on Azure.

## Running it

Press <kbd>F5</kbd> in VS Code. That syncs dependencies, brings the dev tunnel up, and starts the
agent on the port the tunnel forwards to.

Or by hand:

```powershell
uv sync
devtunnel host <tunnel-name>      # separate terminal
.\.venv\Scripts\python.exe -m app.main
```

Then sideload `appPackage.zip` in Teams (**Apps → Manage your apps → Upload a custom app**) and send
it a question. Rebuild the package with `.\build-app-package.ps1` after editing the manifest.

`GET /` is an unauthenticated liveness probe. `POST /api/messages` requires a valid Bot Framework
token.

### Commands

| Command | Effect |
| --- | --- |
| `/help` | What the agent can do |
| `/reset` | Forget the conversation so far |

Conversation memory is per Teams conversation and lives in memory, so restarting the agent clears
every conversation.

### Things worth knowing

**Port 3979, not 3978.** The .NET Teams agent uses 3978. This one is deliberately different so both
can run at once.

**The dev tunnel url is not derived from the tunnel name**, and is only printed while the tunnel is
being hosted. A *named* tunnel keeps that url across restarts, which is why the Azure Bot's messaging
endpoint stays valid; an anonymous tunnel would hand out a new url every run and silently break the
channel.

**Use the x86_64 interpreter on Windows on ARM.** `langchain-openai` pulls in `tiktoken`, which
publishes no `win-arm64` wheel. A native ARM64 CPython therefore tries to build it from Rust source
and fails. Create `.venv` from an x64 interpreter (`C:\Python312-x64\python.exe` here) and pin the
same one in `tasks.json` so <kbd>F5</kbd> does not regress.

**JWT validation is scoped to the messaging endpoint.** The Agents SDK sample registers
`jwt_authorization_middleware` application-wide, which rejects every request that has no
`Authorization` header — including health probes. Here it is applied to `POST /api/messages` only;
that endpoint is still fully protected.

**The MCP handshake is lazy.** The Agents SDK only provides a running event loop once a turn arrives,
so `LearnAgent.start()` runs on the first message rather than at import. A lock makes concurrent first
turns wait for a single handshake instead of racing several.

## Agent 365

### Registering it

From this folder:

```powershell
a365 setup all --authmode obo
```

The generated ids are written to `a365.generated.config.json` and `.env`, both gitignored. Afterwards,
restore the bot channel credentials as described under [Identities](#identities).

You also need an **Azure Bot OAuth connection** for observability — see below.

### The observability token

This agent is a **custom engine agent**: it is reached through its own bot registration, so its
activities carry no agentic identity. `app/main.py` logs the fact once per process:

```text
Turn identity: agentic_app_id=None agentic_user_id=None -> custom engine agent
```

That places it in the documented
[custom engine using OBO](https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup#custom-engine-using-obo)
scenario, where **the export id must be the app registration's client id** — not the activity's
`agenticAppId`, which does not exist here. `app/config.py` exposes this as `observability_agent_id`.

The export route authorises by comparing the token's `azp` against the agent id in the url, and on
this path the Bot Framework Token Service issues the token to the bot app, so both are the bot app's
client id. Traces are still attributed correctly: Microsoft Admin Center resolves the route id back to
the registered agent and reports them under the **agent identity's** display name.

There is no token chain. `app/main.py` → `_publish_observability_token` makes one call:

```python
token = await AGENT_APP.auth.get_token(context, "OBO")
token_store.set_token(token.token, token.expiration)
```

The token comes back already scoped to the Observability API, because the **Azure Bot OAuth
connection** it is bound to is configured that way. The Bot Framework Token Service performs the
on-behalf-of exchange internally. No MSAL, no `fmi_path`, no blueprint secret.

The work is in the OAuth connection:

| Connection | Scope | Used for |
| --- | --- | --- |
| `oboConnectionProfile` | `api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite` | the `OBO` handler — observability |

```bash
az bot authsetting create -g <resource-group> -n <azure-bot-name> \
  -c oboConnectionProfile --client-id <bot-app-client-id> --client-secret <bot-app-secret> \
  --service AadV2 --provider-scope-string \
    "api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite" \
  --parameters tenantID=<tenant-id> tokenExchangeUrl=api://botid-<bot-app-client-id>
```

The scope is a **named** scope, not `/.default`: a delegated token carries scopes, not roles. A
connection left on the default `api://botid-…/defaultScopes` produces `401 InvalidAudience`.

`tokenExchangeUrl` is what keeps Teams SSO silent — the Teams manifest declares one
`webApplicationInfo.resource`, and the connection exchanges that single SSO token for its own
configured scope.

> **Set the agent id in both places, from the same value.** `AgentDetails` on the `InvokeAgentScope`
> decorates only the parent span. Auto-instrumented LangChain and LLM spans read `gen_ai.agent.id`
> from **baggage** instead. If `build_agent_details` and `build_baggage_scope` in
> `app/a365/observability.py` disagree, a single turn splits into two identity groups — one
> authenticates and exports, the other is rejected. Both read `observability_agent_id`.

### Instrumentation notes

* `init_observability` runs in `app/main.py` **before** `app.agent` is imported, otherwise
  auto-instrumentation cannot patch LangChain and the Azure OpenAI client.
* `a365_use_s2s_endpoint=False` selects the delegated route. Its S2S counterpart takes application
  tokens only and refuses a delegated one, so the two are not interchangeable.
* The message route declares `auth_handlers=[OBO]`, which is what makes the SDK complete the Teams SSO
  sign-in before the handler runs. Sign-in is attached to that route alone — attaching it globally
  would prompt on the install-time `conversationUpdate` too.
* The handler name is spelled **uppercase**. It is an environment-variable segment
  (`AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__OBO__…`) and `os.environ` upper-cases every key on
  Windows, so the SDK only ever sees `OBO`.
* Every turn runs inside a `BaggageBuilder` scope. Spans emitted outside one are dropped by the
  exporter as *"Partitioned into 0 identity groups"*.
* `InvokeAgentScope` is used as a context manager. Entering it attaches the span to the OpenTelemetry
  context so the LangChain inference and tool spans nest underneath it; `start()` alone would leave
  them as orphans.
* Agent Framework and Semantic Kernel instrumentation are disabled on purpose. Their span enrichers
  register first and make the distro skip the LangChain enricher, which is the one that maps this
  agent's messages and conversation id into the shape Agent 365 expects.
* `A365_OBSERVABILITY_LOG_LEVEL` is a Node.js-only variable that the Python distro ignores, so
  `app/a365/observability.py` applies it to the distro's loggers itself. Set it to
  `debug|info|warn|error` to confirm spans left the process — a successful export is logged at DEBUG
  and only failures at ERROR.

### No Work IQ

There is no Microsoft-published Work IQ adapter for Python + LangChain
(`microsoft-agents-a365-tooling-extensions-langchain` does not exist on PyPI), so this agent is
observability-only. The .NET Teams agent covers the Work IQ demo.
