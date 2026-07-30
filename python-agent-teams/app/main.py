"""Host for the Microsoft Learn agent on Teams / Microsoft 365 Copilot.

The Microsoft 365 Agents SDK owns the channel: it authenticates the incoming Bot Framework
request, hands the turn to :class:`AgentApplication`, and sends replies back through the
connector. This module is only the bridge between a turn and the LangChain agent core in
``app.agent``.
"""

from __future__ import annotations

import logging
from os import environ, path

from aiohttp.web import Application, Request, Response, middleware, run_app
from dotenv import load_dotenv
from microsoft_agents.activity import load_configuration_from_env
from microsoft_agents.authentication.msal import MsalConnectionManager
from microsoft_agents.hosting.aiohttp import (
    CloudAdapter,
    jwt_authorization_middleware,
    start_agent_process,
)
from microsoft_agents.hosting.core import (
    AgentApplication,
    Authorization,
    MemoryStorage,
    TurnContext,
    TurnState,
)

load_dotenv(path.join(path.dirname(path.dirname(path.abspath(__file__))), ".env"))

from app.config import settings  # noqa: E402  (must follow load_dotenv)
from app.a365.fmi import ObservabilityTokenService  # noqa: E402
from app.a365.observability import (  # noqa: E402
    build_baggage_scope,
    init_observability,
    start_invoke_scope,
)

# Must run before app.agent is imported: the distro patches LangChain and the Azure OpenAI
# client at import time, and anything already imported is missed.
OBSERVABILITY_ENABLED = init_observability(settings)

from app.agent import LearnAgent  # noqa: E402  (must follow init_observability)

logging.basicConfig(
    level=logging.INFO,
    format="%(levelname)s %(name)s: %(message)s",
)
logger = logging.getLogger("learn-teams-agent")

WELCOME = (
    "Hi! I'm the **Microsoft Learn research agent**. Ask me anything about the Microsoft "
    "ecosystem - Azure, Microsoft 365, Power Platform, .NET, Entra, Copilot or Dynamics 365 - "
    "and I'll answer from the official Microsoft Learn documentation and cite my sources.\n\n"
    "Type `/reset` to start a fresh conversation."
)

# ── Agents SDK wiring ────────────────────────────────────────────────────────────────
# Configuration comes from the environment using the SDK's double-underscore convention
# (CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID and friends), not from app.config.
agents_sdk_config = load_configuration_from_env(environ)

STORAGE = MemoryStorage()
CONNECTION_MANAGER = MsalConnectionManager(**agents_sdk_config)
ADAPTER = CloudAdapter(connection_manager=CONNECTION_MANAGER)
AUTHORIZATION = Authorization(STORAGE, CONNECTION_MANAGER, **agents_sdk_config)

AGENT_APP = AgentApplication[TurnState](
    storage=STORAGE,
    adapter=ADAPTER,
    authorization=AUTHORIZATION,
    **agents_sdk_config,
)

LEARN_AGENT = LearnAgent(settings)
TOKEN_SERVICE = ObservabilityTokenService(settings)


def _caller(context: TurnContext) -> dict[str, str]:
    """Identify the Teams user driving the turn.

    ``aad_object_id`` is the Entra object id and is what the Admin Center and Defender
    correlate on; ``from_property.id`` is only a channel-scoped id, so it is the fallback
    rather than the first choice. Teams does not put the address on the activity.
    """
    sender = context.activity.from_property
    if sender is None:
        return {}
    return {
        "id": sender.aad_object_id or sender.id or "",
        "name": sender.name or "",
        "email": "",
    }


async def _welcome(context: TurnContext, _state: TurnState) -> None:
    await context.send_activity(WELCOME)


AGENT_APP.conversation_update("membersAdded")(_welcome)
AGENT_APP.message("/help")(_welcome)


@AGENT_APP.message("/reset")
async def on_reset(context: TurnContext, _state: TurnState) -> None:
    LEARN_AGENT.reset(context.activity.conversation.id)
    await context.send_activity("Done - I've forgotten our conversation so far.")


@AGENT_APP.activity("message")
async def on_message(context: TurnContext, _state: TurnState) -> None:
    question = (context.activity.text or "").strip()
    if not question:
        await context.send_activity("Ask me a question about the Microsoft ecosystem.")
        return

    conversation_id = context.activity.conversation.id

    try:
        answer = await _answer(conversation_id, question, _caller(context))
    except Exception:
        # A failed turn must not take the channel down: Teams would show a bare
        # "the bot failed to respond" with nothing actionable in it.
        logger.exception("The agent failed to answer.")
        await context.send_activity(
            "Sorry - something went wrong while researching that. Please try again."
        )
        return

    await context.send_activity(answer)


async def _answer(conversation_id: str, question: str, user: dict[str, str]) -> str:
    """Run one turn, traced when Agent 365 observability is configured."""
    if not OBSERVABILITY_ENABLED:
        return await LEARN_AGENT.ask(conversation_id, question)

    # The baggage scope has to wrap the invoke scope: the exporter reads the identity
    # dimensions off baggage and drops any span emitted outside one. The inference and
    # tool spans underneath come from the distro's LangChain instrumentation, so only
    # the agent-level span is opened by hand.
    with build_baggage_scope(settings, user, conversation_id):
        with start_invoke_scope(settings, user, question, conversation_id) as scope:
            scope.record_input_messages([question])
            answer = await LEARN_AGENT.ask(conversation_id, question)
            scope.record_output_messages([answer])
            return answer


@AGENT_APP.activity("installationUpdate")
async def on_installation_update(_context: TurnContext, _state: TurnState) -> None:
    """Swallow install/uninstall notifications.

    Teams sends these when the app is added or removed. There is nothing to do -- the
    welcome message is driven by the membersAdded conversation update instead -- but
    without a route the SDK logs a warning on every install, which is noise in a demo.
    """
    return


@AGENT_APP.error
async def on_error(context: TurnContext, error: Exception) -> None:
    logger.error("Unhandled agent error: %s", error, exc_info=error)


# ── aiohttp host ─────────────────────────────────────────────────────────────────────
async def _messages(req: Request) -> Response:
    return await start_agent_process(req, req.app["agent_app"], req.app["adapter"])


async def _health(_: Request) -> Response:
    return Response(
        text=(
            f"Microsoft Learn agent is running. "
            f"MCP tools: {LEARN_AGENT.tool_names or 'not yet connected'}. "
            f"A365 observability: {'on' if OBSERVABILITY_ENABLED else 'off'}"
        ),
        content_type="text/plain",
    )


@middleware
async def _auth_for_messages_only(request: Request, handler):
    """Apply Bot Framework JWT validation to the messaging endpoint only.

    ``jwt_authorization_middleware`` rejects any request without an ``Authorization``
    header, so registering it application-wide (as the SDK sample does) would also put the
    health route behind auth and make it useless as a liveness probe. The messaging
    endpoint itself stays fully protected.
    """
    if request.path.startswith("/api/messages") and request.method == "POST":
        return await jwt_authorization_middleware(request, handler)
    return await handler(request)


async def _start_token_service(_: Application) -> None:
    if OBSERVABILITY_ENABLED:
        await TOKEN_SERVICE.start()


async def _stop_token_service(_: Application) -> None:
    if OBSERVABILITY_ENABLED:
        await TOKEN_SERVICE.stop()


def create_app() -> Application:
    app = Application(middlewares=[_auth_for_messages_only])
    app.router.add_post("/api/messages", _messages)
    # The Bot Framework emulator and health probes issue a GET against the same route.
    app.router.add_get("/api/messages", lambda _: Response(status=200))
    app.router.add_get("/", _health)

    app["agent_configuration"] = CONNECTION_MANAGER.get_default_connection_configuration()
    app["agent_app"] = AGENT_APP
    app["adapter"] = ADAPTER

    # The exporter needs a token before the first turn, and the refresh loop needs the
    # running event loop, so both are bound to the application lifecycle.
    app.on_startup.append(_start_token_service)
    app.on_cleanup.append(_stop_token_service)
    return app


def main() -> None:
    logger.info("Starting the Microsoft Learn Teams agent on port %d.", settings.port)
    run_app(create_app(), host="localhost", port=settings.port)


if __name__ == "__main__":
    main()
