"""Model-trace sink port tests — spec 13 §25.3 (gap-linkage phase 5, stub-backed).

Pins: ``ApiModelTraceSink`` redacts content BEFORE the tool call (belt-and-braces Inv-12),
swallows sink failures (telemetry never breaks the business path), and the ``NullModelTraceSink``
default is a true no-op (nothing invokes the sink in production yet — no LLM nodes exist).
"""

import pytest

from cordia_agent.observability import ApiModelTraceSink, NullModelTraceSink
from cordia_agent.tools.contracts import ModelTraceInput


@pytest.mark.unit
async def test_api_sink_redacts_before_the_tool_call(monkeypatch) -> None:
    calls: list[ModelTraceInput] = []

    async def fake_call(inp: ModelTraceInput):
        calls.append(inp)

    monkeypatch.setattr(
        "cordia_agent.observability.model_trace.record_model_trace_call", fake_call
    )

    await ApiModelTraceSink().record(
        trace_id="trace-1",
        kind="prompt",
        content="the caller said 4111 1111 1111 1111",
        session_id="sess-1",
    )

    assert len(calls) == 1
    assert "4111" not in calls[0].content
    assert "[REDACTED]" in calls[0].content
    assert calls[0].trace_id == "trace-1"
    assert calls[0].kind == "prompt"


@pytest.mark.unit
async def test_api_sink_swallows_failures(monkeypatch) -> None:
    async def failing_call(inp: ModelTraceInput):
        raise RuntimeError("trace backend down")

    monkeypatch.setattr(
        "cordia_agent.observability.model_trace.record_model_trace_call", failing_call
    )

    # Must not raise — telemetry never breaks the model call it observes.
    await ApiModelTraceSink().record(trace_id="trace-2", kind="output", content="hi")


@pytest.mark.unit
async def test_null_sink_is_a_no_op() -> None:
    await NullModelTraceSink().record(trace_id="t", kind="prompt", content="anything")
