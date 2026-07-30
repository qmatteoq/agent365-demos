"""Configuration for the Microsoft Learn agent, loaded from environment / .env."""

from pydantic import Field
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

    host: str = "127.0.0.1"
    port: int = 8000

    # ── Agent 365 ────────────────────────────────────────────────────────────
    # A365 Observability — best-effort instrumentation (verify against official sample)
    # These are written by `a365 setup all`; the agent id is the agent *identity*
    # (the governed principal), which is what Defender reports against.
    a365_agent_id: str = Field("", validation_alias="AGENT365OBSERVABILITY__AGENTID")
    a365_agent_name: str = Field("", validation_alias="AGENT365OBSERVABILITY__AGENTNAME")
    a365_agent_description: str = Field(
        "", validation_alias="AGENT365OBSERVABILITY__AGENTDESCRIPTION"
    )
    a365_tenant_id: str = Field("", validation_alias="AGENT365OBSERVABILITY__TENANTID")
    a365_blueprint_id: str = Field(
        "", validation_alias="AGENT365OBSERVABILITY__AGENTBLUEPRINTID"
    )
    # Blueprint client secret — hop 1 of the agent on-behalf-of chain.
    a365_blueprint_secret: str = Field(
        "", validation_alias="AGENT365OBSERVABILITY__CLIENTSECRET"
    )
    enable_a365_observability_exporter: bool = Field(
        False, validation_alias="ENABLE_A365_OBSERVABILITY_EXPORTER"
    )

    # Interactive sign-in app. An agent blueprint cannot run /authorize flows, so a
    # dedicated web client signs the user in and asks for the blueprint's
    # access_agent_as_user scope. That token is the assertion for the OBO chain.
    webclient_client_id: str = Field("", validation_alias="A365_WEBCLIENT_CLIENT_ID")
    webclient_client_secret: str = Field(
        "", validation_alias="A365_WEBCLIENT_CLIENT_SECRET"
    )
    webclient_redirect_uri: str = Field(
        "http://localhost:8000/signin-oidc",
        validation_alias="A365_WEBCLIENT_REDIRECT_URI",
    )
    session_secret: str = Field("dev-only-insecure", validation_alias="SESSION_SECRET")

    # The Python distro ignores A365_OBSERVABILITY_LOG_LEVEL (it is a Node.js-only
    # variable), so the app applies it to the distro's loggers itself. Pipe-separated,
    # e.g. "debug|info|warn|error"; the most verbose level named wins.
    a365_observability_log_level: str = Field(
        "", validation_alias="A365_OBSERVABILITY_LOG_LEVEL"
    )

    @property
    def agent_user_scope(self) -> str:
        """Scope the web client requests so the user token targets the blueprint."""
        return f"api://{self.a365_blueprint_id}/access_agent_as_user"

    @property
    def a365_configured(self) -> bool:
        return bool(
            self.a365_agent_id
            and self.a365_tenant_id
            and self.a365_blueprint_id
            and self.a365_blueprint_secret
        )

    @property
    def sign_in_configured(self) -> bool:
        return bool(self.webclient_client_id and self.webclient_client_secret)


settings = Settings()  # type: ignore[call-arg]
