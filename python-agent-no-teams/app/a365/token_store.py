"""Holds the Observability API token for each (agent_id, tenant_id) pair.

The A365 exporter flushes on a background thread with no user context, so the token has
to be deposited here by the request path -- which does have the signed-in user's
assertion -- and read back synchronously by the exporter's token resolver.

The resolver signature the distro expects is ``(agent_id, tenant_id) -> str | None``
and it is called from the exporter's flush thread, hence the lock.
"""

from __future__ import annotations

import threading


class ObservabilityTokenStore:
    def __init__(self) -> None:
        self._tokens: dict[str, str] = {}
        self._lock = threading.Lock()

    @staticmethod
    def _key(agent_id: str, tenant_id: str) -> str:
        return f"{agent_id.lower()}|{tenant_id.lower()}"

    def set(self, agent_id: str, tenant_id: str, token: str) -> None:
        with self._lock:
            self._tokens[self._key(agent_id, tenant_id)] = token

    def get(self, agent_id: str, tenant_id: str) -> str | None:
        """Token resolver handed to ``use_microsoft_opentelemetry``."""
        with self._lock:
            return self._tokens.get(self._key(agent_id, tenant_id))


# Single process-wide store shared by the request path and the exporter.
token_store = ObservabilityTokenStore()
