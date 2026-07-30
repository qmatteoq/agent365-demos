const messagesEl = document.getElementById("messages");
const emptyEl = document.getElementById("empty");
const formEl = document.getElementById("chat-form");
const inputEl = document.getElementById("input");
const sendEl = document.getElementById("send");
const resetEl = document.getElementById("reset");
const statusEl = document.getElementById("status");

let sessionId = crypto.randomUUID();
let thinkingEl = null;

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
        statusEl.textContent =
            `Model: ${info.deployment} · ${info.tools.length} Microsoft Learn tool(s): ${info.tools.join(", ")}`;
    })
    .catch(() => {
        statusEl.textContent = "";
    });
