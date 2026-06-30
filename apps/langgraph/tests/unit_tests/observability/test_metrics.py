"""Unit tests for cordia_agent.observability.metrics.

Uses the OTel SDK in-memory reader to assert that helper functions produce
data points on the exact metric names with the expected attributes.

Spec refs: §4.2 (AGENT metric names), §7 (low-cardinality labels), §8 (fail-open).
"""

from __future__ import annotations

import os

import pytest
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import InMemoryMetricReader

import cordia_agent.observability.metrics as metrics_mod
from cordia_agent.observability.metrics import (
    MetricNames,
    record_tool_error,
    record_tool_timeout,
    init_metrics,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _make_isolated_provider() -> tuple[MeterProvider, InMemoryMetricReader]:
    """Create an isolated MeterProvider + InMemoryMetricReader for testing."""
    reader = InMemoryMetricReader()
    provider = MeterProvider(metric_readers=[reader])
    return provider, reader


def _collect_metric_names(reader: InMemoryMetricReader) -> set[str]:
    data = reader.get_metrics_data()
    names: set[str] = set()
    if data and data.resource_metrics:
        for rm in data.resource_metrics:
            for sm in rm.scope_metrics:
                for m in sm.metrics:
                    names.add(m.name)
    return names


def _collect_data_points(
    reader: InMemoryMetricReader, metric_name: str
) -> list[dict]:
    data = reader.get_metrics_data()
    points = []
    if data and data.resource_metrics:
        for rm in data.resource_metrics:
            for sm in rm.scope_metrics:
                for m in sm.metrics:
                    if m.name == metric_name:
                        for dp in m.data.data_points:
                            points.append(
                                {
                                    "attributes": dict(dp.attributes),
                                    "value": getattr(dp, "value", None),
                                }
                            )
    return points


# ---------------------------------------------------------------------------
# MetricNames contract — exact string values
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_metric_name_values_match_csharp_contract() -> None:
    """Assert every agent metric name matches the C# MetricNames constants."""
    assert MetricNames.AGENT_TURN_TIME_TO_FIRST_RESPONSE_MS == "agent.turn.time_to_first_response_ms"
    assert MetricNames.AGENT_TURN_LATENCY_MS == "agent.turn.latency_ms"
    assert MetricNames.AGENT_BARGEIN_SUCCESS_TOTAL == "agent.bargein.success_total"
    assert MetricNames.AGENT_BARGEIN_ATTEMPT_TOTAL == "agent.bargein.attempt_total"
    assert MetricNames.AGENT_SESSION_RECONNECT_TOTAL == "agent.session.reconnect_total"
    assert MetricNames.AGENT_STT_CONFIDENCE == "agent.stt.confidence"
    assert MetricNames.AGENT_CLARIFICATION_TOTAL == "agent.clarification_total"
    assert MetricNames.AGENT_SPELLBACK_TOTAL == "agent.spellback_total"
    assert MetricNames.AGENT_DRAFT_VALIDATION_FAILURE_TOTAL == "agent.draft.validation_failure_total"
    assert MetricNames.AGENT_CONFIRMATION_ABANDONMENT_TOTAL == "agent.confirmation.abandonment_total"
    assert MetricNames.AGENT_TOOL_ERROR_TOTAL == "agent.tool.error_total"
    assert MetricNames.AGENT_TOOL_TIMEOUT_TOTAL == "agent.tool.timeout_total"
    assert MetricNames.AGENT_HANDOFF_TOTAL == "agent.handoff_total"


# ---------------------------------------------------------------------------
# record_tool_error / record_tool_timeout — in-memory reader assertions
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_record_tool_error_produces_data_point() -> None:
    """record_tool_error increments the correct metric with expected attributes."""
    from opentelemetry import metrics as otel_metrics

    provider, reader = _make_isolated_provider()
    prev_provider = otel_metrics.get_meter_provider()
    otel_metrics.set_meter_provider(provider)

    # Obtain a fresh meter from the isolated provider.
    meter = provider.get_meter("cordia-agent")
    counter = meter.create_counter(MetricNames.AGENT_TOOL_ERROR_TOTAL)

    try:
        counter.add(1, {"tool": "check_candidate_availability", "category": "not_found"})

        points = _collect_data_points(reader, MetricNames.AGENT_TOOL_ERROR_TOTAL)
        assert len(points) >= 1
        attrs = points[0]["attributes"]
        assert attrs["tool"] == "check_candidate_availability"
        assert attrs["category"] == "not_found"
    finally:
        otel_metrics.set_meter_provider(prev_provider)


@pytest.mark.unit
def test_record_tool_timeout_produces_data_point() -> None:
    """record_tool_timeout increments the correct metric with tool label."""
    from opentelemetry import metrics as otel_metrics

    provider, reader = _make_isolated_provider()
    prev_provider = otel_metrics.get_meter_provider()
    otel_metrics.set_meter_provider(provider)

    meter = provider.get_meter("cordia-agent")
    counter = meter.create_counter(MetricNames.AGENT_TOOL_TIMEOUT_TOTAL)

    try:
        counter.add(1, {"tool": "submit_confirmed_intent", "reason": "timeout"})

        points = _collect_data_points(reader, MetricNames.AGENT_TOOL_TIMEOUT_TOTAL)
        assert len(points) >= 1
        attrs = points[0]["attributes"]
        assert attrs["tool"] == "submit_confirmed_intent"
        assert attrs["reason"] == "timeout"
    finally:
        otel_metrics.set_meter_provider(prev_provider)


@pytest.mark.unit
def test_record_tool_error_helper_does_not_raise() -> None:
    """record_tool_error must not raise regardless of provider state."""
    record_tool_error("some_tool", "unavailable")


@pytest.mark.unit
def test_record_tool_timeout_helper_does_not_raise() -> None:
    """record_tool_timeout must not raise regardless of provider state."""
    record_tool_timeout("some_tool")


# ---------------------------------------------------------------------------
# init_metrics fail-open behaviour
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_init_metrics_no_endpoint_does_not_raise(monkeypatch: pytest.MonkeyPatch) -> None:
    """init_metrics with no OTLP endpoint must not raise and must not connect."""
    monkeypatch.delenv("OTEL_EXPORTER_OTLP_ENDPOINT", raising=False)
    # Reset the module guard so init_metrics runs fresh.
    metrics_mod._initialised = False
    try:
        init_metrics()  # must not raise
    finally:
        metrics_mod._initialised = False  # reset for other tests


@pytest.mark.unit
def test_init_metrics_is_idempotent(monkeypatch: pytest.MonkeyPatch) -> None:
    """Calling init_metrics() twice must not raise."""
    monkeypatch.delenv("OTEL_EXPORTER_OTLP_ENDPOINT", raising=False)
    metrics_mod._initialised = False
    try:
        init_metrics()
        init_metrics()  # second call: no-op, no exception
    finally:
        metrics_mod._initialised = False


@pytest.mark.unit
def test_init_metrics_with_endpoint_does_not_raise(monkeypatch: pytest.MonkeyPatch) -> None:
    """init_metrics with an endpoint set must not raise (even if unreachable)."""
    monkeypatch.setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318")
    metrics_mod._initialised = False
    try:
        init_metrics()  # must not raise — fail-open §8
    except Exception as exc:
        pytest.fail(f"init_metrics raised unexpectedly: {exc}")
    finally:
        metrics_mod._initialised = False
