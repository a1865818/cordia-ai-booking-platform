"""Environment-driven Azure audio settings (voice pipeline provider seam).

Mirrors :mod:`cordia_agent.llm.config` — a frozen slots dataclass built by a
``*_from_env`` resolver that fails closed with ``ModelNotConfigured`` at
construction time, so a misconfigured deployment surfaces at startup rather
than as a 400 mid-call.  Audio settings live here rather than on
``AzureModelSettings`` because they describe two *different* deployments with
knobs (voice, sample rate, VAD thresholds) that have no meaning for a chat
model.

Both audio deployments sit on the same Azure AI Services resource as the chat
deployment, so ``AZURE_OPENAI_ENDPOINT`` and ``AZURE_OPENAI_API_KEY`` are
reused verbatim.  Everything audio-specific is namespaced ``CORDIA_AUDIO_*``
and, like the chat settings, may be overridden per logical role by suffixing
the variable with ``_<ROLE>`` upper-cased.

Two STT surfaces, one settings object
    ``CORDIA_AUDIO_REALTIME_*`` configures the realtime WebSocket route used by
    ``AzureRealtimeSTTProvider`` (the default: it streams, and the service does
    the endpointing).  ``CORDIA_AUDIO_STT_DEPLOYMENT`` plus the
    ``CORDIA_AUDIO_VAD_*`` knobs configure the batch route used by
    ``AzureWhisperSTTProvider``, which stays available for resources where only
    a plain ``whisper`` deployment exists.  Exactly one provider is constructed
    per session; the unused half of the settings is inert.

Sample-rate gap (deliberate, documented, NOT solved here)
    ``gpt-4o-mini-tts`` emits 24 kHz 16-bit mono PCM.  PSTN carriage needs
    8 kHz mulaw.  This module makes the output format and rate a *setting* and
    stamps them onto every ``AudioChunkMetadata``; it does NOT resample.
    Downsampling 24 kHz to 8 kHz correctly needs an anti-aliasing filter, and a
    naive decimation would alias audibly — so the conversion belongs to the
    media plane (or a sink that wraps a real codec), not to a hand-rolled
    resampler in the provider.
"""

from __future__ import annotations

import os
from dataclasses import dataclass

from cordia_agent.llm.config import ModelNotConfigured


class AudioNotConfigured(ModelNotConfigured):
    """Raised when the Azure audio environment is absent or inconsistent.

    Subclasses ``ModelNotConfigured`` so startup code that already fails closed
    on model misconfiguration catches audio misconfiguration for free, while
    audio-specific handling can still discriminate.
    """


class AudioProviderError(RuntimeError):
    """Raised when an Azure audio endpoint returns a non-success response.

    Deliberately carries only the deployment kind and the failure code.
    Response bodies are never interpolated: a transcription error body can
    echo caller speech, and an auth error body can echo the key.

    The code is an HTTP status for the REST routes and a symbolic error code
    (e.g. ``"invalid_request_error"``) for the realtime WebSocket surface,
    which reports failures as ``error`` frames rather than status lines.
    """

    def __init__(self, operation: str, status_code: int | str) -> None:
        """Initialise the error from safe, non-sensitive facts only.

        Args:
            operation: Coarse operation label, e.g. ``"transcription"``.
            status_code: The HTTP status code, or the symbolic error code from
                a realtime ``error`` frame.  Never a message or a body.
        """
        detail = (
            f"HTTP {status_code}"
            if isinstance(status_code, int)
            else f"code {status_code}"
        )
        super().__init__(f"azure {operation} failed with {detail}")
        self.operation = operation
        self.status_code = status_code


# Default deployment names on the ``<your-ai-services-resource>`` AI Services resource.
DEFAULT_STT_DEPLOYMENT = "whisper"
DEFAULT_TTS_DEPLOYMENT = "gpt-4o-mini-tts"

# New enough for both the audio/transcriptions and audio/speech routes.
DEFAULT_AUDIO_API_VERSION = "2025-03-01-preview"

# api-version for the realtime WebSocket surface.  Distinct from the REST
# api-version above: the two routes version independently, and the realtime
# handshake rejects the older value.
DEFAULT_REALTIME_API_VERSION = "2025-04-01-preview"

# Transcription model named INSIDE the realtime session config.  It is not a
# querystring ``deployment`` — passing one to /openai/realtime is answered with
# HTTP 400 OperationNotSupported.
DEFAULT_REALTIME_TRANSCRIPTION_MODEL = "gpt-realtime-whisper"

# Trailing silence the service's server-side VAD requires before it endpoints
# an utterance.  This IS the endpointing latency, exactly as
# ``vad_hangover_frames`` is for the local detector (25 x 20 ms = 500 ms), and
# it is the single most impactful realtime knob.
DEFAULT_REALTIME_SILENCE_DURATION_MS = 500

# Maximum PCM carried by one ``input_audio_buffer.append`` frame.  16000 bytes
# is 500 ms at 16 kHz 16-bit mono: large enough that base64 framing overhead is
# negligible, small enough that a media plane handing over a multi-second block
# is still delivered incrementally rather than as one oversized frame.
DEFAULT_REALTIME_SEND_CHUNK_BYTES = 16000

# Native output of gpt-4o-mini-tts on response_format="pcm": 24 kHz, 16-bit,
# mono, little-endian, header-less.
DEFAULT_TTS_SAMPLE_RATE_HZ = 24000

# Per-attempt audio deadline.  Looser than the chat model's 8 s: whisper is a
# batch call over a whole utterance, and a cold TTS deployment's first byte can
# lag.  Still bounded — an unbounded audio call would hold a caller on a dead
# line with no route to handoff.
DEFAULT_AUDIO_BASE_TIMEOUT_SECONDS = 10.0

# Response formats the Azure speech route accepts.  ``pcm`` is the only one
# that streams usefully chunk-by-chunk without a container header, so it is the
# default; the rest are allowed for callers whose media plane wants a container.
VALID_TTS_RESPONSE_FORMATS: frozenset[str] = frozenset(
    {"pcm", "wav", "mp3", "opus", "aac", "flac"}
)

_CONTENT_TYPES: dict[str, str] = {
    "pcm": "audio/pcm",
    "wav": "audio/wav",
    "mp3": "audio/mpeg",
    "opus": "audio/opus",
    "aac": "audio/aac",
    "flac": "audio/flac",
}


@dataclass(frozen=True, slots=True)
class AzureAudioSettings:
    """Resolved connection and tuning settings for the voice pipeline.

    Attributes:
        endpoint: Azure AI Services resource endpoint, reused from the chat
            configuration (``https://<resource>.openai.azure.com/`` form).
        api_key: Azure API key.  Never logged.
        api_version: Azure REST api-version for both audio routes.
        realtime_api_version: api-version for the realtime WebSocket route.
        realtime_transcription_model: Model named in the realtime session
            config.  The realtime route takes no ``deployment`` querystring.
        realtime_silence_duration_ms: Trailing silence the service's server-side
            VAD requires before endpointing.  The realtime equivalent of
            ``vad_hangover_frames``, and the endpointing latency.
        realtime_send_chunk_bytes: Maximum PCM per ``input_audio_buffer.append``
            frame.  Caps the size of a single write, not a buffer: the sender
            awaits each frame, so the caller's stream is back-pressured rather
            than accumulated.
        stt_deployment: Deployment name for batch transcription.
        tts_deployment: Deployment name for streaming speech synthesis.
        tts_voice: Voice preset requested from the speech route.
        tts_response_format: One of :data:`VALID_TTS_RESPONSE_FORMATS`.
        tts_sample_rate_hz: Sample rate stamped onto emitted chunk metadata.
            Descriptive, not prescriptive — the model emits 24 kHz on ``pcm``
            and nothing here resamples (see the module docstring).
        tts_chunk_bytes: Byte budget per streamed audio chunk.  Smaller values
            cut time-to-first-chunk at the cost of more sink writes.
        input_sample_rate_hz: Sample rate of the inbound caller PCM, assumed
            16-bit signed little-endian mono.
        vad_frame_ms: Analysis frame size for voice-activity detection.
        vad_energy_threshold: RMS amplitude (int16 scale, 0..32767) at or above
            which a frame counts as speech.
        vad_onset_frames: Consecutive speech frames required to declare an
            utterance start.  Guards against single-frame clicks.
        vad_hangover_frames: Consecutive silence frames required to declare an
            utterance end.  This is the endpointing latency and the single most
            impactful knob: too short truncates mid-sentence pauses, too long
            delays every reply.
        vad_preroll_frames: Frames retained before onset and prepended to the
            buffered utterance, so the first phoneme is not clipped.
        max_utterance_seconds: Hard cap on buffered speech.  A caller who never
            pauses (or a stuck-open line) is force-endpointed rather than
            buffered without bound.
        fallback_asr_confidence: Confidence reported when the transcription
            response carries no per-segment log-probabilities.
        base_timeout_seconds: First-attempt deadline for either audio call.
    """

    endpoint: str
    api_key: str
    api_version: str = DEFAULT_AUDIO_API_VERSION
    realtime_api_version: str = DEFAULT_REALTIME_API_VERSION
    realtime_transcription_model: str = DEFAULT_REALTIME_TRANSCRIPTION_MODEL
    realtime_silence_duration_ms: int = DEFAULT_REALTIME_SILENCE_DURATION_MS
    realtime_send_chunk_bytes: int = DEFAULT_REALTIME_SEND_CHUNK_BYTES
    stt_deployment: str = DEFAULT_STT_DEPLOYMENT
    tts_deployment: str = DEFAULT_TTS_DEPLOYMENT
    tts_voice: str = "alloy"
    tts_response_format: str = "pcm"
    tts_sample_rate_hz: int = DEFAULT_TTS_SAMPLE_RATE_HZ
    tts_chunk_bytes: int = 4096
    input_sample_rate_hz: int = 16000
    vad_frame_ms: int = 20
    vad_energy_threshold: float = 300.0
    vad_onset_frames: int = 2
    vad_hangover_frames: int = 25
    vad_preroll_frames: int = 5
    max_utterance_seconds: float = 30.0
    fallback_asr_confidence: float = 0.9
    base_timeout_seconds: float = DEFAULT_AUDIO_BASE_TIMEOUT_SECONDS

    def __post_init__(self) -> None:
        """Validate the combination, failing closed on anything unusable.

        Raises:
            AudioNotConfigured: on a missing endpoint/key, an unsupported
                response format, a non-positive tuning value, an out-of-range
                confidence, or a frame size that is not a whole number of
                samples at the configured input rate.
        """
        for name, value in (("endpoint", self.endpoint), ("api_key", self.api_key)):
            if not value:
                raise AudioNotConfigured(f"{name} must not be empty")

        if self.tts_response_format not in VALID_TTS_RESPONSE_FORMATS:
            raise AudioNotConfigured(
                f"invalid tts_response_format {self.tts_response_format!r}; "
                f"expected one of {sorted(VALID_TTS_RESPONSE_FORMATS)} "
                "(set CORDIA_AUDIO_TTS_RESPONSE_FORMAT)"
            )

        positives: tuple[tuple[str, float], ...] = (
            ("tts_sample_rate_hz", self.tts_sample_rate_hz),
            ("tts_chunk_bytes", self.tts_chunk_bytes),
            ("input_sample_rate_hz", self.input_sample_rate_hz),
            ("vad_frame_ms", self.vad_frame_ms),
            ("vad_energy_threshold", self.vad_energy_threshold),
            ("vad_onset_frames", self.vad_onset_frames),
            ("vad_hangover_frames", self.vad_hangover_frames),
            ("max_utterance_seconds", self.max_utterance_seconds),
            ("base_timeout_seconds", self.base_timeout_seconds),
            ("realtime_silence_duration_ms", self.realtime_silence_duration_ms),
            ("realtime_send_chunk_bytes", self.realtime_send_chunk_bytes),
        )
        for name, value in positives:
            if value <= 0:
                raise AudioNotConfigured(f"{name} must be > 0 (got {value!r})")

        if self.vad_preroll_frames < 0:
            raise AudioNotConfigured(
                f"vad_preroll_frames must be >= 0 (got {self.vad_preroll_frames!r})"
            )

        if not 0.0 <= self.fallback_asr_confidence <= 1.0:
            raise AudioNotConfigured(
                "fallback_asr_confidence must be within 0.0..1.0 "
                f"(got {self.fallback_asr_confidence!r})"
            )

        if (self.input_sample_rate_hz * self.vad_frame_ms) % 1000 != 0:
            raise AudioNotConfigured(
                f"vad_frame_ms={self.vad_frame_ms} does not divide into whole "
                f"samples at input_sample_rate_hz={self.input_sample_rate_hz}; "
                "pick a frame size such that rate * ms is a multiple of 1000"
            )

    @property
    def vad_frame_bytes(self) -> int:
        """Bytes in one VAD analysis frame of 16-bit mono PCM."""
        return (self.input_sample_rate_hz * self.vad_frame_ms // 1000) * 2

    @property
    def tts_content_type(self) -> str:
        """IANA media type matching :attr:`tts_response_format`."""
        return _CONTENT_TYPES[self.tts_response_format]

    def transcription_url(self) -> str:
        """Return the absolute Azure batch-transcription URL."""
        return (
            f"{self.endpoint.rstrip('/')}/openai/deployments/"
            f"{self.stt_deployment}/audio/transcriptions"
            f"?api-version={self.api_version}"
        )

    def realtime_transcription_url(self) -> str:
        """Return the absolute Azure realtime transcription WebSocket URL.

        Two details are load-bearing and were confirmed against the live
        service:

        - the scheme is ``wss``, so the ``https://`` on the configured endpoint
          is stripped rather than rewritten around;
        - there is NO ``deployment`` query parameter.  Supplying one is
          answered with HTTP 400 ``OperationNotSupported`` — the model is named
          in the ``transcription_session.update`` payload instead.
        """
        host = self.endpoint.rstrip("/")
        for scheme in ("https://", "http://"):
            if host.startswith(scheme):
                host = host[len(scheme) :]
                break
        return (
            f"wss://{host}/openai/realtime"
            f"?api-version={self.realtime_api_version}&intent=transcription"
        )

    def speech_url(self) -> str:
        """Return the absolute Azure streaming-speech URL."""
        return (
            f"{self.endpoint.rstrip('/')}/openai/deployments/"
            f"{self.tts_deployment}/audio/speech"
            f"?api-version={self.api_version}"
        )

    def auth_headers(self) -> dict[str, str]:
        """Return the Azure auth header.

        The value is a secret: never log the returned mapping.
        """
        return {"api-key": self.api_key}


def _env(name: str) -> str | None:
    """Return a stripped environment value, treating blank as absent."""
    value = os.environ.get(name, "").strip()
    return value or None


def _as_float(name: str, raw: str | None, default: float) -> float:
    """Parse *raw* as a float, failing closed on garbage.

    Args:
        name: Environment variable name, used only in the error message.
        raw: The raw string value, or ``None`` to take *default*.
        default: Value used when *raw* is absent.

    Returns:
        The parsed float.

    Raises:
        AudioNotConfigured: if *raw* is present but not a number.
    """
    if raw is None:
        return default
    try:
        return float(raw)
    except ValueError as exc:
        raise AudioNotConfigured(f"{name} must be a number, got {raw!r}") from exc


def _as_int(name: str, raw: str | None, default: int) -> int:
    """Parse *raw* as an int, failing closed on garbage.

    Args:
        name: Environment variable name, used only in the error message.
        raw: The raw string value, or ``None`` to take *default*.
        default: Value used when *raw* is absent.

    Returns:
        The parsed integer.

    Raises:
        AudioNotConfigured: if *raw* is present but not an integer.
    """
    if raw is None:
        return default
    try:
        return int(raw)
    except ValueError as exc:
        raise AudioNotConfigured(f"{name} must be an integer, got {raw!r}") from exc


def audio_settings_from_env(role: str | None = None) -> AzureAudioSettings:
    """Build :class:`AzureAudioSettings` from the process environment.

    Reuses ``AZURE_OPENAI_ENDPOINT`` and ``AZURE_OPENAI_API_KEY`` from the chat
    configuration; every audio knob is read from its ``CORDIA_AUDIO_*``
    variable.  When *role* is given, each variable may be overridden by
    suffixing it with ``_<ROLE>`` upper-cased — e.g. ``role="pstn"`` checks
    ``CORDIA_AUDIO_TTS_RESPONSE_FORMAT_PSTN`` before the shared value.

    Args:
        role: Optional logical role selecting per-role environment overrides.

    Returns:
        A validated :class:`AzureAudioSettings`.

    Raises:
        AudioNotConfigured: if the endpoint or key is missing, if any numeric
            knob is unparseable, or if the resulting combination is invalid.
    """
    suffix = f"_{role.upper()}" if role else ""

    def read(name: str) -> str | None:
        return (_env(f"{name}{suffix}") if suffix else None) or _env(name)

    endpoint = read("AZURE_OPENAI_ENDPOINT")
    api_key = read("AZURE_OPENAI_API_KEY")

    missing = [
        name
        for name, value in (
            ("AZURE_OPENAI_ENDPOINT", endpoint),
            ("AZURE_OPENAI_API_KEY", api_key),
        )
        if not value
    ]
    if missing:
        role_hint = f" for role {role!r}" if role else ""
        raise AudioNotConfigured(
            f"Azure audio not configured{role_hint}: missing "
            f"{', '.join(missing)}. See apps/langgraph/.env.example."
        )

    # asserted non-None by the `missing` guard above
    assert endpoint is not None and api_key is not None

    return AzureAudioSettings(
        endpoint=endpoint,
        api_key=api_key,
        api_version=read("CORDIA_AUDIO_API_VERSION") or DEFAULT_AUDIO_API_VERSION,
        realtime_api_version=read("CORDIA_AUDIO_REALTIME_API_VERSION")
        or DEFAULT_REALTIME_API_VERSION,
        realtime_transcription_model=read("CORDIA_AUDIO_REALTIME_MODEL")
        or DEFAULT_REALTIME_TRANSCRIPTION_MODEL,
        realtime_silence_duration_ms=_as_int(
            "CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS",
            read("CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS"),
            DEFAULT_REALTIME_SILENCE_DURATION_MS,
        ),
        realtime_send_chunk_bytes=_as_int(
            "CORDIA_AUDIO_REALTIME_SEND_CHUNK_BYTES",
            read("CORDIA_AUDIO_REALTIME_SEND_CHUNK_BYTES"),
            DEFAULT_REALTIME_SEND_CHUNK_BYTES,
        ),
        stt_deployment=read("CORDIA_AUDIO_STT_DEPLOYMENT") or DEFAULT_STT_DEPLOYMENT,
        tts_deployment=read("CORDIA_AUDIO_TTS_DEPLOYMENT") or DEFAULT_TTS_DEPLOYMENT,
        tts_voice=read("CORDIA_AUDIO_TTS_VOICE") or "alloy",
        tts_response_format=read("CORDIA_AUDIO_TTS_RESPONSE_FORMAT") or "pcm",
        tts_sample_rate_hz=_as_int(
            "CORDIA_AUDIO_TTS_SAMPLE_RATE_HZ",
            read("CORDIA_AUDIO_TTS_SAMPLE_RATE_HZ"),
            DEFAULT_TTS_SAMPLE_RATE_HZ,
        ),
        tts_chunk_bytes=_as_int(
            "CORDIA_AUDIO_TTS_CHUNK_BYTES", read("CORDIA_AUDIO_TTS_CHUNK_BYTES"), 4096
        ),
        input_sample_rate_hz=_as_int(
            "CORDIA_AUDIO_INPUT_SAMPLE_RATE_HZ",
            read("CORDIA_AUDIO_INPUT_SAMPLE_RATE_HZ"),
            16000,
        ),
        vad_frame_ms=_as_int(
            "CORDIA_AUDIO_VAD_FRAME_MS", read("CORDIA_AUDIO_VAD_FRAME_MS"), 20
        ),
        vad_energy_threshold=_as_float(
            "CORDIA_AUDIO_VAD_ENERGY_THRESHOLD",
            read("CORDIA_AUDIO_VAD_ENERGY_THRESHOLD"),
            300.0,
        ),
        vad_onset_frames=_as_int(
            "CORDIA_AUDIO_VAD_ONSET_FRAMES", read("CORDIA_AUDIO_VAD_ONSET_FRAMES"), 2
        ),
        vad_hangover_frames=_as_int(
            "CORDIA_AUDIO_VAD_HANGOVER_FRAMES",
            read("CORDIA_AUDIO_VAD_HANGOVER_FRAMES"),
            25,
        ),
        vad_preroll_frames=_as_int(
            "CORDIA_AUDIO_VAD_PREROLL_FRAMES",
            read("CORDIA_AUDIO_VAD_PREROLL_FRAMES"),
            5,
        ),
        max_utterance_seconds=_as_float(
            "CORDIA_AUDIO_MAX_UTTERANCE_SECONDS",
            read("CORDIA_AUDIO_MAX_UTTERANCE_SECONDS"),
            30.0,
        ),
        fallback_asr_confidence=_as_float(
            "CORDIA_AUDIO_FALLBACK_ASR_CONFIDENCE",
            read("CORDIA_AUDIO_FALLBACK_ASR_CONFIDENCE"),
            0.9,
        ),
        base_timeout_seconds=_as_float(
            "CORDIA_AUDIO_BASE_TIMEOUT_SECONDS",
            read("CORDIA_AUDIO_BASE_TIMEOUT_SECONDS"),
            DEFAULT_AUDIO_BASE_TIMEOUT_SECONDS,
        ),
    )
