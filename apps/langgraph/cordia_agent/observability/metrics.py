"""Agent OTel metric instruments -- spec 13 §4.2 AGENT metrics registry.

Declares a ``Meter`` from ``opentelemetry.metrics.get_meter("cordia-agent")``
and instrument singletons for every §4.2 AGENT metric.  Metric name string
values match the C# ``MetricNames`` agent-group constants exactly (the names
are the cross-service contract -- see
``apps/api/src/Cordia.Domain/Observability/MetricNames.cs``).

Fail-open contract (§8):
- ``init_metrics()`` never raises; if ``OTEL_EXPORTER_OTLP_ENDPOINT`` is not
  set the function returns after configuring no-op provider.
- All record helpers catch every exception so a metrics call never propagates
  into agent business logic.

Spec refs: §4.2 (AGENT metric names), §7 (low-cardinality labels only --
never raw IDs), §8 (fail-open).
"""

from __future__ import annotations

import contextlib
import logging
import os
from typing import TYPE_CHECKING

from opentelemetry import metrics
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader


if TYPE_CHECKING:
    from opentelemetry.metrics import Counter, Histogram


_log = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Metric name constants -- values MUST match MetricNames.cs agent group exactly.
# ---------------------------------------------------------------------------


class MetricNames:
    """Frozen agent-metric name registry (mirrors C# ``MetricNames`` agent group).

    String values are the cross-service contract.  Changing a value here causes
    the metric to disappear in the backend.
    """

    AGENT_TURN_TIME_TO_FIRST_RESPONSE_MS = "agent.turn.time_to_first_response_ms"
    AGENT_TURN_LATENCY_MS = "agent.turn.latency_ms"
    AGENT_BARGEIN_SUCCESS_TOTAL = "agent.bargein.success_total"
    AGENT_BARGEIN_ATTEMPT_TOTAL = "agent.bargein.attempt_total"
    AGENT_SESSION_RECONNECT_TOTAL = "agent.session.reconnect_total"
    AGENT_STT_CONFIDENCE = "agent.stt.confidence"
    AGENT_CLARIFICATION_TOTAL = "agent.clarification_total"
    AGENT_SPELLBACK_TOTAL = "agent.spellback_total"
    AGENT_DRAFT_VALIDATION_FAILURE_TOTAL = "agent.draft.validation_failure_total"
    AGENT_CONFIRMATION_ABANDONMENT_TOTAL = "agent.confirmation.abandonment_total"
    AGENT_TOOL_ERROR_TOTAL = "agent.tool.error_total"
    AGENT_TOOL_TIMEOUT_TOTAL = "agent.tool.timeout_total"
    AGENT_HANDOFF_TOTAL = "agent.handoff_total"


# ---------------------------------------------------------------------------
# Module-level meter and instrument singletons.
# Instruments are created once at import time using the global meter so that
# callers get no-op instruments until init_metrics() installs a real provider.
# ---------------------------------------------------------------------------

_meter = metrics.get_meter("cordia-agent")

# Histograms
_turn_ttfr: Histogram = _meter.create_histogram(
    MetricNames.AGENT_TURN_TIME_TO_FIRST_RESPONSE_MS,
    unit="ms",
    description="Time to first response per agent turn (§4.2).",
)
_turn_latency: Histogram = _meter.create_histogram(
    MetricNames.AGENT_TURN_LATENCY_MS,
    unit="ms",
    description="Per-turn end-to-end latency (§4.2).",
)
_stt_confidence: Histogram = _meter.create_histogram(
    MetricNames.AGENT_STT_CONFIDENCE,
    description="STT/ASR confidence score by field type (§4.2).",
)

# Counters
_bargein_success: Counter = _meter.create_counter(
    MetricNames.AGENT_BARGEIN_SUCCESS_TOTAL,
    description="Barge-in events that successfully interrupted the agent (§4.2).",
)
_bargein_attempt: Counter = _meter.create_counter(
    MetricNames.AGENT_BARGEIN_ATTEMPT_TOTAL,
    description="Total barge-in attempts (§4.2).",
)
_session_reconnect: Counter = _meter.create_counter(
    MetricNames.AGENT_SESSION_RECONNECT_TOTAL,
    description="Session reconnects (§4.2).",
)
_clarification: Counter = _meter.create_counter(
    MetricNames.AGENT_CLARIFICATION_TOTAL,
    description="Clarification prompts issued, labelled by reason (§4.2).",
)
_spellback: Counter = _meter.create_counter(
    MetricNames.AGENT_SPELLBACK_TOTAL,
    description="Spell-back prompts issued (§4.2).",
)
_draft_validation_failure: Counter = _meter.create_counter(
    MetricNames.AGENT_DRAFT_VALIDATION_FAILURE_TOTAL,
    description="Draft validation failures, labelled by field (§4.2).",
)
_confirmation_abandonment: Counter = _meter.create_counter(
    MetricNames.AGENT_CONFIRMATION_ABANDONMENT_TOTAL,
    description="Confirmation flow abandonments (§4.2).",
)
_tool_error: Counter = _meter.create_counter(
    MetricNames.AGENT_TOOL_ERROR_TOTAL,
    description="Tool errors, labelled by tool and category (§4.2).",
)
_tool_timeout: Counter = _meter.create_counter(
    MetricNames.AGENT_TOOL_TIMEOUT_TOTAL,
    description="Tool timeouts, labelled by tool (§4.2).",
)
_handoff: Counter = _meter.create_counter(
    MetricNames.AGENT_HANDOFF_TOTAL,
    description="Human-handoff events, labelled by reason (§4.2).",
)

# ---------------------------------------------------------------------------
# Initialisation
# ---------------------------------------------------------------------------

_initialised = False


def init_metrics() -> None:
    """Initialise the OTel ``MeterProvider`` once at graph startup.

    Installs an OTLP HTTP exporter **only** when
    ``OTEL_EXPORTER_OTLP_ENDPOINT`` is set.  When the variable is absent the
    function returns without configuring a real exporter so the agent starts
    cleanly in any environment (§8 fail-open).

    Safe to call multiple times -- guarded by a module-level flag.  Never
    raises.
    """
    global _initialised
    if _initialised:
        return
    _initialised = True

    endpoint = os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT", "").strip()
    if not endpoint:
        # No endpoint configured -- leave the SDK default (no-op) provider.
        # This is intentional: §8 fail-open means no OTel connection is
        # attempted when the env var is absent.
        return

    with contextlib.suppress(Exception):
        # Import lazily so the exporter package is only required when actually
        # exporting.
        from opentelemetry.exporter.otlp.proto.http.metric_exporter import (
            OTLPMetricExporter,
        )

        exporter = OTLPMetricExporter(endpoint=endpoint)
        reader = PeriodicExportingMetricReader(exporter)
        provider = MeterProvider(metric_readers=[reader])
        metrics.set_meter_provider(provider)


# ---------------------------------------------------------------------------
# Helper record functions (low-cardinality labels only -- §7)
# ---------------------------------------------------------------------------


def record_tool_error(tool: str, category: str) -> None:
    """Increment the tool-error counter with low-cardinality labels.

    Args:
        tool: The canonical tool name (e.g. ``"check_candidate_availability"``).
            Use the 17-tool registry names only -- never raw IDs.
        category: A coarse error category (e.g. ``"not_found"``, ``"conflict"``,
            ``"unavailable"``, ``"request_error"``).  Never a raw message.
    """
    with contextlib.suppress(Exception):
        _tool_error.add(1, {"tool": tool, "category": category})


def record_tool_timeout(tool: str) -> None:
    """Increment the tool-timeout counter.

    Args:
        tool: The canonical tool name.
    """
    with contextlib.suppress(Exception):
        _tool_timeout.add(1, {"tool": tool, "reason": "timeout"})


def record_turn_ttfr(latency_ms: float) -> None:
    """Record time-to-first-response for an agent turn (ms).

    Args:
        latency_ms: Elapsed milliseconds from turn start to first response token.
    """
    with contextlib.suppress(Exception):
        _turn_ttfr.record(latency_ms)


def record_turn_latency(latency_ms: float) -> None:
    """Record end-to-end turn latency (ms).

    Args:
        latency_ms: Total elapsed milliseconds for the turn.
    """
    with contextlib.suppress(Exception):
        _turn_latency.record(latency_ms)


def record_bargein_success() -> None:
    """Increment the successful barge-in counter."""
    with contextlib.suppress(Exception):
        _bargein_success.add(1)


def record_bargein_attempt() -> None:
    """Increment the barge-in attempt counter."""
    with contextlib.suppress(Exception):
        _bargein_attempt.add(1)


def record_session_reconnect() -> None:
    """Increment the session reconnect counter."""
    with contextlib.suppress(Exception):
        _session_reconnect.add(1)


def record_stt_confidence(score: float, field_type: str) -> None:
    """Record an STT/ASR confidence score.

    Args:
        score: Confidence value in [0.0, 1.0].
        field_type: Low-cardinality field type label (e.g. ``"date"``,
            ``"name"``, ``"number"``).  Never a raw field value.
    """
    with contextlib.suppress(Exception):
        _stt_confidence.record(score, {"field_type": field_type})


def record_clarification(reason: str) -> None:
    """Increment the clarification counter.

    Args:
        reason: Low-cardinality reason label.
    """
    with contextlib.suppress(Exception):
        _clarification.add(1, {"reason": reason})


def record_spellback() -> None:
    """Increment the spell-back prompt counter."""
    with contextlib.suppress(Exception):
        _spellback.add(1)


def record_draft_validation_failure(field: str) -> None:
    """Increment the draft validation failure counter.

    Args:
        field: Low-cardinality field name label (e.g. ``"date"``, ``"party_size"``).
            Never a raw field value.
    """
    with contextlib.suppress(Exception):
        _draft_validation_failure.add(1, {"field": field})


def record_confirmation_abandonment() -> None:
    """Increment the confirmation abandonment counter."""
    with contextlib.suppress(Exception):
        _confirmation_abandonment.add(1)


def record_handoff(reason: str) -> None:
    """Increment the human-handoff counter.

    Args:
        reason: Low-cardinality handoff reason (e.g. ``"complaint"``,
            ``"escalation"``).
    """
    with contextlib.suppress(Exception):
        _handoff.add(1, {"reason": reason})
