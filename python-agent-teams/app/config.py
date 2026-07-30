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

    # The Bot Framework channel posts to /api/messages on this port. 3978 is the
    # convention the Azure Bot registration and the dev tunnel are configured for.
    port: int = 3978


settings = Settings()  # type: ignore[call-arg]
