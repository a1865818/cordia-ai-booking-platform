"""Azure realtime speech-to-text provider (WebSocket, server-side VAD).

This is the DEFAULT ``STTProvider`` implementation.  It speaks the realtime
transcription surface — ``wss://<host>/openai/realtime?...&intent=transcription``
— which streams audio in and streams recognition events back out, and which
performs endpointing itself.  That is the whole reason it exists: the batch
sibling (``azure_stt.AzureWhisperSTTProvider``) has to bolt a local energy VAD
onto a file-at-a-time REST route to fake the same shape, and the deployment
that actually exists (``gpt-realtime-whisper``) answers that REST route with
``OperationNotSupported``.

What the port gains, and what it costs
    ``input_audio_buffer.speech_started`` is an AUTHORITATIVE barge-in trigger.
    The batch provider derives the same signal from a local energy heuristic
    that fires on any loud noise; here the service itself says the caller
    started talking.  The port has no event for "the caller began speaking", so
    the signal is carried exactly as the batch provider carries it: a synthetic
    :class:`~...types.STTPartialEvent` with empty text and zero stability.
    ``STTAdapter`` turns it into a ``TranscriptPartialFrame`` unchanged, which
    is what ``BargeInController.caller_speech_started`` consumes, and Inv-1
    forbids partials from mutating booking state — so an empty-text partial can
    never be mistaken for content.  Downstream is untouched.

    ``input_audio_buffer.speech_stopped`` is the service's endpoint decision.
    It carries no transcript, so nothing is emitted for it; the matching
    ``...transcription.completed`` follows and becomes the final.

Concurrency
    Sending audio and receiving events are independent directions of one
    socket, so they run as two concurrently scheduled coroutines: a background
    pump task drains ``pcm_chunks`` into ``input_audio_buffer.append`` frames,
    while this generator's own body owns the receive side and yields.  The
    receive side is never starved — it is a separate task from the pump, and
    the pump yields to the loop after every frame it writes.  The receive side
    also never blocks on the pump: it waits on the socket and on the
    "audio exhausted" signal together, so a producer that finishes while the
    receiver is parked on ``recv`` is noticed immediately rather than after a
    timeout.

Session reuse (pre-warm)
    Opening the socket costs a handshake plus the ``transcription_session
    .created`` round trip — measured at ~1.5 s against the live service, which
    is the bulk of the "time to first onset partial" figure and is NOT
    detection latency.  Per-utterance connects therefore make the FIRST
    barge-in of every call about a second and a half late.  :meth:`prewarm`
    opens the session once, at call setup, and every subsequent
    :meth:`transcribe` reuses it; :meth:`aclose` (or ``async with``) releases it
    at the end of the call.  A session that dies mid-call is detected and
    replaced on the next utterance.  Skipping the pre-warm keeps the original
    per-stream behaviour exactly.

    Reuse carries one hazard a fresh socket does not: a consumer takes one
    final per stream, so a long utterance the server split in two leaves a
    second transcription queued.  A per-stream socket discards it on close; a
    reused one would hand it to the NEXT turn.  So the end of every stream over
    a shared session clears the input buffer and drains what is left — see
    ``_reset_shared_session``.

Teardown
    The pump task and the socket are released in a ``finally``, so they go away
    when the stream ends, when it raises, and when a consumer calls
    ``aclose()``.  The one case the port's generator shape cannot make
    deterministic is a consumer that simply STOPS iterating without closing —
    ``STTAdapter`` returns at the first final, so the generator is left
    suspended and finalised by the event loop's async-generator hook rather
    than immediately.  The pump still ends on its own when the caller's audio
    runs out; only the socket lingers, and only until finalisation.  A caller
    that wants determinism wraps the stream in ``contextlib.aclosing``.

Backpressure
    There is NO intermediate queue.  The pump awaits each ``send`` before
    pulling the next chunk from the caller, so a producer faster than the
    socket is blocked by the socket, and a producer slower than realtime simply
    sends less.  Large chunks are sliced to ``realtime_send_chunk_bytes`` so
    one oversized handover becomes several bounded writes rather than a single
    huge frame.

Redaction
    Audio bytes, transcripts, deltas, the api-key and every server error body
    stay out of this module's log records entirely — only event types, counts
    and symbolic codes are logged.  Nothing here relies on
    ``observability.redaction`` scrubbing it after the fact.
"""

from __future__ import annotations

import asyncio
import base64
import contextlib
import json
import logging
import math
from typing import TYPE_CHECKING, Any, Protocol

from websockets.exceptions import InvalidStatus, WebSocketException

from cordia_agent.streaming.providers.audio_config import AudioProviderError
from cordia_agent.streaming.providers.audio_http import attempt_timeouts
from cordia_agent.streaming.providers.types import STTFinalEvent, STTPartialEvent


if TYPE_CHECKING:
    from collections.abc import (
        AsyncIterable,
        AsyncIterator,
        Awaitable,
        Callable,
        Mapping,
    )

    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings
    from cordia_agent.streaming.providers.types import STTProviderEvent


logger = logging.getLogger(__name__)


# --- Wire protocol -----------------------------------------------------------
# Verified against the live service; see the module docstring.

SESSION_CREATED_EVENT = "transcription_session.created"
SESSION_UPDATE_EVENT = "transcription_session.update"
AUDIO_APPEND_EVENT = "input_audio_buffer.append"
AUDIO_CLEAR_EVENT = "input_audio_buffer.clear"
SPEECH_STARTED_EVENT = "input_audio_buffer.speech_started"
SPEECH_STOPPED_EVENT = "input_audio_buffer.speech_stopped"
TRANSCRIPTION_DELTA_EVENT = "conversation.item.input_audio_transcription.delta"
TRANSCRIPTION_COMPLETED_EVENT = "conversation.item.input_audio_transcription.completed"
ERROR_EVENT = "error"

# Coarse operation label on every AudioProviderError raised from here.
OPERATION = "realtime transcription"

# How long to wait for one more stale frame while resetting a reused session,
# and the total budget for that reset.  Short: this runs after a stream has
# ended, so the only frames still outstanding are for audio already spoken.
RESET_QUIET_SECONDS = 0.08
RESET_BUDGET_SECONDS = 1.0

# Reported when an error frame carries no usable symbolic code.  A placeholder
# is used rather than the frame's message, which can echo caller speech.
UNKNOWN_ERROR_CODE = "unknown"

# Text and stability on the synthetic speech-onset partial.  Empty text so no
# consumer renders a fabricated word; zero stability because the event carries
# no recognition hypothesis at all, only "the caller has started speaking".
# Identical to the batch provider's values — the barge-in path cannot tell the
# two providers apart, which is the point.
BARGE_IN_PARTIAL_TEXT = ""
BARGE_IN_PARTIAL_STABILITY = 0.0

# Stability on a real interim hypothesis from a ``...transcription.delta``.
# The realtime surface supplies no per-delta score, and the port's `stability`
# has no defined scale beyond 0..1, so the value is a policy choice: 0.5 is the
# midpoint, which says "this is genuine recognised text, and it is revisable".
# It must be strictly greater than BARGE_IN_PARTIAL_STABILITY so a consumer can
# distinguish a content-bearing partial from the bare onset trigger, and
# strictly less than 1.0 because a delta is never the committed transcript —
# only STTFinalEvent is.  ``gpt-realtime-whisper`` did not emit deltas in the
# verified run; other transcription models do, so they are handled.
DELTA_PARTIAL_STABILITY = 0.5


class RealtimeSocket(Protocol):
    """Minimal duplex socket surface this provider needs.

    Structurally satisfied by ``websockets.asyncio.client.ClientConnection``
    and by the test double, which is the point: the provider depends on this
    Protocol, never on ``websockets`` itself, so the suite runs offline.
    """

    async def send(self, message: str) -> None:
        """Send one text frame."""
        ...

    async def recv(self) -> str | bytes:
        """Receive the next frame, waiting until one arrives."""
        ...

    async def close(self) -> None:
        """Close the connection."""
        ...


# Lazily evaluated (PEP 695), so the collections.abc names stay import-free at
# runtime — importing this module must not be the reason a consumer pays for
# anything it does not use.
type RealtimeConnect = Callable[[str, Mapping[str, str]], Awaitable[RealtimeSocket]]


async def websockets_connect(url: str, headers: Mapping[str, str]) -> RealtimeSocket:
    """Open a real WebSocket connection to *url* with *headers*.

    The default :data:`RealtimeConnect` factory, and the ONLY place in this
    module that can touch a network socket.  Injecting a replacement is how the
    tests stay offline — exactly as the REST providers take an injected
    ``httpx.AsyncClient``.

    Args:
        url: Absolute ``wss://`` URL from
            :meth:`AzureAudioSettings.realtime_transcription_url`.
        headers: Handshake headers.  Carries the api-key: never logged.

    Returns:
        The connected socket.
    """
    from websockets.asyncio.client import connect

    return await connect(url, additional_headers=dict(headers))


def confidence_from_logprobs(payload: Mapping[str, Any], fallback: float) -> float:
    """Derive an ASR confidence in 0..1 from a completed-transcription payload.

    The realtime surface reports no confidence field.  When the model returns
    token ``logprobs`` (some transcription models do, gated on the session
    config), ``exp(mean(logprob))`` is a usable likelihood — the same treatment
    the batch provider gives whisper's per-segment ``avg_logprob``.  Otherwise
    the configured fallback is reported rather than a fabricated number.

    Args:
        payload: The parsed ``...transcription.completed`` frame.
        fallback: Confidence to report when no log-probabilities are present.

    Returns:
        A confidence within 0.0..1.0.
    """
    entries = payload.get("logprobs")
    if not isinstance(entries, list):
        return fallback

    logprobs: list[float] = []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        value = entry.get("logprob")
        if isinstance(value, int | float) and not isinstance(value, bool):
            logprobs.append(float(value))
    if not logprobs:
        return fallback

    return min(max(math.exp(sum(logprobs) / len(logprobs)), 0.0), 1.0)


def error_code(payload: Mapping[str, Any]) -> str:
    """Extract the symbolic code from an ``error`` frame — and nothing else.

    The frame's ``message`` is deliberately never read: a transcription error
    message can echo caller speech, and an auth error can echo the key.

    Args:
        payload: The parsed ``error`` frame.

    Returns:
        The frame's ``code``, else its ``type``, else
        :data:`UNKNOWN_ERROR_CODE`.
    """
    error = payload.get("error")
    if isinstance(error, dict):
        for key in ("code", "type"):
            value = error.get(key)
            if isinstance(value, str) and value:
                return value
    return UNKNOWN_ERROR_CODE


@contextlib.asynccontextmanager
async def _closing_iterator(
    source: AsyncIterable[bytes],
) -> AsyncIterator[AsyncIterator[bytes]]:
    """Iterate *source*, closing it deterministically on the way out.

    The pump task is cancelled whenever the consumer abandons the transcript
    stream, which leaves the caller's PCM generator suspended mid-``yield``.
    Closing it here finalises it at a known point instead of leaving it to the
    event loop's async-generator shutdown hook.
    """
    iterator = aiter(source)
    try:
        yield iterator
    finally:
        aclose = getattr(iterator, "aclose", None)
        if aclose is not None:
            await aclose()


class AzureRealtimeSTTProvider:
    """``STTProvider`` backed by the Azure realtime transcription WebSocket.

    One instance drives ONE concurrent stream: every :meth:`transcribe` call
    opens its own socket and owns its own pump task.  Construct one per
    session, as every other port in this package is constructed and injected.
    """

    def __init__(
        self,
        settings: AzureAudioSettings,
        *,
        connect: RealtimeConnect | None = None,
    ) -> None:
        """Initialise the provider with an injected connection factory.

        Args:
            settings: Resolved audio settings; supplies the endpoint, the key,
                the realtime api-version, the transcription model, the
                server-VAD silence window and every deadline.
            connect: Factory opening the socket.  Defaults to
                :func:`websockets_connect`; tests inject a fake so the suite
                never opens a socket.
        """
        self._settings = settings
        self._connect: RealtimeConnect = connect or websockets_connect
        # Set by prewarm(); see "Session reuse" in the module docstring.
        self._session_socket: RealtimeSocket | None = None
        self._session_failed = False

    # -- pre-warmed session --------------------------------------------------

    async def prewarm(self) -> None:
        """Open the transcription session now and hold it for the call.

        Measured against the live service, the first onset partial arrives
        ~1.5 s after audio starts — and that number is almost entirely the
        WebSocket handshake plus the ``transcription_session.created`` round
        trip, NOT detection latency.  Paying it per utterance makes the first
        barge-in of every call a second and a half late; paying it once at call
        setup, while the caller is still hearing the greeting, keeps it off the
        critical path entirely.

        Idempotent: calling it twice keeps the first session.  A caller that
        skips it still works — :meth:`transcribe` opens a session per stream in
        that case, which is the previous behaviour exactly.

        Raises:
            AudioProviderError: if the handshake was rejected.
            TimeoutError: if the session could not be opened in budget.
        """
        if self._session_socket is not None and not self._session_failed:
            return
        await self._discard_session()
        self._session_socket = await self._open_session()
        self._session_failed = False
        logger.debug("azure realtime transcription session pre-warmed")

    async def aclose(self) -> None:
        """Release a pre-warmed session.  Idempotent, and safe if never warmed."""
        await self._discard_session()

    async def __aenter__(self) -> AzureRealtimeSTTProvider:
        """Pre-warm on entry — ``async with`` is the natural call-scoped use."""
        await self.prewarm()
        return self

    async def __aexit__(self, *exc: object) -> None:
        """Release the pre-warmed session on exit."""
        await self.aclose()

    async def _discard_session(self) -> None:
        """Close and forget the held session, if there is one."""
        socket, self._session_socket = self._session_socket, None
        self._session_failed = False
        if socket is not None:
            await self._close_quietly(socket)

    async def _acquire(self) -> tuple[RealtimeSocket, bool]:
        """Return the socket for one stream, and whether the stream owns it.

        A pre-warmed session is reused and NOT owned by the stream — closing it
        when an utterance ends would defeat the entire point.  A session that
        died since it was warmed is replaced transparently rather than silently
        degrading the rest of the call to per-utterance connects.
        """
        if self._session_socket is not None and not self._session_failed:
            return self._session_socket, False
        if self._session_socket is not None:
            logger.debug("pre-warmed realtime session was lost; reopening")
            await self._discard_session()
            self._session_socket = await self._open_session()
            return self._session_socket, False
        return await self._open_session(), True

    async def transcribe(
        self, pcm_chunks: AsyncIterable[bytes]
    ) -> AsyncIterator[STTProviderEvent]:
        """Yield recognition events for an async stream of caller PCM.

        Per utterance the sequence is: one synthetic
        :class:`~...types.STTPartialEvent` when the service reports speech
        onset (the barge-in trigger), zero or more content partials if the
        model emits deltas, then one :class:`~...types.STTFinalEvent` when the
        transcription completes.  An utterance whose transcript is empty or
        whitespace yields NO final — that is a cough or a door slam, and an
        empty final would push an empty committable transcript downstream.

        The stream may carry several utterances; the caller decides how many to
        consume (``STTAdapter`` stops at the first final).  Iteration ends when
        the caller's audio is exhausted and no transcription is outstanding,
        when the socket closes, or when the bounded drain window elapses.

        Args:
            pcm_chunks: Async stream of 16-bit signed little-endian mono PCM at
                ``settings.input_sample_rate_hz``.

        Yields:
            :class:`~...types.STTPartialEvent` at each speech onset and each
            interim delta, :class:`~...types.STTFinalEvent` at each
            transcribed endpoint.

        Raises:
            AudioProviderError: on a rejected handshake, or on an ``error``
                frame.  Carries the operation and the code only, never a body.
            TimeoutError: if the session could not be opened within any
                attempt's budget, or if the service fell silent for longer than
                ``settings.max_utterance_seconds`` while audio was still being
                sent.
        """
        socket, owned = await self._acquire()
        exhausted = asyncio.Event()
        pump = asyncio.create_task(
            self._pump_audio(socket, pcm_chunks, exhausted),
            name="azure-realtime-stt-audio-pump",
        )
        try:
            async with contextlib.aclosing(
                self._receive_events(socket, exhausted)
            ) as events:
                async for event in events:
                    yield event
        finally:
            await self._shutdown(pump, socket, close_socket=owned)

    # -- session -------------------------------------------------------------

    async def _open_session(self) -> RealtimeSocket:
        """Connect, wait for ``transcription_session.created``, configure it.

        Every step is bounded, and the budget grows per attempt exactly as in
        ``audio_http.attempt_timeouts`` — a first-attempt timeout is usually a
        cold route, so retrying on the same budget would fail the same way.
        Retrying is safe because nothing has been emitted and transcription is
        a pure read.  A rejected handshake (401/403/400) is NOT retried: it
        will fail identically.

        Returns:
            A connected socket with the transcription session configured.

        Raises:
            AudioProviderError: if the handshake was rejected.
            TimeoutError: if every attempt exceeded its budget.
        """
        settings = self._settings
        url = settings.realtime_transcription_url()
        headers = settings.auth_headers()

        last_error: BaseException | None = None
        for timeout in attempt_timeouts(settings.base_timeout_seconds):
            try:
                socket = await asyncio.wait_for(self._connect(url, headers), timeout)
            except InvalidStatus as exc:
                # `from None`: the websockets exception carries the rejection
                # response, whose headers/body must not ride along in a
                # traceback.  Only the status code crosses over.
                raise AudioProviderError(OPERATION, exc.response.status_code) from None
            except (TimeoutError, OSError, WebSocketException) as exc:
                last_error = exc
                continue

            try:
                await asyncio.wait_for(self._await_session_created(socket), timeout)
                await asyncio.wait_for(
                    socket.send(json.dumps(self._session_update())), timeout
                )
            except (TimeoutError, OSError, WebSocketException) as exc:
                await self._close_quietly(socket)
                last_error = exc
                continue

            logger.debug("azure realtime transcription session opened")
            return socket

        raise TimeoutError(
            "azure realtime transcription session could not be opened"
        ) from last_error

    async def _await_session_created(self, socket: RealtimeSocket) -> None:
        """Consume frames until the service confirms the session.

        Args:
            socket: The freshly connected socket.

        Raises:
            AudioProviderError: if the service answers with an ``error`` frame.
        """
        while True:
            payload = _decode(await socket.recv())
            if payload is None:
                continue
            message_type = payload.get("type")
            if message_type == SESSION_CREATED_EVENT:
                return
            if message_type == ERROR_EVENT:
                raise AudioProviderError(OPERATION, error_code(payload))

    def _session_update(self) -> dict[str, Any]:
        """Build the ``transcription_session.update`` payload.

        The model is named HERE rather than in the connect querystring: the
        realtime route rejects a ``deployment`` parameter outright.
        """
        settings = self._settings
        return {
            "type": SESSION_UPDATE_EVENT,
            "session": {
                "input_audio_format": "pcm16",
                "input_audio_transcription": {
                    "model": settings.realtime_transcription_model
                },
                "turn_detection": {
                    "type": "server_vad",
                    "silence_duration_ms": settings.realtime_silence_duration_ms,
                },
            },
        }

    # -- send side -----------------------------------------------------------

    async def _pump_audio(
        self,
        socket: RealtimeSocket,
        pcm_chunks: AsyncIterable[bytes],
        exhausted: asyncio.Event,
    ) -> None:
        """Stream caller PCM into the socket until the source runs out.

        *exhausted* is set in a ``finally``, so it means "no further audio will
        be sent" for EVERY ending — clean exhaustion, a closed socket, a
        failing source, or cancellation.  The receive side keys its shutdown
        off that single fact.

        Args:
            socket: The configured session socket.
            pcm_chunks: The caller's PCM stream.
            exhausted: Signal set once no more audio will be sent.
        """
        settings = self._settings
        chunk_bytes = settings.realtime_send_chunk_bytes
        frames = 0
        try:
            async with _closing_iterator(pcm_chunks) as chunks:
                async for chunk in chunks:
                    for offset in range(0, len(chunk), chunk_bytes):
                        window = chunk[offset : offset + chunk_bytes]
                        if not window:
                            continue
                        await asyncio.wait_for(
                            socket.send(_append_frame(window)),
                            settings.base_timeout_seconds,
                        )
                        frames += 1
                        # Hand the loop back after every write so a producer
                        # that never awaits cannot starve the receive task.
                        await asyncio.sleep(0)
        except WebSocketException:
            logger.debug("realtime socket closed while sending audio")
            self._note_session_lost(socket)
        finally:
            exhausted.set()
            logger.debug("realtime audio pump finished after %d frame(s)", frames)

    # -- receive side --------------------------------------------------------

    async def _receive_events(
        self, socket: RealtimeSocket, exhausted: asyncio.Event
    ) -> AsyncIterator[STTProviderEvent]:
        """Translate server frames into port events until the stream ends.

        Waiting is done on the socket AND on *exhausted* together, so the
        receiver reacts to the producer finishing without waiting out a
        timeout.  Two deadlines bound every wait:

        - while audio is still flowing, ``max_utterance_seconds`` — the service
          is legitimately silent during caller silence, so this is a
          dead-socket watchdog, not an endpointing deadline;
        - once audio is exhausted, ``base_timeout_seconds`` as a drain window
          for a transcription still in flight, after which the stream ends
          rather than hanging.

        Args:
            socket: The configured session socket.
            exhausted: Set by the pump once no more audio will be sent.

        Yields:
            Port recognition events.

        Raises:
            AudioProviderError: on an ``error`` frame.
            TimeoutError: if the service produced no frame for longer than the
                watchdog while audio was still being sent.
        """
        settings = self._settings
        pending = False
        recv_task: asyncio.Task[str | bytes] | None = None
        exhausted_task = asyncio.create_task(
            exhausted.wait(), name="azure-realtime-stt-exhausted"
        )
        try:
            while True:
                if exhausted.is_set() and not pending:
                    return

                if recv_task is None:
                    recv_task = asyncio.create_task(
                        socket.recv(), name="azure-realtime-stt-recv"
                    )

                draining = exhausted.is_set()
                waiters: set[asyncio.Future[Any]] = {recv_task}
                if not draining:
                    waiters.add(exhausted_task)

                done, _ = await asyncio.wait(
                    waiters,
                    timeout=(
                        settings.base_timeout_seconds
                        if draining
                        else settings.max_utterance_seconds
                    ),
                    return_when=asyncio.FIRST_COMPLETED,
                )

                if not done:
                    if draining:
                        logger.debug(
                            "realtime drain window elapsed with a transcription "
                            "outstanding; ending the stream"
                        )
                        return
                    raise TimeoutError(
                        "azure realtime transcription produced no event within "
                        f"{settings.max_utterance_seconds}s"
                    )

                if recv_task not in done:
                    # The producer finished; re-evaluate under the drain policy.
                    continue

                try:
                    raw = recv_task.result()
                except WebSocketException:
                    logger.debug("realtime socket closed; ending the transcript stream")
                    self._note_session_lost(socket)
                    return
                finally:
                    recv_task = None

                payload = _decode(raw)
                if payload is None:
                    continue
                for event, still_pending in self._translate(payload, pending):
                    pending = still_pending
                    if event is not None:
                        yield event
        finally:
            exhausted_task.cancel()
            if recv_task is not None:
                recv_task.cancel()
            with contextlib.suppress(asyncio.CancelledError, WebSocketException):
                await asyncio.gather(
                    *(task for task in (exhausted_task, recv_task) if task is not None),
                    return_exceptions=True,
                )

    def _translate(
        self, payload: Mapping[str, Any], pending: bool
    ) -> list[tuple[STTProviderEvent | None, bool]]:
        """Map one server frame onto zero or one port events.

        Returns a list of ``(event, pending)`` pairs rather than yielding, so
        the caller keeps the single ``yield`` site that drives the generator.

        Args:
            payload: The parsed server frame.
            pending: Whether a transcription is currently outstanding.

        Returns:
            The events to emit and the updated pending flag.

        Raises:
            AudioProviderError: on an ``error`` frame.  Carries the symbolic
                code only — never the frame's message.
        """
        message_type = payload.get("type")

        if message_type == SPEECH_STARTED_EVENT:
            # The authoritative barge-in trigger; see the module docstring.
            return [
                (
                    STTPartialEvent(
                        text=BARGE_IN_PARTIAL_TEXT,
                        stability=BARGE_IN_PARTIAL_STABILITY,
                    ),
                    True,
                )
            ]

        if message_type == SPEECH_STOPPED_EVENT:
            # Server-side endpointing.  No transcript rides on this frame, so
            # nothing is emitted; the completed frame follows.
            return [(None, True)]

        if message_type == TRANSCRIPTION_DELTA_EVENT:
            delta = payload.get("delta")
            if not isinstance(delta, str) or not delta:
                return []
            return [
                (
                    STTPartialEvent(text=delta, stability=DELTA_PARTIAL_STABILITY),
                    pending,
                )
            ]

        if message_type == TRANSCRIPTION_COMPLETED_EVENT:
            transcript = payload.get("transcript")
            if not isinstance(transcript, str) or not transcript.strip():
                # A cough must not become a committable empty transcript.
                logger.debug("realtime transcription was empty; no final emitted")
                return [(None, False)]
            return [
                (
                    STTFinalEvent(
                        text=transcript.strip(),
                        asr_confidence=confidence_from_logprobs(
                            payload, self._settings.fallback_asr_confidence
                        ),
                        is_endpoint=True,
                    ),
                    False,
                )
            ]

        if message_type == ERROR_EVENT:
            raise AudioProviderError(OPERATION, error_code(payload))

        return []

    # -- teardown ------------------------------------------------------------

    async def _shutdown(
        self,
        pump: asyncio.Task[None],
        socket: RealtimeSocket,
        *,
        close_socket: bool = True,
    ) -> None:
        """Stop the pump and, when this stream owns it, close the socket.

        Runs on every exit path: normal end of stream, an exception, and
        ``aclose()`` from a consumer that stopped at the first final.

        Args:
            pump: The audio pump task.
            socket: The session socket.
            close_socket: ``False`` for a pre-warmed session, which outlives
                the utterance and is released by :meth:`aclose`.

        Raises:
            Exception: whatever the caller's PCM source raised, if anything —
                a failing producer is not silently swallowed.
        """
        pump.cancel()
        try:
            await pump
        except asyncio.CancelledError:
            if not pump.cancelled():
                # The cancellation is OURS, not the pump's — never swallow it.
                raise
        finally:
            if close_socket:
                await self._close_quietly(socket)
            else:
                await self._reset_shared_session(socket)

    async def _reset_shared_session(self, socket: RealtimeSocket) -> None:
        """Clear a reused session's buffer and discard frames from this stream.

        A consumer takes ONE final per stream (``STTAdapter`` returns at the
        first), but the service does not know that: a long utterance the
        server-side VAD split in two produces a second transcription that is
        still queued on the socket when the stream ends.  On a per-stream
        socket that queue dies with the connection.  On a REUSED one it does
        not — and a live run proved it: the tail of turn 2 ("and we'll need a
        high chair") arrived as the FIRST transcript of turn 3, so the
        customer's "yes, go ahead" was never seen and the booking was never
        confirmed.

        Draining here rather than before the next turn keeps the cost off the
        critical path: it runs while the agent is thinking and speaking, not
        while the caller is waiting to be understood.
        """
        if self._session_failed:
            return
        try:
            await asyncio.wait_for(
                socket.send(json.dumps({"type": AUDIO_CLEAR_EVENT})),
                RESET_QUIET_SECONDS,
            )
        except (TimeoutError, OSError, WebSocketException):
            self._note_session_lost(socket)
            return

        discarded = 0
        deadline = asyncio.get_running_loop().time() + RESET_BUDGET_SECONDS
        while asyncio.get_running_loop().time() < deadline:
            try:
                await asyncio.wait_for(socket.recv(), RESET_QUIET_SECONDS)
            except TimeoutError:
                break  # quiet: nothing left from the finished stream
            except (OSError, WebSocketException):
                self._note_session_lost(socket)
                return
            discarded += 1
        if discarded:
            logger.debug(
                "discarded %d stale frame(s) when resetting the reused session",
                discarded,
            )

    def _note_session_lost(self, socket: RealtimeSocket) -> None:
        """Flag a dropped PRE-WARMED session so the next stream reopens one."""
        if socket is self._session_socket:
            self._session_failed = True

    @staticmethod
    async def _close_quietly(socket: RealtimeSocket) -> None:
        """Close *socket*, tolerating a connection that is already gone."""
        with contextlib.suppress(OSError, WebSocketException):
            await socket.close()


def _append_frame(pcm: bytes) -> str:
    """Build one ``input_audio_buffer.append`` frame for *pcm*."""
    return json.dumps(
        {
            "type": AUDIO_APPEND_EVENT,
            "audio": base64.b64encode(pcm).decode("ascii"),
        }
    )


def _decode(raw: str | bytes) -> dict[str, Any] | None:
    """Parse a server frame, tolerating anything unparseable.

    Args:
        raw: The received text or binary frame.

    Returns:
        The parsed object, or ``None`` if the frame was not a JSON object.  The
        frame itself is never logged: it can carry a transcript.
    """
    try:
        payload: Any = json.loads(raw)
    except (ValueError, UnicodeDecodeError):
        logger.debug("discarding an unparseable realtime frame")
        return None
    if not isinstance(payload, dict):
        logger.debug("discarding a non-object realtime frame")
        return None
    return payload
