"""Unit tests for barge-in cancellation of active assistant TTS."""

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import BargeInController, TTSChunker, TurnManager
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    TranscriptPartialFrame,
)
from tests._stubs.streaming_tts_fake import FakeTTSProvider


ENVELOPE_FIELDS = {
    "tenant_id": "tenant_1",
    "session_id": "session_1",
    "correlation_id": "correlation_1",
    "causation_id": "causation_1",
    "ts": "2026-06-18T00:00:00+00:00",
}


def _agent_delta(delta: str, *, seq: int = 0) -> AgentTextDeltaFrame:
    return AgentTextDeltaFrame(
        type=StreamFrameType.AGENT_TEXT_DELTA,
        **ENVELOPE_FIELDS,
        turn_id="assistant_turn_1",
        seq=seq,
        delta=delta,
    )


def _agent_final(text: str, *, seq: int = 1) -> AgentTextFinalFrame:
    return AgentTextFinalFrame(
        type=StreamFrameType.AGENT_TEXT_FINAL,
        **ENVELOPE_FIELDS,
        turn_id="assistant_turn_1",
        seq=seq,
        text=text,
    )


def _caller_partial() -> TranscriptPartialFrame:
    return TranscriptPartialFrame(
        type=StreamFrameType.TRANSCRIPT_PARTIAL,
        **ENVELOPE_FIELDS,
        turn_id="caller_turn_1",
        seq=5,
        text="wait",
        stability=0.8,
    )


@pytest.mark.unit
async def test_caller_speech_during_active_tts_emits_barge_in_then_cancelled() -> None:
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=2),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_agent_delta("Your booking is confirmed."))
    first_audio = await anext(synthesis)
    result = controller.caller_speech_started(_caller_partial())

    assert first_audio.type == StreamFrameType.TTS_AUDIO_DELTA
    assert [frame.type for frame in result.frames] == [
        StreamFrameType.BARGE_IN_DETECTED,
        StreamFrameType.TURN_CANCELLED,
    ]
    assert result.cancelled_turn_id == "assistant_turn_1"


@pytest.mark.unit
async def test_barge_in_cancellation_is_undelivered_and_non_authoritative() -> None:
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=2),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_agent_delta("Your booking is confirmed."))
    await anext(synthesis)
    result = controller.caller_speech_started(_caller_partial())

    cancellation = result.frames[1]
    assert cancellation.cancelled_turn_id == "assistant_turn_1"
    assert cancellation.material_content_delivered is False
    assert not turn_manager.is_authoritative_turn("assistant_turn_1")


@pytest.mark.unit
async def test_synthesis_stops_within_one_chunk_after_barge_in() -> None:
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=3),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_agent_delta("Your booking is confirmed."))
    await anext(synthesis)
    controller.caller_speech_started(_caller_partial())

    escaped_after_cancellation = await anext(synthesis)
    with pytest.raises(StopAsyncIteration):
        await anext(synthesis)

    assert escaped_after_cancellation.audio_chunk_ref == (
        "tts://fake/assistant_turn_1/0/1"
    )


@pytest.mark.unit
async def test_cancelled_readback_final_is_structural_but_not_authoritative() -> None:
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=2),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_agent_delta("Your booking is confirmed."))
    await anext(synthesis)
    controller.caller_speech_started(_caller_partial())
    final = _agent_final("Your booking is confirmed.")

    assert final.type == StreamFrameType.AGENT_TEXT_FINAL
    assert not turn_manager.is_authoritative_turn(final.turn_id)


@pytest.mark.unit
def test_no_active_tts_produces_no_barge_in_frames() -> None:
    turn_manager = TurnManager()
    chunker = TTSChunker(provider=FakeTTSProvider(), turn_manager=turn_manager)
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    result = controller.caller_speech_started(_caller_partial())

    assert result.frames == ()
    assert result.cancelled_turn_id is None


@pytest.mark.unit
async def test_repeated_caller_partials_emit_single_barge_in_cancellation() -> None:
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=2),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_agent_delta("Your booking is confirmed."))
    await anext(synthesis)
    partial = _caller_partial()

    first = controller.caller_speech_started(partial)
    second = controller.caller_speech_started(partial)

    assert [frame.type for frame in first.frames] == [
        StreamFrameType.BARGE_IN_DETECTED,
        StreamFrameType.TURN_CANCELLED,
    ]
    assert first.cancelled_turn_id == "assistant_turn_1"
    assert second.frames == ()
    assert second.cancelled_turn_id is None


@pytest.mark.unit
async def test_queued_sentence_chunks_are_not_synthesized_after_barge_in() -> None:
    turn_manager = TurnManager()
    provider = FakeTTSProvider(refs_per_text_chunk=1)
    chunker = TTSChunker(provider=provider, turn_manager=turn_manager)
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_agent_delta("One. Two. Three."))
    await anext(synthesis)
    controller.caller_speech_started(_caller_partial())

    remaining = [frame async for frame in synthesis]

    assert remaining == []
    assert provider.history == [("One.", "assistant_turn_1", 0)]


@pytest.mark.unit
async def test_completed_assistant_turn_does_not_trigger_barge_in() -> None:
    turn_manager = TurnManager()
    chunker = TTSChunker(provider=FakeTTSProvider(), turn_manager=turn_manager)
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    async for _frame in chunker.process_frame(_agent_delta("All done.")):
        pass
    async for _frame in chunker.process_frame(_agent_final("All done.")):
        pass

    assert chunker.active_turn is None
    result = controller.caller_speech_started(_caller_partial())

    assert result.frames == ()
    assert result.cancelled_turn_id is None
    assert turn_manager.is_authoritative_turn("assistant_turn_1")
