"""A365 observability wiring.

This module must be imported -- and :func:`init_observability` called -- *before* the
LangChain / Azure OpenAI modules are imported, so the distro's auto-instrumentation can
patch them. ``app/main.py`` does exactly that.
"""

from __future__ import annotations

import logging

from microsoft.opentelemetry import use_microsoft_opentelemetry
from microsoft.opentelemetry.a365.core.agent_details import AgentDetails
from microsoft.opentelemetry.a365.core.channel import Channel
from microsoft.opentelemetry.a365.core.invoke_agent_details import InvokeAgentScopeDetails
from microsoft.opentelemetry.a365.core.invoke_agent_scope import InvokeAgentScope
from microsoft.opentelemetry.a365.core.middleware.baggage_builder import BaggageBuilder
from microsoft.opentelemetry.a365.core.models.caller_details import CallerDetails
from microsoft.opentelemetry.a365.core.models.service_endpoint import ServiceEndpoint
from microsoft.opentelemetry.a365.core.models.user_details import UserDetails
from microsoft.opentelemetry.a365.core.request import Request

from app.a365.token_store import token_store
from app.config import Settings

logger = logging.getLogger(__name__)

# Teams is the channel this agent is reached through; the exporter records it verbatim.
CHANNEL_NAME = "msteams"

# Configuration is global process state, and the module can be imported more than once.
_initialized = False


def _apply_log_level(settings: Settings) -> None:
    """Honour A365_OBSERVABILITY_LOG_LEVEL, which the Python distro itself ignores.

    The exporter logs a successful export at DEBUG and only failures at ERROR, so
    without this there is no positive confirmation that spans left the process.
    """
    levels = {
        "debug": logging.DEBUG,
        "info": logging.INFO,
        "warn": logging.WARNING,
        "warning": logging.WARNING,
        "error": logging.ERROR,
    }
    named = [
        levels[part.strip().lower()]
        for part in settings.a365_observability_log_level.split("|")
        if part.strip().lower() in levels
    ]
    if not named:
        return

    logging.getLogger("microsoft.opentelemetry").setLevel(min(named))


def init_observability(settings: Settings) -> bool:
    """Configure the Agent 365 exporter. Returns True when it was wired."""
    global _initialized

    if _initialized:
        return True

    if not settings.a365_configured:
        logger.warning(
            "Agent 365 is not configured (run 'a365 setup all') - observability is off."
        )
        return False

    # Applied before the distro call so its own startup diagnostics are visible too.
    _apply_log_level(settings)

    # A365 Observability - added by instrument-observability skill
    # A365 auth mode: obo, custom engine. The token comes straight from the Azure Bot
    # OAuth connection, which is scoped to the observability API - see
    # app/main.py::_publish_observability_token.
    use_microsoft_opentelemetry(
        enable_a365=True,
        # Both flags are required: enable_a365 only registers the span processors.
        a365_enable_observability_exporter=settings.enable_a365_observability_exporter,
        # Called on the exporter's flush thread, which has no turn context.
        a365_token_resolver=token_store.get,
        # The delegated route. Its S2S counterpart takes application tokens only and
        # refuses a delegated one, so the two are not interchangeable.
        a365_use_s2s_endpoint=False,
        # Metrics add a noisy histogram dump on a timer without helping the demo.
        disable_metrics=True,
        # Records prompts and tool arguments on the spans so Defender shows content.
        enable_sensitive_data=True,
        # The OpenAI Agents SDK is not used here; without this the distro tries to
        # import it on every start and logs a traceback. Agent Framework and Semantic
        # Kernel are off for a different reason: their span enrichers register first
        # and make the distro skip the LangChain enricher, which is the one that maps
        # this agent's messages and conversation id into the shape A365 expects.
        instrumentation_options={
            "openai_agents": {"enabled": False},
            "agent_framework": {"enabled": False},
            "semantic_kernel": {"enabled": False},
        },
    )

    logger.info(
        "A365 observability wired for agent %s (exporter %s).",
        settings.observability_agent_id,
        "enabled" if settings.enable_a365_observability_exporter else "disabled",
    )
    _initialized = True
    return True


def build_agent_details(settings: Settings) -> AgentDetails:
    return AgentDetails(
        # The exporter reads this back off the span as gen_ai.agent.id and builds the
        # export URL from it, so for a custom engine agent it must be the app
        # registration's client id. See Settings.observability_agent_id.
        agent_id=settings.observability_agent_id,
        agent_name=settings.a365_agent_name or "Microsoft Learn agent",
        agent_description=settings.a365_agent_description
        or "Microsoft Learn research agent",
        agent_blueprint_id=settings.a365_blueprint_id,
        tenant_id=settings.a365_tenant_id,
    )


def build_caller_details(user: dict[str, str]) -> CallerDetails:
    # Required for traces to surface in the Microsoft Admin Center: without caller
    # details the spans are accepted by the API but never shown.
    return CallerDetails(
        user_details=UserDetails(
            user_id=user.get("id") or "unknown",
            user_name=user.get("name") or "unknown",
            user_email=user.get("email") or "",
        )
    )


def build_baggage_scope(settings: Settings, user: dict[str, str], conversation_id: str):
    """Baggage carries the identity dimensions the exporter partitions spans by.

    Spans emitted outside an active baggage scope are dropped by the exporter with
    "Partitioned into 0 identity groups", so every turn must run inside one.
    """
    return (
        BaggageBuilder()
        .tenant_id(settings.a365_tenant_id)
        # Must match build_agent_details: auto-instrumented spans (LangChain, the LLM
        # call) take their gen_ai.agent.id from baggage rather than from the invoke
        # scope, and the exporter partitions on that attribute. If the two disagree the
        # turn splits into two identity groups and only one of them has a token.
        .agent_id(settings.observability_agent_id)
        .agent_name(settings.a365_agent_name or "Microsoft Learn agent")
        .agent_blueprint_id(settings.a365_blueprint_id)
        .conversation_id(conversation_id)
        .session_id(conversation_id)
        .user_id(user.get("id") or "")
        .user_name(user.get("name") or "")
        .user_email(user.get("email") or "")
        .channel_name(CHANNEL_NAME)
        .build()
    )


def start_invoke_scope(
    settings: Settings, user: dict[str, str], message: str, conversation_id: str
) -> InvokeAgentScope:
    # The blueprint id is a GUID and therefore always URI-safe; the display name is not.
    endpoint = ServiceEndpoint(hostname=f"{settings.a365_blueprint_id}.agent.invalid")

    return InvokeAgentScope.start(
        Request(
            content=message,
            session_id=conversation_id,
            conversation_id=conversation_id,
            channel=Channel(name=CHANNEL_NAME),
        ),
        InvokeAgentScopeDetails(endpoint=endpoint),
        build_agent_details(settings),
        build_caller_details(user),
    )
