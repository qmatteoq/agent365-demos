"""The service-to-service token chain that authenticates the observability exporter.

The observability backend binds the caller to the agent being written to: the token's
principal has to be the *agent identity* that appears in the export route. A delegated
user token cannot satisfy that -- its principal is the human -- so the backend answers 403.

    Hop 1  blueprint + client secret + fmi_path=<agent identity>
             -> assertion (audience api://AzureADTokenExchange)
    Hop 2  agent identity authenticates with that assertion (client_credentials)
             -> Observability API token

This is the same chain the .NET Teams agent uses, and it is deliberately *not* the
on-behalf-of chain used by the two web-hosted agents in this repo: a Teams agent has no
interactive web sign-in, so there is no user assertion to exchange.

MSAL Python 1.37 supports the ``fmi_path`` argument on ``acquire_token_for_client``
directly, so no hand-rolled token requests are needed.

See https://learn.microsoft.com/entra/agent-id/agent-on-behalf-of-oauth-flow
"""

from __future__ import annotations

import asyncio
import logging

import msal

from app.a365.token_store import token_store
from app.config import Settings

logger = logging.getLogger(__name__)

TOKEN_EXCHANGE_SCOPE = "api://AzureADTokenExchange/.default"

# The Agent 365 Observability API -- the resource the agent posts its OTLP traces to.
OBSERVABILITY_SCOPE = "api://9b975845-388f-4429-889e-eab1ef63949c/.default"

# Observability tokens live for roughly an hour. Refresh early enough that a slow or
# failed attempt still has time to retry before the cached token expires.
REFRESH_SECONDS = 50 * 60
RETRY_SECONDS = 60


class ObservabilityTokenService:
    """Keeps a current Observability API token in the token store."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._authority = (
            f"https://login.microsoftonline.com/{settings.a365_tenant_id}"
        )
        self._task: asyncio.Task | None = None

    # MSAL is synchronous and does blocking network I/O, so every call is pushed to a
    # worker thread rather than run on the event loop that is serving Teams turns.
    def _acquire(self) -> str | None:
        blueprint = msal.ConfidentialClientApplication(
            client_id=self._settings.a365_blueprint_id,
            client_credential=self._settings.a365_blueprint_secret,
            authority=self._authority,
        )
        exchange = blueprint.acquire_token_for_client(
            scopes=[TOKEN_EXCHANGE_SCOPE],
            fmi_path=self._settings.a365_agent_id,
        )
        assertion = exchange.get("access_token")
        if not assertion:
            logger.warning(
                "Observability token hop 1 (token exchange) failed: %s - %s",
                exchange.get("error"),
                exchange.get("error_description"),
            )
            return None

        agent = msal.ConfidentialClientApplication(
            client_id=self._settings.a365_agent_id,
            client_credential={"client_assertion": assertion},
            authority=self._authority,
        )
        result = agent.acquire_token_for_client(scopes=[OBSERVABILITY_SCOPE])
        token = result.get("access_token")
        if not token:
            logger.warning(
                "Observability token hop 2 (observability API) failed: %s - %s",
                result.get("error"),
                result.get("error_description"),
            )
            return None

        return token

    async def refresh_once(self) -> bool:
        try:
            token = await asyncio.to_thread(self._acquire)
        except Exception:
            logger.warning("Observability token acquisition raised.", exc_info=True)
            return False

        if not token:
            return False

        token_store.set(
            self._settings.a365_agent_id, self._settings.a365_tenant_id, token
        )
        logger.info(
            "Registered an Agent 365 observability token for agent %s.",
            self._settings.a365_agent_id,
        )
        return True

    async def _loop(self, initial_ok: bool) -> None:
        ok = initial_ok
        while True:
            # Sleep first: start() has already acquired the current token.
            try:
                await asyncio.sleep(REFRESH_SECONDS if ok else RETRY_SECONDS)
            except asyncio.CancelledError:
                break
            ok = await self.refresh_once()

    async def start(self) -> None:
        """Acquire a token now, then keep it fresh in the background.

        The first acquisition is awaited so the very first turn already has a token; the
        exporter drops spans it cannot authenticate rather than queueing them.
        """
        ok = await self.refresh_once()
        self._task = asyncio.create_task(self._loop(ok))

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except (asyncio.CancelledError, Exception):
                pass
            self._task = None
