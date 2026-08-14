"""Unit tests for the graph-backed ``AgentPort``.

The graph is replaced by a scripted stand-in so these stay offline and fast.
What is under test is the seam: how a committed transcript becomes an
``AgentInputState``, how node updates become speakable frames, and how a slow
node becomes progress rather than dead air.
"""

from __future__ import annotations

import asyncio
from datetime import UTC, datetime
from typing import TYPE_CHECKING, Any

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming.agent_reply import HANDOFF_TEXT, reply_text, segment_for
from cordia_agent.streaming.graph_agent_port import (
    AgentSessionContext,
    GraphAgentPort,
)
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    ToolProgressFrame,
    TranscriptFinalFrame,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterator, Sequence


SESSION = AgentSessionContext(
    signed_tenant_context="header.payload.signature",
    session_id="sess-1",
    tenant_id="tenant-1",
    correlation_id="corr-1",
)


class ScriptedGraph:
    """A compiled-graph stand-in that replays a scripted ``astream``.

    Entries are either ``("updates", {node: update})`` or
    ``("tasks", {...})``, exactly as LangGraph's multi-mode stream yields them,
    plus a ``float`` meaning "stall here for this long" so a slow node can be
    modelled without a real network call.
    """

    def __init__(
        self, script: Sequence[Any], *, checkpointer: object | None = object()
    ) -> None:
        self.checkpointer = checkpointer
        self._script = list(script)
        self.inputs: list[dict[str, Any]] = []
        self.configs: list[dict[str, Any]] = []

    def astream(
        self, payload: dict[str, Any], *, config: dict[str, Any], stream_mode: Any
    ) -> AsyncIterator[tuple[str, Any]]:
        self.inputs.append(payload)
        self.configs.append(config)

        async def generate() -> AsyncIterator[tuple[str, Any]]:
            for entry in self._script:
                if isinstance(entry, float | int):
                    await asyncio.sleep(entry)
                    continue
                if isinstance(entry, Exception):
                    raise entry
                yield entry

        return generate()


def transcript(
    text: str = "book me a table", turn_id: str = "turn-1"
) -> TranscriptFinalFrame:
    return TranscriptFinalFrame(
        type=StreamFrameType.TRANSCRIPT_FINAL,
        tenant_id="tenant-1",
        session_id="sess-1",
        turn_id=turn_id,
        correlation_id="corr-1",
        causation_id=None,
        seq=3,
        ts=datetime.now(UTC),
        text=text,
        asr_confidence=0.9,
    )


def task_start(node: str) -> tuple[str, dict[str, Any]]:
    return ("tasks", {"id": "t1", "name": node, "input": {}, "triggers": ()})


def task_end(node: str) -> tuple[str, dict[str, Any]]:
    return (
        "tasks",
        {"id": "t1", "name": node, "error": None, "result": {}, "interrupts": []},
    )


async def drain(port: GraphAgentPort, frame: TranscriptFinalFrame) -> list[Any]:
    return [item async for item in port.process(frame)]


# ---------------------------------------------------------------------------
# Input mapping
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_transcript_maps_onto_agent_input_state() -> None:
    """frame.text → transcript_text, frame.turn_id → current_turn_id, final=True."""
    graph = ScriptedGraph(
        [("updates", {"clarify": {"pending_clarify_prompt": "When?"}})]
    )
    port = GraphAgentPort(graph, session=SESSION)

    await drain(port, transcript("tomorrow at seven", turn_id="turn-9"))

    assert graph.inputs[0] == {
        "signed_tenant_context": "header.payload.signature",
        "session_id": "sess-1",
        "source": {"channel": "voice", "language": "en-AU"},
        "transcript_text": "tomorrow at seven",
        "transcript_is_final": True,
        "current_turn_id": "turn-9",
    }


@pytest.mark.unit
async def test_the_session_id_is_the_checkpointer_thread() -> None:
    """A call is one thread; that is what carries the draft between turns."""
    graph = ScriptedGraph([])
    port = GraphAgentPort(graph, session=SESSION)
    await drain(port, transcript())

    assert graph.configs[0]["configurable"]["thread_id"] == "sess-1"
    assert graph.configs[0]["recursion_limit"] == SESSION.recursion_limit


@pytest.mark.unit
def test_a_graph_without_a_checkpointer_is_refused() -> None:
    """Silently forgetting the booking between turns is not an option."""
    with pytest.raises(ValueError, match="checkpointer"):
        GraphAgentPort(ScriptedGraph([], checkpointer=None), session=SESSION)


@pytest.mark.unit
def test_a_non_positive_progress_interval_is_refused() -> None:
    with pytest.raises(ValueError, match="progress_interval_seconds"):
        GraphAgentPort(ScriptedGraph([]), session=SESSION, progress_interval_seconds=0)


# ---------------------------------------------------------------------------
# Reply frames
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_each_speaking_node_becomes_a_delta_then_one_final() -> None:
    """Deltas let TTS start on sentence one; the final closes the turn."""
    graph = ScriptedGraph(
        [
            ("updates", {"read_back": {"read_back_text": "Four people at 7pm."}}),
            (
                "updates",
                {"submit_intent": {"submission_message": "I've submitted that."}},
            ),
        ]
    )
    port = GraphAgentPort(graph, session=SESSION)

    frames = await drain(port, transcript())
    deltas = [f for f in frames if isinstance(f, AgentTextDeltaFrame)]
    finals = [f for f in frames if isinstance(f, AgentTextFinalFrame)]

    assert [d.delta for d in deltas] == ["Four people at 7pm.", " I've submitted that."]
    assert len(finals) == 1
    assert finals[0].text == "Four people at 7pm. I've submitted that."


@pytest.mark.unit
async def test_deltas_concatenate_to_exactly_the_final_text() -> None:
    """TTSChunker diffs the final against the deltas; a mismatch is heard twice."""
    graph = ScriptedGraph(
        [
            (
                "updates",
                {"consent_gate": {"pending_consent_notice": "We record calls."}},
            ),
            ("updates", {"clarify": {"pending_clarify_prompt": "How many people?"}}),
        ]
    )
    port = GraphAgentPort(graph, session=SESSION)

    frames = await drain(port, transcript())
    spoken = "".join(f.delta for f in frames if isinstance(f, AgentTextDeltaFrame))
    final = next(f for f in frames if isinstance(f, AgentTextFinalFrame))

    assert spoken == final.text


@pytest.mark.unit
async def test_silent_nodes_produce_no_frames() -> None:
    """capture_fields captures and check_candidates calls a tool; neither speaks."""
    graph = ScriptedGraph(
        [
            ("updates", {"capture_fields": {"draft": {"party_size": 4}}}),
            ("updates", {"check_candidates": {"availability_candidates": []}}),
            ("updates", {"clarify": {"pending_clarify_prompt": "Another time?"}}),
        ]
    )
    port = GraphAgentPort(graph, session=SESSION)

    deltas = [
        f for f in await drain(port, transcript()) if isinstance(f, AgentTextDeltaFrame)
    ]
    assert [d.delta for d in deltas] == ["Another time?"]


@pytest.mark.unit
async def test_a_turn_that_says_nothing_falls_back_rather_than_going_silent() -> None:
    """Dead air on a call invites the caller to talk over the next utterance."""
    port = GraphAgentPort(ScriptedGraph([]), session=SESSION)
    frames = await drain(port, transcript())

    final = next(f for f in frames if isinstance(f, AgentTextFinalFrame))
    assert final.text == "Sorry, could you say that again?"


@pytest.mark.unit
async def test_the_fallback_can_be_switched_off() -> None:
    port = GraphAgentPort(ScriptedGraph([]), session=SESSION, fallback_text=None)
    assert await drain(port, transcript()) == []


@pytest.mark.unit
async def test_handoff_speaks_even_though_the_node_records_no_text() -> None:
    graph = ScriptedGraph(
        [("updates", {"human_handoff": {"terminal_state": "human_handoff"}})]
    )
    port = GraphAgentPort(graph, session=SESSION)

    final = next(
        f for f in await drain(port, transcript()) if isinstance(f, AgentTextFinalFrame)
    )
    assert final.text == HANDOFF_TEXT


@pytest.mark.unit
async def test_frames_carry_the_transcript_envelope() -> None:
    """Session/turn/correlation identity is propagated, not invented."""
    graph = ScriptedGraph(
        [("updates", {"clarify": {"pending_clarify_prompt": "When?"}})]
    )
    port = GraphAgentPort(graph, session=SESSION)

    for frame in await drain(port, transcript(turn_id="turn-42")):
        assert frame.session_id == "sess-1"
        assert frame.turn_id == "turn-42"
        assert frame.correlation_id == "corr-1"
        assert frame.tenant_id == "tenant-1"


# ---------------------------------------------------------------------------
# Progress
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_a_slow_node_emits_progress_with_real_elapsed_time() -> None:
    """The caller gets "checking availability", not silence — and the ms are measured."""
    graph = ScriptedGraph(
        [
            task_start("check_candidates"),
            0.12,
            task_end("check_candidates"),
            ("updates", {"read_back": {"read_back_text": "Four at 7pm."}}),
        ]
    )
    port = GraphAgentPort(graph, session=SESSION, progress_interval_seconds=0.02)

    progress = [
        f for f in await drain(port, transcript()) if isinstance(f, ToolProgressFrame)
    ]

    assert progress, "a node held for 120ms must produce at least one progress frame"
    assert all(f.tool_name == "check_candidate_availability" for f in progress)
    assert all(f.phase == "check_candidates" for f in progress)
    assert progress[-1].elapsed_ms >= progress[0].elapsed_ms
    assert progress[0].elapsed_ms > 0


@pytest.mark.unit
async def test_a_fast_node_emits_no_progress() -> None:
    """Nothing is announced for work the caller cannot perceive."""
    graph = ScriptedGraph(
        [
            task_start("check_candidates"),
            task_end("check_candidates"),
            ("updates", {"read_back": {"read_back_text": "Four at 7pm."}}),
        ]
    )
    port = GraphAgentPort(graph, session=SESSION, progress_interval_seconds=0.5)

    frames = await drain(port, transcript())
    assert not [f for f in frames if isinstance(f, ToolProgressFrame)]


@pytest.mark.unit
async def test_progress_stops_once_the_node_finishes() -> None:
    """A completed node must not keep announcing itself while later ones run."""
    graph = ScriptedGraph(
        [
            task_start("submit_intent"),
            0.06,
            task_end("submit_intent"),
            0.15,
            ("updates", {"submit_intent": {"submission_message": "Submitted."}}),
        ]
    )
    port = GraphAgentPort(graph, session=SESSION, progress_interval_seconds=0.02)

    progress = [
        f for f in await drain(port, transcript()) if isinstance(f, ToolProgressFrame)
    ]
    assert len(progress) <= 4, (
        f"progress kept firing after the node ended: {[f.elapsed_ms for f in progress]}"
    )


# ---------------------------------------------------------------------------
# Failure propagation
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_a_graph_failure_reaches_the_caller() -> None:
    """A rejected TenantContext must not be dressed up as a cheerful reply."""

    class Boom(RuntimeError):
        pass

    graph = ScriptedGraph([Boom("tenant context rejected")])
    port = GraphAgentPort(graph, session=SESSION)

    with pytest.raises(Boom):
        await drain(port, transcript())


@pytest.mark.unit
async def test_abandoning_the_stream_does_not_orphan_the_graph_task() -> None:
    """A barge-in stops consuming mid-turn; the pump must be cancelled."""
    graph = ScriptedGraph(
        [
            ("updates", {"clarify": {"pending_clarify_prompt": "When?"}}),
            5.0,
            ("updates", {"read_back": {"read_back_text": "never reached"}}),
        ]
    )
    port = GraphAgentPort(graph, session=SESSION, progress_interval_seconds=0.02)

    stream = port.process(transcript())
    first = await anext(stream)
    assert isinstance(first, AgentTextDeltaFrame)
    await stream.aclose()

    running = [
        t for t in asyncio.all_tasks() if t.get_name() == "cordia-agent-graph-turn"
    ]
    assert not running, "the graph pump outlived the abandoned turn"


# ---------------------------------------------------------------------------
# agent_reply helpers
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    ("node", "update", "expected"),
    [
        ("read_back", {"read_back_text": "Four at 7pm."}, "Four at 7pm."),
        ("clarify", {"pending_clarify_prompt": " When? "}, "When?"),
        ("consent_gate", {"pending_consent_notice": None}, None),
        ("clarify", {"pending_clarify_prompt": "   "}, None),
        ("capture_fields", {"draft": {}}, None),
        ("read_back", "not a dict", None),
    ],
)
def test_segment_for_reads_only_what_the_node_wrote(
    node: str, update: object, expected: str | None
) -> None:
    assert segment_for(node, update) == expected


@pytest.mark.unit
def test_reply_text_joins_in_node_order() -> None:
    assert reply_text(["One.", "Two."]) == "One. Two."


@pytest.mark.unit
def test_reply_text_returns_none_when_told_to_stay_silent() -> None:
    assert reply_text([], fallback=None) is None
