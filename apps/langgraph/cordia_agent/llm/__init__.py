"""Azure AI Foundry model layer for the Cordia agent (spec 02 model seam).

Public surface:

- :func:`get_chat_model` — build (and memoise) a ``BaseChatModel`` for any
  deployment in a Foundry project, across both provider surfaces.
- :func:`ainvoke_with_timeout` — the ONLY sanctioned way to call a model: a
  progressive-timeout wrapper that can never hang a conversational turn.
- :func:`check_empty_response` — reject structurally-valid empty completions.

Importing this package pulls in no provider SDK; the clients are imported
lazily inside :func:`load_chat_model`, so the offline test suite is unaffected.
"""

from cordia_agent.llm.config import (
    AzureModelSettings,
    ModelNotConfigured,
    settings_from_env,
)
from cordia_agent.llm.factory import (
    get_chat_model,
    load_chat_model,
    reset_model_cache,
)
from cordia_agent.llm.invoke import (
    EmptyModelResponse,
    ainvoke_with_timeout,
    check_empty_response,
)


__all__ = [
    "AzureModelSettings",
    "EmptyModelResponse",
    "ModelNotConfigured",
    "ainvoke_with_timeout",
    "check_empty_response",
    "get_chat_model",
    "load_chat_model",
    "reset_model_cache",
    "settings_from_env",
]
