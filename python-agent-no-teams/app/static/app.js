const messagesEl = document.getElementById("messages");
const emptyEl = document.getElementById("empty");
const formEl = document.getElementById("chat-form");
const inputEl = document.getElementById("input");
const sendEl = document.getElementById("send");
const resetEl = document.getElementById("reset");
const statusEl = document.getElementById("status");
const accountEl = document.getElementById("account");

let sessionId = crypto.randomUUID();
let thinkingEl = null;
let signedIn = false;

function addMessage(role, text) {
    if (emptyEl) emptyEl.remove();

    const msg = document.createElement("div");
    msg.className = `msg ${role}`;

    const roleEl = document.createElement("div");
    roleEl.className = "role";
    roleEl.textContent = role;

    const textEl = document.createElement("div");
    textEl.className = "text";
    textEl.textContent = text;

    msg.append(roleEl, textEl);
    messagesEl.append(msg);
    messagesEl.scrollTop = messagesEl.scrollHeight;
    return msg;
}

function setBusy(busy) {
    inputEl.disabled = busy;
    sendEl.disabled = busy;

    if (busy) {
        thinkingEl = addMessage("assistant", "Searching Microsoft Learn...");
        thinkingEl.querySelector(".text").style.fontStyle = "italic";
    } else if (thinkingEl) {
        thinkingEl.remove();
        thinkingEl = null;
    }
}

formEl.addEventListener("submit", async (event) => {
    event.preventDefault();

    const message = inputEl.value.trim();
    if (!message) return;

    inputEl.value = "";
    addMessage("user", message);
    setBusy(true);

    try {
        const response = await fetch("/api/chat", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ session_id: sessionId, message }),
        });

        if (response.status === 401) {
            setBusy(false);
            addMessage("assistant", "Please sign in first - Agent 365 traces every turn against the signed-in user.");
            inputEl.focus();
            return;
        }

        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const data = await response.json();
        setBusy(false);
        addMessage("assistant", data.reply);
    } catch (error) {
        setBusy(false);
        addMessage("assistant", `Error: ${error.message}`);
    }

    inputEl.focus();
});

resetEl.addEventListener("click", () => {
    // A new thread id is all it takes: conversation memory is keyed by it server-side.
    sessionId = crypto.randomUUID();
    messagesEl.replaceChildren();
    addMessage("assistant", "Started a new conversation.");
    inputEl.focus();
});

fetch("/api/info")
    .then((response) => response.json())
    .then((info) => {
        const parts = [
            `Model: ${info.deployment}`,
            `${info.tools.length} Microsoft Learn tool(s): ${info.tools.join(", ")}`,
        ];
        if (info.agent365 && info.agent365.configured) {
            parts.push(
                info.agent365.exporterEnabled
                    ? "Agent 365 observability: exporting"
                    : "Agent 365 observability: wired, exporter off");
        }
        statusEl.textContent = parts.join(" \u00b7 ");
    })
    .catch(() => {
        statusEl.textContent = "";
    });

// Agent 365 signs every turn against the signed-in user, so surface who that is.
function renderAccount(me) {
    signedIn = me.signedIn;
    accountEl.replaceChildren();

    if (!me.signInRequired) return;

    if (me.signedIn) {
        const who = document.createElement("span");
        who.className = "who";
        who.textContent = me.name || me.username;
        const out = document.createElement("a");
        out.href = "/signout";
        out.className = "secondary-link";
        out.textContent = "Sign out";
        accountEl.append(who, out);
    } else {
        const link = document.createElement("a");
        link.href = "/signin";
        link.className = "signin-link";
        link.textContent = "Sign in";
        accountEl.append(link);
    }
}

fetch("/api/me")
    .then((response) => response.json())
    .then(renderAccount)
    .catch(() => {});

if (new URLSearchParams(location.search).get("signin") === "failed") {
    addMessage("assistant", "Sign-in failed. Check the app registration and try again.");
}
