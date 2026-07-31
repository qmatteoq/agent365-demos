# python-agent-no-teams

A Microsoft ecosystem research assistant built with **LangChain (Python)**, **Azure OpenAI** and the
official [Microsoft Learn MCP server](https://learn.microsoft.com/training/support/mcp), served as a small
**FastAPI** web app with a chat page.

It is the Python counterpart of [`dotnet-agent-no-teams`](../dotnet-agent-no-teams): same behaviour,
same system prompt, same Azure OpenAI deployment, different stack.

## Prerequisites

- [uv](https://docs.astral.sh/uv/) — dependency management and the runner
- Python 3.12
- The Azure CLI, signed in to the tenant that owns the Azure OpenAI resource:

  ```powershell
  az login --tenant <tenant-id>
  ```

  The agent authenticates to Azure OpenAI with **Entra ID, not an API key**, so your account needs
  the *Cognitive Services OpenAI User* role on the resource.

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

`.env` is gitignored.

`AZURE_OPENAI_TENANT_ID` is not optional in a multi-tenant setup. The Azure CLI credential will
happily hand back a token from whichever tenant it last used, and Azure OpenAI then answers
`HTTP 400 Tenant provided in token does not match resource token`. Pinning the tenant avoids it.

## Running

Open **this folder** in VS Code and press <kbd>F5</kbd>. That syncs dependencies, starts the app
with the debugger attached, and opens whatever url uvicorn prints — normally
<http://127.0.0.1:8000>.

⚠️ **Change that to <http://localhost:8000> before signing in.** The two are different cookie hosts
and the registered redirect URI uses `localhost`; see [Sign in on `localhost`](#sign-in-on-localhost).

From a terminal instead:

```powershell
uv run python -m app.main
```

## How it works

```
app/
  config.py           settings from .env
  agent.py            the agent: Learn MCP tools + Azure OpenAI + conversation memory
  main.py             FastAPI host, /api/chat and /api/info
  static/             the chat page
```

- **Tools.** `MultiServerMCPClient` connects to the Learn MCP server over streamable HTTP once at
  startup and discovers its tools (`microsoft_docs_search`, `microsoft_code_sample_search`,
  `microsoft_docs_fetch`), which are handed to the agent as LangChain tools.
- **The agent loop.** `langchain.agents.create_agent` builds the tool-calling loop. The system
  prompt tells it to search Learn before answering and to cite the source urls, which is why replies
  end in a list of `learn.microsoft.com` links.
- **Memory.** `InMemorySaver` keeps one conversation per `thread_id`; the chat page generates a
  fresh id, and "New chat" simply generates another. Memory is process local by design, so a restart
  clears every conversation.
- **Authentication.** `AzureCliCredential` locally, `DefaultAzureCredential` when
  `AZURE_OPENAI_USE_MANAGED_IDENTITY` is `true`. There is no IMDS endpoint on a laptop, so managed
  identity is skipped entirely rather than attempted and caught.

## Notes on the dependencies

**`mcp` is pinned below 2.0.** `langchain-mcp-adapters` 0.3.1 declares `mcp>=1.24.0` with no upper
bound, but the MCP Python SDK 2.0 removed `mcp.shared.context.RequestContext`, which the adapters
import. Without the pin, a fresh resolve picks up `mcp` 2.x and the app fails at import. Drop the
pin once the adapters support 2.x.

**Windows on ARM.** `tiktoken`, pulled in by `langchain-openai`, publishes no `win_arm64` wheel at
any version, so on an ARM64 machine `uv sync` tries to build it from source and stops at
`can't find Rust compiler`. Create the virtual environment from an **x64** interpreter instead —
it runs fine under emulation for an I/O bound agent:

```powershell
uv venv --clear --python C:\Python312-x64\python.exe
uv sync
```

This is not needed on x64 Windows, macOS or Linux.

## Agent 365

This agent is registered in Agent 365 and exports OpenTelemetry traces to Microsoft
Defender. Registration was done with `a365 setup all`; the generated ids live in
`a365.generated.config.json` and `.env` (both gitignored).

| | |
|---|---|
| Auth mode | `obo` (on-behalf-of the signed-in user) |
| Blueprint | `bb49ad8e-3857-469d-bc1b-6a9141089214` |
| Agent identity | `ae1e93ef-18b6-49e4-9d60-8eae0225c8f1` |
| Sign-in app | `python-agent-noteams WebClient` |

### Why there is a separate sign-in app

An agent blueprint cannot run interactive `/authorize` flows, so a dedicated web
client app signs the user in and asks for `api://<blueprint>/access_agent_as_user`.
That user token is the assertion for the agent on-behalf-of chain:

1. **Hop 1** - blueprint + client secret + `fmi_path=<agent identity>` -> token
   exchange assertion (T1).
2. **Hop 2** - agent identity + T1 as `client_assertion` + the user token as
   `assertion` -> Observability API token.

Both hops are plain HTTP POSTs. That is a historical choice rather than a
limitation: MSAL Python 1.37 does support `fmi_path` on `acquire_token_for_client`,
as `python-agent-teams/app/a365/fmi.py` shows. Hop 2 here is an on-behalf-of grant
rather than a client-credentials one, so the two chains are not interchangeable, but
hop 1 could be rewritten on MSAL. Earlier revisions of this file claimed MSAL Python
cannot serialise `fmi_path`; that was wrong.

The result is a token for *the agent acting for the user*, which is what the backend
requires - a plain delegated user token is rejected, because its principal is the human
rather than the agent.

The exporter flushes on a background thread with no user context, so the token is
deposited into `app/a365/token_store.py` by the request path and read back by the
exporter's resolver.

### Sign in on `localhost`

Browse to `http://localhost:8000`, **not** `http://127.0.0.1:8000`. Cookies are
host-specific and the registered redirect URI uses `localhost`, so starting on
`127.0.0.1` silently loses the session at the redirect.

### Instrumentation notes

* `use_microsoft_opentelemetry` runs at the top of `app/main.py` **before** the
  LangChain and Azure OpenAI imports, otherwise auto-instrumentation cannot patch them.
* Every turn runs inside a `BaggageBuilder` scope. Spans emitted outside one are
  dropped by the exporter as *"Partitioned into 0 identity groups"*.
* `InvokeAgentScope` is used as a context manager. Entering it is what attaches the
  span to the OpenTelemetry context, so the LangChain inference and tool spans nest
  underneath it; `start()` alone would leave them as orphans.
* Agent Framework and Semantic Kernel instrumentation are disabled on purpose. Their
  span enrichers register first and make the distro skip the LangChain enricher, which
  is the one that maps this agent's messages and conversation id into the shape
  Agent 365 expects.
* `A365_OBSERVABILITY_LOG_LEVEL` is a Node.js-only variable that the Python distro
  ignores, so `app/a365/observability.py` applies it to the distro's loggers itself.
  Set it to `debug|info|warn|error` to see export confirmation - a successful export
  is logged at DEBUG, and only failures are logged at ERROR.

### Verifying the export

With debug logging on, a successful turn logs:

```
Found 1 identity groups with N total spans to export
Token resolved successfully for agent ae1e93ef-...
HTTP 200 success on attempt 1. Correlation ID: ...
  {"results":[{"spanId":"...","sinks":{"flashpoint":{"status":"sent"}, ...
```

`Found 0 identity groups` means the baggage scope is missing. Traces take roughly
15-90 minutes to surface in Advanced Hunting; filter `CloudAppEvents` on the **agent
identity** id, not the blueprint id.