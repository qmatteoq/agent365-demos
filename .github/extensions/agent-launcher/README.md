# Agent 365 agents — launcher canvas

A Copilot CLI **canvas extension**: a dashboard for the five demo agents in this repo, with live
status, start / stop, and the identifiers you need when hunting their traces in Defender.

![scope](https://img.shields.io/badge/scope-project-blue) — because it lives in
`.github/extensions/`, the CLI discovers it automatically when this repository is open. There is
nothing to install.

## Using it

Ask Copilot to open the **Agent 365 agents** canvas, or invoke the canvas directly. Each card gives
you:

- **Live status**, polled from the agent's listening port every three seconds, plus its pid
- **Start / Stop**, running the same command the agent's <kbd>F5</kbd> profile runs
- **Open**, for the two agents that have a browsable url
- **Blueprint id and AUID**, click-to-copy, read from each agent's `a365.generated.config.json`
- The **capability tags** (identity / observability / Work IQ) for that agent

Agents are started **detached**, so closing the panel leaves them running. Their stdout and stderr go
to `%TEMP%\agent-launcher-<id>.log`.

## What it launches

| Card | Command | Port |
| --- | --- | --- |
| `dotnet-agent-no-teams` | `dotnet run --project LearnMcpAgent.csproj --launch-profile https` | 7199 |
| `dotnet-agent-teams` | `dotnet run --project LearnTeamsAgent.csproj` | 3978 |
| `dotnet-agent-teammate` | `dotnet run --project LearnTeammateAgent.csproj` | 3980 |
| `python-agent-no-teams` | `.venv\Scripts\python.exe -m app.main` | 8000 |
| `python-agent-teams` | `.venv\Scripts\python.exe -m app.main` | 3979 |
| dev tunnel × 3 | `devtunnel host <tunnel-name>` | — |

The three Teams-hosted agents are only reachable from Teams while their dev tunnel is hosting, so
each has its own tunnel card. **The tunnel names are placeholders for whichever named tunnels you
created** — edit `registry()` in `extension.mjs` to match yours. A tunnel has no local port to probe,
so its status reads `unknown` until you start it from here.

## How it finds the agents

`resolveRoot()` takes the first of:

1. the `root` canvas input
2. the `AGENT365_ROOT` environment variable
3. the nearest ancestor of `extension.mjs` that contains the agent folders
4. three levels up, the repository root for a copy living in `.github/extensions/agent-launcher`

Steps 3 and 4 are what make the committed copy portable: it walks up from
`.github/extensions/agent-launcher` to the repository root, so the extension works on any clone with
no path to edit. A card whose folder cannot be found says so on its face rather than reporting
`stopped`.

`process.cwd()` is deliberately **not** used. For a user-scoped extension it is the Copilot config
folder rather than the repository, which is measured behaviour, not an assumption.

## Secrets

`a365.generated.config.json` holds the blueprint client secret alongside the ids. The extension
allowlists exactly two fields — `agentBlueprintId` and `agenticAppId` — so nothing else can reach the
browser. The file is gitignored, so on a fresh clone the cards simply show no ids until you run
`a365 setup`.

`dotnet-agent-teammate` never shows an AUID. An AI Teammate's instance id is provisioned when an
admin approves an instance and is read off the activity per turn, so there is no static value to
display; the card says so rather than showing a blank.

## Files

| File | Contents |
| --- | --- |
| `extension.mjs` | Canvas registration, the agent registry, process control, the local HTTP API |
| `ui.mjs` | The dashboard markup, kept separate so the entry point stays wiring only |
