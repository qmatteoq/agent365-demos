"""The on-behalf-of token chain that authenticates the observability exporter.

The export route authorises on the token's ``azp``: it must equal the agent id in the
URL. That single rule is what shapes this whole module.

    POST /observability/tenants/{tenant}/otlp/agents/{agentId}/traces

A plain on-behalf-of exchange performed by the bot channel app returns a token with
``azp`` = the bot app, and the route answers **403**. So does a token minted by the
blueprint. Only a token whose ``azp`` is the agent identity is accepted -- verified by
probing the live endpoint with one token against all three ids.

The token also has to name the human, because this agent has a user on every turn and a
trace that cannot be traced back to the caller is the wrong shape for a Teams agent. Both
requirements are met by ending the chain in an exchange performed *by* the agent identity
*for* the user:

    Hop 1  bot channel app + user's Teams SSO token (on-behalf-of)
             -> token for the blueprint's access_agent_as_user scope
    Hop 2  blueprint + client secret + fmi_path=<agent identity>
             -> token-exchange assertion, proving it owns the agent identity
    Hop 3  agent identity + that assertion as its client credential
             + the hop 1 token as the user assertion (on-behalf-of)
             -> Observability API token, azp = agent identity, sub = the user

Hop 1 exists because the Azure Bot OAuth connection issues a token whose audience is the
channel app, and hop 3 only accepts an assertion issued to the blueprint family.

The blueprint never performs the final exchange itself: Entra bars agentic applications
from client-credentials flows (AADSTS82001). It only proves ownership at hop 2.

This is the same chain the .NET Teams agent uses (``Agent365/WorkIqTokenService.cs``),
which is also how its WorkIQ tokens are minted.

MSAL Python 1.37 supports ``fmi_path`` on ``acquire_token_for_client`` directly, so no
hand-rolled token requests are needed.

See https://learn.microsoft.com/entra/agent-id/agent-on-behalf-of-oauth-flow
"""

from __future__ import annotations

import asyncio
import logging
import time

import msal

from app.a365.token_store import token_store
from app.config import Settings

logger = logging.getLogger(__name__)

TOKEN_EXCHANGE_SCOPE = "api://AzureADTokenExchange/.default"

# The Agent 365 Observability API. A named delegated scope, not /.default: the token is
# delegated, so it carries scopes rather than roles.
OBSERVABILITY_SCOPE = (
    "api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite"
)

# Refresh a little before the token actually expires so a slow exchange never leaves the
# exporter without one.
EXPIRY_MARGIN_SECONDS = 5 * 60


class ObservabilityTokenService:
    """Mints the Observability API token for a turn and keeps it in the token store.

    The exporter flushes on a background thread with no turn context, so it cannot run
    this chain itself: the token has to be deposited while the user's assertion is still
    in hand.
    """

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._authority = f"https://login.microsoftonline.com/{settings.a365_tenant_id}"
        self._expires_at = 0.0
        self._lock = asyncio.Lock()

    @property
    def _blueprint_user_scope(self) -> str:
        """The scope the blueprint exposes so a child agent identity can act for the user."""
        return f"api://{self._settings.a365_blueprint_id}/access_agent_as_user"

    # MSAL is synchronous and does blocking network I/O, so every call is pushed to a
    # worker thread rather than run on the event loop that is serving Teams turns.
    def _acquire(self, user_assertion: str) -> tuple[str, int] | None:
        # Hop 1 - re-target the user's Teams token from the bot app to the blueprint.
        bot = msal.ConfidentialClientApplication(
            client_id=self._settings.bot_client_id,
            client_credential=self._settings.bot_client_secret,
            authority=self._authority,
        )
        hop1 = bot.acquire_token_on_behalf_of(
            user_assertion=user_assertion,
            scopes=[self._blueprint_user_scope],
        )
        blueprint_user_token = hop1.get("access_token")
        if not blueprint_user_token:
            logger.warning(
                "Observability token hop 1 (user token -> blueprint) failed: %s - %s",
                hop1.get("error"),
                hop1.get("error_description"),
            )
            return None

        # Hop 2 - the blueprint proves it owns the agent identity through fmi_path.
        blueprint = msal.ConfidentialClientApplication(
            client_id=self._settings.a365_blueprint_id,
            client_credential=self._settings.a365_blueprint_secret,
            authority=self._authority,
        )
        hop2 = blueprint.acquire_token_for_client(
            scopes=[TOKEN_EXCHANGE_SCOPE],
            fmi_path=self._settings.a365_agent_id,
        )
        assertion = hop2.get("access_token")
        if not assertion:
            logger.warning(
                "Observability token hop 2 (token exchange) failed: %s - %s",
                hop2.get("error"),
                hop2.get("error_description"),
            )
            return None

        # Hop 3 - the agent identity performs the final on-behalf-of exchange. This is
        # the hop that makes azp the agent identity, which the export route requires.
        agent = msal.ConfidentialClientApplication(
            client_id=self._settings.a365_agent_id,
            client_credential={"client_assertion": assertion},
            authority=self._authority,
        )
        hop3 = agent.acquire_token_on_behalf_of(
            user_assertion=blueprint_user_token,
            scopes=[OBSERVABILITY_SCOPE],
        )
        token = hop3.get("access_token")
        if not token:
            logger.warning(
                "Observability token hop 3 (observability API) failed: %s - %s",
                hop3.get("error"),
                hop3.get("error_description"),
            )
            return None

        return token, int(hop3.get("expires_in", 3600))

    async def publish(self, user_assertion: str) -> bool:
        """Mint a token for this turn and deposit it, unless the cached one is still good."""
        if not user_assertion:
            logger.warning("No user token this turn; traces cannot be exported.")
            return False

        async with self._lock:
            if time.monotonic() < self._expires_at:
                return True

            try:
                result = await asyncio.to_thread(self._acquire, user_assertion)
            except Exception:
                logger.warning("Observability token acquisition raised.", exc_info=True)
                return False

            if result is None:
                return False

            token, expires_in = result
            token_store.set(
                self._settings.a365_agent_id, self._settings.a365_tenant_id, token
            )
            self._expires_at = time.monotonic() + max(
                expires_in - EXPIRY_MARGIN_SECONDS, 60
            )
            logger.info(
                "Registered an Agent 365 observability token for agent %s.",
                self._settings.a365_agent_id,
            )
            return True
