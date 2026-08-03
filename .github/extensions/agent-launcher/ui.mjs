// Dashboard markup for the agent-launcher canvas.
// Kept separate from extension.mjs so the entry point stays wiring-only.

export function renderHtml() {
    return `<!doctype html>
<html>
<head>
<meta charset="utf-8" />
<title>Agent 365 demo agents</title>
<style>
  :root {
    --bg: #0d1117; --panel: #161b22; --border: #30363d; --text: #e6edf3;
    --muted: #8b949e; --accent: #2f81f7; --ok: #3fb950; --off: #6e7681;
    --warn: #d29922; --danger: #f85149;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 20px; background: var(--bg); color: var(--text);
    font: 14px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  header { display: flex; align-items: baseline; gap: 12px; margin-bottom: 4px; }
  h1 { font-size: 17px; margin: 0; font-weight: 600; }
  .sub { color: var(--muted); font-size: 12px; margin-bottom: 18px; }
  .grid { display: grid; gap: 14px; }
  .card {
    background: var(--panel); border: 1px solid var(--border);
    border-radius: 8px; padding: 14px 16px;
  }
  .card.busy { opacity: .6; }
  .row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .name { font-weight: 600; font-size: 15px; }
  .badge {
    font-size: 11px; padding: 2px 8px; border-radius: 999px;
    border: 1px solid var(--border); color: var(--muted);
    display: inline-flex; align-items: center; gap: 5px; white-space: nowrap;
  }
  .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--off); }
  .badge.on { color: var(--ok); border-color: rgba(63,185,80,.4); }
  .badge.on .dot { background: var(--ok); box-shadow: 0 0 6px var(--ok); }
  .badge.unknown { color: var(--warn); border-color: rgba(210,153,34,.4); }
  .badge.unknown .dot { background: var(--warn); }
  .badge.starting { color: var(--accent); border-color: rgba(47,129,247,.4); }
  .badge.starting .dot { background: var(--accent); animation: pulse 1s infinite; }
  @keyframes pulse { 50% { opacity: .25; } }
  .desc { color: var(--muted); font-size: 12.5px; margin: 8px 0 10px; }
  .meta { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 12px; }
  .tag {
    font-size: 11px; padding: 2px 7px; border-radius: 5px;
    background: #21262d; color: var(--muted); border: 1px solid var(--border);
  }
  .tag.cap { color: var(--ok); border-color: rgba(63,185,80,.3); }
  .tag.no { color: var(--off); }
  .ids { font-size: 11px; color: var(--muted); margin-bottom: 12px; }
  .ids code {
    background: #21262d; padding: 1px 5px; border-radius: 4px;
    font-size: 11px; cursor: pointer; border: 1px solid transparent;
  }
  .ids code:hover { border-color: var(--accent); color: var(--text); }
  button, a.btn {
    font: inherit; font-size: 12.5px; padding: 5px 12px; border-radius: 6px;
    border: 1px solid var(--border); background: #21262d; color: var(--text);
    cursor: pointer; text-decoration: none; display: inline-block;
  }
  button:hover:not(:disabled), a.btn:hover { border-color: var(--accent); }
  button:disabled { opacity: .4; cursor: not-allowed; }
  button.start { color: var(--ok); }
  button.stop { color: var(--danger); }
  .note {
    font-size: 11.5px; color: var(--warn); margin-top: 10px;
    border-left: 2px solid var(--warn); padding-left: 8px;
  }
  .err {
    font-size: 11.5px; color: var(--danger); margin-top: 10px;
    border-left: 2px solid var(--danger); padding-left: 8px;
    white-space: pre-wrap; font-family: ui-monospace, monospace;
  }
  footer { margin-top: 16px; color: var(--muted); font-size: 11.5px; }
</style>
</head>
<body>
<header>
  <h1>Agent 365 demo agents</h1>
  <span class="badge" id="clock"><span class="dot"></span><span id="clocktxt">connecting</span></span>
</header>
<div class="sub">Live status polled from listening ports. Start / stop runs the same command as F5.</div>
<div class="grid" id="grid"></div>
<footer id="foot"></footer>

<script>
var busy = {};

function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
  });
}

function capTag(label, on) {
  return '<span class="tag ' + (on ? 'cap' : 'no') + '">' + (on ? '&#10003; ' : '&#8212; ') + esc(label) + '</span>';
}

function card(a) {
  var isBusy = !!busy[a.id];
  var h = '<div class="card' + (isBusy ? ' busy' : '') + '">';

  var cls = a.status === 'running' ? 'on' : (a.status === 'unknown' ? 'unknown' : (a.status === 'starting' ? 'starting' : ''));
  h += '<div class="row"><span class="name">' + esc(a.name) + '</span>';
  h += '<span class="badge ' + cls + '"><span class="dot"></span>' + esc(a.status) + '</span>';
  if (a.pid) h += '<span class="tag">pid ' + esc(a.pid) + '</span>';
  h += '</div>';

  h += '<div class="desc">' + esc(a.description) + '</div>';

  h += '<div class="meta">';
  h += '<span class="tag">' + esc(a.stack) + '</span>';
  h += '<span class="tag">' + esc(a.hosting) + '</span>';
  if (a.port) h += '<span class="tag">:' + esc(a.port) + '</span>';
  if (a.openOnStart) h += '<span class="tag" title="The browser opens on this page once the agent is serving">opens browser</span>';
  if (a.caps) {
    h += capTag('identity', a.caps.identity);
    h += capTag('observability', a.caps.observability);
    h += capTag('WorkIQ', a.caps.workiq);
  }
  h += '</div>';

  if (a.auid || a.blueprint) {
    h += '<div class="ids">';
    if (a.auid) {
      h += 'AUID <code title="click to copy" onclick="copy(this)">' + esc(a.auid) + '</code>';
    } else if (a.auidNote) {
      h += 'AUID <span class="tag">' + esc(a.auidNote) + '</span>';
    }
    if (a.blueprint) {
      if (a.auid || a.auidNote) h += ' &nbsp;';
      h += 'blueprint <code title="click to copy" onclick="copy(this)">' + esc(a.blueprint) + '</code>';
    }
    h += '</div>';
  }

  h += '<div class="row">';
  if (a.status === 'running' || a.status === 'starting') {
    h += '<button class="stop" onclick="act(\\'stop\\',\\'' + a.id + '\\')"' + (isBusy ? ' disabled' : '') + '>Stop</button>';
  } else {
    h += '<button class="start" onclick="act(\\'start\\',\\'' + a.id + '\\')"' + (isBusy ? ' disabled' : '') + '>Start</button>';
  }
  if (a.url && a.status === 'running') {
    h += '<a class="btn" href="' + esc(a.url) + '" target="_blank" rel="noopener">Open</a>';
  }
  if (a.logPath) {
    h += '<span class="tag" title="' + esc(a.logPath) + '">log</span>';
  }
  h += '</div>';

  if (a.note) h += '<div class="note">' + esc(a.note) + '</div>';
  if (a.missing) h += '<div class="err">Folder not found under the current root.</div>';
  if (a.error) h += '<div class="err">' + esc(a.error) + '</div>';

  h += '</div>';
  return h;
}

function copy(el) {
  navigator.clipboard.writeText(el.textContent);
  var old = el.style.color;
  el.style.color = '#3fb950';
  setTimeout(function () { el.style.color = old; }, 600);
}

function act(what, id) {
  busy[id] = true;
  render(window.__last || []);
  fetch('/api/' + what + '/' + encodeURIComponent(id), { method: 'POST' })
    .then(function (r) { return r.json(); })
    .then(function () {
      // Give the process a beat to bind or release its port.
      setTimeout(function () { busy[id] = false; refresh(); }, 1500);
    })
    .catch(function () { busy[id] = false; refresh(); });
}

function render(agents) {
  window.__last = agents;
  document.getElementById('grid').innerHTML = agents.map(card).join('');
}

function refresh() {
  fetch('/api/agents')
    .then(function (r) { return r.json(); })
    .then(function (d) {
      render(d.agents);
      document.getElementById('clocktxt').textContent =
        d.agents.filter(function (a) { return a.status === 'running'; }).length + ' running';
      document.getElementById('clock').className = 'badge on';
      document.getElementById('foot').textContent =
        'Root: ' + d.root + (d.reason ? '  (' + d.reason + ')' : '') +
        '  |  refreshed ' + new Date().toLocaleTimeString();
    })
    .catch(function (e) {
      document.getElementById('clocktxt').textContent = 'disconnected';
      document.getElementById('clock').className = 'badge unknown';
    });
}

refresh();
setInterval(refresh, 3000);
</script>
</body>
</html>`;
}
