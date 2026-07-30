"""The Entra "agent on-behalf-of" token chain.

    Hop 1  blueprint + client secret + fmi_path=<agent identity>
             -> T1 (token exchange assertion)
    Hop 2  agent identity + T1 (client_assertion) + user token (assertion)
             -> downstream resource token

The resulting token belongs to the *governed agent identity acting for the user*, which
is what the Agent 365 observability backend requires: it binds the caller to the agent
in the export route. A plain delegated user token is rejected with HTTP 403 because its
principal is the human, not the agent.

Both hops are issued with a direct HTTP POST rather than MSAL, because MSAL Python does
not serialise the ``fmi_path`` parameter.

See https://learn.microsoft.com/entra/agent-id/agent-on-behalf-of-oauth-flow
"""

from __future__ import annotations

import asyncio
import hashlib
import logging
import time

import httpx

logger = logging.getLogger(__name__)

TOKEN_EXCHANGE_SCOPE = "api://AzureADTokenExchange/.default"

# The Agent 365 Observability API -- the resource the agent posts its OTLP traces to.
OBSERVABILITY_SCOPE = "api://9b975845-388f-4429-889e-eab1ef63949c/.default"

# Refresh a few minutes early so a request never races the expiry.
_EXPIRY_SKEW_SECONDS = 300


class _CachedToken:
    __slots__ = ("token", "expires_at")

    def __init__(self, token: str, expires_in: int) -> None:
        self.token = token
        self.expires_at = time.time() + expires_in

    @property
    def is_expiring(self) -> bool:
        return time.time() >= self.expires_at - _EXPIRY_SKEW_SECONDS


class AgentOboTokenService:
    """Exchanges a signed-in user's token for a token issued to the agent identity."""

    def __init__(
        self,
        tenant_id: str,
        blueprint_client_id: str,
        blueprint_client_secret: str,
        agent_identity_client_id: str,
    ) -> None:
        self._tenant_id = tenant_id
        self._blueprint_client_id = blueprint_client_id
        self._blueprint_client_secret = blueprint_client_secret
        self._agent_identity_client_id = agent_identity_client_id

        self._cache: dict[str, _CachedToken] = {}
        self._lock = asyncio.Lock()
        self._http = httpx.AsyncClient(timeout=30.0)

    async def aclose(self) -> None:
        await self._http.aclose()

    async def get_agent_token(
        self, user_assertion: str, resource_scope: str
    ) -> str | None:
        """Return a resource token for ``resource_scope``, or None if any hop fails."""
        digest = hashlib.sha256(user_assertion.encode("utf-8")).hexdigest()[:16]
        cache_key = f"{resource_scope}|{digest}"

        async with self._lock:
            cached = self._cache.get(cache_key)
            if cached is not None and not cached.is_expiring:
                return cached.token

            try:
                t1 = await self._acquire_exchange_token()
                if t1 is None:
                    return None

                resource_token = await self._acquire_resource_token(
                    t1, user_assertion, resource_scope
                )
                if resource_token is None:
                    return None

                self._cache[cache_key] = resource_token
                return resource_token.token
            except Exception:
                logger.warning(
                    "Agent OBO token acquisition failed for scope %s.",
                    resource_scope,
                    exc_info=True,
                )
                return None

    async def _acquire_exchange_token(self) -> _CachedToken | None:
        """Hop 1 -- the blueprint asks for a token exchange assertion scoped to the
        child agent identity via fmi_path."""
        cached = self._cache.get("__t1")
        if cached is not None and not cached.is_expiring:
            return cached

        token = await self._post_token_request(
            {
                "client_id": self._blueprint_client_id,
                "client_secret": self._blueprint_client_secret,
                "scope": TOKEN_EXCHANGE_SCOPE,
                "fmi_path": self._agent_identity_client_id,
                "grant_type": "client_credentials",
            },
            stage="hop 1 (token exchange)",
        )
        if token is not None:
            self._cache["__t1"] = token
        return token

    async def _acquire_resource_token(
        self, exchange_token: _CachedToken, user_assertion: str, resource_scope: str
    ) -> _CachedToken | None:
        """Hop 2 -- the agent identity performs the OBO exchange, presenting T1 as its
        client assertion and the signed-in user's token as the user assertion."""
        return await self._post_token_request(
            {
                "client_id": self._agent_identity_client_id,
                "scope": resource_scope,
                "client_assertion_type": (
                    "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
                ),
                "client_assertion": exchange_token.token,
                "grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer",
                "assertion": user_assertion,
                "requested_token_use": "on_behalf_of",
            },
            stage=f"hop 2 ({resource_scope})",
        )

    async def _post_token_request(
        self, form: dict[str, str], stage: str
    ) -> _CachedToken | None:
        url = f"https://login.microsoftonline.com/{self._tenant_id}/oauth2/v2.0/token"
        response = await self._http.post(url, data=form)

        if response.status_code >= 400:
            logger.warning(
                "Agent OBO %s failed: %d %s", stage, response.status_code, response.text
            )
            return None

        payload = response.json()
        access_token = payload.get("access_token")
        if not access_token:
            logger.warning("Agent OBO %s returned no access_token.", stage)
            return None

        return _CachedToken(access_token, int(payload.get("expires_in", 3600)))
