"""Azure Whisper speech-to-text provider (batch model behind a streaming port).

WHICH PROVIDER TO USE
    :mod:`azure_realtime_stt` is the default.  It talks to the realtime
    WebSocket surface, which streams recognition events and does its own
    endpointing, and it is what ``gpt-realtime-whisper`` supports —
    that deployment answers the batch ``audio/transcriptions`` route with
    ``OperationNotSupported``.

    THIS provider is the alternative for a resource where only a plain
    ``whisper`` deployment exists (it is deployable in australiaeast, where the
    realtime models are not universally available).  It is a complete
    implementation of the same ``STTProvider`` port, so swapping it in is a
    construction-site change and nothing downstream moves.  Its costs are
    stated below: local endpointing, and a barge-in trigger derived from an
    energy heuristic rather than from the service.

``STTProvider`` is a streaming port: ``transcribe`` consumes an async stream of
PCM and yields recognition events as they become available.  The ``whisper``
deployment is the opposite — it accepts one complete audio file and returns one
transcript.  Bridging the two is this module's entire job, and it has exactly
two consequences.

**This provider owns endpointing.**  Nothing upstream tells it where an
utterance ends, so it runs a :class:`~...vad.VoiceActivityDetector` over the
inbound PCM, buffers the frames between speech onset and the hangover-confirmed
end, and posts that buffered segment for transcription.  The hangover length is
the endpointing latency, and it is a setting.

**Whisper emits no partials, and barge-in is driven by partials.**
``BargeInController.caller_speech_started`` is only ever called with a
``TranscriptPartialFrame``.  With a batch recogniser there are no interim
hypotheses, so without intervention a caller could never interrupt the agent
mid-sentence — the first evidence of caller speech would be a final that lands
a full hangover AFTER the caller started talking, by which point the agent has
kept talking over them.

So on VAD speech-onset this provider emits a SYNTHETIC
``STTPartialEvent(text="", stability=0.0)``.  It carries no hypothesis, because
there is none; it exists purely as the barge-in trigger.  ``STTAdapter`` turns
it into a ``TranscriptPartialFrame`` unchanged, which is exactly what
``BargeInController`` needs, and Inv-1 already forbids partials from mutating
booking state — so an empty-text partial can never be mistaken for content.
The zero stability marks it as carrying no recognition confidence at all.
"""

from __future__ import annotations

import asyncio
import io
import logging
import math
import wave
from collections import deque
from typing import TYPE_CHECKING, Any

from cordia_agent.streaming.providers.audio_config import AudioProviderError
from cordia_agent.streaming.providers.audio_http import (
    RETRYABLE_ERRORS,
    attempt_timeouts,
)
from cordia_agent.streaming.providers.types import STTFinalEvent, STTPartialEvent
from cordia_agent.streaming.providers.vad import (
    EnergyVoiceActivityDetector,
    VadEvent,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator

    import httpx

    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings
    from cordia_agent.streaming.providers.types import STTProviderEvent
    from cordia_agent.streaming.providers.vad import VoiceActivityDetector


logger = logging.getLogger(__name__)

# Stability reported on the synthetic barge-in partial.  Zero is the honest
# value: the event carries no recognition hypothesis whatsoever, only the fact
# that the caller has started speaking.
BARGE_IN_PARTIAL_STABILITY = 0.0

# Text on the synthetic barge-in partial.  Empty, so that any consumer which
# renders partials shows nothing rather than a fabricated word.
BARGE_IN_PARTIAL_TEXT = ""


def pcm_to_wav(pcm: bytes, sample_rate_hz: int) -> bytes:
    """Wrap raw 16-bit mono PCM in a RIFF/WAVE container.

    The transcription route takes an audio FILE, not a raw sample stream, so
    the buffered utterance needs a container.  WAV is chosen because wrapping
    is header-only: no re-encoding, no quality loss, and no dependency beyond
    the stdlib ``wave`` module.

    Args:
        pcm: Raw 16-bit signed little-endian mono PCM samples.
        sample_rate_hz: Sample rate of *pcm*.

    Returns:
        The complete WAV file bytes.
    """
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate_hz)
        wav_file.writeframes(pcm)
    return buffer.getvalue()


def confidence_from_segments(payload: dict[str, Any], fallback: float) -> float:
    """Derive an ASR confidence in 0..1 from a ``verbose_json`` payload.

    Whisper reports no confidence directly.  Its per-segment ``avg_logprob`` is
    the mean log-probability of the emitted tokens, so ``exp(avg_logprob)`` is
    a usable per-segment likelihood; the segments are averaged and clamped.

    Args:
        payload: Parsed transcription response body.
        fallback: Confidence to report when the payload carries no usable
            ``avg_logprob`` — e.g. when the deployment answered a plain
            ``json`` response format.

    Returns:
        A confidence within 0.0..1.0.
    """
    segments = payload.get("segments")
    if not isinstance(segments, list):
        return fallback

    logprobs: list[float] = []
    for segment in segments:
        if not isinstance(segment, dict):
            continue
        value = segment.get("avg_logprob")
        if isinstance(value, int | float) and not isinstance(value, bool):
            logprobs.append(float(value))
    if not logprobs:
        return fallback

    mean_logprob = sum(logprobs) / len(logprobs)
    return min(max(math.exp(mean_logprob), 0.0), 1.0)


class AzureWhisperSTTProvider:
    """``STTProvider`` backed by an Azure ``whisper`` batch deployment.

    One instance drives ONE concurrent stream: the injected detector and the
    utterance buffer are per-stream state, reset at the top of every
    :meth:`transcribe` call.  Construct one per session, as every other port in
    this package is constructed and injected.
    """

    def __init__(
        self,
        settings: AzureAudioSettings,
        http: httpx.AsyncClient,
        *,
        vad: VoiceActivityDetector | None = None,
    ) -> None:
        """Initialise the provider with injected transport and detector.

        Args:
            settings: Resolved audio settings; supplies the deployment, the
                endpoint, the key, and every VAD threshold.
            http: An ``httpx.AsyncClient`` — backed by a ``MockTransport`` in
                tests, so the suite never touches the network.
            vad: Optional detector override.  Defaults to an
                :class:`EnergyVoiceActivityDetector` built from *settings*.
        """
        self._settings = settings
        self._http = http
        self._vad: VoiceActivityDetector = vad or EnergyVoiceActivityDetector(
            sample_rate_hz=settings.input_sample_rate_hz,
            frame_ms=settings.vad_frame_ms,
            energy_threshold=settings.vad_energy_threshold,
            onset_frames=settings.vad_onset_frames,
            hangover_frames=settings.vad_hangover_frames,
        )

    async def transcribe(
        self, pcm_chunks: AsyncIterable[bytes]
    ) -> AsyncIterator[STTProviderEvent]:
        """Yield recognition events for an async stream of caller PCM.

        Inbound chunks arrive at whatever size the media plane produces, so
        they are re-framed into whole VAD frames first; a partial trailing
        frame is carried over to the next chunk rather than analysed short.

        Per utterance the sequence is: one synthetic
        :class:`~...types.STTPartialEvent` at speech onset (the barge-in
        trigger — see the module docstring), then one
        :class:`~...types.STTFinalEvent` once the hangover confirms the end and
        whisper has returned.  An utterance whose transcript is empty or
        whitespace yields NO final: that is noise or a cough, and emitting an
        empty final would push an empty committable transcript downstream.

        The stream may carry several utterances; the caller decides how many to
        consume (``STTAdapter`` stops at the first final).  If the stream ends
        mid-utterance, the buffered speech is flushed and transcribed rather
        than discarded.

        Args:
            pcm_chunks: Async stream of 16-bit signed little-endian mono PCM at
                ``settings.input_sample_rate_hz``.

        Yields:
            :class:`~...types.STTPartialEvent` at each speech onset and
            :class:`~...types.STTFinalEvent` at each transcribed endpoint.

        Raises:
            AudioProviderError: if the transcription route returns a non-2xx
                status.
            TimeoutError: if every bounded attempt exceeded its deadline.
        """
        settings = self._settings
        self._vad.reset()

        frame_bytes = self._vad.frame_bytes
        max_utterance_bytes = int(
            settings.max_utterance_seconds * settings.input_sample_rate_hz * 2
        )
        preroll: deque[bytes] = deque(maxlen=settings.vad_preroll_frames or 1)

        pending = bytearray()
        utterance = bytearray()
        in_speech = False

        async for chunk in pcm_chunks:
            pending += chunk

            while len(pending) >= frame_bytes:
                frame = bytes(pending[:frame_bytes])
                del pending[:frame_bytes]
                event = self._vad.process_frame(frame)

                if event is VadEvent.SPEECH_START:
                    in_speech = True
                    utterance = bytearray()
                    if settings.vad_preroll_frames:
                        for buffered in preroll:
                            utterance += buffered
                    preroll.clear()
                    utterance += frame
                    # The barge-in trigger — see the module docstring.
                    yield STTPartialEvent(
                        text=BARGE_IN_PARTIAL_TEXT,
                        stability=BARGE_IN_PARTIAL_STABILITY,
                    )
                    continue

                if not in_speech:
                    if settings.vad_preroll_frames:
                        preroll.append(frame)
                    continue

                utterance += frame
                force_endpoint = len(utterance) >= max_utterance_bytes
                if event is not VadEvent.SPEECH_END and not force_endpoint:
                    continue

                in_speech = False
                segment = bytes(utterance)
                utterance = bytearray()
                if force_endpoint:
                    # Nothing said "stop" — reset so the detector re-arms and a
                    # still-talking caller triggers a fresh onset (and a fresh
                    # barge-in partial) rather than staying stuck in-speech.
                    self._vad.reset()
                final = await self._transcribe_segment(segment)
                if final is not None:
                    yield final

        if in_speech and utterance:
            final = await self._transcribe_segment(bytes(utterance))
            if final is not None:
                yield final

    async def _transcribe_segment(self, pcm: bytes) -> STTFinalEvent | None:
        """Transcribe one buffered utterance.

        Args:
            pcm: The buffered raw PCM for a single utterance.

        Returns:
            The final event, or ``None`` when the transcript is empty (silence
            or non-speech noise that cleared the energy threshold).

        Raises:
            AudioProviderError: on a non-2xx response.
            TimeoutError: if every bounded attempt exceeded its deadline.
        """
        payload = await self._post_transcription(pcm)
        text = payload.get("text")
        if not isinstance(text, str) or not text.strip():
            logger.debug("whisper returned an empty transcript; no final emitted")
            return None

        return STTFinalEvent(
            text=text.strip(),
            asr_confidence=confidence_from_segments(
                payload, self._settings.fallback_asr_confidence
            ),
            is_endpoint=True,
        )

    async def _post_transcription(self, pcm: bytes) -> dict[str, Any]:
        """POST one utterance to the transcription route under a bounded retry.

        Args:
            pcm: The buffered raw PCM for a single utterance.

        Returns:
            The parsed response body, or an empty mapping if the body is not a
            JSON object.

        Raises:
            AudioProviderError: on a non-2xx response.  The body is never read
                into the message — it can echo caller speech.
            TimeoutError: if every attempt exceeded its deadline.
        """
        settings = self._settings
        wav_bytes = pcm_to_wav(pcm, settings.input_sample_rate_hz)
        url = settings.transcription_url()

        last_error: BaseException | None = None
        for timeout in attempt_timeouts(settings.base_timeout_seconds):
            try:
                response = await asyncio.wait_for(
                    self._http.post(
                        url,
                        headers=settings.auth_headers(),
                        files={"file": ("utterance.wav", wav_bytes, "audio/wav")},
                        data={"response_format": "verbose_json"},
                        timeout=timeout,
                    ),
                    timeout=timeout,
                )
            except RETRYABLE_ERRORS as exc:
                last_error = exc
                continue

            if response.status_code >= 300:
                raise AudioProviderError("transcription", response.status_code)

            try:
                body: Any = response.json()
            except ValueError:
                return {}
            return body if isinstance(body, dict) else {}

        raise TimeoutError(
            "azure transcription timed out on every attempt"
        ) from last_error
