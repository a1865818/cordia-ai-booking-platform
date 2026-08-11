"""Streaming provider ports (STT, TTS, S2S, audio sink) and their event types.

The Azure implementations (``azure_realtime_stt``, ``azure_stt``, ``azure_tts``)
are deliberately NOT re-exported here.  Importing this package must stay free of
any provider or transport import so the port surface can be used — and stubbed —
without pulling ``httpx``, ``websockets`` or any Azure setting into the graph's
import path.  Import the concrete providers from their own modules at the
construction site.

Two ``STTProvider`` implementations ship: ``azure_realtime_stt`` (the default,
WebSocket, service-side VAD) and ``azure_stt`` (batch REST plus the local
:mod:`~cordia_agent.streaming.providers.vad` detector, for resources where only
a ``whisper`` deployment exists).  See either module's docstring for which
applies.
"""

from cordia_agent.streaming.providers.audio_sink import AudioChunkMetadata, AudioSink
from cordia_agent.streaming.providers.s2s_port import (
    S2SAgentText,
    S2SAudioChunk,
    S2SFinalTranscript,
    S2SPartialTranscript,
    S2SProvider,
    S2SProviderEvent,
    S2STurnCancelled,
)
from cordia_agent.streaming.providers.tts_port import TTSCancellationToken, TTSProvider
from cordia_agent.streaming.providers.types import (
    STTFinalEvent,
    STTPartialEvent,
    STTProvider,
    STTProviderEvent,
)
from cordia_agent.streaming.providers.vad import (
    EnergyVoiceActivityDetector,
    VadEvent,
    VoiceActivityDetector,
)


__all__ = [
    "AudioChunkMetadata",
    "AudioSink",
    "EnergyVoiceActivityDetector",
    "S2SAgentText",
    "S2SAudioChunk",
    "S2SFinalTranscript",
    "S2SPartialTranscript",
    "S2SProvider",
    "S2SProviderEvent",
    "S2STurnCancelled",
    "STTFinalEvent",
    "STTPartialEvent",
    "STTProvider",
    "STTProviderEvent",
    "TTSCancellationToken",
    "TTSProvider",
    "VadEvent",
    "VoiceActivityDetector",
]
