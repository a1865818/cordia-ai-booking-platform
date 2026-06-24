"""Unit tests for sentence-chunked streaming TTS."""

from collections.abc import AsyncIterator
from datetime import UTC
from inspect import getsource

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import TTSChunker, TurnManager
from cordia_agent.streaming.providers import FakeTTSProvider
from cordia_agent.streaming.providers import fake_tts as fake_tts_module
from cordia_agent.streaming.providers import tts_port as tts_port_module
from cordia_agent.streaming_models import AgentTextDeltaFrame, AgentTextFinalFrame


ENVELOPE_FIELDS = {
    "tenant_id": "tenant_1",
    "session_id": "session_1",
    "turn_id": "assistant_turn_1",
    "correlation_id": "correlation_1",
    "causation_id": "causation_1",
    "ts": "2026-06-18T00:00:00+00:00",
}


def _delta(delta: str, *, seq: int = 0) -> AgentTextDeltaFrame:
    return AgentTextDeltaFrame(
        type=StreamFrameType.AGENT_TEXT_DELTA,
        **ENVELOPE_FIELDS,
        seq=seq,
        delta=delta,
    )


def _final(text: str, *, seq: int = 10) -> AgentTextFinalFrame:
    return AgentTextFinalFrame(
        type=StreamFrameType.AGENT_TEXT_FINAL,
        **ENVELOPE_FIELDS,
        seq=seq,
        text=text,
    )


async def _frames(
    items: list[AgentTextDeltaFrame | AgentTextFinalFrame],
) -> AsyncIterator[AgentTextDeltaFrame | AgentTextFinalFrame]:
    for item in items:
        yield item


@pytest.mark.unit
async def test_first_tts_delta_emits_at_sentence_boundary_before_agent_final() -> None:
    provider = FakeTTSProvider()
    chunker = TTSChunker(provider=provider, turn_manager=TurnManager())

    no_boundary = [frame async for frame in chunker.process_frame(_delta("Hello"))]
    first_sentence = [
        frame async for frame in chunker.process_frame(_delta(".", seq=1))
    ]

    assert no_boundary == []
    assert [frame.type for frame in first_sentence] == [StreamFrameType.TTS_AUDIO_DELTA]
    assert provider.history == [("Hello.", "assistant_turn_1", 0)]


@pytest.mark.unit
async def test_remaining_buffer_is_synthesized_when_agent_final_arrives() -> None:
    provider = FakeTTSProvider()
    chunker = TTSChunker(provider=provider, turn_manager=TurnManager())

    frames = [
        frame
        async for frame in chunker.process(
            _frames(
                [
                    _delta("Hello. I can help with that"),
                    _final("Hello. I can help with that"),
                ]
            )
        )
    ]

    assert [frame.audio_chunk_ref for frame in frames] == [
        "tts://fake/assistant_turn_1/0",
        "tts://fake/assistant_turn_1/1",
    ]
    assert provider.history == [
        ("Hello.", "assistant_turn_1", 0),
        ("I can help with that", "assistant_turn_1", 1),
    ]


@pytest.mark.unit
async def test_phrase_boundaries_flush_and_chunk_seq_is_monotonic_per_turn() -> None:
    provider = FakeTTSProvider()
    chunker = TTSChunker(provider=provider, turn_manager=TurnManager())

    frames = [
        frame
        async for frame in chunker.process(
            _frames(
                [
                    _delta("First: second; third", seq=0),
                    _final("First: second; third", seq=1),
                ]
            )
        )
    ]

    assert [frame.chunk_seq for frame in frames] == [0, 1, 2]
    assert [request[0] for request in provider.history] == [
        "First:",
        "second;",
        "third",
    ]


@pytest.mark.unit
async def test_seq_values_are_sourced_from_shared_turn_manager() -> None:
    turn_manager = TurnManager()
    provider = FakeTTSProvider()
    chunker = TTSChunker(provider=provider, turn_manager=turn_manager)

    assert turn_manager.next_seq("session_1") == 0
    frames = [
        frame
        async for frame in chunker.process(_frames([_delta("Ready. Set.", seq=1)]))
    ]

    assert [frame.seq for frame in frames] == [1, 2]
    assert turn_manager.next_seq("session_1") == 3


@pytest.mark.unit
async def test_tts_frame_exposes_audio_ref_without_raw_audio_bytes() -> None:
    chunker = TTSChunker(provider=FakeTTSProvider(), turn_manager=TurnManager())

    frames = [frame async for frame in chunker.process_frame(_delta("Hello."))]

    payload = frames[0].model_dump()
    assert payload["audio_chunk_ref"] == "tts://fake/assistant_turn_1/0"
    assert "audio" not in payload
    assert "audio_bytes" not in payload
    assert frames[0].ts.tzinfo is not None
    assert frames[0].ts.utcoffset() == UTC.utcoffset(frames[0].ts)


@pytest.mark.unit
def test_tts_provider_port_and_fake_do_not_import_real_network_dependencies() -> None:
    source = "\n".join(
        [
            getsource(tts_port_module),
            getsource(fake_tts_module),
        ]
    )

    forbidden_tokens = [
        "boto3",
        "deepgram",
        "google.cloud",
        "httpx",
        "openai",
        "requests",
        "socket",
        "websocket",
    ]
    assert not any(token in source for token in forbidden_tokens)


@pytest.mark.unit
async def test_final_text_with_prefix_drift_is_still_synthesized() -> None:
    provider = FakeTTSProvider()
    chunker = TTSChunker(provider=provider, turn_manager=TurnManager())

    await anext(chunker.process_frame(_delta("Hello.")))
    frames = [
        frame async for frame in chunker.process_frame(_final("Hello! How are you?"))
    ]

    assert [frame.audio_chunk_ref for frame in frames] == [
        "tts://fake/assistant_turn_1/1",
        "tts://fake/assistant_turn_1/2",
    ]
    assert provider.history == [
        ("Hello.", "assistant_turn_1", 0),
        ("!", "assistant_turn_1", 1),
        ("How are you?", "assistant_turn_1", 2),
    ]


@pytest.mark.unit
async def test_final_only_response_after_streamed_turn_still_synthesizes() -> None:
    provider = FakeTTSProvider()
    chunker = TTSChunker(provider=provider, turn_manager=TurnManager())

    turn_one = {**ENVELOPE_FIELDS, "turn_id": "assistant_turn_1"}
    turn_two = {**ENVELOPE_FIELDS, "turn_id": "assistant_turn_2"}

    await anext(
        chunker.process_frame(
            AgentTextDeltaFrame(
                type=StreamFrameType.AGENT_TEXT_DELTA,
                **turn_one,
                seq=0,
                delta="Hello. I can help with that",
            )
        )
    )
    await anext(
        chunker.process_frame(
            AgentTextFinalFrame(
                type=StreamFrameType.AGENT_TEXT_FINAL,
                **turn_one,
                seq=1,
                text="Hello. I can help with that",
            )
        )
    )

    frames = [
        frame
        async for frame in chunker.process_frame(
            AgentTextFinalFrame(
                type=StreamFrameType.AGENT_TEXT_FINAL,
                **turn_two,
                seq=2,
                text="Hello.",
            )
        )
    ]

    assert [frame.turn_id for frame in frames] == ["assistant_turn_2"]
    assert [frame.audio_chunk_ref for frame in frames] == [
        "tts://fake/assistant_turn_2/0",
    ]
    assert provider.history[-1] == ("Hello.", "assistant_turn_2", 0)
