// Extension: agent-launcher
// A dashboard for the Agent 365 demo agents: live status, start/stop, and the
// identifiers you need when hunting for their traces in Defender.
//
// extension.mjs stays wiring + backend; the dashboard markup lives in ui.mjs.

import { createServer } from "node:http";
import { connect } from "node:net";
import { spawn, execFile, execFileSync } from "node:child_process";
import { readFileSync, existsSync, openSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { tmpdir } from "node:os";
import { joinSession, createCanvas } from "@github/copilot-sdk/extension";
import { renderHtml } from "./ui.mjs";

// Which folder holds the agents?
//
// Finding a checkout is not the same as finding a *runnable* checkout. Every
// artifact an agent needs to start - .venv, .env, a365.generated.config.json -
// is gitignored, so it exists only where you provisioned it. A git worktree
// created for editing has the same source tree and none of that, and pointing
// the dashboard at one produces "Interpreter not found".
//
// So candidate roots are scored on evidence of provisioning rather than on
// source layout, and the best-provisioned one wins. An explicit choice (canvas
// input or AGENT365_ROOT) always wins outright, even if it scores zero.
const AGENTS = [
    "dotnet-agent-no-teams",
    "dotnet-agent-teams",
    "dotnet-agent-teammate",
    "python-agent-no-teams",
    "python-agent-teams",
];

// Gitignored, so their presence means "provisioned here", not "checked out here".
const PROVISIONED = [
    "a365.generated.config.json",
    ".env",
    join(".venv", "Scripts", "python.exe"),
];

function looksLikeRepo(dir) {
    return AGENTS.some((a) => existsSync(join(dir, a)));
}

function scoreRoot(dir) {
    if (!dir || !existsSync(dir)) return -1;
    let score = 0;
    for (const agent of AGENTS) {
        const d = join(dir, agent);
        if (!existsSync(d)) continue;
        for (const marker of PROVISIONED) if (existsSync(join(d, marker))) score++;
    }
    return score;
}

// Ancestors of this file that hold the agents - covers both a copy committed to
// <repo>/.github/extensions/ and one dropped straight into the repo root.
function ancestorRoots() {
    const found = [];
    let dir = import.meta.dirname;
    for (let i = 0; i < 6; i++) {
        if (looksLikeRepo(dir)) found.push(dir);
        const parent = dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    return found;
}

// The checkout a worktree was created from. This is the one case where the
// provisioned copy is derivable rather than guessed.
function mainWorktree(from) {
    try {
        const out = execFileSync("git", ["worktree", "list", "--porcelain"], {
            cwd: from,
            encoding: "utf8",
            stdio: ["ignore", "pipe", "ignore"],
        });
        const first = out.split(/\r?\n/).find((l) => l.startsWith("worktree "));
        return first ? first.slice("worktree ".length).trim() : null;
    } catch {
        return null;
    }
}

function resolveRoot(input) {
    const explicit = input || process.env.AGENT365_ROOT;
    if (explicit) {
        return { root: resolve(explicit), reason: input ? "canvas input" : "AGENT365_ROOT" };
    }

    const candidates = [];
    const ancestors = ancestorRoots();
    for (const dir of ancestors) candidates.push({ dir, reason: "repository containing this extension" });
    for (const dir of ancestors) {
        const main = mainWorktree(dir);
        if (main && !candidates.some((c) => c.dir === main)) {
            candidates.push({ dir: main, reason: "main checkout of this worktree" });
        }
    }

    let best = null;
    for (const c of candidates) {
        const score = scoreRoot(c.dir);
        if (!best || score > best.score) best = { ...c, score };
    }

    if (best && best.score > 0) return { root: resolve(best.dir), reason: best.reason };
    // Nothing is provisioned anywhere: fall back to the repo so the cards at
    // least render, and let each one report what it is missing.
    const fallback = ancestors[0] || dirname(dirname(dirname(import.meta.dirname)));
    return { root: resolve(fallback), reason: "no provisioned checkout found" };
}

// Launch commands mirror each agent's .vscode/launch.json so the button and F5
// do the same thing. Ports come from launchSettings.json / the app's own config.
function registry(root) {
    return [
        {
            id: "dotnet-no-teams",
            name: "dotnet-agent-no-teams",
            description:
                "Blazor web app on Agent Framework. Signs the user in through its own web client app, then calls Microsoft Learn over MCP.",
            stack: ".NET / Agent Framework",
            hosting: "Web app",
            dir: join(root, "dotnet-agent-no-teams"),
            port: 7199,
            url: "https://localhost:7199",
            cmd: "dotnet",
            args: ["run", "--project", "LearnMcpAgent.csproj", "--launch-profile", "https"],
            caps: { identity: true, observability: true, workiq: true },
        },
        {
            id: "dotnet-teams",
            name: "dotnet-agent-teams",
            description:
                "Teams-hosted custom engine agent. Observability and Work IQ both go through the agent On-Behalf-Of chain, seeded by Teams SSO.",
            stack: ".NET / Agent Framework",
            hosting: "Teams",
            dir: join(root, "dotnet-agent-teams"),
            port: 3978,
            url: null,
            cmd: "dotnet",
            args: ["run", "--project", "LearnTeamsAgent.csproj"],
            caps: { identity: true, observability: true, workiq: true },
            note: "Reachable from Teams only while its dev tunnel is hosting. Start the tunnel below first.",
        },
        {
            id: "dotnet-teammate",
            name: "dotnet-agent-teammate",
            description:
                "The AI Teammate. Same stack and surface as dotnet-agent-teams, but it acts under its own Agentic User identity instead of on behalf of the caller.",
            stack: ".NET / Agent Framework",
            hosting: "Teams",
            dir: join(root, "dotnet-agent-teammate"),
            port: 3980,
            url: null,
            cmd: "dotnet",
            args: ["run", "--project", "LearnTeammateAgent.csproj"],
            caps: { identity: true, observability: true, workiq: true },
            // The AUID is provisioned per approved instance and read off the
            // activity at runtime, so there is no static id to show here.
            auidNote: "resolved per turn from the activity",
            note: "Runs as Production on purpose, so the real agentic Work IQ path is exercised. Reachable from Teams only while its dev tunnel is hosting.",
        },
        {
            id: "python-no-teams",
            name: "python-agent-no-teams",
            description:
                "FastAPI app on LangChain. Same shape as the .NET web agent; observability exports on-behalf-of the signed-in user.",
            stack: "Python / LangChain",
            hosting: "Web app",
            dir: join(root, "python-agent-no-teams"),
            port: 8000,
            url: "http://localhost:8000",
            cmd: join(root, "python-agent-no-teams", ".venv", "Scripts", "python.exe"),
            args: ["-m", "app.main"],
            caps: { identity: true, observability: true, workiq: false },
            note: "Sign in on localhost, not 127.0.0.1 - the session cookie is host-specific.",
        },
        {
            id: "python-teams",
            name: "python-agent-teams",
            description:
                "Teams-hosted agent on LangChain. Python counterpart of the .NET Teams agent; observability goes through the same agent On-Behalf-Of chain.",
            stack: "Python / LangChain",
            hosting: "Teams",
            dir: join(root, "python-agent-teams"),
            port: 3979,
            url: null,
            cmd: join(root, "python-agent-teams", ".venv", "Scripts", "python.exe"),
            args: ["-m", "app.main"],
            caps: { identity: true, observability: true, workiq: false },
            note: "Reachable from Teams only while its dev tunnel is hosting. Start the tunnel below first.",
        },
        {
            id: "devtunnel",
            name: "dev tunnel (dotnet-agent-teams)",
            description:
                "Hosts the tunnel that lets Teams reach the .NET Teams agent on port 3978.",
            stack: "devtunnel",
            hosting: "Relay",
            dir: join(root, "dotnet-agent-teams"),
            port: null,
            url: null,
            cmd: "devtunnel",
            args: ["host", "dotnet-agent-teams-tunnel"],
            caps: null,
            trackedOnly: true,
            note: "No local port to probe, so status is only known when started from here.",
        },
        {
            id: "devtunnel-python",
            name: "dev tunnel (python-agent-teams)",
            description:
                "Hosts the tunnel that lets Teams reach the Python Teams agent on port 3979.",
            stack: "devtunnel",
            hosting: "Relay",
            dir: join(root, "python-agent-teams"),
            port: null,
            url: null,
            cmd: "devtunnel",
            args: ["host", "python-agent-teams-tunnel"],
            caps: null,
            trackedOnly: true,
            note: "No local port to probe, so status is only known when started from here.",
        },
        {
            id: "devtunnel-teammate",
            name: "dev tunnel (dotnet-agent-teammate)",
            description:
                "Hosts the tunnel that lets Teams reach the AI Teammate on port 3980.",
            stack: "devtunnel",
            hosting: "Relay",
            dir: join(root, "dotnet-agent-teammate"),
            port: null,
            url: null,
            cmd: "devtunnel",
            args: ["host", "dotnet-teammate-tunnel"],
            caps: null,
            trackedOnly: true,
            note: "No local port to probe, so status is only known when started from here.",
        },
    ];
}

// Only ever surface identifiers. a365.generated.config.json also holds the
// blueprint client secret, which must never reach the browser.
const ID_FIELDS = { auid: "agenticAppId", blueprint: "agentBlueprintId" };

function readIds(dir) {
    const out = { auid: null, blueprint: null };
    const path = join(dir, "a365.generated.config.json");
    if (!existsSync(path)) return out;
    try {
        const parsed = JSON.parse(readFileSync(path, "utf8"));
        for (const [key, field] of Object.entries(ID_FIELDS)) {
            const value = parsed[field];
            if (typeof value === "string" && value) out[key] = value;
        }
    } catch {
        /* a malformed config just means no ids to show */
    }
    return out;
}

function probePort(port, timeout = 400) {
    return new Promise((resolve) => {
        const socket = connect({ host: "127.0.0.1", port });
        const done = (result) => {
            socket.destroy();
            resolve(result);
        };
        socket.setTimeout(timeout);
        socket.once("connect", () => done(true));
        socket.once("timeout", () => done(false));
        socket.once("error", () => done(false));
    });
}

function findPidByPort(port) {
    return new Promise((resolve) => {
        execFile("netstat", ["-ano", "-p", "tcp"], (err, stdout) => {
            if (err) return resolve(null);
            for (const line of stdout.split(/\r?\n/)) {
                if (!line.includes("LISTENING")) continue;
                const parts = line.trim().split(/\s+/);
                const local = parts[1] || "";
                if (local.endsWith(":" + port)) {
                    const pid = Number(parts[parts.length - 1]);
                    if (Number.isInteger(pid) && pid > 0) return resolve(pid);
                }
            }
            resolve(null);
        });
    });
}

// pid of processes this canvas started, so the tunnel (which has no port to
// probe) is still stoppable and so we can show a pid straight after launch.
const started = new Map();
const errors = new Map();

function logPathFor(id) {
    return join(tmpdir(), `agent-launcher-${id}.log`);
}

function isAlive(pid) {
    try {
        process.kill(pid, 0);
        return true;
    } catch {
        return false;
    }
}

async function describe(agent) {
    // Tunnels are relays, not agents - they have no identity of their own, so
    // showing the agent's ids on their card would be misleading.
    const ids = agent.trackedOnly ? { auid: null, blueprint: null } : readIds(agent.dir);
    const tracked = started.get(agent.id);
    let status = "stopped";
    let pid = null;

    if (agent.port) {
        const up = await probePort(agent.port);
        if (up) {
            status = "running";
            pid = (await findPidByPort(agent.port)) ?? tracked ?? null;
        } else if (tracked && isAlive(tracked)) {
            // Spawned and still alive, but nothing is listening yet. `dotnet run`
            // builds first, which can take a minute - reporting "stopped" here
            // invites a second click and a duplicate process.
            status = "starting";
            pid = tracked;
        }
    } else if (agent.trackedOnly) {
        // No port to probe: only trust a child we started that is still alive.
        if (tracked && isAlive(tracked)) {
            status = "running";
            pid = tracked;
        } else {
            status = tracked ? "stopped" : "unknown";
        }
    }

    return {
        id: agent.id,
        name: agent.name,
        description: agent.description,
        stack: agent.stack,
        hosting: agent.hosting,
        port: agent.port,
        url: agent.url,
        caps: agent.caps,
        note: agent.note || null,
        auid: ids.auid,
        auidNote: agent.auidNote || null,
        blueprint: ids.blueprint,
        status,
        pid,
        logPath: logPathFor(agent.id),
        error: errors.get(agent.id) || null,
        missing: !existsSync(agent.dir),
    };
}

function startAgent(agent) {
    errors.delete(agent.id);
    if (!existsSync(agent.dir)) {
        errors.set(agent.id, `Folder not found: ${agent.dir}`);
        return { ok: false, error: errors.get(agent.id) };
    }
    if (agent.cmd.includes("\\") && !existsSync(agent.cmd)) {
        errors.set(
            agent.id,
            `No virtual environment at ${agent.dir}\\.venv.\n` +
                `Create one, then press Start again:\n` +
                `  cd ${agent.dir}\n` +
                `  uv venv --clear --python C:\\Python312-x64\\python.exe\n` +
                `  uv sync\n` +
                `On Windows ARM the x64 interpreter is required - tiktoken has no win_arm64 wheel.`
        );
        return { ok: false, error: errors.get(agent.id) };
    }
    try {
        const fd = openSync(logPathFor(agent.id), "a");
        const child = spawn(agent.cmd, agent.args, {
            cwd: agent.dir,
            detached: true,
            windowsHide: true,
            stdio: ["ignore", fd, fd],
            shell: false,
        });
        child.on("error", (e) => errors.set(agent.id, String(e.message || e)));
        child.unref();
        started.set(agent.id, child.pid);
        return { ok: true, pid: child.pid };
    } catch (e) {
        errors.set(agent.id, String(e.message || e));
        return { ok: false, error: errors.get(agent.id) };
    }
}

async function stopAgent(agent) {
    errors.delete(agent.id);
    let pid = agent.port ? await findPidByPort(agent.port) : null;
    if (!pid) pid = started.get(agent.id) ?? null;
    if (!pid) return { ok: false, error: "No process found to stop." };

    return new Promise((resolve) => {
        // /T also takes down the child processes dotnet run spawns.
        execFile("taskkill", ["/PID", String(pid), "/T", "/F"], (err, _out, stderr) => {
            if (err) {
                errors.set(agent.id, stderr || String(err));
                resolve({ ok: false, error: errors.get(agent.id) });
            } else {
                started.delete(agent.id);
                resolve({ ok: true, pid });
            }
        });
    });
}

const servers = new Map();

async function startServer({ root, reason }) {
    const agents = registry(root);
    const byId = new Map(agents.map((a) => [a.id, a]));

    const server = createServer(async (req, res) => {
        const url = new URL(req.url, "http://127.0.0.1");
        const send = (code, body) => {
            res.writeHead(code, { "Content-Type": "application/json; charset=utf-8" });
            res.end(JSON.stringify(body));
        };

        if (url.pathname === "/api/agents") {
            return send(200, { root, reason, agents: await Promise.all(agents.map(describe)) });
        }

        const action = url.pathname.match(/^\/api\/(start|stop)\/(.+)$/);
        if (action && req.method === "POST") {
            const agent = byId.get(decodeURIComponent(action[2]));
            if (!agent) return send(404, { ok: false, error: "Unknown agent" });
            return send(200, action[1] === "start" ? startAgent(agent) : await stopAgent(agent));
        }

        res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
        res.end(renderHtml());
    });

    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    return { server, agents, byId, url: `http://127.0.0.1:${address.port}/` };
}

function entryFor(ctx) {
    const entry = servers.get(ctx.instanceId);
    if (!entry) throw new Error(`Canvas instance '${ctx.instanceId}' is not open.`);
    return entry;
}

const session = await joinSession({
    canvases: [
        createCanvas({
            id: "agent-launcher",
            displayName: "Agent 365 agents",
            description:
                "Dashboard for the Agent 365 demo agents: live running status, start/stop, and the AUIDs used to hunt their traces in Defender.",
            inputSchema: {
                type: "object",
                properties: {
                    root: {
                        type: "string",
                        description:
                            "Folder holding the agent subfolders. Defaults to AGENT365_ROOT, or the repository this extension is installed in.",
                    },
                },
            },
            actions: [
                {
                    name: "list_agents",
                    description:
                        "Return every agent with its live status, pid, port, AUID and blueprint id.",
                    handler: async (ctx) => {
                        const { agents } = entryFor(ctx);
                        return { agents: await Promise.all(agents.map(describe)) };
                    },
                },
                {
                    name: "start_agent",
                    description:
                        "Start one agent or dev tunnel by id: dotnet-no-teams, dotnet-teams, dotnet-teammate, python-no-teams, python-teams, devtunnel, devtunnel-python, devtunnel-teammate.",
                    inputSchema: {
                        type: "object",
                        properties: { id: { type: "string" } },
                        required: ["id"],
                    },
                    handler: async (ctx) => {
                        const agent = entryFor(ctx).byId.get(ctx.input?.id);
                        if (!agent) return { ok: false, error: `Unknown agent '${ctx.input?.id}'` };
                        return startAgent(agent);
                    },
                },
                {
                    name: "stop_agent",
                    description: "Stop one agent by id.",
                    inputSchema: {
                        type: "object",
                        properties: { id: { type: "string" } },
                        required: ["id"],
                    },
                    handler: async (ctx) => {
                        const agent = entryFor(ctx).byId.get(ctx.input?.id);
                        if (!agent) return { ok: false, error: `Unknown agent '${ctx.input?.id}'` };
                        return stopAgent(agent);
                    },
                },
            ],
            open: async (ctx) => {
                let entry = servers.get(ctx.instanceId);
                if (!entry) {
                    entry = await startServer(resolveRoot(ctx.input?.root));
                    servers.set(ctx.instanceId, entry);
                }
                return { title: "Agent 365 agents", url: entry.url };
            },
            // Closing the panel only tears down the dashboard's own server -
            // agents keep running, because they were started detached.
            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (entry) {
                    servers.delete(ctx.instanceId);
                    await new Promise((resolve) => entry.server.close(() => resolve()));
                }
            },
        }),
    ],
});
