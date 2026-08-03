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
- **Open page**, for the two agents that have a browsable url
- **Log**, opening that agent's captured stdout/stderr in your default editor
- **Blueprint id and AUID**, click-to-copy, read from each agent's `a365.generated.config.json`
- The **capability tags** (identity / observability / Work IQ) for that agent

Agents are started **detached**, so closing the panel leaves them running. Their stdout and stderr go
to `%TEMP%\agent-launcher-<id>.log`, which is what **Log** opens. The button is disabled until that
file exists, since an agent that has never been started from here has nothing to show.

Both buttons act through the extension backend rather than from the page. The canvas webview blocks
`target="_blank"` navigation, so an ordinary link renders as a perfectly normal-looking button that
silently does nothing when clicked. If either button cannot do its job it says so on the button face
instead of failing quietly.

## What it launches

| Card | Command | Port |
| --- | --- | --- |
| `dotnet-agent-no-teams` | `dotnet run --project LearnMcpAgent.csproj --launch-profile https` | 7199 |
| `dotnet-agent-teams` | `dotnet run --project LearnTeamsAgent.csproj` | 3978 |
| `dotnet-agent-teammate` | `dotnet run --project LearnTeammateAgent.csproj` | 3980 |
| `python-agent-no-teams` | `.venv\Scripts\python.exe -m app.main` | 8000 |
| `python-agent-teams` | `.venv\Scripts\python.exe -m app.main` | 3979 |
| dev tunnel × 3 | `devtunnel host <tunnel-name>` | — |

The two `no-teams` agents serve their own web UI and open in your browser once they are up — see
[Opening the web agents](#opening-the-web-agents).

The three Teams-hosted agents are only reachable from Teams while their dev tunnel is hosting, so
each has its own tunnel card. **The tunnel names are placeholders for whichever named tunnels you
created** — edit `registry()` in `extension.mjs` to match yours. A tunnel has no local port to probe,
so its status reads `unknown` until you start it from here.

## How it finds the agents

This is the part worth understanding, because getting it wrong is what makes Start fail.

Finding a *checkout* is not the same as finding a *runnable* checkout. Everything an agent needs to
start — `.venv`, `.env`, `a365.generated.config.json` — is gitignored, so it exists only where you
provisioned it. A git worktree created for editing has an identical source tree and none of that, and
pointing the dashboard at one produces `Interpreter not found`.

So candidate roots are scored on **evidence of provisioning** rather than on source layout, and the
best-scoring one wins:

| Candidate | Where it comes from |
| --- | --- |
| `root` canvas input | Passed when the canvas is opened |
| `AGENT365_ROOT` | Environment variable |
| Ancestors of `extension.mjs` | The repository this copy is committed to |
| `git worktree list` | The main checkout a worktree was created from |

An explicit choice — canvas input or `AGENT365_ROOT` — always wins outright, even if it scores zero.
Otherwise each candidate scores a point per gitignored provisioning artifact found, and the highest
wins. The footer reports which root was chosen and why, so a surprising result is visible rather than
silent.

`process.cwd()` is deliberately **not** a candidate. For a user-scoped extension it is the Copilot
config folder rather than the repository, which is measured behaviour, not an assumption.

## Status

| Status | Meaning |
| --- | --- |
| `running` | Something is listening on the agent's port |
| `starting` | Spawned and alive, but not listening yet — `dotnet run` builds first |
| `stopped` | Not listening, nothing tracked |
| `unknown` | A tunnel that was not started from here, so there is nothing to probe |

`starting` exists so a slow first build does not read as a failed launch. While it shows, the button
is **Stop**, not Start, so a second click cannot leave a duplicate process behind.

## Opening the web agents

The two agents that serve their own UI — `dotnet-agent-no-teams` and `python-agent-no-teams` — open
in your default browser as soon as they finish starting. Their cards are tagged **opens browser**.
The Teams-hosted agents have no page of their own, so nothing opens for them.

The behaviour is driven by `openOnStart: true` in `registry()`; remove it from an entry to opt out.

Two details make it behave rather than becoming a nuisance:

- It fires only for a launch you started **from this dashboard**, and only once. Refreshing the
  dashboard, or opening it on an agent that was already running, never reopens the browser.
- It waits for the port to actually accept connections. `dotnet run` builds before it listens, so
  opening at spawn time would land on a connection error.

Stopping an agent while it is still `starting` cancels the pending open.

The URLs use `localhost`, not `127.0.0.1`, because both agents sign you in and the registered
redirect URIs and auth cookies are host-specific.

## Secrets

`a365.generated.config.json` holds the blueprint client secret alongside the ids. The extension
allowlists exactly two fields — `agentBlueprintId` and `agenticAppId` — so nothing else can reach the
browser. The file is gitignored, so on a fresh clone the cards simply show no ids until you run
`a365 setup`.

`dotnet-agent-teammate` never shows an AUID. An AI Teammate's instance id is provisioned when an
admin approves an instance and is read off the activity per turn, so there is no static value to
display; the card says so rather than showing a blank.

The tunnel cards show no ids at all. A tunnel is a relay with no identity of its own, so showing the
agent's ids there would suggest a relationship that does not exist.

## Files

| File | Contents |
| --- | --- |
| `extension.mjs` | Canvas registration, the agent registry, process control, the local HTTP API |
| `ui.mjs` | The dashboard markup, kept separate so the entry point stays wiring only |
