"""Streaming provider ports and deterministic test providers."""

from cordia_agent.streaming.providers.fake import (
    FakeAgentResumeHook,
    FakeHandoffHook,
    FakeSessionLookup,
    FakeSTTProvider,
)
from cordia_agent.streaming.providers.fake_s2s import FakeS2SProvider
from cordia_agent.streaming.providers.fake_tts import FakeTTSProvider
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


__all__ = [
    "FakeAgentResumeHook",
    "FakeHandoffHook",
    "FakeS2SProvider",
    "FakeSTTProvider",
    "FakeSessionLookup",
    "FakeTTSProvider",
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
]
