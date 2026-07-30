# Microsoft Learn agent — .NET, Agent Framework, AI Teammate

A research agent for the Microsoft ecosystem, grounded in the official
[Microsoft Learn MCP server](https://learn.microsoft.com/api/mcp). It answers questions about
Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot and Dynamics 365, and cites the
documentation it used.

This is the fifth agent in this repo. Functionally it is the same agent as
[`dotnet-agent-teams`](../dotnet-agent-teams) — same stack, same hosting, same system prompt.
The difference is entirely in **how it is onboarded to Agent 365**: this one becomes an
**AI Teammate**, so it acts under its **own identity** (the Agentic User) rather than
on behalf of the signed-in user.

| | |
|---|---|
| Language | .NET 10 |
| Agent framework | Microsoft Agent Framework (`Microsoft.Agents.AI.OpenAI`) |
| Hosting | Microsoft 365 Agents SDK (`Microsoft.Agents.Hosting.AspNetCore`) |
| Surface | Teams, Microsoft 365 Copilot |
| Model | Azure OpenAI (`gpt-4.1`) |
| Tools | Microsoft Learn MCP server |

> This is the **plain** agent — no Agent 365 registration, observability or WorkIQ tools.
> Onboarding is applied afterwards and is visible as the diff from
> `plain/dotnet-agent-teammate` to `main`.

## How it fits together

```
Teams / M365 Copilot
        │  Bot Framework activity
        ▼
ASP.NET Core host ──► LearnAgent : AgentApplication   Agent/LearnAgent.cs
                            │
                            ▼
                     AIAgent (Agent Framework)        Program.cs
                            ├──► Azure OpenAI  gpt-4.1
                            └──► Microsoft Learn MCP  (3 tools)
```

`Agent/ConversationSessionStore.cs` keeps one `AgentSession` per conversation, so chats are
multi-turn. Memory is in-process, so restarting the agent clears every conversation.

## Why there is no Azure Bot registration

This is the biggest structural difference from `dotnet-agent-teams`, and it is deliberate.

An AI Teammate's messaging endpoint is registered **on the blueprint**, not on an Azure Bot
resource. `a365 setup all --aiteammate --m365` calls the MCP Platform `createAgentBlueprint`
endpoint, which proxies Teams Graph and sets the bot `callbackUri` — the same value the Teams
Developer Portal shows as **Notification URL**. When the endpoint changes later:

```powershell
a365 setup blueprint --update-endpoint <url> --m365
```

`--m365` is required; without it the Teams Graph re-registration is skipped silently.

Because there is no separate bot channel app, none of `dotnet-agent-teams`' identity gotchas
apply here:

| `dotnet-agent-teams` | This agent |
|---|---|
| Bot channel app + blueprint, kept strictly separate | Blueprint only |
| Blueprint can't sign channel replies (`AADSTS82001`) | Not applicable — no channel app to sign as |
| `a365 setup all` overwrites the bot credentials in place | No bot credentials to overwrite |
| Observability exported service-to-service | Agentic User identity |

## Running it

The agent runs in **anonymous mode**, so no registration is needed to try it locally:

```powershell
az login --tenant 57db880c-370a-428d-9139-2b346b4eb220
dotnet run
```

It listens on `http://localhost:3980`, with the channel endpoint at `/api/messages`.
`GET /` is a plain liveness string.

In a second terminal, start the Microsoft 365 Agents Playground:

```powershell
npm install -g @microsoft/teams-app-test-tool
teamsapptester
```

**Port 3980, not 3978 or 3979.** `dotnet-agent-teams` uses 3978 and `python-agent-teams` uses
3979, so all three can run at once.

### Commands

| Command | Effect |
|---|---|
| `/reset` | Forget the conversation so far |

## Configuration

| Setting | Purpose |
|---|---|
| `AzureOpenAI:Endpoint` / `Deployment` / `TenantId` | The model the agent reasons with |
| `LearnMcp:Endpoint` | Microsoft Learn MCP server |
| `TokenValidation:Audiences` | Empty = anonymous mode |

`AzureOpenAI:TenantId` pins `DefaultAzureCredential` to the tenant that owns the resource.
Without it a token from another tenant produces
`HTTP 400 – Tenant provided in token does not match resource token`.

You need the **Cognitive Services OpenAI User** role on the Azure OpenAI resource. There is no
API key anywhere.

There is deliberately **no `Connections` section** and **no `appPackage/`**. The Agents SDK logs
`No connections found in configuration` at startup and runs fine in anonymous mode; the Agent 365
CLI writes the real connection settings during onboarding, and it also owns `manifest.json` —
`a365 setup all --aiteammate` and `a365 publish` generate and stamp it, so it must not be
hand-written.

## Next step — onboarding as an AI Teammate

Registration will use the Agentic User identity. Notes gathered ahead of time:

- Omit `--authmode`. `s2s` and `both` are rejected alongside `--aiteammate`; `obo` is accepted
  but warns that it is superfluous, since it is the default for an AI Teammate.
- The CLI derives the blueprint display name as `"<name> Blueprint"` and stamps it into the Teams
  manifest's `name.short`, which is capped at 30 characters. The `--agent-name` passed to the CLI
  must therefore be **20 characters or fewer** — shorter than this folder's name.
- Reaching Teams requires a tenant admin to approve an agent instance from
  `https://admin.cloud.microsoft/#/agents/all/requested`. That is asynchronous and can take
  minutes to hours.
