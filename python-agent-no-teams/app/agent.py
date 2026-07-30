"""The Microsoft Learn research agent: LangChain + Azure OpenAI + Microsoft Learn MCP."""

from __future__ import annotations

import logging

from azure.identity import (
    AzureCliCredential,
    DefaultAzureCredential,
    get_bearer_token_provider,
)
from langchain.agents import create_agent
from langchain_mcp_adapters.client import MultiServerMCPClient
from langchain_openai import AzureChatOpenAI
from langgraph.checkpoint.memory import InMemorySaver

from app.config import Settings

logger = logging.getLogger(__name__)

AZURE_OPENAI_SCOPE = "https://cognitiveservices.azure.com/.default"

SYSTEM_PROMPT = (
    "You are a Microsoft ecosystem research assistant. You specialise in answering questions about "
    "Microsoft products and technologies - Azure, Microsoft 365, Power Platform, .NET, Windows, "
    "Microsoft Entra, Copilot, Dynamics 365 and related services.\n"
    "Always use the Microsoft Learn MCP tools to search and fetch authoritative documentation before "
    "answering, even when you believe you already know the answer. Ground every factual statement in "
    "the content you retrieved and cite the source URLs at the end of your answer.\n"
    "If the documentation does not cover the question, say so explicitly instead of guessing. "
    "Keep answers clear, concise and structured."
)


def _build_credential(settings: Settings):
    if settings.azure_openai_use_managed_identity:
        return DefaultAzureCredential()

    # Locally there is no IMDS endpoint, so managed identity is skipped entirely and the
    # tenant is pinned on the Azure CLI credential.
    return AzureCliCredential(tenant_id=settings.azure_openai_tenant_id)


class LearnAgent:
    """Wraps the LangChain agent and the Microsoft Learn MCP connection."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._mcp_client: MultiServerMCPClient | None = None
        self._agent = None
        self._tool_names: list[str] = []

    @property
    def tool_names(self) -> list[str]:
        return self._tool_names

    async def start(self) -> None:
        """Connect to the Microsoft Learn MCP server and build the agent graph."""
        self._mcp_client = MultiServerMCPClient(
            {
                "microsoft_learn": {
                    "transport": "streamable_http",
                    "url": self._settings.learn_mcp_endpoint,
                }
            }
        )

        tools = await self._mcp_client.get_tools()
        self._tool_names = [tool.name for tool in tools]
        logger.info(
            "Connected to Microsoft Learn MCP server, %d tool(s): %s",
            len(tools),
            ", ".join(self._tool_names),
        )

        credential = _build_credential(self._settings)
        token_provider = get_bearer_token_provider(credential, AZURE_OPENAI_SCOPE)

        model = AzureChatOpenAI(
            azure_endpoint=self._settings.azure_openai_endpoint,
            azure_deployment=self._settings.azure_openai_deployment,
            api_version=self._settings.azure_openai_api_version,
            azure_ad_token_provider=token_provider,
            temperature=0,
        )

        # InMemorySaver keeps one conversation per thread_id, which is what gives the
        # chat page its multi-turn memory. It is process-local by design: restarting the
        # app clears every conversation.
        self._agent = create_agent(
            model=model,
            tools=tools,
            system_prompt=SYSTEM_PROMPT,
            checkpointer=InMemorySaver(),
        )

    async def ask(self, session_id: str, message: str) -> str:
        if self._agent is None:
            raise RuntimeError("The agent has not been started.")

        result = await self._agent.ainvoke(
            {"messages": [{"role": "user", "content": message}]},
            config={"configurable": {"thread_id": session_id}},
        )

        return result["messages"][-1].text
