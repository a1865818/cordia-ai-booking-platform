"""Unit tests for the streaming STT adapter."""

from collections.abc import AsyncIterator
from datetime import UTC
from inspect import getsource

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import (
    STTAdapter,
    STTFrameEnvelope,
    TurnManager,
    is_committable,
)
from cordia_agent.streaming.providers import (
    STTFinalEvent,
    STTPartialEvent,
)
from cordia_agent.streaming.providers import types as provider_types_module
from cordia_agent.streaming_models import TranscriptFinalFrame, TranscriptPartialFrame
from tests._stubs import streaming_providers_fake as fake_provider_module
from tests._stubs.streaming_providers_fake import FakeSTTProvider


ENVELOPE = STTFrameEnvelope(
    tenant_id="tenant_1",
    session_id="session_1",
    turn_id="turn_1",
    correlation_id="correlation_1",
    causation_id="causation_1",
)


async def _pcm_chunks(count: int) -> AsyncIterator[bytes]:
    for index in range(count):
        yield f"pcm_{index}".encode()


async def _collect_frames(
    provider: FakeSTTProvider,
    *,
    turn_manager: TurnManager | None = None,
    chunks: int = 3,
) -> list[TranscriptPartialFrame | TranscriptFinalFrame]:
    adapter = STTAdapter(provider=provider, turn_manager=turn_manager or TurnManager())
    return [
        frame
        async for frame in adapter.transcribe(_pcm_chunks(chunks), envelope=ENVELOPE)
    ]


@pytest.mark.unit
async def test_fake_provider_events_become_ordered_partial_then_final_frames() -> None:
    frames = await _collect_frames(
        FakeSTTProvider(
            [
                STTPartialEvent(text="i need", stability=0.35),
                STTPartialEvent(text="i need a table", stability=0.72),
                STTFinalEvent(text="i need a table", asr_confidence=0.91),
            ]
        )
    )

    assert [frame.type for frame in frames] == [
        StreamFrameType.TRANSCRIPT_PARTIAL,
        StreamFrameType.TRANSCRIPT_PARTIAL,
        StreamFrameType.TRANSCRIPT_FINAL,
    ]
    assert all(isinstance(frame, TranscriptPartialFrame) for frame in frames[:-1])
    assert isinstance(frames[-1], TranscriptFinalFrame)
    assert (
        len([frame for frame in frames if isinstance(frame, TranscriptFinalFrame)]) == 1
    )


@pytest.mark.unit
async def test_adapter_propagates_envelope_fields_and_aware_utc_timestamps() -> None:
    frames = await _collect_frames(
        FakeSTTProvider(
            [
                STTPartialEvent(text="hello", stability=0.2),
                STTFinalEvent(text="hello", asr_confidence=0.8),
            ]
        ),
        chunks=2,
    )

    for frame in frames:
        assert frame.tenant_id == ENVELOPE.tenant_id
        assert frame.session_id == ENVELOPE.session_id
        assert frame.turn_id == ENVELOPE.turn_id
        assert frame.correlation_id == ENVELOPE.correlation_id
        assert frame.causation_id == ENVELOPE.causation_id
        assert frame.ts.tzinfo is not None
        assert frame.ts.utcoffset() == UTC.utcoffset(frame.ts)


@pytest.mark.unit
async def test_seq_values_are_monotonic_and_sourced_from_shared_turn_manager() -> None:
    turn_manager = TurnManager()

    assert turn_manager.next_seq(ENVELOPE.session_id) == 0
    frames = await _collect_frames(
        FakeSTTProvider(
            [
                STTPartialEvent(text="hello", stability=0.2),
                STTFinalEvent(text="hello", asr_confidence=0.8),
            ]
        ),
        turn_manager=turn_manager,
        chunks=2,
    )

    assert [frame.seq for frame in frames] == [1, 2]
    assert turn_manager.next_seq(ENVELOPE.session_id) == 3


@pytest.mark.unit
async def test_final_frame_includes_confidence_and_endpoint_flag() -> None:
    frames = await _collect_frames(
        FakeSTTProvider([STTFinalEvent(text="done", asr_confidence=0.64)]),
        chunks=1,
    )

    final = frames[0]
    assert isinstance(final, TranscriptFinalFrame)
    assert final.asr_confidence == 0.64
    assert final.is_endpoint is True


@pytest.mark.unit
@pytest.mark.parametrize(
    ("provider_confidence", "expected_confidence"),
    [
        (-0.5, 0.0),
        (1.7, 1.0),
    ],
)
async def test_final_confidence_is_clamped_before_frame_validation(
    provider_confidence: float, expected_confidence: float
) -> None:
    frames = await _collect_frames(
        FakeSTTProvider(
            [STTFinalEvent(text="done", asr_confidence=provider_confidence)]
        ),
        chunks=1,
    )

    final = frames[0]
    assert isinstance(final, TranscriptFinalFrame)
    assert final.asr_confidence == expected_confidence


@pytest.mark.unit
async def test_partial_frames_are_not_committable_and_final_frame_is_committable() -> (
    None
):
    frames = await _collect_frames(
        FakeSTTProvider(
            [
                STTPartialEvent(text="book", stability=0.25),
                STTFinalEvent(text="book", asr_confidence=0.93),
            ]
        ),
        chunks=2,
    )

    assert is_committable(frames[0]) is False
    assert is_committable(frames[1]) is True


@pytest.mark.unit
async def test_adapter_emits_only_first_endpoint_final() -> None:
    frames = await _collect_frames(
        FakeSTTProvider(
            [
                STTFinalEvent(text="first", asr_confidence=0.9),
                STTFinalEvent(text="second", asr_confidence=0.8),
            ]
        ),
        chunks=2,
    )

    assert [frame.text for frame in frames] == ["first"]


@pytest.mark.unit
async def test_non_endpoint_final_event_is_rejected() -> None:
    provider = FakeSTTProvider(
        [STTFinalEvent(text="not an endpoint", asr_confidence=0.8, is_endpoint=False)]
    )

    with pytest.raises(ValueError, match="non-endpoint STTFinalEvent"):
        await _collect_frames(provider, chunks=1)


@pytest.mark.unit
def test_provider_port_and_fake_do_not_import_real_stt_or_network_dependencies() -> (
    None
):
    source = "\n".join(
        [
            getsource(provider_types_module),
            getsource(fake_provider_module),
        ]
    )

    forbidden_tokens = [
        "boto3",
        "deepgram",
        "google.cloud",
        "httpx",
        "openai",
        "requests",
        "socket",
        "websocket",
    ]
    assert not any(token in source for token in forbidden_tokens)
