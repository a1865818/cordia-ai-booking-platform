"""Barge-in must still fire on the realtime provider, and fire authoritatively.

``BargeInController.caller_speech_started`` is only ever reached from a
``TranscriptPartialFrame``.  The batch provider manufactures that frame from a
local energy heuristic; the realtime provider gets it from the service's own
``input_audio_buffer.speech_started``.  These tests are the batch suite's
structural twin — the REAL provider through the REAL ``STTAdapter`` into the
REAL ``BargeInController`` — proving the path is identical from the port
downwards, and that the interrupt still lands BEFORE any transcript exists.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import (
    BargeInController,
    STTAdapter,
    STTFrameEnvelope,
    TTSChunker,
    TurnManager,
)
from cordia_agent.streaming.providers.azure_realtime_stt import (
    AzureRealtimeSTTProvider,
)
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
)
from tests._stubs.streaming_tts_fake import FakeTTSProvider
from tests.unit_tests.streaming.conftest import (
    FakeRealtimeSocket,
    make_realtime_connect,
    pcm_stream,
    speech,
)


if TYPE_CHECKING:
    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings


CALLER_ENVELOPE = STTFrameEnvelope(
    tenant_id="tenant_1",
    session_id="session_1",
    turn_id="caller_turn_1",
    correlation_id="correlation_1",
    causation_id="causation_1",
)

SPEECH_STARTED = {"type": "input_audio_buffer.speech_started"}
SPEECH_STOPPED = {"type": "input_audio_buffer.speech_stopped"}
COMPLETED = {
    "type": "conversation.item.input_audio_transcription.completed",
    "transcript": "actually, cancel that",
}


def _agent_delta() -> AgentTextDeltaFrame:
    return AgentTextDeltaFrame(
        type=StreamFrameType.AGENT_TEXT_DELTA,
        tenant_id="tenant_1",
        session_id="session_1",
        turn_id="assistant_turn_1",
        correlation_id="correlation_1",
        causation_id="causation_1",
        seq=0,
        ts="2026-06-18T00:00:00+00:00",
        delta="Your booking is confirmed for seven.",
    )


def _stt_provider(
    settings: AzureAudioSettings, socket: FakeRealtimeSocket
) -> AzureRealtimeSTTProvider:
    return AzureRealtimeSTTProvider(settings, connect=make_realtime_connect(socket))


def _socket() -> FakeRealtimeSocket:
    return FakeRealtimeSocket([SPEECH_STARTED, SPEECH_STOPPED, COMPLETED])


@pytest.mark.unit
async def test_service_speech_onset_becomes_a_transcript_partial_frame(
    realtime_settings: AzureAudioSettings,
) -> None:
    adapter = STTAdapter(
        provider=_stt_provider(realtime_settings, _socket()),
        turn_manager=TurnManager(),
    )

    frames = [
        frame
        async for frame in adapter.transcribe(
            pcm_stream(speech(2)), envelope=CALLER_ENVELOPE
        )
    ]

    assert isinstance(frames[0], TranscriptPartialFrame)
    assert frames[0].type == StreamFrameType.TRANSCRIPT_PARTIAL
    assert frames[0].text == ""
    assert frames[0].stability == 0.0
    assert frames[0].turn_id == "caller_turn_1"
    assert isinstance(frames[1], TranscriptFinalFrame)


@pytest.mark.unit
async def test_onset_partial_cancels_active_tts_before_any_transcript_exists(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = _socket()
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=3), turn_manager=turn_manager
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    # The agent is mid-sentence: an active, cancellable TTS turn exists.
    synthesis = chunker.process_frame(_agent_delta())
    await anext(synthesis)
    assert chunker.active_turn is not None

    adapter = STTAdapter(
        provider=_stt_provider(realtime_settings, socket), turn_manager=turn_manager
    )
    stream = adapter.transcribe(pcm_stream(speech(2)), envelope=CALLER_ENVELOPE)
    first_frame = await anext(stream)

    assert isinstance(first_frame, TranscriptPartialFrame)
    # The decisive assertion: the SERVICE has told us the caller is talking and
    # no transcript has been produced yet, so the interrupt lands while the
    # agent is still speaking rather than a full endpoint later.
    assert first_frame.text == ""

    result = controller.caller_speech_started(first_frame)

    assert [frame.type for frame in result.frames] == [
        StreamFrameType.BARGE_IN_DETECTED,
        StreamFrameType.TURN_CANCELLED,
    ]
    assert result.cancelled_turn_id == "assistant_turn_1"
    assert chunker.active_turn is None

    await stream.aclose()


@pytest.mark.unit
async def test_the_cancelled_tts_turn_stops_producing_audio(
    realtime_settings: AzureAudioSettings,
) -> None:
    turn_manager = TurnManager()
    fake_tts = FakeTTSProvider(refs_per_text_chunk=5)
    chunker = TTSChunker(provider=fake_tts, turn_manager=turn_manager)
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_agent_delta())
    await anext(synthesis)

    adapter = STTAdapter(
        provider=_stt_provider(realtime_settings, _socket()),
        turn_manager=turn_manager,
    )
    stream = adapter.transcribe(pcm_stream(speech(2)), envelope=CALLER_ENVELOPE)
    partial = await anext(stream)
    assert isinstance(partial, TranscriptPartialFrame)
    controller.caller_speech_started(partial)

    remaining = [frame async for frame in synthesis]

    # Without the onset partial there would have been nothing to trigger
    # cancellation, and all five refs would have played out.
    assert len(remaining) < 4
    await stream.aclose()
