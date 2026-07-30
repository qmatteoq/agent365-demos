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
        # The blueprint is both the observability client and hop 1 of the token chain,
        # so the CLI writes the same GUID to two keys. Prefer the explicit one.
        return (
            self.agent365observability__agentblueprintid
            or self.agent365observability__clientid
        )

    @property
    def a365_blueprint_secret(self) -> str:
        return self.agent365observability__clientsecret

    @property
    def a365_configured(self) -> bool:
        """True when every value the S2S token chain needs is present."""
        return bool(
            self.a365_agent_id
            and self.a365_tenant_id
            and self.a365_blueprint_id
            and self.a365_blueprint_secret
        )


settings = Settings()  # type: ignore[call-arg]
