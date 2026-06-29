"""Unit tests for streaming turn-management behavior."""

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import TurnManager, is_committable
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    AudioStartedFrame,
    BargeInFrame,
    ToolProgressFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
    TtsAudioDeltaFrame,
    TurnCancelledFrame,
)


ENVELOPE_FIELDS = {
    "tenant_id": "tenant_1",
    "session_id": "session_1",
    "turn_id": "turn_1",
    "correlation_id": "correlation_1",
    "seq": 0,
    "ts": "2026-06-18T00:00:00+00:00",
}


def _transcript_final(**overrides: object) -> TranscriptFinalFrame:
    return TranscriptFinalFrame(
        type=StreamFrameType.TRANSCRIPT_FINAL,
        **{
            **ENVELOPE_FIELDS,
            "text": "final transcript",
            "asr_confidence": 0.92,
            **overrides,
        },
    )


def _transcript_partial(**overrides: object) -> TranscriptPartialFrame:
    return TranscriptPartialFrame(
        type=StreamFrameType.TRANSCRIPT_PARTIAL,
        **{
            **ENVELOPE_FIELDS,
            "text": "partial transcript",
            "stability": 0.4,
            **overrides,
        },
    )


def _agent_text_final(**overrides: object) -> AgentTextFinalFrame:
    return AgentTextFinalFrame(
        type=StreamFrameType.AGENT_TEXT_FINAL,
        **{
            **ENVELOPE_FIELDS,
            "text": "complete assistant response",
            **overrides,
        },
    )


def _tool_progress(**overrides: object) -> ToolProgressFrame:
    return ToolProgressFrame(
        type=StreamFrameType.TOOL_PROGRESS,
        **{
            **ENVELOPE_FIELDS,
            "tool_name": "check_availability",
            "phase": "checking_availability",
            "elapsed_ms": 120,
            **overrides,
        },
    )


def _turn_cancelled(**overrides: object) -> TurnCancelledFrame:
    return TurnCancelledFrame(
        type=StreamFrameType.TURN_CANCELLED,
        **{
            **ENVELOPE_FIELDS,
            "cancelled_turn_id": "turn_1",
            "material_content_delivered": False,
            **overrides,
        },
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    "frame",
    [
        _transcript_final(),
        _agent_text_final(),
    ],
)
def test_final_text_frames_are_committable(frame: object) -> None:
    assert is_committable(frame)


@pytest.mark.unit
@pytest.mark.parametrize(
    "frame",
    [
        AudioStartedFrame(type=StreamFrameType.AUDIO_STARTED, **ENVELOPE_FIELDS),
        _transcript_partial(),
        AgentTextDeltaFrame(
            type=StreamFrameType.AGENT_TEXT_DELTA,
            **ENVELOPE_FIELDS,
            delta="partial assistant response",
        ),
        _tool_progress(),
        TtsAudioDeltaFrame(
            type=StreamFrameType.TTS_AUDIO_DELTA,
            **ENVELOPE_FIELDS,
            audio_chunk_ref="chunk_1",
            chunk_seq=0,
        ),
        BargeInFrame(type=StreamFrameType.BARGE_IN_DETECTED, **ENVELOPE_FIELDS),
        _turn_cancelled(),
    ],
)
def test_non_final_frames_are_not_committable(frame: object) -> None:
    assert not is_committable(frame)


@pytest.mark.unit
def test_seq_is_strictly_monotonic_per_session_and_independent() -> None:
    manager = TurnManager()

    assert manager.next_seq("session_a") == 0
    assert manager.next_seq("session_a") == 1
    assert manager.next_seq("session_b") == 0
    assert manager.next_seq("session_a") == 2
    assert manager.next_seq("session_b") == 1


@pytest.mark.unit
def test_duplicate_frame_key_is_detected_per_session() -> None:
    manager = TurnManager()
    frame = _transcript_final(session_id="session_a", seq=7)
    duplicate = _transcript_partial(session_id="session_a", seq=7)
    same_seq_different_session = _transcript_final(session_id="session_b", seq=7)

    assert not manager.is_already_seen(frame)
    assert not manager.record_seen(frame)
    assert manager.is_already_seen(duplicate)
    assert manager.record_seen(duplicate)
    assert not manager.is_already_seen(same_seq_different_session)
    assert not manager.record_seen(same_seq_different_session)


@pytest.mark.unit
def test_undelivered_cancellation_marks_cancelled_turn_non_authoritative() -> None:
    manager = TurnManager()

    manager.handle_cancellation(_turn_cancelled(cancelled_turn_id="assistant_turn_1"))

    assert not manager.is_authoritative_turn("assistant_turn_1")
    assert manager.is_authoritative_turn("customer_turn_2")


@pytest.mark.unit
def test_delivered_cancellation_does_not_invalidate_turn() -> None:
    manager = TurnManager()

    manager.handle_cancellation(
        _turn_cancelled(
            cancelled_turn_id="assistant_turn_1",
            material_content_delivered=True,
        )
    )

    assert manager.is_authoritative_turn("assistant_turn_1")


@pytest.mark.unit
def test_subsequent_commit_must_come_from_authoritative_new_turn() -> None:
    manager = TurnManager()
    manager.handle_cancellation(_turn_cancelled(cancelled_turn_id="assistant_turn_1"))
    cancelled_turn_commit = _agent_text_final(turn_id="assistant_turn_1")
    new_customer_commit = _transcript_final(turn_id="customer_turn_2", seq=1)

    assert is_committable(cancelled_turn_commit)
    assert not manager.is_authoritative_turn(cancelled_turn_commit.turn_id)
    assert is_committable(new_customer_commit)
    assert manager.is_authoritative_turn(new_customer_commit.turn_id)


@pytest.mark.unit
def test_tool_call_acknowledgement_is_idempotent_by_turn_and_tool() -> None:
    manager = TurnManager()

    assert manager.ack_tool_call(
        _tool_progress(turn_id="turn_1", tool_name="check_availability")
    )
    assert not manager.ack_tool_call(
        _tool_progress(turn_id="turn_1", tool_name="check_availability", seq=1)
    )
    assert manager.ack_tool_call(
        _tool_progress(turn_id="turn_1", tool_name="get_tenant_context", seq=2)
    )
    assert manager.ack_tool_call(
        _tool_progress(turn_id="turn_2", tool_name="check_availability", seq=3)
    )
