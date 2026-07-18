"""Model-trace sink port — spec 13 §25.3 (stricter access/retention for prompts/outputs).

Model prompts and outputs are NOT ordinary logs: they route through this port to the
Booking API's model-trace tier (encrypted at rest, short TTL, staff-only audited reads) and
must never reach the app-log sink. STUB-BACKED today — no LLM node exists on the hot path
yet, so nothing invokes the sink in production; the port fixes the contract so the first
model call has exactly one sanctioned place to send its traces.

The agent never touches storage directly (spec 14 boundary): ``ApiModelTraceSink`` writes
through the ``record_model_trace`` tool, and content passes ``redact_text`` HERE as well —
belt (agent), braces (this sink), and the API re-sanitizes at the boundary (Inv-12).
"""

from __future__ import annotations

import logging
from typing import Literal, Protocol

from cordia_agent.security.redaction import redact_text
from cordia_agent.tools.client_binding import record_model_trace_call
from cordia_agent.tools.contracts import ModelTraceInput


_log = logging.getLogger(__name__)


class ModelTraceSink(Protocol):
    """Port: record one model prompt/output into the telemetry retention tier."""

    async def record(
        self,
        *,
        trace_id: str,
        kind: Literal["prompt", "output"],
        content: str,
        session_id: str | None = None,
    ) -> None: ...


class NullModelTraceSink:
    """Default sink: drops traces (no LLM nodes exist yet; wiring is opt-in)."""

    async def record(
        self,
        *,
        trace_id: str,
        kind: Literal["prompt", "output"],
        content: str,
        session_id: str | None = None,
    ) -> None:
        return


class ApiModelTraceSink:
    """Writes redacted traces through the Booking API tool client (spec 13 §25.3).

    Best-effort: a sink failure is logged and swallowed — telemetry must never
    break the model call it observes (spec 13 §8 fail-open for the business path).
    """

    async def record(
        self,
        *,
        trace_id: str,
        kind: Literal["prompt", "output"],
        content: str,
        session_id: str | None = None,
    ) -> None:
        try:
            await record_model_trace_call(
                ModelTraceInput(
                    trace_id=trace_id,
                    session_id=session_id,
                    kind=kind,
                    content=redact_text(content),
                )
            )
        except Exception:
            _log.exception(
                "model-trace record failed for trace=%s; continuing", trace_id
            )
