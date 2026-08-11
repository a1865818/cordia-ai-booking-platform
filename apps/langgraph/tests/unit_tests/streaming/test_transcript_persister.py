"""Transcript-persister port tests — spec 14 §4.3/§5.2 (gap-linkage phase 4).

Pins: the pipeline only hands the persister SCRUBBED committed finals (never partials,
never raw PAN text); no persister wired ⇒ behavior byte-identical; a raising persister
never breaks the caller's turn (fail-open for the pipe); ``ToolTranscriptPersister`` is
fail-closed on consent (None/False ⇒ no tool call at all).
"""

from collections.abc import AsyncIterator

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import Pipeline, PipelineSession
from cordia_agent.streaming.providers import (
    STTFinalEvent,
    STTPartialEvent,
)
from cordia_agent.streaming.transcript_persister import ToolTranscriptPersister
from cordia_agent.streaming_models import AgentTextFinalFrame, TranscriptFinalFrame
from cordia_agent.tools.contracts import ToolResultEnvelope, TranscriptTurnInput
from tests._stubs.streaming_providers_fake import FakeSTTProvider
from tests._stubs.streaming_tts_fake import FakeTTSProvider
from tests.unit_tests.streaming.fakes.fake_agent import FakeAgent


def _ok_envelope(inp: TranscriptTurnInput) -> ToolResultEnvelope:
    return ToolResultEnvelope(
        ok=True,
        status="ok",
        data={"session_id": inp.session_id, "seq": inp.seq, "persisted": True},
        correlation_id="corr-test",
    )


SESSION = PipelineSession(
    tenant_id="tenant_1",
    session_id="session_1",
    turn_id="turn_1",
    correlation_id="correlation_1",
)

AGENT_ENVELOPE = {
    "tenant_id": "tenant_1",
    "session_id": "session_1",
    "turn_id": "turn_1",
    "correlation_id": "correlation_1",
    "causation_id": None,
    "ts": "2026-06-18T00:00:00+00:00",
}


async def _pcm(count: int = 2) -> AsyncIterator[bytes]:
    for index in range(count):
        yield f"pcm_{index}".encode()


def _agent() -> FakeAgent:
    return FakeAgent(
        [
            AgentTextFinalFrame(
                type=StreamFrameType.AGENT_TEXT_FINAL,
                **AGENT_ENVELOPE,
                seq=100,
                text="Hello.",
            )
        ]
    )


class RecordingPersister:
    def __init__(self, *, raise_on_persist: bool = False) -> None:
        self.frames: list[TranscriptFinalFrame] = []
        self._raise = raise_on_persist

    async def persist(self, frame: TranscriptFinalFrame) -> None:
        self.frames.append(frame)
        if self._raise:
            raise RuntimeError("persist backend down")


def _pipeline(persister: RecordingPersister | None) -> Pipeline:
    return Pipeline(
        stt_provider=FakeSTTProvider(
            [
                STTPartialEvent(text="my card is 4111 1111 1111 1111", stability=0.4),
                STTFinalEvent(
                    text="my card is 4111 1111 1111 1111", asr_confidence=0.9
                ),
            ]
        ),
        tts_provider=FakeTTSProvider(),
        agent=_agent(),
        transcript_persister=persister,
    )


@pytest.mark.unit
async def test_persister_receives_only_scrubbed_finals() -> None:
    persister = RecordingPersister()

    frames = [f async for f in _pipeline(persister).run(_pcm(), session=SESSION)]

    assert len(persister.frames) == 1, "one committed final ⇒ exactly one persist call"
    persisted = persister.frames[0]
    assert isinstance(persisted, TranscriptFinalFrame)
    # Inv-12: the persister NEVER sees the raw PAN — only the scrubbed text.
    assert "4111" not in persisted.text
    assert "[REDACTED]" in persisted.text
    # And the pipe still produced its frames (persistence is a side port).
    assert any(isinstance(f, TranscriptFinalFrame) for f in frames)


@pytest.mark.unit
async def test_raising_persister_never_breaks_the_turn() -> None:
    persister = RecordingPersister(raise_on_persist=True)

    frames = [f async for f in _pipeline(persister).run(_pcm(), session=SESSION)]

    assert len(persister.frames) == 1
    # §8 fail-open for the pipe: the agent/TTS frames still flowed.
    assert any(isinstance(f, AgentTextFinalFrame) for f in frames)


@pytest.mark.unit
async def test_no_persister_means_no_behavior_change() -> None:
    frames = [f async for f in _pipeline(None).run(_pcm(), session=SESSION)]

    assert any(isinstance(f, TranscriptFinalFrame) for f in frames)


# ── ToolTranscriptPersister consent gating (fail-closed) ────────────────────────────


def _final_frame(text: str = "table for two") -> TranscriptFinalFrame:
    return TranscriptFinalFrame(
        type=StreamFrameType.TRANSCRIPT_FINAL,
        **AGENT_ENVELOPE,
        seq=7,
        text=text,
        asr_confidence=0.9,
        is_endpoint=True,
    )


@pytest.mark.unit
@pytest.mark.parametrize("recording_allowed", [None, False])
async def test_tool_persister_consent_fail_closed(
    monkeypatch, recording_allowed
) -> None:
    calls: list[TranscriptTurnInput] = []

    async def fake_call(inp: TranscriptTurnInput):
        calls.append(inp)
        return _ok_envelope(inp)

    monkeypatch.setattr(
        "cordia_agent.streaming.transcript_persister.append_transcript_turn_call",
        fake_call,
    )
    persister = ToolTranscriptPersister(recording_allowed=lambda: recording_allowed)

    await persister.persist(_final_frame())

    assert calls == [], "no consent ⇒ no tool call at all (§5.2 fail-closed)"


@pytest.mark.unit
async def test_tool_persister_posts_the_turn_when_allowed(monkeypatch) -> None:
    calls: list[TranscriptTurnInput] = []

    async def fake_call(inp: TranscriptTurnInput):
        calls.append(inp)
        return _ok_envelope(inp)

    monkeypatch.setattr(
        "cordia_agent.streaming.transcript_persister.append_transcript_turn_call",
        fake_call,
    )
    persister = ToolTranscriptPersister(recording_allowed=lambda: True)

    await persister.persist(_final_frame())

    assert len(calls) == 1
    assert calls[0].session_id == "session_1"
    assert calls[0].seq == 7
    assert calls[0].speaker == "customer"
