# python-agent-no-teams

A Microsoft ecosystem research assistant built with **LangChain (Python)**, **Azure OpenAI** and the
official [Microsoft Learn MCP server](https://learn.microsoft.com/api/mcp), served as a small
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
with the debugger attached, and opens the browser on <http://127.0.0.1:8000>.

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
