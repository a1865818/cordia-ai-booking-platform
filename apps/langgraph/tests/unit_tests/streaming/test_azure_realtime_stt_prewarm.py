"""Unit tests for holding the realtime transcription session open for a call.

Opening the socket costs a handshake plus a session round trip — measured at
~1.5 s against the live service — so paying it per utterance makes the first
barge-in of every call that much late.  These tests pin that the session is
opened once, reused, released on close, and transparently replaced if it dies.
"""

from __future__ import annotations

import asyncio
import json
from contextlib import aclosing
from typing import TYPE_CHECKING

import pytest
from websockets.exceptions import ConnectionClosedOK

from cordia_agent.streaming.providers.azure_realtime_stt import (
    SESSION_CREATED_EVENT,
    SESSION_UPDATE_EVENT,
    AzureRealtimeSTTProvider,
)
from tests.unit_tests.streaming.conftest import (
    ConnectRecord,
    FakeRealtimeSocket,
    make_realtime_connect,
    pcm_stream,
    speech,
)


if TYPE_CHECKING:
    from collections.abc import Mapping

    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings


COMPLETED = "conversation.item.input_audio_transcription.completed"


def transcript_events(text: str) -> list[dict[str, object]]:
    return [
        {"type": "input_audio_buffer.speech_started"},
        {"type": "input_audio_buffer.speech_stopped"},
        {"type": COMPLETED, "transcript": text},
    ]


def sequenced_connect(sockets: list[FakeRealtimeSocket], captured: list[ConnectRecord]):
    """A connect factory handing out *sockets* in order, recording each call."""

    async def connect(url: str, headers: Mapping[str, str]) -> FakeRealtimeSocket:
        captured.append(ConnectRecord(url=url, headers=dict(headers)))
        return sockets[min(len(captured) - 1, len(sockets) - 1)]

    return connect


class TurnScriptedSocket:
    """A realtime socket that only answers AFTER audio has been sent.

    ``FakeRealtimeSocket`` hands out its whole script from the first ``recv``,
    which is fine for a single stream and wrong for a reused session: it makes
    the second utterance's transcript available before the second utterance has
    been spoken.  This one arms the next batch of server frames when it sees
    fresh audio, which is what the service actually does — and is what makes
    "a stale transcript must not leak into the next turn" testable at all.
    """

    def __init__(self, batches: list[list[dict[str, object]]]) -> None:
        self._batches = [list(batch) for batch in batches]
        self._pending: list[dict[str, object]] = [{"type": SESSION_CREATED_EVENT}]
        self._armed = False
        self._never = asyncio.Event()
        self.sent: list[dict[str, object]] = []
        self.closed = False
        self.clears = 0

    async def send(self, message: str) -> None:
        if self.closed:
            raise ConnectionClosedOK(None, None)
        frame = json.loads(message)
        self.sent.append(frame)
        if frame.get("type") == "input_audio_buffer.clear":
            self.clears += 1
            self._armed = False
        elif frame.get("type") == "input_audio_buffer.append" and not self._armed:
            self._armed = True
            if self._batches:
                self._pending.extend(self._batches.pop(0))

    async def recv(self) -> str:
        if self._pending:
            return json.dumps(self._pending.pop(0))
        await self._never.wait()
        raise AssertionError("unreachable")

    async def close(self) -> None:
        self.closed = True


@pytest.mark.unit
async def test_prewarm_opens_the_session_before_any_audio(
    realtime_settings: AzureAudioSettings,
) -> None:
    """The handshake and session config happen at call setup, not first speech."""
    captured: list[ConnectRecord] = []
    socket = FakeRealtimeSocket(when_drained="block")
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket, captured)
    )

    await provider.prewarm()

    assert len(captured) == 1, "prewarm must open exactly one socket"
    assert any(frame.get("type") == SESSION_UPDATE_EVENT for frame in socket.sent), (
        "the transcription session must be configured during pre-warm, not later"
    )
    await provider.aclose()


@pytest.mark.unit
async def test_prewarm_is_idempotent(realtime_settings: AzureAudioSettings) -> None:
    captured: list[ConnectRecord] = []
    provider = AzureRealtimeSTTProvider(
        realtime_settings,
        connect=make_realtime_connect(
            FakeRealtimeSocket(when_drained="block"), captured
        ),
    )

    await provider.prewarm()
    await provider.prewarm()

    assert len(captured) == 1
    await provider.aclose()


async def first_final(provider: AzureRealtimeSTTProvider) -> str | None:
    """Consume one stream exactly as STTAdapter does: stop at the first final."""
    async with aclosing(provider.transcribe(pcm_stream(speech(2)))) as events:
        async for event in events:
            text = getattr(event, "text", "")
            if text:
                return text
    return None


@pytest.mark.unit
async def test_successive_utterances_reuse_the_pre_warmed_session(
    realtime_settings: AzureAudioSettings,
) -> None:
    """This is the point: two utterances, one handshake."""
    captured: list[ConnectRecord] = []
    socket = TurnScriptedSocket(
        [transcript_events("first"), transcript_events("second")]
    )
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket, captured)
    )

    await provider.prewarm()

    said = [await first_final(provider), await first_final(provider)]

    assert said == ["first", "second"]
    assert len(captured) == 1, "a reused session must not reconnect per utterance"
    assert not socket.closed, "the session must survive the end of an utterance"

    await provider.aclose()
    assert socket.closed, "aclose must release the pre-warmed session"


@pytest.mark.unit
async def test_a_stale_transcript_never_leaks_into_the_next_turn(
    realtime_settings: AzureAudioSettings,
) -> None:
    """A split utterance leaves a second final queued; it must not become turn 2.

    Found live: the server VAD split the caller's booking sentence in two, the
    consumer took the first final, and the tail ("and we'll need a high chair")
    surfaced as the FIRST transcript of the following turn — so the customer's
    "yes, go ahead" was never seen and the booking never confirmed.  On a
    per-stream socket the leftover dies with the connection; on a reused one it
    has to be drained.
    """
    socket = TurnScriptedSocket(
        [
            transcript_events("book a table for four")
            + transcript_events("and a high chair"),
            transcript_events("yes, go ahead"),
        ]
    )
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket)
    )

    await provider.prewarm()

    assert await first_final(provider) == "book a table for four"
    assert socket.clears == 1, "the reused session's input buffer must be cleared"
    assert await first_final(provider) == "yes, go ahead", (
        "the previous turn's leftover transcript leaked into this turn"
    )

    await provider.aclose()


@pytest.mark.unit
async def test_without_prewarm_each_stream_opens_and_closes_its_own_socket(
    realtime_settings: AzureAudioSettings,
) -> None:
    """The previous behaviour is preserved exactly for callers that skip it."""
    captured: list[ConnectRecord] = []
    first = FakeRealtimeSocket(transcript_events("one"), when_drained="block")
    second = FakeRealtimeSocket(transcript_events("two"), when_drained="block")
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=sequenced_connect([first, second], captured)
    )

    for _ in range(2):
        # aclosing mirrors what STTAdapter does at the real call site: stopping
        # at the first final without closing leaves teardown to the event
        # loop's async-generator hook.
        async with aclosing(provider.transcribe(pcm_stream(speech(2)))) as events:
            async for event in events:
                if getattr(event, "text", ""):
                    break

    assert len(captured) == 2
    assert first.closed and second.closed, (
        "a stream that owns its socket must close it when the stream ends"
    )


@pytest.mark.unit
async def test_a_session_lost_mid_call_is_replaced_transparently(
    realtime_settings: AzureAudioSettings,
) -> None:
    """A dropped socket must not degrade the rest of the call to silence."""
    captured: list[ConnectRecord] = []
    dead = FakeRealtimeSocket([ConnectionClosedOK(None, None)], when_drained="block")
    replacement = FakeRealtimeSocket(
        transcript_events("recovered"), when_drained="block"
    )
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=sequenced_connect([dead, replacement], captured)
    )

    await provider.prewarm()

    # First utterance: the socket is gone, so nothing is recognised.
    assert [event async for event in provider.transcribe(pcm_stream(speech(2)))] == []

    # Second: the provider notices and reopens rather than staying broken.
    said = [
        event.text
        async for event in provider.transcribe(pcm_stream(speech(2)))
        if getattr(event, "text", "")
    ]
    assert said == ["recovered"]
    assert len(captured) == 2, "the lost session must be replaced exactly once"

    await provider.aclose()


@pytest.mark.unit
async def test_async_with_warms_on_entry_and_releases_on_exit(
    realtime_settings: AzureAudioSettings,
) -> None:
    captured: list[ConnectRecord] = []
    socket = FakeRealtimeSocket(when_drained="block")
    provider = AzureRealtimeSTTProvider(
        realtime_settings, connect=make_realtime_connect(socket, captured)
    )

    async with provider as warmed:
        assert warmed is provider
        assert len(captured) == 1
        assert not socket.closed

    assert socket.closed


@pytest.mark.unit
async def test_aclose_without_prewarm_is_a_no_op(
    realtime_settings: AzureAudioSettings,
) -> None:
    provider = AzureRealtimeSTTProvider(
        realtime_settings,
        connect=make_realtime_connect(FakeRealtimeSocket(when_drained="block")),
    )
    await provider.aclose()
    await provider.aclose()
