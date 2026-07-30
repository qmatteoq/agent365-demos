"""FastAPI host for the Microsoft Learn agent."""

from __future__ import annotations

import logging
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
logger = logging.getLogger("learn-agent")

# ---------------------------------------------------------------------------
# A365 Observability - best-effort instrumentation (verify against official sample)
# The distro patches LangChain and Azure OpenAI when it is configured, so it has to run
# before those modules are imported. Everything below this block is a deferred import.
# ---------------------------------------------------------------------------
from app.a365.observability import (  # noqa: E402
    build_baggage_scope,
    init_observability,
    start_invoke_scope,
)
from app.config import settings  # noqa: E402

OBSERVABILITY_ENABLED = init_observability(settings)

from contextlib import asynccontextmanager  # noqa: E402

from fastapi import FastAPI, HTTPException, Request  # noqa: E402
from fastapi.responses import FileResponse, RedirectResponse  # noqa: E402
from fastapi.staticfiles import StaticFiles  # noqa: E402
from opentelemetry import trace  # noqa: E402
from pydantic import BaseModel, Field  # noqa: E402

from app.a365.auth import SESSION_COOKIE, SignInService  # noqa: E402
from app.a365.obo import OBSERVABILITY_SCOPE, AgentOboTokenService  # noqa: E402
from app.a365.token_store import token_store  # noqa: E402
from app.agent import LearnAgent  # noqa: E402

STATIC_DIR = Path(__file__).parent / "static"

agent = LearnAgent(settings)
sign_in = SignInService(settings)

obo_tokens = (
    AgentOboTokenService(
        tenant_id=settings.a365_tenant_id,
        blueprint_client_id=settings.a365_blueprint_id,
        blueprint_client_secret=settings.a365_blueprint_secret,
        agent_identity_client_id=settings.a365_agent_id,
    )
    if settings.a365_configured
    else None
)


@asynccontextmanager
async def lifespan(_: FastAPI):
    # Connect to the Microsoft Learn MCP server and discover its tools once at startup,
    # so every chat turn reuses the same tool list.
    await agent.start()
    # Sign-in cookies are host-specific, so the browser must use the same host as the
    # registered redirect URI. Binding and logging 'localhost' keeps them aligned.
    logger.info("Listening on http://%s:%d", settings.host, settings.port)
    yield

    if obo_tokens is not None:
        await obo_tokens.aclose()

    # Flush any spans still sitting in the batch processor before the process exits.
    provider = trace.get_tracer_provider()
    for method in ("force_flush", "shutdown"):
        action = getattr(provider, method, None)
        if callable(action):
            try:
                action()
            except Exception:  # pragma: no cover - shutdown is best effort
                logger.debug("Tracer provider %s failed.", method, exc_info=True)


app = FastAPI(title="Microsoft Learn Agent", lifespan=lifespan)
app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")


class ChatRequest(BaseModel):
    session_id: str = Field(min_length=1)
    message: str = Field(min_length=1)


class ChatResponse(BaseModel):
    reply: str


def _sid(request: Request) -> str | None:
    return request.cookies.get(SESSION_COOKIE)


@app.get("/")
async def index() -> FileResponse:
    return FileResponse(STATIC_DIR / "index.html")


@app.get("/api/info")
async def info() -> dict[str, object]:
    return {
        "deployment": settings.azure_openai_deployment,
        "learnMcpEndpoint": settings.learn_mcp_endpoint,
        "tools": agent.tool_names,
        "agent365": {
            "configured": settings.a365_configured,
            "signInConfigured": settings.sign_in_configured,
            "exporterEnabled": settings.enable_a365_observability_exporter,
            "agentId": settings.a365_agent_id,
        },
    }


@app.get("/api/me")
async def me(request: Request) -> dict[str, object]:
    user = sign_in.current_user(_sid(request))
    return {
        "signedIn": user is not None,
        "name": (user or {}).get("name", ""),
        "username": (user or {}).get("username", ""),
        "signInRequired": settings.sign_in_configured,
    }


@app.get("/signin")
async def signin(request: Request) -> RedirectResponse:
    if not settings.sign_in_configured:
        raise HTTPException(500, "Sign-in is not configured.")

    sid, created = sign_in.ensure_session(_sid(request))
    response = RedirectResponse(sign_in.begin_sign_in(sid), status_code=302)
    if created:
        response.set_cookie(SESSION_COOKIE, sid, httponly=True, samesite="lax", path="/")
    return response


@app.get("/signin-oidc")
async def signin_callback(request: Request) -> RedirectResponse:
    try:
        sign_in.complete_sign_in(_sid(request), dict(request.query_params))
    except Exception as ex:
        logger.warning("Sign-in failed: %s", ex)
        return RedirectResponse("/?signin=failed", status_code=302)

    return RedirectResponse("/", status_code=302)


@app.get("/signout")
async def signout(request: Request) -> RedirectResponse:
    sign_in.sign_out(_sid(request))
    response = RedirectResponse("/", status_code=302)
    response.delete_cookie(SESSION_COOKIE, path="/")
    return response


async def _prepare_observability_token(user_assertion: str | None) -> bool:
    """Run the agent OBO chain and hand the result to the exporter's token resolver."""
    if not OBSERVABILITY_ENABLED or obo_tokens is None or not user_assertion:
        return False

    token = await obo_tokens.get_agent_token(user_assertion, OBSERVABILITY_SCOPE)
    if not token:
        logger.warning("No Observability API token - traces are not exported.")
        return False

    token_store.set(settings.a365_agent_id, settings.a365_tenant_id, token)
    return True


@app.post("/api/chat")
async def chat(request: Request, body: ChatRequest) -> ChatResponse:
    sid = _sid(request)
    user = sign_in.current_user(sid)

    # The OBO chain starts from the signed-in user, so a turn without one cannot be
    # attributed to the agent identity and would not be exported.
    if settings.sign_in_configured and user is None:
        raise HTTPException(401, "Sign in to talk to the agent.")

    user = user or {}
    traced = await _prepare_observability_token(sign_in.acquire_user_assertion(sid))

    try:
        if not traced:
            reply = await agent.ask(body.session_id, body.message)
        else:
            # Spans emitted outside an active baggage scope are dropped by the exporter,
            # and InvokeAgentScope has to be entered (not just started) so the LangChain
            # inference and tool spans nest underneath it.
            with build_baggage_scope(settings, user, body.session_id):
                with start_invoke_scope(
                    settings, user, body.message, body.session_id
                ) as scope:
                    scope.record_input_messages([body.message])
                    reply = await agent.ask(body.session_id, body.message)
                    scope.record_output_messages([reply])
    except Exception:
        logger.exception("Chat turn failed")
        return ChatResponse(
            reply="Sorry, something went wrong while researching that. Please try again."
        )

    return ChatResponse(reply=reply)


def run() -> None:
    import uvicorn

    uvicorn.run("app.main:app", host=settings.host, port=settings.port)


if __name__ == "__main__":
    run()
