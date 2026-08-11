"""Unit tests for the Azure realtime STT provider.

Every connection is served by :class:`FakeRealtimeSocket` through the
provider's injected factory; the suite is fully offline and no socket is ever
opened.  The focus is the verified wire protocol (session bootstrap, the
server-VAD event sequence), the mapping of those events onto the ``STTProvider``
port, and the concurrency contract: bounded waits, clean shutdown of both
directions, and no orphaned tasks.
"""

from __future__ import annotations

import asyncio
import dataclasses
import logging
from typing import TYPE_CHECKING, Any

import pytest
from websockets.datastructures import Headers
from websockets.exceptions import ConnectionClosedError, InvalidStatus
from websockets.http11 import Response

from cordia_agent.streaming.providers.audio_config import AudioProviderError
from cordia_agent.streaming.providers.azure_realtime_stt import (
    DELTA_PARTIAL_STABILITY,
    AzureRealtimeSTTProvider,
    confidence_from_logprobs,
    error_code,
)
from cordia_agent.streaming.providers.types import STTFinalEvent, STTPartialEvent
from tests.unit_tests.streaming.conftest import (
    ConnectRecord,
    FakeRealtimeSocket,
    make_realtime_connect,
    pcm_stream,
    speech,
)


if TYPE_CHECKING:
    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings
    from cordia_agent.streaming.providers.types import STTProviderEvent


SPEECH_STARTED = {"type": "input_audio_buffer.speech_started"}
SPEECH_STOPPED = {"type": "input_audio_buffer.speech_stopped"}


def completed(transcript: str, **extra: Any) -> dict[str, Any]:
    """Build a canned ``...transcription.completed`` frame."""
    return {
        "type": "conversation.item.input_audio_transcription.completed",
        "transcript": transcript,
        **extra,
    }


def delta(text: str) -> dict[str, Any]:
    """Build a canned ``...transcription.delta`` frame."""
    return {
        "type": "conversation.item.input_audio_transcription.delta",
        "delta": text,
    }


def utterance(transcript: str) -> list[dict[str, Any]]:
    """The full verified frame sequence for one spoken utterance."""
    return [SPEECH_STARTED, SPEECH_STOPPED, completed(transcript)]


async def parked_source() -> Any:
    """A caller stream that delivers one chunk and then stays open forever.

    Models a live line: the media plane has not hung up, it simply has nothing
    to send right now.  Because the audio is never exhausted, the provider's
    "caller finished and nothing is outstanding" shortcut cannot end the stream,
    so a test can script an arbitrary sequence of server frames and be sure all
    of them are consumed.
    """
    yield speech(1)
    await asyncio.Event().wait()


async def collect(
    settings: AzureAudioSettings,
    socket: FakeRealtimeSocket,
    *,
    payload: bytes | None = None,
) -> list[STTProviderEvent]:
    """Drain the provider against *socket* and return every event.

    With no *payload* the caller stream is :func:`parked_source`, so the
    scripted frames — not the audio running out — decide when the stream ends.
    """
    provider = AzureRealtimeSTTProvider(settings, connect=make_realtime_connect(socket))
    source = parked_source() if payload is None else pcm_stream(payload)
    return [event async for event in provider.transcribe(source)]


# ---------------------------------------------------------------------------
# Pure helpers
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_confidence_falls_back_when_no_logprobs_are_present() -> None:
    assert confidence_from_logprobs({"transcript": "hi"}, 0.42) == 0.42
    assert confidence_from_logprobs({"logprobs": []}, 0.42) == 0.42
    assert confidence_from_logprobs({"logprobs": [{"token": "hi"}]}, 0.42) == 0.42


@pytest.mark.unit
def test_confidence_is_derived_from_token_logprobs_and_clamped() -> None:
    payload = {"logprobs": [{"logprob": 0.0}, {"logprob": 0.0}]}

    assert confidence_from_logprobs(payload, 0.5) == pytest.approx(1.0)
    # exp(+2) > 1 — a nonsense value must not escape the frame contract.
    assert confidence_from_logprobs({"logprobs": [{"logprob": 2.0}]}, 0.5) == 1.0


@pytest.mark.unit
def test_error_code_never_reads_the_message() -> None:
    payload = {
        "type": "error",
        "error": {
            "code": "invalid_request_error",
            "message": "caller said: my card is 4111111111111111",
        },
    }

    assert error_code(payload) == "invalid_request_error"
    assert error_code({"type": "error", "error": {"type": "server_error"}}) == (
        "server_error"
    )
    assert error_code({"type": "error"}) == "unknown"


# ---------------------------------------------------------------------------
# Session bootstrap — the verified handshake
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_connect_targets_the_realtime_url_with_the_api_key(
    realtime_settings: AzureAudioSettings,
) -> None:
    captured: list[ConnectRecord] = []
    socket = FakeRealtimeSocket(utterance("hello"))
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket, captured)
    )

    async for _ in provider.transcribe(pcm_stream(speech(1))):
        pass

    assert len(captured) == 1
    url = captured[0].url
    assert url.startswith("wss://demo.openai.azure.com/openai/realtime?")
    assert "api-version=2025-04-01-preview" in url
    assert "intent=transcription" in url
    # Verified live: a `deployment` parameter is answered with HTTP 400
    # OperationNotSupported.  The model is named in the session config instead.
    assert "deployment" not in url
    assert captured[0].headers == {"api-key": "secret-key"}


@pytest.mark.unit
async def test_session_update_configures_the_model_and_server_vad(
    realtime_settings: AzureAudioSettings,
) -> None:
    settings = dataclasses.replace(realtime_settings, realtime_silence_duration_ms=700)
    socket = FakeRealtimeSocket(utterance("hello"))

    await collect(settings, socket)

    update = socket.sent[0]
    assert update["type"] == "transcription_session.update"
    assert update["session"] == {
        "input_audio_format": "pcm16",
        "input_audio_transcription": {"model": "gpt-realtime-whisper"},
        "turn_detection": {"type": "server_vad", "silence_duration_ms": 700},
    }


@pytest.mark.unit
async def test_a_rejected_handshake_raises_without_retrying_or_leaking(
    realtime_settings: AzureAudioSettings,
) -> None:
    captured: list[ConnectRecord] = []
    rejection = InvalidStatus(Response(401, "Unauthorized", Headers()))
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(rejection, captured)
    )

    with pytest.raises(AudioProviderError) as exc_info:
        async for _ in provider.transcribe(pcm_stream(speech(1))):
            pass

    assert exc_info.value.status_code == 401
    assert "secret-key" not in str(exc_info.value)
    # A 401 fails identically on a retry — one attempt only.
    assert len(captured) == 1


@pytest.mark.unit
async def test_every_connect_attempt_failing_raises_timeout_error(
    realtime_settings: AzureAudioSettings,
) -> None:
    captured: list[ConnectRecord] = []
    provider = AzureRealtimeSTTProvider(
        realtime_settings,
        connect=make_realtime_connect(ConnectionRefusedError("down"), captured),
    )

    with pytest.raises(TimeoutError):
        async for _ in provider.transcribe(pcm_stream(speech(1))):
            pass

    # Two attempts on a growing budget, per the shared audio retry policy.
    assert len(captured) == 2


@pytest.mark.unit
async def test_an_error_frame_during_the_handshake_is_surfaced(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(
        [{"type": "error", "error": {"code": "session_expired"}}],
        session_created=False,
    )

    with pytest.raises(AudioProviderError) as exc_info:
        await collect(realtime_settings, socket)

    assert exc_info.value.status_code == "session_expired"


# ---------------------------------------------------------------------------
# Event mapping onto the port
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_speech_started_then_completed_yields_a_partial_then_a_final(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(utterance("  a table for four  "))

    events = await collect(realtime_settings, socket)

    assert [type(event) for event in events] == [STTPartialEvent, STTFinalEvent]
    final = events[1]
    assert isinstance(final, STTFinalEvent)
    assert final.text == "a table for four"
    assert final.is_endpoint is True
    assert final.asr_confidence == realtime_settings.fallback_asr_confidence


@pytest.mark.unit
async def test_the_onset_partial_carries_no_hypothesis_and_zero_stability(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(utterance("hello"))

    events = await collect(realtime_settings, socket)
    partial = events[0]

    # speech_started is the authoritative barge-in trigger and nothing else; it
    # must not fabricate text.
    assert isinstance(partial, STTPartialEvent)
    assert partial.text == ""
    assert partial.stability == 0.0


@pytest.mark.unit
async def test_speech_stopped_alone_emits_nothing(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket([SPEECH_STARTED, SPEECH_STOPPED], when_drained="close")

    events = await collect(realtime_settings, socket)

    # Server-side endpointing carries no transcript, so no final rides on it.
    assert [type(event) for event in events] == [STTPartialEvent]


@pytest.mark.unit
async def test_transcription_deltas_become_content_bearing_partials(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(
        [SPEECH_STARTED, delta("a table"), delta(" for four"), completed("a table")]
    )

    events = await collect(realtime_settings, socket)

    assert [type(event) for event in events] == [
        STTPartialEvent,
        STTPartialEvent,
        STTPartialEvent,
        STTFinalEvent,
    ]
    assert [e.text for e in events[1:3] if isinstance(e, STTPartialEvent)] == [
        "a table",
        " for four",
    ]
    # Strictly above the bare onset trigger, strictly below a committed final.
    for event in events[1:3]:
        assert isinstance(event, STTPartialEvent)
        assert event.stability == DELTA_PARTIAL_STABILITY
        assert 0.0 < event.stability < 1.0


@pytest.mark.unit
async def test_an_empty_transcript_yields_no_final(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket([SPEECH_STARTED, SPEECH_STOPPED, completed("   ")])

    events = await collect(realtime_settings, socket)

    # The onset partial still fired (the caller DID make a noise), but a cough
    # must never become a committable empty transcript.
    assert [type(event) for event in events] == [STTPartialEvent]


@pytest.mark.unit
async def test_logprobs_on_the_completed_frame_drive_the_confidence(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(
        [SPEECH_STARTED, completed("hello", logprobs=[{"logprob": -0.2}])]
    )

    events = await collect(realtime_settings, socket)
    final = events[-1]

    assert isinstance(final, STTFinalEvent)
    assert final.asr_confidence == pytest.approx(0.8187, abs=1e-3)


@pytest.mark.unit
async def test_two_utterances_yield_two_partial_final_pairs(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(
        utterance("first") + utterance("second"), when_drained="close"
    )
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket)
    )

    events = [event async for event in provider.transcribe(parked_source())]

    assert [type(event) for event in events] == [
        STTPartialEvent,
        STTFinalEvent,
        STTPartialEvent,
        STTFinalEvent,
    ]
    assert [e.text for e in events if isinstance(e, STTFinalEvent)] == [
        "first",
        "second",
    ]


@pytest.mark.unit
async def test_an_error_frame_raises_carrying_the_code_only(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(
        [
            SPEECH_STARTED,
            {
                "type": "error",
                "error": {
                    "code": "invalid_request_error",
                    "message": "caller said: my card is 4111111111111111",
                },
            },
        ]
    )

    with pytest.raises(AudioProviderError) as exc_info:
        await collect(realtime_settings, socket)

    message = str(exc_info.value)
    assert exc_info.value.status_code == "invalid_request_error"
    assert exc_info.value.operation == "realtime transcription"
    assert "4111111111111111" not in message
    assert "caller said" not in message
    assert "secret-key" not in message


@pytest.mark.unit
async def test_unparseable_and_unknown_frames_are_ignored(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(
        [
            "not json at all",
            "[1, 2, 3]",
            {"type": "rate_limits.updated"},
            *utterance("still fine"),
        ]
    )

    events = await collect(realtime_settings, socket)

    assert [type(event) for event in events] == [STTPartialEvent, STTFinalEvent]


# ---------------------------------------------------------------------------
# Audio sending, backpressure, and redaction
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_caller_pcm_is_appended_as_base64_pcm16_frames(
    realtime_settings: AzureAudioSettings,
) -> None:
    # A silent-but-open socket: the stream can only end once the caller's audio
    # is exhausted, so every chunk is guaranteed to have been written.
    socket = FakeRealtimeSocket(when_drained="hang")
    payload = speech(3)

    events = await collect(realtime_settings, socket, payload=payload)

    assert events == []
    assert b"".join(socket.appended_audio) == payload


@pytest.mark.unit
async def test_an_oversized_chunk_is_split_into_bounded_append_frames(
    realtime_settings: AzureAudioSettings,
) -> None:
    settings = dataclasses.replace(realtime_settings, realtime_send_chunk_bytes=640)
    socket = FakeRealtimeSocket(when_drained="hang")

    # One 4-frame handover from the media plane, delivered as a single chunk.
    provider = AzureRealtimeSTTProvider(settings, connect=make_realtime_connect(socket))
    async for _ in provider.transcribe(pcm_stream(speech(4), chunk_bytes=2560)):
        pass

    appended = socket.appended_audio
    assert len(appended) == 4
    assert all(len(window) <= 640 for window in appended)
    assert b"".join(appended) == speech(4)


@pytest.mark.unit
async def test_the_sender_pulls_no_faster_than_the_socket_accepts(
    realtime_settings: AzureAudioSettings,
) -> None:
    """The producer is back-pressured by the socket, not buffered ahead of it."""
    pulled = 0
    released = asyncio.Event()

    async def producer() -> Any:
        nonlocal pulled
        for _ in range(5):
            pulled += 1
            yield speech(1)
        released.set()

    class BlockingSocket(FakeRealtimeSocket):
        """Accepts the session bootstrap, then stalls every audio write."""

        async def send(self, message: str) -> None:
            if "input_audio_buffer.append" in message:
                await blocked.wait()
            await super().send(message)

    blocked = asyncio.Event()
    socket = BlockingSocket(utterance("hello"))
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket)
    )
    stream = provider.transcribe(producer())
    first = await anext(stream)

    # The onset partial has already been delivered while every write is still
    # blocked — proof the receive side is not gated on the send side — and the
    # producer has been pulled from at most once: no queue ran ahead.
    assert isinstance(first, STTPartialEvent)
    assert pulled <= 1
    assert not released.is_set()

    blocked.set()
    await stream.aclose()


@pytest.mark.unit
async def test_no_audio_transcript_or_key_is_ever_logged(
    realtime_settings: AzureAudioSettings,
    caplog: pytest.LogCaptureFixture,
) -> None:
    socket = FakeRealtimeSocket(
        [SPEECH_STARTED, delta("my card is 4111111111111111"), completed("a secret")]
    )

    with caplog.at_level(logging.DEBUG):
        await collect(realtime_settings, socket, payload=speech(2))

    logged = "\n".join(record.getMessage() for record in caplog.records)
    assert "4111111111111111" not in logged
    assert "a secret" not in logged
    assert "secret-key" not in logged
    # base64 of the scripted PCM must not appear either.
    assert "AAA" not in logged or "input_audio_buffer.append" not in logged


# ---------------------------------------------------------------------------
# Concurrency: bounded waits, clean shutdown, no orphans
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_a_socket_closing_early_ends_the_stream_cleanly(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(
        [SPEECH_STARTED, ConnectionClosedError(None, None)],
    )

    events = await collect(realtime_settings, socket)

    assert [type(event) for event in events] == [STTPartialEvent]
    assert socket.closed is True


@pytest.mark.unit
async def test_the_drain_window_bounds_a_transcription_that_never_arrives(
    realtime_settings: AzureAudioSettings,
) -> None:
    # speech_started with no completion, and a socket that stays open and
    # silent: only the provider's own deadline can end this.
    socket = FakeRealtimeSocket([SPEECH_STARTED], when_drained="hang")

    loop = asyncio.get_running_loop()
    started = loop.time()
    events = await collect(realtime_settings, socket, payload=speech(1))
    elapsed = loop.time() - started

    assert [type(event) for event in events] == [STTPartialEvent]
    assert elapsed < 2.0
    assert elapsed >= realtime_settings.base_timeout_seconds


@pytest.mark.unit
async def test_a_silent_service_while_audio_flows_raises_timeout_error(
    realtime_settings: AzureAudioSettings,
) -> None:
    settings = dataclasses.replace(realtime_settings, max_utterance_seconds=0.05)
    socket = FakeRealtimeSocket(when_drained="hang")

    async def never_ending() -> Any:
        while True:
            yield speech(1)
            await asyncio.sleep(0.01)

    provider = AzureRealtimeSTTProvider(
        settings, connect=make_realtime_connect(socket)
    )

    with pytest.raises(TimeoutError):
        async for _ in provider.transcribe(never_ending()):
            pass


@pytest.mark.unit
async def test_abandoning_the_stream_leaves_no_task_and_closes_the_socket(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(utterance("hello"), when_drained="hang")

    async def endless() -> Any:
        while True:
            yield speech(1)
            await asyncio.sleep(0)

    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket)
    )
    before = {task for task in asyncio.all_tasks() if not task.done()}

    stream = provider.transcribe(endless())
    assert isinstance(await anext(stream), STTPartialEvent)
    # A consumer that stops at the first event — exactly what STTAdapter does
    # once it has a final.
    await stream.aclose()

    leaked = {
        task for task in asyncio.all_tasks() if not task.done() and task not in before
    }
    assert leaked == set()
    assert socket.closed is True


@pytest.mark.unit
async def test_a_failing_pcm_source_is_surfaced_not_swallowed(
    realtime_settings: AzureAudioSettings,
) -> None:
    socket = FakeRealtimeSocket(utterance("hello"), when_drained="hang")

    async def failing() -> Any:
        yield speech(1)
        raise RuntimeError("media plane died")

    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket)
    )

    with pytest.raises(RuntimeError, match="media plane died"):
        async for _ in provider.transcribe(failing()):
            pass

    assert socket.closed is True
