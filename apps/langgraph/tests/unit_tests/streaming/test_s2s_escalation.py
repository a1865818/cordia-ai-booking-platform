"""Unit tests for the S2S escalation adapter (§6.2, OPTIONAL/DEFERRED).

Covers:
- Port + fake (no real dependency): FakeS2SProvider satisfies S2SProvider structurally.
- Committable discipline (Inv-16): only S2SFinalTranscript → TranscriptFinalFrame and
  S2SAgentText → AgentTextFinalFrame are committable; partials and turn.cancelled are not.
  S2STurnCancelled → TurnCancelledFrame(material_content_delivered=False) and the turn
  is marked non-authoritative via TurnManager.
- Envelope propagation (Inv-13): all frames carry the same tenant_id/session_id/turn_id/
  correlation_id passed to engage; seq is monotonic per session.
- Opt-in / no behavior change: pipeline.py source contains no s2s/S2S reference; the
  adapter requires an explicit S2SProvider and cannot be engaged implicitly.
"""

from __future__ import annotations

import inspect
from datetime import UTC
from typing import TYPE_CHECKING

import pytest

import cordia_agent.streaming.pipeline as pipeline_module
from cordia_agent.state import StreamFrameType
from cordia_agent.streaming.providers.s2s_port import (
    S2SAgentText,
    S2SAudioChunk,
    S2SFinalTranscript,
    S2SPartialTranscript,
    S2STurnCancelled,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterator
from cordia_agent.streaming.s2s_escalation import S2SEscalationAdapter
from cordia_agent.streaming.turn_manager import TurnManager, is_committable
from cordia_agent.streaming_models import (
    AgentTextFinalFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
    TtsAudioDeltaFrame,
    TurnCancelledFrame,
)
from tests._stubs.streaming_s2s_fake import FakeS2SProvider


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

ENGAGE_KWARGS = {
    "tenant_id": "tenant_1",
    "session_id": "session_1",
    "turn_id": "turn_1",
    "correlation_id": "correlation_1",
    "causation_id": "causation_1",
}


async def _empty_pcm() -> AsyncIterator[bytes]:
    return
    yield  # make it an async generator


async def _collect(
    script: list[object],
    *,
    turn_manager: TurnManager | None = None,
    engage_kwargs: dict[str, str] | None = None,
) -> list[object]:
    tm = turn_manager or TurnManager()
    provider = FakeS2SProvider(script)  # type: ignore[arg-type]
    adapter = S2SEscalationAdapter(provider=provider, turn_manager=tm)
    kw = engage_kwargs or ENGAGE_KWARGS
    return [frame async for frame in adapter.engage(_empty_pcm(), **kw)]


# ---------------------------------------------------------------------------
# Port + fake — structural conformance, no real dependency
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_fake_s2s_provider_is_structurally_s2s_provider() -> None:
    """FakeS2SProvider satisfies the S2SProvider Protocol structurally."""
    provider = FakeS2SProvider([])
    # S2SProvider is a Protocol — check duck-type attribute presence
    assert hasattr(provider, "converse")
    assert callable(provider.converse)


@pytest.mark.unit
async def test_fake_s2s_provider_yields_scripted_events_in_order() -> None:
    events = [
        S2SPartialTranscript(text="hello", stability=0.5),
        S2SFinalTranscript(text="hello world", asr_confidence=0.95),
        S2SAgentText(text="Hi there!"),
        S2SAudioChunk(audio_ref="s2s://fake/chunk/0"),
        S2STurnCancelled(),
    ]
    collected: list[object] = []
    provider = FakeS2SProvider(events)

    async def _pcm() -> AsyncIterator[bytes]:
        yield b"pcm"

    collected = [ev async for ev in provider.converse(_pcm())]

    assert collected == events


@pytest.mark.unit
def test_s2s_port_and_fake_import_no_real_provider_modules() -> None:
    """s2s_port and fake_s2s must not import any real network/provider libraries."""
    import cordia_agent.streaming.providers.s2s_port as s2s_port_mod
    import tests._stubs.streaming_s2s_fake as fake_s2s_mod

    combined_source = "\n".join(
        [inspect.getsource(s2s_port_mod), inspect.getsource(fake_s2s_mod)]
    )
    forbidden = [
        "boto3",
        "deepgram",
        "google.cloud",
        "httpx",
        "openai",
        "langchain",
        "requests",
        "socket",
        "websocket",
        "grpc",
        "twilio",
    ]
    assert not any(token in combined_source for token in forbidden)


# ---------------------------------------------------------------------------
# Committable discipline (Inv-16)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_committable_discipline_full_script() -> None:
    """Only transcript.final and agent.text.final are committable (Inv-16)."""
    script = [
        S2SPartialTranscript(text="um", stability=0.3),
        S2SFinalTranscript(text="book a table", asr_confidence=0.92),
        S2SAgentText(text="Sure, booking now."),
        S2SAudioChunk(audio_ref="s2s://fake/chunk/0"),
        S2STurnCancelled(),
    ]
    frames = await _collect(script)

    assert len(frames) == 5

    partial_frame = frames[0]
    transcript_final_frame = frames[1]
    agent_text_final_frame = frames[2]
    audio_frame = frames[3]
    turn_cancelled_frame = frames[4]

    assert isinstance(partial_frame, TranscriptPartialFrame)
    assert isinstance(transcript_final_frame, TranscriptFinalFrame)
    assert isinstance(agent_text_final_frame, AgentTextFinalFrame)
    assert isinstance(audio_frame, TtsAudioDeltaFrame)
    assert isinstance(turn_cancelled_frame, TurnCancelledFrame)

    # Committable: only finals
    assert is_committable(transcript_final_frame) is True
    assert is_committable(agent_text_final_frame) is True

    # Non-committable
    assert is_committable(partial_frame) is False
    assert is_committable(audio_frame) is False
    assert is_committable(turn_cancelled_frame) is False


@pytest.mark.unit
async def test_turn_cancelled_has_material_content_delivered_false() -> None:
    """S2STurnCancelled → TurnCancelledFrame with material_content_delivered=False."""
    frames = await _collect([S2STurnCancelled()])
    assert len(frames) == 1
    frame = frames[0]
    assert isinstance(frame, TurnCancelledFrame)
    assert frame.material_content_delivered is False
    assert frame.type == StreamFrameType.TURN_CANCELLED


@pytest.mark.unit
async def test_turn_cancelled_marks_turn_non_authoritative() -> None:
    """After S2STurnCancelled, turn_manager.is_authoritative_turn(turn_id) is False (Inv-16)."""
    tm = TurnManager()
    frames = await _collect([S2STurnCancelled()], turn_manager=tm)

    cancelled = frames[0]
    assert isinstance(cancelled, TurnCancelledFrame)
    assert cancelled.cancelled_turn_id == ENGAGE_KWARGS["turn_id"]

    assert not tm.is_authoritative_turn(ENGAGE_KWARGS["turn_id"])
    # An unrelated turn is unaffected
    assert tm.is_authoritative_turn("some_other_turn")


# ---------------------------------------------------------------------------
# Envelope propagation (Inv-13)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_envelope_propagated_unchanged_to_all_frames() -> None:
    """Every frame carries the exact tenant_id/session_id/turn_id/correlation_id (Inv-13)."""
    script = [
        S2SPartialTranscript(text="hi", stability=0.5),
        S2SFinalTranscript(text="hi there", asr_confidence=0.9),
        S2SAgentText(text="Hello!"),
        S2SAudioChunk(audio_ref="s2s://chunk/0"),
        S2STurnCancelled(),
    ]
    frames = await _collect(script)

    for frame in frames:
        assert frame.tenant_id == ENGAGE_KWARGS["tenant_id"]
        assert frame.session_id == ENGAGE_KWARGS["session_id"]
        assert frame.turn_id == ENGAGE_KWARGS["turn_id"]
        assert frame.correlation_id == ENGAGE_KWARGS["correlation_id"]
        assert frame.causation_id == ENGAGE_KWARGS["causation_id"]


@pytest.mark.unit
async def test_seq_is_monotonic_per_session() -> None:
    """seq values are monotonically increasing (assigned via turn_manager.next_seq)."""
    script = [
        S2SPartialTranscript(text="hi", stability=0.5),
        S2SFinalTranscript(text="hi there", asr_confidence=0.9),
        S2SAgentText(text="Hello!"),
    ]
    frames = await _collect(script)

    seqs = [f.seq for f in frames]
    assert seqs == list(range(len(seqs)))


@pytest.mark.unit
async def test_seq_continues_from_shared_turn_manager_state() -> None:
    """If TurnManager already has seq advances for the session, new frames continue from there."""
    tm = TurnManager()
    # Simulate 5 prior seq assignments on the same session
    for _ in range(5):
        tm.next_seq(ENGAGE_KWARGS["session_id"])

    frames = await _collect(
        [S2SPartialTranscript(text="x", stability=0.1)], turn_manager=tm
    )
    assert len(frames) == 1
    assert frames[0].seq == 5


@pytest.mark.unit
async def test_ts_is_utc_aware_datetime() -> None:
    """Every emitted frame has a UTC-aware timestamp."""
    frames = await _collect([S2SPartialTranscript(text="x", stability=0.1)])
    frame = frames[0]
    assert frame.ts.utcoffset() == UTC.utcoffset(frame.ts)


# ---------------------------------------------------------------------------
# Event → frame type mapping
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_partial_transcript_maps_to_transcript_partial() -> None:
    frames = await _collect([S2SPartialTranscript(text="interim", stability=0.4)])
    assert len(frames) == 1
    frame = frames[0]
    assert isinstance(frame, TranscriptPartialFrame)
    assert frame.type == StreamFrameType.TRANSCRIPT_PARTIAL
    assert frame.text == "interim"
    assert frame.stability == 0.4


@pytest.mark.unit
async def test_final_transcript_maps_to_transcript_final() -> None:
    frames = await _collect(
        [S2SFinalTranscript(text="book a table", asr_confidence=0.88)]
    )
    assert len(frames) == 1
    frame = frames[0]
    assert isinstance(frame, TranscriptFinalFrame)
    assert frame.type == StreamFrameType.TRANSCRIPT_FINAL
    assert frame.text == "book a table"
    assert frame.asr_confidence == 0.88


@pytest.mark.unit
async def test_agent_text_maps_to_agent_text_final() -> None:
    frames = await _collect([S2SAgentText(text="Booking confirmed.")])
    assert len(frames) == 1
    frame = frames[0]
    assert isinstance(frame, AgentTextFinalFrame)
    assert frame.type == StreamFrameType.AGENT_TEXT_FINAL
    assert frame.text == "Booking confirmed."


@pytest.mark.unit
async def test_audio_chunk_maps_to_tts_audio_delta() -> None:
    frames = await _collect([S2SAudioChunk(audio_ref="s2s://fake/0")])
    assert len(frames) == 1
    frame = frames[0]
    assert isinstance(frame, TtsAudioDeltaFrame)
    assert frame.type == StreamFrameType.TTS_AUDIO_DELTA
    assert frame.audio_chunk_ref == "s2s://fake/0"
    assert frame.chunk_seq == 0


@pytest.mark.unit
async def test_audio_chunk_seq_increments_across_chunks() -> None:
    """chunk_seq increments for successive S2SAudioChunk events."""
    script = [
        S2SAudioChunk(audio_ref="s2s://fake/0"),
        S2SAudioChunk(audio_ref="s2s://fake/1"),
        S2SAudioChunk(audio_ref="s2s://fake/2"),
    ]
    frames = await _collect(script)
    assert all(isinstance(f, TtsAudioDeltaFrame) for f in frames)
    assert [f.chunk_seq for f in frames] == [0, 1, 2]


# ---------------------------------------------------------------------------
# Opt-in / no behavior change to 01-H (Inv-16 + structural guarantee)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_pipeline_source_has_no_s2s_reference() -> None:
    """pipeline.py must NOT reference s2s or S2S — the adapter is opt-in only."""
    source = inspect.getsource(pipeline_module)
    assert "s2s" not in source
    assert "S2S" not in source


@pytest.mark.unit
def test_adapter_requires_explicit_provider_cannot_be_engaged_implicitly() -> None:
    """S2SEscalationAdapter __init__ requires an S2SProvider — no default/implicit wiring."""
    import inspect as _inspect

    sig = _inspect.signature(S2SEscalationAdapter.__init__)
    params = sig.parameters
    assert "provider" in params, "S2SEscalationAdapter must have a 'provider' parameter"
    # provider has no default — it must be supplied explicitly
    assert params["provider"].default is _inspect.Parameter.empty, (
        "'provider' must not have a default value"
    )
