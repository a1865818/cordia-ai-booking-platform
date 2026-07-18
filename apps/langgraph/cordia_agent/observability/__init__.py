"""Observability sub-package: correlation context, metric instruments, and the model-trace sink (spec 13).

The correlation context (``context.py`` / ``correlation.py``) and metric instruments (``metrics.py``)
carry the spec 13 §10/§25 telemetry wiring. The model-trace sink (spec 13 §25.3) routes prompts/outputs
to the stricter retention tier through the Booking API, never to app logs.

The model-trace symbols are re-exported LAZILY (PEP 562 ``__getattr__``): ``tools.client`` imports
``observability.metrics`` on the tool-client import path, so eagerly importing ``model_trace`` here —
which pulls ``tools.client_binding`` → ``tools.foreground`` → ``tools.client`` — would close an import
cycle. Deferring the model_trace import until an attribute is first accessed keeps
``from cordia_agent.observability import ModelTraceSink`` working without the cycle.
"""

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.observability.model_trace import (
        ApiModelTraceSink,
        ModelTraceSink,
        NullModelTraceSink,
    )


__all__ = [
    "ApiModelTraceSink",
    "ModelTraceSink",
    "NullModelTraceSink",
]


def __getattr__(name: str) -> object:
    if name in __all__:
        from cordia_agent.observability import model_trace

        return getattr(model_trace, name)
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")
