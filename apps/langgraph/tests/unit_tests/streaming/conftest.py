"""Shared fixtures for the Azure audio provider unit tests.

Everything here is offline.  The ``httpx.AsyncClient`` handed to a REST
provider is always backed by an ``httpx.MockTransport``, and the realtime
provider is always handed :class:`FakeRealtimeSocket` through its injected
connection factory — so no test can reach Azure or open a socket.
"""

from __future__ import annotations

import asyncio
import base64
import dataclasses
import json
import struct
from typing import TYPE_CHECKING, Any

import httpx
import pytest
from websockets.exceptions import ConnectionClosedOK

from cordia_agent.streaming.providers.audio_config import AzureAudioSettings
from cordia_agent.streaming.providers.azure_realtime_stt import (
    SESSION_CREATED_EVENT,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterator, Callable, Iterable, Mapping


# 16 kHz / 20 ms / 16-bit mono.
FRAME_BYTES = 640

# Comfortably above and below the 300.0 default energy threshold.
SPEECH_AMPLITUDE = 5000
SILENCE_AMPLITUDE = 10


def pcm_frame(amplitude: int, *, frame_bytes: int = FRAME_BYTES) -> bytes:
    """Build one PCM frame whose every sample has value *amplitude*."""
    sample_count = frame_bytes // 2
    return struct.pack(f"<{sample_count}h", *([amplitude] * sample_count))


def speech(frames: int) -> bytes:
    """Return *frames* worth of above-threshold PCM."""
    return pcm_frame(SPEECH_AMPLITUDE) * frames


def silence(frames: int) -> bytes:
    """Return *frames* worth of below-threshold PCM."""
    return pcm_frame(SILENCE_AMPLITUDE) * frames


async def pcm_stream(
    payload: bytes, *, chunk_bytes: int = FRAME_BYTES
) -> AsyncIterator[bytes]:
    """Yield *payload* as an async stream of arbitrarily sized chunks.

    Chunk size deliberately need not equal the VAD frame size — the provider is
    responsible for re-framing whatever the media plane produces.
    """
    for offset in range(0, len(payload), chunk_bytes):
        yield payload[offset : offset + chunk_bytes]


@pytest.fixture
def audio_settings() -> AzureAudioSettings:
    """Fast-endpointing settings so tests need only a handful of frames."""
    return AzureAudioSettings(
        endpoint="https://demo.openai.azure.com/",
        api_key="secret-key",
        vad_onset_frames=1,
        vad_hangover_frames=2,
        vad_preroll_frames=1,
        tts_chunk_bytes=4,
        base_timeout_seconds=1.0,
    )


def make_client(
    handler: Callable[[httpx.Request], httpx.Response],
) -> httpx.AsyncClient:
    """Build a ``MockTransport``-backed async client — never touches a socket."""
    return httpx.AsyncClient(transport=httpx.MockTransport(handler))


@pytest.fixture
def realtime_settings(audio_settings: AzureAudioSettings) -> AzureAudioSettings:
    """Realtime settings with short deadlines so bounded waits stay quick.

    ``base_timeout_seconds`` doubles as the drain window after the caller's
    audio is exhausted, and ``max_utterance_seconds`` as the no-event watchdog
    — both are dialled down so a test that exercises either bound finishes in
    milliseconds rather than seconds.
    """
    return dataclasses.replace(
        audio_settings,
        base_timeout_seconds=0.2,
        max_utterance_seconds=0.5,
        realtime_send_chunk_bytes=640,
    )


@dataclasses.dataclass(slots=True)
class ConnectRecord:
    """What the provider asked the connection factory to open."""

    url: str
    headers: dict[str, str]


class FakeRealtimeSocket:
    """In-memory stand-in for the Azure realtime WebSocket.

    Server frames are scripted up front and handed out by :meth:`recv` in
    order; ``transcription_session.created`` is prepended automatically because
    the real service always sends it first.  Everything the provider writes is
    parsed and recorded on :attr:`sent`, so tests assert on the wire protocol
    without a socket existing anywhere.

    Once the script is exhausted the socket either closes (modelling a service
    that hangs up) or blocks forever (modelling a live-but-quiet session).  The
    blocking case is the one that proves the provider's own deadlines are what
    ends the stream: nothing here ever unblocks it.
    """

    def __init__(
        self,
        events: Iterable[dict[str, Any] | str | Exception] = (),
        *,
        when_drained: str = "close",
        session_created: bool = True,
    ) -> None:
        queue: list[dict[str, Any] | str | Exception] = list(events)
        if session_created:
            queue.insert(0, {"type": SESSION_CREATED_EVENT})
        self._queue = queue
        self._when_drained = when_drained
        self._never = asyncio.Event()
        self.sent: list[dict[str, Any]] = []
        self.closed = False

    async def send(self, message: str) -> None:
        """Record one client frame, or fail if the socket is already closed."""
        if self.closed:
            raise ConnectionClosedOK(None, None)
        self.sent.append(json.loads(message))

    async def recv(self) -> str:
        """Return the next scripted frame; then close or block forever."""
        while self._queue:
            item = self._queue.pop(0)
            if isinstance(item, Exception):
                raise item
            return item if isinstance(item, str) else json.dumps(item)
        if self._when_drained == "close":
            self.closed = True
            raise ConnectionClosedOK(None, None)
        await self._never.wait()
        raise AssertionError("unreachable: the fake socket never unblocks")

    async def close(self) -> None:
        """Mark the socket closed, as the real client's ``close`` does."""
        self.closed = True

    @property
    def appended_audio(self) -> list[bytes]:
        """Every PCM payload the provider appended, decoded back from base64."""
        return [
            base64.b64decode(frame["audio"])
            for frame in self.sent
            if frame.get("type") == "input_audio_buffer.append"
        ]


def make_realtime_connect(
    socket: FakeRealtimeSocket | Exception,
    captured: list[ConnectRecord] | None = None,
) -> Callable[[str, Mapping[str, str]], Any]:
    """Build an injected connection factory that never opens a socket.

    Args:
        socket: The fake socket to hand back, or an exception to raise instead.
        captured: Optional list recording each connection attempt.

    Returns:
        A ``RealtimeConnect``-shaped factory.
    """

    async def connect(url: str, headers: Mapping[str, str]) -> FakeRealtimeSocket:
        if captured is not None:
            captured.append(ConnectRecord(url=url, headers=dict(headers)))
        if isinstance(socket, Exception):
            raise socket
        return socket

    return connect


def recording_handler(
    responses: Iterable[httpx.Response | Exception],
    captured: list[httpx.Request],
) -> Callable[[httpx.Request], httpx.Response]:
    """Build a handler replaying *responses* in order and recording requests.

    Args:
        responses: Responses (or exceptions to raise) for successive calls.
            The last entry repeats once exhausted.
        captured: List that each received ``httpx.Request`` is appended to.

    Returns:
        A ``MockTransport`` handler.
    """
    queue = list(responses)

    def handle(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        # Force the body to be read so multipart assertions are possible.
        request.read()
        item = queue.pop(0) if len(queue) > 1 else queue[0]
        if isinstance(item, Exception):
            raise item
        return item

    return handle
