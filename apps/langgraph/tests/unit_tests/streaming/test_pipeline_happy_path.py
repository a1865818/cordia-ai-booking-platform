"""Unit tests for the streaming session pipeline orchestrator."""

from collections.abc import AsyncIterator
from datetime import UTC
from inspect import getsource

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import Pipeline, PipelineSession, TurnManager
from cordia_agent.streaming import agent_port as agent_port_module
from cordia_agent.streaming.providers import (
    FakeSTTProvider,
    FakeTTSProvider,
    STTFinalEvent,
    STTPartialEvent,
)
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    ToolProgressFrame,
)
from tests.unit_tests.streaming.fakes import fake_agent as fake_agent_module
from tests.unit_tests.streaming.fakes.fake_agent import FakeAgent

SESSION = PipelineSession(
    tenant_id="tenant_1",
    session_id="session_1",
    turn_id="turn_1",
    correlation_id="correlation_1",
    causation_id="causation_1",
)

AGENT_ENVELOPE = {
    "tenant_id": "tenant_1",
    "session_id": "session_1",
    "turn_id": "turn_1",
    "correlation_id": "correlation_1",
    "causation_id": "causation_1",
    "ts": "2026-06-18T00:00:00+00:00",
}


async def _pcm(count: int) -> AsyncIterator[bytes]:
    for index in range(count):
        yield f"pcm_{index}".encode()


def _agent_delta(delta: str, *, seq: int) -> AgentTextDeltaFrame:
    return AgentTextDeltaFrame(
        type=StreamFrameType.AGENT_TEXT_DELTA, **AGENT_ENVELOPE, seq=seq, delta=delta
    )


def _agent_final(text: str, *, seq: int) -> AgentTextFinalFrame:
    return AgentTextFinalFrame(
        type=StreamFrameType.AGENT_TEXT_FINAL, **AGENT_ENVELOPE, seq=seq, text=text
    )


def _tool_progress(elapsed_ms: int, *, seq: int) -> ToolProgressFrame:
    return ToolProgressFrame(
        type=StreamFrameType.TOOL_PROGRESS, **AGENT_ENVELOPE, seq=seq,
        tool_name="check_candidate_availability", phase="checking",
        elapsed_ms=elapsed_ms,
    )


def _pipeline(agent: FakeAgent) -> Pipeline:
    return Pipeline(
        stt_provider=FakeSTTProvider(
            [
                STTPartialEvent(text="i need a table", stability=0.4),
                STTFinalEvent(text="i need a table", asr_confidence=0.9),
            ]
        ),
        tts_provider=FakeTTSProvider(),
        agent=agent,
    )


@pytest.mark.unit
async def test_happy_path_frame_sequence() -> None:
    agent = FakeAgent([_agent_delta("Hello.", seq=100), _agent_final("Hello.", seq=101)])
    frames = [f async for f in _pipeline(agent).run(_pcm(2), session=SESSION)]

    assert [f.type for f in frames] == [
        StreamFrameType.AUDIO_STARTED,
        StreamFrameType.TRANSCRIPT_PARTIAL,
        StreamFrameType.TRANSCRIPT_FINAL,
        StreamFrameType.AGENT_TEXT_DELTA,
        StreamFrameType.TTS_AUDIO_DELTA,
        StreamFrameType.AGENT_TEXT_FINAL,
    ]


@pytest.mark.unit
async def test_first_tts_delta_precedes_agent_text_final() -> None:
    agent = FakeAgent([_agent_delta("Hello.", seq=100), _agent_final("Hello.", seq=101)])
    types = [f.type async for f in _pipeline(agent).run(_pcm(2), session=SESSION)]
    assert types.index(StreamFrameType.TTS_AUDIO_DELTA) < types.index(
        StreamFrameType.AGENT_TEXT_FINAL
    )


@pytest.mark.unit
async def test_audio_started_emitted_once_and_carries_session_ids() -> None:
    agent = FakeAgent([_agent_delta("Hello.", seq=100), _agent_final("Hello.", seq=101)])
    frames = [f async for f in _pipeline(agent).run(_pcm(2), session=SESSION)]

    started = [f for f in frames if f.type == StreamFrameType.AUDIO_STARTED]
    assert len(started) == 1
    audio = started[0]
    assert audio.tenant_id == SESSION.tenant_id
    assert audio.session_id == SESSION.session_id
    assert audio.turn_id == SESSION.turn_id
    assert audio.correlation_id == SESSION.correlation_id
    assert audio.seq == 0  # first next_seq() call of the run
    assert audio.ts.utcoffset() == UTC.utcoffset(audio.ts)


@pytest.mark.unit
async def test_pipeline_sourced_frames_share_session_identity() -> None:
    agent = FakeAgent([_agent_delta("Hello.", seq=100), _agent_final("Hello.", seq=101)])
    frames = [f async for f in _pipeline(agent).run(_pcm(2), session=SESSION)]
    for f in frames:
        assert f.session_id == SESSION.session_id
        assert f.correlation_id == SESSION.correlation_id


@pytest.mark.unit
async def test_tool_progress_is_emitted_and_rendered_through_tts() -> None:
    agent = FakeAgent(
        [
            _tool_progress(1500, seq=100),
            _agent_delta("You're booked.", seq=101),
            _agent_final("You're booked.", seq=102),
        ]
    )
    frames = [f async for f in _pipeline(agent).run(_pcm(2), session=SESSION)]
    types = [f.type for f in frames]

    # original tool.progress survives on the wire
    assert StreamFrameType.TOOL_PROGRESS in types
    # the filler phrase is rendered as tts before the real agent text is synthesized
    progress_idx = types.index(StreamFrameType.TOOL_PROGRESS)
    first_tts_idx = types.index(StreamFrameType.TTS_AUDIO_DELTA)
    assert progress_idx < first_tts_idx
    # filler audio rides a dedicated filler turn, not the assistant turn
    filler_tts = next(
        f for f in frames
        if f.type == StreamFrameType.TTS_AUDIO_DELTA
        and f.turn_id != SESSION.turn_id
    )
    assert filler_tts.turn_id == "turn_1:filler:check_candidate_availability"


@pytest.mark.unit
async def test_subthreshold_tool_progress_renders_no_filler_audio() -> None:
    agent = FakeAgent(
        [_tool_progress(100, seq=100), _agent_final("Hi.", seq=101)]
    )
    frames = [f async for f in _pipeline(agent).run(_pcm(2), session=SESSION)]
    filler_audio = [
        f for f in frames
        if f.type == StreamFrameType.TTS_AUDIO_DELTA and f.turn_id != SESSION.turn_id
    ]
    assert filler_audio == []


@pytest.mark.unit
def test_agent_port_and_fake_do_not_import_real_network_dependencies() -> None:
    source = "\n".join(
        [getsource(agent_port_module), getsource(fake_agent_module)]
    )
    forbidden_tokens = [
        "boto3", "deepgram", "google.cloud", "httpx", "openai",
        "langchain", "requests", "socket", "websocket",
    ]
    assert not any(token in source for token in forbidden_tokens)
