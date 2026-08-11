"""Azure ``gpt-4o-mini-tts`` streaming text-to-speech provider.

Time-to-first-chunk is the design constraint.  ``TTSChunker`` splits agent text
on sentence boundaries precisely so the caller hears sentence one while
sentence two is still being generated; a provider that buffered a whole
synthesis before yielding would throw that away.  So this provider streams the
HTTP response body and yields a ref per audio chunk as it arrives.

Cancellation is the other constraint, and it is the reason barge-in is audible
rather than theoretical.  ``TTSChunker.cancel_active_turn`` flips a
:class:`~...tts_port.TTSCancellationToken`; this provider polls it between
chunks and, when set, **aborts the in-flight HTTP response** — it does not
merely stop yielding.  Abandoning the stream without closing it would leave
Azure generating and sending the rest of a sentence the caller has already
talked over, holding a connection and burning quota for audio nobody will hear.

Audio bytes never leave through the port: they go to the injected
:class:`~...audio_sink.AudioSink`, which returns the opaque ref that is yielded
instead.  The port's contract ("raw audio bytes stay out of band on the media
plane") is preserved exactly.

Output format
    The deployment emits 24 kHz 16-bit mono PCM on ``response_format="pcm"``.
    PSTN needs 8 kHz mulaw.  The format and rate are settings, stamped onto
    every chunk's metadata so a sink knows what it received — but NOTHING here
    resamples.  See ``audio_config`` for why that gap is deliberate.
"""

from __future__ import annotations

import logging
from contextlib import aclosing
from typing import TYPE_CHECKING, Any

from cordia_agent.streaming.providers.audio_config import AudioProviderError
from cordia_agent.streaming.providers.audio_http import (
    RETRYABLE_ERRORS,
    attempt_timeouts,
)
from cordia_agent.streaming.providers.audio_sink import AudioChunkMetadata


if TYPE_CHECKING:
    from collections.abc import AsyncIterator

    import httpx

    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings
    from cordia_agent.streaming.providers.audio_sink import AudioSink
    from cordia_agent.streaming.providers.tts_port import TTSCancellationToken


logger = logging.getLogger(__name__)


class AzureTtsProvider:
    """``TTSProvider`` backed by an Azure ``gpt-4o-mini-tts`` deployment."""

    def __init__(
        self,
        settings: AzureAudioSettings,
        http: httpx.AsyncClient,
        sink: AudioSink,
    ) -> None:
        """Initialise the provider with injected transport and sink.

        Args:
            settings: Resolved audio settings; supplies the deployment, the
                endpoint, the key, the voice, and the output format.
            http: An ``httpx.AsyncClient`` — backed by a ``MockTransport`` in
                tests, so the suite never touches the network.
            sink: Where synthesized bytes are placed.  Its returned refs are
                what this provider yields.
        """
        self._settings = settings
        self._http = http
        self._sink = sink

    async def synthesize(
        self,
        text: str,
        *,
        turn_id: str,
        chunk_seq: int,
        cancellation: TTSCancellationToken | None = None,
    ) -> AsyncIterator[str]:
        """Stream synthesis for one text chunk, yielding one ref per audio chunk.

        The cancellation token is polled before the request is opened and
        between every audio chunk.  On cancellation the in-flight response is
        closed immediately (see :meth:`_stream_attempt`) and iteration stops —
        no further ref is yielded, and no further byte is received.

        Retries follow the audio policy in ``audio_http``: synthesis is a pure
        read, so a failed attempt may be retried on a longer budget — but ONLY
        while nothing has been emitted yet.  Once a ref has been yielded the
        caller is already hearing audio, and re-running the request would
        replay the sentence from the start.

        Args:
            text: One sentence-sized chunk of agent text.
            turn_id: The assistant turn this audio belongs to.
            chunk_seq: The text-chunk ordinal within the turn.
            cancellation: Cooperative cancellation signal; ``None`` disables
                cancellation for this call.

        Yields:
            Opaque audio chunk references returned by the sink.

        Raises:
            AudioProviderError: if the speech route returns a non-2xx status.
            TimeoutError: if every attempt exceeded its deadline before any
                audio was emitted.
        """
        if cancellation is not None and cancellation.is_cancelled:
            return
        if not text.strip():
            return

        emitted = False
        last_error: BaseException | None = None

        for deadline in attempt_timeouts(self._settings.base_timeout_seconds):
            try:
                # aclosing: if the CONSUMER abandons us (breaks out of the
                # iteration), the inner generator is closed deterministically
                # here rather than whenever the loop finalises it — which is
                # what actually releases the in-flight HTTP response.
                async with aclosing(
                    self._stream_attempt(
                        text,
                        turn_id=turn_id,
                        chunk_seq=chunk_seq,
                        cancellation=cancellation,
                        attempt_timeout=deadline,
                    )
                ) as stream:
                    async for ref in stream:
                        emitted = True
                        yield ref
            except RETRYABLE_ERRORS as exc:
                if emitted:
                    # Mid-playback failure: replaying would repeat the sentence
                    # the caller has already partly heard.
                    raise
                last_error = exc
                continue
            return

        raise TimeoutError(
            "azure speech synthesis timed out on every attempt"
        ) from last_error

    async def _stream_attempt(
        self,
        text: str,
        *,
        turn_id: str,
        chunk_seq: int,
        cancellation: TTSCancellationToken | None,
        attempt_timeout: float,
    ) -> AsyncIterator[str]:
        """Run one bounded streaming attempt against the speech route.

        Args:
            text: The text to synthesize.
            turn_id: The assistant turn this audio belongs to.
            chunk_seq: The text-chunk ordinal within the turn.
            cancellation: Cooperative cancellation signal, polled between chunks.
            attempt_timeout: Deadline for this attempt, applied to connect and to
                read — so a stalled body cannot hang the turn even after the
                first byte has arrived.

        Yields:
            Opaque audio chunk references returned by the sink.

        Raises:
            AudioProviderError: on a non-2xx response.  The error body is
                discarded unread: it can echo the api-key or the agent text.
        """
        settings = self._settings
        body: dict[str, Any] = {
            "model": settings.tts_deployment,
            "input": text,
            "voice": settings.tts_voice,
            "response_format": settings.tts_response_format,
        }

        frame_seq = 0
        async with self._http.stream(
            "POST",
            settings.speech_url(),
            headers=settings.auth_headers(),
            json=body,
            timeout=attempt_timeout,
        ) as response:
            if response.status_code >= 300:
                # Never read the body — see the docstring.
                await response.aclose()
                raise AudioProviderError("speech", response.status_code)

            async for audio in response.aiter_bytes(settings.tts_chunk_bytes):
                if cancellation is not None and cancellation.is_cancelled:
                    # Abort the transfer, do not just stop consuming it: this
                    # is what makes barge-in actually cut the audio off.
                    await response.aclose()
                    logger.debug("tts synthesis cancelled after %d chunk(s)", frame_seq)
                    return

                if not audio:
                    continue

                ref = await self._sink.write(
                    audio,
                    AudioChunkMetadata(
                        turn_id=turn_id,
                        chunk_seq=chunk_seq,
                        frame_seq=frame_seq,
                        content_type=settings.tts_content_type,
                        sample_rate_hz=settings.tts_sample_rate_hz,
                    ),
                )
                frame_seq += 1
                yield ref
