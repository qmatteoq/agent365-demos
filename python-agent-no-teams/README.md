# Microsoft Learn agent — Python, LangChain, FastAPI web app

A research agent for the Microsoft ecosystem, built with **LangChain (Python)**, **Azure OpenAI** and
the official [Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp), served as
a small **FastAPI** web app with a chat page.

The Python counterpart of [`dotnet-agent-no-teams`](../dotnet-agent-no-teams): same behaviour, same
system prompt, same Azure OpenAI deployment, different stack.

| | |
| --- | --- |
| Language | Python 3.12 |
| Agent framework | LangChain (`langchain.agents.create_agent`) |
| Hosting | FastAPI + uvicorn |
| Surface | Browser |
| Model | Azure OpenAI |
| Tools | Microsoft Learn MCP |
| Port | 8000 |

## How it fits together

```text
app/
  config.py           settings from .env
  agent.py            the agent: Learn MCP tools + Azure OpenAI + conversation memory
  main.py             FastAPI host, /api/chat and /api/info
  a365/               observability wiring and the agent OBO token chain
  static/             the chat page
```

- **Tools.** `MultiServerMCPClient` connects to the Learn MCP server over streamable HTTP once at
  startup and discovers its tools (`microsoft_docs_search`, `microsoft_code_sample_search`,
  `microsoft_docs_fetch`), which are handed to the agent as LangChain tools.
- **The agent loop.** `langchain.agents.create_agent` builds the tool-calling loop. The system prompt
  tells it to search Learn before answering and to cite the source urls, which is why replies end in
  a list of `learn.microsoft.com` links.
- **Memory.** `InMemorySaver` keeps one conversation per `thread_id`; the chat page generates a fresh
  id, and "New chat" simply generates another. Memory is process local by design, so a restart clears
  every conversation.
- **Authentication to Azure OpenAI.** `AzureCliCredential` locally, `DefaultAzureCredential` when
  `AZURE_OPENAI_USE_MANAGED_IDENTITY` is `true`. There is no IMDS endpoint on a laptop, so managed
  identity is skipped entirely rather than attempted and caught.

There is **no Work IQ** here — Microsoft publishes no Work IQ extension adapter for Python LangChain.

## Identities

This agent needs **two** Entra app registrations, and they must stay separate:

| Identity | Placeholder | Job |
| --- | --- | --- |
| Web sign-in client | `<web-client-id>` | Signs the human in and requests the blueprint's scope |
| Agent blueprint | `<blueprint-id>` | Owns the agent identity; hop 1 of the token chain |
| A365 agent identity | `<agent-identity-id>` | The principal spans are exported as |

A blueprint cannot run interactive `/authorize` flows at all, so a dedicated web client signs the user
in and asks for `api://<blueprint-id>/access_agent_as_user`. That user token is the assertion for the
on-behalf-of chain below.

## Configuration

Copy `.env.example` to `.env` and fill it in:

```powershell
Copy-Item .env.example .env
```

| Setting | Purpose |
| --- | --- |
| `AZURE_OPENAI_ENDPOINT` | The Azure OpenAI resource endpoint |
| `AZURE_OPENAI_DEPLOYMENT` | The chat deployment name |
| `AZURE_OPENAI_API_VERSION` | Azure OpenAI REST API version |
| `AZURE_OPENAI_TENANT_ID` | Tenant that owns the resource, see below |
| `AZURE_OPENAI_USE_MANAGED_IDENTITY` | `false` locally, `true` when hosted on Azure |
| `LEARN_MCP_ENDPOINT` | Microsoft Learn MCP server, streamable HTTP |
| `A365_*` | Tenant, blueprint, agent identity and blueprint secret |

`.env` is gitignored and holds live secrets.

`AZURE_OPENAI_TENANT_ID` is not optional in a multi-tenant setup. The Azure CLI credential will
happily hand back a token from whichever tenant it last used, and Azure OpenAI then answers
`HTTP 400 Tenant provided in token does not match resource token`. Pinning the tenant avoids it.

## Running it

### Prerequisites

- [uv](https://docs.astral.sh/uv/) — dependency management and the runner
- Python 3.12
- Your own Agent 365 registration (see [Agent 365](#agent-365) below)
- The Azure CLI, signed in to the tenant that owns the Azure OpenAI resource:

  ```powershell
  az login --tenant <tenant-id>
  ```

  The agent authenticates to Azure OpenAI with **Entra ID, not an API key**, so your account needs the
  *Cognitive Services OpenAI User* role on the resource.

### Start it

Open **this folder** in VS Code and press <kbd>F5</kbd>. That syncs dependencies, starts the app with
the debugger attached, and opens whatever url uvicorn prints.

> Browse to **<http://localhost:8000>**, not `http://127.0.0.1:8000`. The two are different cookie
> hosts and the registered redirect URI uses `localhost`, so starting on `127.0.0.1` silently loses
> the session at the redirect.

From a terminal instead:

```powershell
uv run python -m app.main
```

### Dependency constraints

**`mcp` is pinned below 2.0.** `langchain-mcp-adapters` 0.3.1 declares `mcp>=1.24.0` with no upper
bound, but the MCP Python SDK 2.0 removed `mcp.shared.context.RequestContext`, which the adapters
import. Without the pin a fresh resolve picks up `mcp` 2.x and the app fails at import. Drop the pin
once the adapters support 2.x.

**On Windows ARM64, build the venv from an x64 interpreter.** `tiktoken`, pulled in by
`langchain-openai`, publishes no `win_arm64` wheel at any version, so `uv sync` tries to build it from
source and stops at `can't find Rust compiler`. x64 runs fine under emulation for an I/O bound agent:

```powershell
uv venv --clear --python C:\Python312-x64\python.exe
uv sync
```

Not needed on x64 Windows, macOS or Linux.

## Agent 365

### Registering it

```powershell
a365 setup all --authmode obo
```

Then register the web sign-in client separately, expose
`api://<blueprint-id>/access_agent_as_user` on the blueprint, and grant the web client consent to it.
The generated ids land in `a365.generated.config.json`; copy them into `.env` along with the blueprint
secret. Both files are gitignored.

When hunting traces in Defender, filter `CloudAppEvents` on the **agent identity**, not the blueprint
id.

### The agent on-behalf-of chain

> **Where this sits in the documented scenarios.** Microsoft's
> [observability authentication guide](https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup)
> defines four scenarios, and **none of them covers this agent**. All four assume an Agents SDK /
> Bot Framework agent; the closest, *Custom engine using OBO*, requires an **Azure Bot OAuth
> connection**, and this is a plain FastAPI web app with no Azure Bot. The chain below is this repo's
> own, not a documented pattern.
>
> It still honours the invariant the docs enforce — *the id in the export route must equal the token's
> `azp`, or the service answers HTTP 403*. Here both are the **A365 agent identity**, because hop 2 is
> performed by that identity. The two Teams-hosted agents reach the same invariant from the other
> side, with `azp` = the bot app. See the
> [root README](../README.md#how-this-maps-onto-microsofts-documented-scenarios).

The observability API is reached with a token for *the agent acting for the user*. A plain delegated
user token is rejected, because its principal is the human rather than the agent.

1. **Hop 1** — blueprint + client secret + `fmi_path=<agent-identity-id>` → a token exchange assertion
   for `api://AzureADTokenExchange/.default`. This is the blueprint proving it owns the agent
   identity.
2. **Hop 2** — agent identity + that assertion as `client_assertion` + the user token as `assertion` →
   an Observability API token, scoped to
   `api://9b975845-388f-4429-889e-eab1ef63949c/.default`.

`9b975845-388f-4429-889e-eab1ef63949c` is the Agent 365 Observability API — a Microsoft first-party
app id, identical in every tenant.

Both hops are plain HTTP POSTs (`app/a365/obo.py`). MSAL Python 1.37 does support `fmi_path` on
`acquire_token_for_client`, as [`python-agent-teams`](../python-agent-teams) shows, so hop 1 could be
rewritten on MSAL. Hop 2 is an on-behalf-of grant rather than a client-credentials one, so the two
chains are not interchangeable.

The exporter flushes on a background thread with no user context, so the token is deposited into
`app/a365/token_store.py` by the request path and read back by the exporter's resolver.

### Instrumentation notes

- `use_microsoft_opentelemetry` runs at the top of `app/main.py` **before** the LangChain and Azure
  OpenAI imports, otherwise auto-instrumentation cannot patch them.
- Every turn runs inside a `BaggageBuilder` scope. Spans emitted outside one are dropped by the
  exporter as *"Partitioned into 0 identity groups"*.
- `InvokeAgentScope` is used as a **context manager**. Entering it is what attaches the span to the
  OpenTelemetry context, so the LangChain inference and tool spans nest underneath it; `start()` alone
  would leave them as orphans.
- Agent Framework and Semantic Kernel instrumentation are disabled on purpose. Their span enrichers
  register first and make the distro skip the LangChain enricher, which is the one that maps this
  agent's messages and conversation id into the shape Agent 365 expects.
- `A365_OBSERVABILITY_LOG_LEVEL` is a Node.js-only variable that the Python distro ignores, so
  `app/a365/observability.py` applies it to the distro's loggers itself. Set it to
  `debug|info|warn|error` to see export confirmation — a successful export is logged at DEBUG, and
  only failures are logged at ERROR.

### Verifying the export

With debug logging on, a successful turn logs:

```text
Found 1 identity groups with N total spans to export
Token resolved successfully for agent <agent-identity-id>
HTTP 200 success on attempt 1. Correlation ID: ...
  {"results":[{"spanId":"...","sinks":{"flashpoint":{"status":"sent"}, ...
```

`Found 0 identity groups` means the baggage scope is missing. Traces take roughly 15-90 minutes to
surface in Advanced Hunting.
