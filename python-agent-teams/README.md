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
| Bot channel app | `d1fbe2ae-6c95-492f-b34a-f14451b994f5` | Authenticates the Teams channel and signs outbound replies |
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
