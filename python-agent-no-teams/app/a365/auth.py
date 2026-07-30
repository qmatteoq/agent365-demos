"""Entra ID sign-in for the web UI.

An agent blueprint cannot run interactive ``/authorize`` flows, so the app signs users
in with its own client app registration and requests the blueprint's
``access_agent_as_user`` scope. The resulting token is the user assertion for the agent
on-behalf-of chain in :mod:`app.a365.obo`.

Session state (the MSAL token cache and the signed-in user's claims) is kept
server-side and referenced by an opaque cookie, so no token ever reaches the browser.
It is process-local by design: restarting the app signs everyone out.
"""

from __future__ import annotations

import logging
import secrets
import threading
from typing import Any

import msal

from app.config import Settings

logger = logging.getLogger(__name__)

SESSION_COOKIE = "learn_agent_sid"


class _Session:
    __slots__ = ("cache", "user", "flow")

    def __init__(self) -> None:
        self.cache = msal.SerializableTokenCache()
        self.user: dict[str, str] | None = None
        self.flow: dict[str, Any] | None = None


class SignInService:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._sessions: dict[str, _Session] = {}
        self._lock = threading.Lock()

    # ── session plumbing ────────────────────────────────────────────────────
    def new_session_id(self) -> str:
        sid = secrets.token_urlsafe(32)
        with self._lock:
            self._sessions[sid] = _Session()
        return sid

    def _session(self, sid: str | None) -> _Session | None:
        if not sid:
            return None
        with self._lock:
            return self._sessions.get(sid)

    def ensure_session(self, sid: str | None) -> tuple[str, bool]:
        """Return (session_id, created). Creates one when the cookie is missing."""
        if self._session(sid) is not None:
            return sid, False  # type: ignore[return-value]
        return self.new_session_id(), True

    def sign_out(self, sid: str | None) -> None:
        if not sid:
            return
        with self._lock:
            self._sessions.pop(sid, None)

    def current_user(self, sid: str | None) -> dict[str, str] | None:
        session = self._session(sid)
        return session.user if session else None

    # ── auth code flow ──────────────────────────────────────────────────────
    def _msal_app(self, session: _Session) -> msal.ConfidentialClientApplication:
        return msal.ConfidentialClientApplication(
            client_id=self._settings.webclient_client_id,
            client_credential=self._settings.webclient_client_secret,
            authority=(
                f"https://login.microsoftonline.com/{self._settings.a365_tenant_id}"
            ),
            token_cache=session.cache,
        )

    def begin_sign_in(self, sid: str) -> str:
        session = self._session(sid)
        if session is None:
            raise RuntimeError("Unknown session.")

        # initiate_auth_code_flow handles state, nonce and PKCE for us; the flow dict
        # has to survive until the redirect comes back, hence the server-side session.
        flow = self._msal_app(session).initiate_auth_code_flow(
            scopes=[self._settings.agent_user_scope],
            redirect_uri=self._settings.webclient_redirect_uri,
        )
        session.flow = flow
        return flow["auth_uri"]

    def complete_sign_in(self, sid: str, query_params: dict[str, Any]) -> dict[str, str]:
        session = self._session(sid)
        if session is None or session.flow is None:
            raise RuntimeError("Sign-in did not start in this session.")

        result = self._msal_app(session).acquire_token_by_auth_code_flow(
            session.flow, query_params
        )
        session.flow = None

        if "error" in result:
            raise RuntimeError(
                f"{result.get('error')}: {result.get('error_description', '')}"
            )

        claims = result.get("id_token_claims", {})
        session.user = {
            "oid": claims.get("oid", ""),
            "name": claims.get("name", ""),
            "username": claims.get("preferred_username", ""),
        }
        return session.user

    def acquire_user_assertion(self, sid: str | None) -> str | None:
        """Token whose audience is the agent blueprint -- the OBO user assertion."""
        session = self._session(sid)
        if session is None or session.user is None:
            return None

        app = self._msal_app(session)
        accounts = app.get_accounts()
        if not accounts:
            return None

        result = app.acquire_token_silent(
            scopes=[self._settings.agent_user_scope], account=accounts[0]
        )
        if not result or "access_token" not in result:
            logger.warning(
                "Could not silently acquire the user assertion for the blueprint: %s",
                (result or {}).get("error_description", "no cached token"),
            )
            return None

        return result["access_token"]
