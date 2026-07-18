"""Telemetry-plane ports for the agent (spec 13).

Currently: the model-trace sink (spec 13 §25.3) — prompts/outputs route to the stricter
retention tier through the Booking API, never to app logs. The correlation/metrics wiring
described by spec 13 §10 lands here when the OTel build-out happens (out of scope today).
"""

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
