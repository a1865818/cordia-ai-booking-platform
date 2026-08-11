"""Azure AI Foundry chat-model factory (spec 02 model seam).

Builds a ``BaseChatModel`` for ANY deployment in a Foundry project by routing
across the two client surfaces Azure actually exposes:

===============  ==========================================  ==================
provider         endpoint                                    served by
===============  ==========================================  ==================
azure_openai     https://<res>.openai.azure.com/             langchain-openai
azure_ai         <foundry endpoint>/openai/v1                langchain-azure-ai
===============  ==========================================  ==================

Both provider SDKs are imported lazily inside the branch that needs them, so a
deployment on one surface never requires the other's package to be installed —
and importing this module costs nothing in the offline test suite.

Construction is memoised per (role, settings) so repeated node invocations
reuse one client (and therefore one HTTP connection pool) instead of rebuilding
a fresh one per turn.
"""

from __future__ import annotations

from functools import lru_cache
from typing import TYPE_CHECKING, Any

from cordia_agent.llm.config import AzureModelSettings, settings_from_env


if TYPE_CHECKING:
    from langchain_core.language_models import BaseChatModel


def load_chat_model(settings: AzureModelSettings, **overrides: Any) -> BaseChatModel:
    """Build a chat model for *settings* via ``init_chat_model``.

    ``None``-valued kwargs are dropped before the call so an unset optional
    override (``reasoning_effort``, ``max_tokens``) leaves the provider default
    in place instead of explicitly nulling it.

    On ``api_surface="responses"`` the flat ``reasoning_effort`` kwarg is
    dropped and replaced by the nested ``reasoning={"effort", "summary"}``
    object — passing both raises ``TypeError`` from the Responses client.
    ``store=False`` makes LangChain build a self-contained request: the agent
    replays full state each turn from its checkpoint rather than relying on
    server-side item persistence, so bare ``rs_``/``msg_`` back-references
    would 404.

    Args:
        settings: Resolved connection settings for one deployment.
        **overrides: Extra kwargs forwarded to ``init_chat_model``; these win
            over everything derived here.

    Returns:
        A configured ``BaseChatModel``.

    Raises:
        ModelNotConfigured: propagated from ``AzureModelSettings`` validation.
        ImportError: if the provider's SDK is not installed.
    """
    # Lazy: keeps provider SDKs out of the offline test suite's import graph.
    from langchain.chat_models import init_chat_model

    kwargs: dict[str, Any] = {
        "max_tokens": overrides.pop("max_tokens", None),
    }

    if settings.provider == "azure_openai":
        if settings.api_surface == "responses":
            kwargs |= {
                "use_responses_api": True,
                "output_version": "responses/v1",
                # Self-contained request; see docstring.
                "store": False,
            }
            if settings.reasoning_effort:
                kwargs["reasoning"] = {
                    "effort": settings.reasoning_effort,
                    "summary": "auto",
                }
        else:
            # Valid flat on Chat Completions; dropped below when None.
            kwargs["reasoning_effort"] = settings.reasoning_effort

        clean = {k: v for k, v in (kwargs | overrides).items() if v is not None}
        return init_chat_model(
            model=settings.deployment,
            model_provider="azure_openai",
            azure_endpoint=settings.endpoint,
            api_key=settings.api_key,
            api_version=settings.api_version,
            azure_deployment=settings.deployment,
            **clean,
        )

    # provider == "azure_ai" — Foundry OpenAI-compatible inference surface.
    # Lazy: only the azure_ai branch needs the Azure SDK.
    from azure.core.credentials import AzureKeyCredential

    kwargs["reasoning_effort"] = settings.reasoning_effort
    clean = {k: v for k, v in (kwargs | overrides).items() if v is not None}
    return init_chat_model(
        model=settings.deployment,
        model_provider="azure_ai",
        endpoint=_foundry_endpoint(settings.endpoint),
        credential=AzureKeyCredential(settings.api_key),
        api_version=settings.api_version,
        **clean,
    )


# Version segments the Foundry inference endpoint may already carry.  `/openai/v1`
# is the current OpenAI-compatible route that `AzureAIOpenAIApiChatModel` expects;
# `/models` is the older Azure AI model-inference route, still accepted by custom
# gateways and model routers.
_FOUNDRY_VERSION_SEGMENTS = ("/openai/v1", "/models", "/v1")


def _foundry_endpoint(endpoint: str) -> str:
    """Normalise a Foundry project endpoint for the ``azure_ai`` provider.

    ``AzureAIOpenAIApiChatModel`` requires a version segment on the URL and
    warns (then fails at request time) without one.  A bare project endpoint —
    ``https://<res>.services.ai.azure.com`` — is therefore suffixed with
    ``/openai/v1``, the current OpenAI-compatible route.

    An endpoint that already carries a version segment is returned untouched,
    so a custom gateway, a model router, or the older ``/models`` inference
    route can be configured verbatim via ``AZURE_OPENAI_ENDPOINT``.

    Args:
        endpoint: The configured endpoint URL.

    Returns:
        The endpoint with exactly one version segment.
    """
    trimmed = endpoint.rstrip("/")
    if any(seg in trimmed for seg in _FOUNDRY_VERSION_SEGMENTS):
        return trimmed
    return f"{trimmed}/openai/v1"


@lru_cache(maxsize=8)
def _cached_model(settings: AzureModelSettings) -> BaseChatModel:
    """Memoise one client per distinct settings tuple.

    ``AzureModelSettings`` is a frozen slots dataclass, so it hashes by value
    and a settings change (e.g. a test monkeypatching the environment) yields a
    fresh client rather than a stale cache hit.
    """
    return load_chat_model(settings)


def get_chat_model(role: str | None = None, **overrides: Any) -> BaseChatModel:
    """Return the chat model for *role*, reading settings from the environment.

    The common entry point for production code.  Resolution honours per-role
    environment overrides (see
    :func:`cordia_agent.llm.config.settings_from_env`), so ``role`` selects
    which deployment is used without any caller-side branching.

    Args:
        role: Optional logical role, e.g. ``"slot_extractor"``.
        **overrides: Extra kwargs forwarded to ``init_chat_model``.  Passing
            any override bypasses the cache so callers cannot poison the shared
            client with a one-off setting.

    Returns:
        A configured ``BaseChatModel``.

    Raises:
        ModelNotConfigured: if the environment is missing or inconsistent.
    """
    settings = settings_from_env(role)
    if overrides:
        return load_chat_model(settings, **overrides)
    return _cached_model(settings)


def reset_model_cache() -> None:
    """Clear the memoised clients — call in test teardown to isolate tests."""
    _cached_model.cache_clear()
