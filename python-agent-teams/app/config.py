"""Configuration for the Microsoft Learn Teams agent.

Only the *application's own* settings live here. The Microsoft 365 Agents SDK reads its
own configuration straight from the environment via ``load_configuration_from_env`` using
the ``CONNECTIONS__…`` / ``AGENTAPPLICATION__…`` double-underscore convention, so those
keys are deliberately absent from this model.
"""

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    azure_openai_endpoint: str
    azure_openai_deployment: str = "gpt-4.1"
    azure_openai_api_version: str = "2024-10-21"

    # Tenant that owns the Azure OpenAI resource. A token issued by a different tenant
    # makes Azure OpenAI answer HTTP 400 "Tenant provided in token does not match
    # resource token", so the credential is pinned to it explicitly.
    azure_openai_tenant_id: str | None = None

    # Managed identity only exists on Azure infrastructure; locally there is no IMDS
    # endpoint, so the Azure CLI credential is used instead.
    azure_openai_use_managed_identity: bool = False

    learn_mcp_endpoint: str = "https://learn.microsoft.com/api/mcp"

    # The Bot Framework channel posts to /api/messages on this port. 3979 keeps this
    # agent from colliding with the .NET Teams agent, which uses 3978.
    port: int = 3979

    # -- Agent 365 observability --------------------------------------------------
    # Written by `a365 setup all`. Names follow the CLI's AGENT365OBSERVABILITY__*
    # convention so the file it stamps out works without renaming anything.
    enable_a365_observability_exporter: bool = False
    agent365observability__agentid: str = ""
    agent365observability__agentname: str = ""
    agent365observability__agentdescription: str = ""
    agent365observability__tenantid: str = ""
    agent365observability__agentblueprintid: str = ""
    agent365observability__clientid: str = ""
    agent365observability__clientsecret: str = ""
    a365_observability_log_level: str = "warn|error"

    # -- Bot channel app ----------------------------------------------------------
    # The Agents SDK reads these from the environment itself; the client id is mirrored
    # into this model because it *is* the observability agent id (see below).
    connections__service_connection__settings__clientid: str = ""

    # Short aliases: the stamped names are unwieldy at every call site.
    @property
    def a365_agent_id(self) -> str:
        return self.agent365observability__agentid

    @property
    def a365_agent_name(self) -> str:
        return self.agent365observability__agentname

    @property
    def a365_agent_description(self) -> str:
        return self.agent365observability__agentdescription

    @property
    def a365_tenant_id(self) -> str:
        return self.agent365observability__tenantid

    @property
    def a365_blueprint_id(self) -> str:
        # Recorded for the record and for the README; the observability path does not
        # use it. The blueprint is the agentic parent, and a custom engine agent never
        # authenticates through it.
        return (
            self.agent365observability__agentblueprintid
            or self.agent365observability__clientid
        )

    @property
    def bot_client_id(self) -> str:
        """The bot channel app registration -- and the observability agent id.

        Read from the Agents SDK's own connection settings rather than duplicated, so
        the two can never drift apart.
        """
        return self.connections__service_connection__settings__clientid

    @property
    def observability_agent_id(self) -> str:
        """The id the exporter puts in the export URL and in ``gen_ai.agent.id``.

        For a *custom engine* agent this is the app registration's client id, not the
        Agent 365 agent identity. The exporter reads ``gen_ai.agent.id`` off the span,
        uses it to build

            POST /observability/tenants/{tenant}/otlp/agents/{agentId}/traces

        and looks the token up under the same value, so this id, the token's ``azp`` and
        the token store key are all one and the same. A mismatch is HTTP 403.

        The Agent 365 agent identity in ``a365_agent_id`` is deliberately *not* used
        here: a custom engine agent's turns carry no agentic identity, so it has no
        credential with which to make itself the ``azp``.

        https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup
        """
        return self.bot_client_id

    @property
    def a365_configured(self) -> bool:
        """True when every value the observability path needs is present."""
        return bool(
            self.a365_tenant_id
            and self.bot_client_id
        )


settings = Settings()  # type: ignore[call-arg]
