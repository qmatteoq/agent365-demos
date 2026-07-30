"""The Microsoft Learn research agent: LangChain + Azure OpenAI + Microsoft Learn MCP.

This module is deliberately free of any Teams or Agents SDK types. It is the same agent
core used by the non-Teams Python agent in this repo, which keeps the interesting diff
between the two samples confined to the hosting layer.
"""

from __future__ import annotations

import asyncio
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
    "Keep answers clear, concise and structured.\n"
    "You are talking to the user inside Microsoft Teams, so format answers with short paragraphs "
    "and bullet points rather than long prose, and keep them under roughly 300 words."
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

        # The Agents SDK hands us a running event loop only once a turn arrives, so the
        # MCP handshake happens on first use rather than at import. The lock makes the
        # concurrent first-turn case wait for one handshake instead of racing several.
        self._lock = asyncio.Lock()

        # Bumped by /reset. It is part of the LangGraph thread id, so incrementing it
        # starts a brand new conversation thread while leaving the old one to be garbage
        # collected -- simpler and safer than reaching into the checkpointer's internals.
        self._generations: dict[str, int] = {}

    @property
    def tool_names(self) -> list[str]:
        return self._tool_names

    @property
    def started(self) -> bool:
        return self._agent is not None

    async def start(self) -> None:
        """Connect to the Microsoft Learn MCP server and build the agent graph."""
        async with self._lock:
            if self._agent is not None:
                return

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

            # InMemorySaver keeps one conversation per thread_id, which is what gives each
            # Teams chat its multi-turn memory. It is process-local by design: restarting
            # the agent clears every conversation.
            self._agent = create_agent(
                model=model,
                tools=tools,
                system_prompt=SYSTEM_PROMPT,
                checkpointer=InMemorySaver(),
            )

    def reset(self, conversation_id: str) -> None:
        """Forget the history of one conversation."""
        self._generations[conversation_id] = self._generations.get(conversation_id, 0) + 1

    async def ask(self, conversation_id: str, message: str) -> str:
        if self._agent is None:
            await self.start()

        assert self._agent is not None
        thread_id = f"{conversation_id}#{self._generations.get(conversation_id, 0)}"

        result = await self._agent.ainvoke(
            {"messages": [{"role": "user", "content": message}]},
            config={"configurable": {"thread_id": thread_id}},
        )

        return result["messages"][-1].text
