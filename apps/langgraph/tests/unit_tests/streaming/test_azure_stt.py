"""Unit tests for the Azure Whisper STT provider.

Every request is served by an ``httpx.MockTransport``; the suite is fully
offline.  The focus is the batch-model-behind-a-streaming-port bridge: VAD
endpointing, the synthetic barge-in partial, and bounded/retried transport.
"""

from __future__ import annotations

import dataclasses
from typing import TYPE_CHECKING

import httpx
import pytest

from cordia_agent.streaming.providers.audio_config import AudioProviderError
from cordia_agent.streaming.providers.azure_stt import (
    AzureWhisperSTTProvider,
    confidence_from_segments,
    pcm_to_wav,
)
from cordia_agent.streaming.providers.types import STTFinalEvent, STTPartialEvent
from tests.unit_tests.streaming.conftest import (
    make_client,
    pcm_stream,
    recording_handler,
    silence,
    speech,
)


if TYPE_CHECKING:
    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings
    from cordia_agent.streaming.providers.types import STTProviderEvent


def transcript_response(
    text: str, *, avg_logprob: float | None = -0.2
) -> httpx.Response:
    """Build a canned ``verbose_json`` transcription response."""
    body: dict[str, object] = {"text": text}
    if avg_logprob is not None:
        body["segments"] = [{"id": 0, "avg_logprob": avg_logprob}]
    return httpx.Response(200, json=body)


async def collect(
    provider: AzureWhisperSTTProvider, payload: bytes, *, chunk_bytes: int = 640
) -> list[STTProviderEvent]:
    """Drain the provider over *payload* and return every event."""
    return [
        event
        async for event in provider.transcribe(
            pcm_stream(payload, chunk_bytes=chunk_bytes)
        )
    ]


# ---------------------------------------------------------------------------
# Pure helpers
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_pcm_to_wav_produces_a_riff_header_with_the_configured_rate() -> None:
    wav = pcm_to_wav(b"\x00\x01" * 100, 16000)

    assert wav[:4] == b"RIFF"
    assert wav[8:12] == b"WAVE"
    # Sample rate is stored little-endian at byte 24 of the canonical header.
    assert int.from_bytes(wav[24:28], "little") == 16000
    assert len(wav) == 44 + 200


@pytest.mark.unit
def test_confidence_is_derived_from_segment_log_probabilities() -> None:
    payload = {"segments": [{"avg_logprob": 0.0}, {"avg_logprob": 0.0}]}

    assert confidence_from_segments(payload, 0.5) == pytest.approx(1.0)


@pytest.mark.unit
def test_confidence_falls_back_when_no_log_probabilities_are_present() -> None:
    assert confidence_from_segments({"text": "hi"}, 0.42) == 0.42
    assert confidence_from_segments({"segments": []}, 0.42) == 0.42
    assert confidence_from_segments({"segments": [{"id": 1}]}, 0.42) == 0.42


@pytest.mark.unit
def test_confidence_is_clamped_into_the_zero_to_one_range() -> None:
    # exp(+2) > 1 — a nonsense value must not escape the frame contract.
    assert confidence_from_segments({"segments": [{"avg_logprob": 2.0}]}, 0.5) == 1.0


# ---------------------------------------------------------------------------
# Endpointing + event sequence
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_utterance_yields_synthetic_partial_then_transcribed_final(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(
            recording_handler([transcript_response("  a table for two ")], captured)
        ),
    )

    events = await collect(provider, speech(3) + silence(2))

    assert isinstance(events[0], STTPartialEvent)
    assert isinstance(events[1], STTFinalEvent)
    assert len(events) == 2
    assert events[1].text == "a table for two"
    assert events[1].is_endpoint is True
    assert events[1].asr_confidence == pytest.approx(0.8187, abs=1e-3)
    assert len(captured) == 1


@pytest.mark.unit
async def test_synthetic_partial_carries_no_hypothesis_and_zero_stability(
    audio_settings: AzureAudioSettings,
) -> None:
    provider = AzureWhisperSTTProvider(
        audio_settings, make_client(recording_handler([transcript_response("hi")], []))
    )

    events = await collect(provider, speech(3) + silence(2))
    partial = events[0]

    assert isinstance(partial, STTPartialEvent)
    # Whisper produces no interim hypotheses; the partial exists ONLY as the
    # barge-in trigger, so it must not fabricate text.
    assert partial.text == ""
    assert partial.stability == 0.0


@pytest.mark.unit
async def test_silence_only_stream_produces_no_events_and_no_request(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([transcript_response("nope")], captured)),
    )

    events = await collect(provider, silence(20))

    assert events == []
    assert captured == []


@pytest.mark.unit
async def test_empty_transcript_emits_no_final(
    audio_settings: AzureAudioSettings,
) -> None:
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([transcript_response("   ")], [])),
    )

    events = await collect(provider, speech(3) + silence(2))

    # The onset partial still fired (the caller DID make a noise), but nothing
    # committable is emitted for a cough.
    assert [type(event) for event in events] == [STTPartialEvent]


@pytest.mark.unit
async def test_two_utterances_yield_two_partial_final_pairs(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(
            recording_handler(
                [transcript_response("first"), transcript_response("second")], captured
            )
        ),
    )

    payload = speech(3) + silence(3) + speech(3) + silence(3)
    events = await collect(provider, payload)

    assert [type(event) for event in events] == [
        STTPartialEvent,
        STTFinalEvent,
        STTPartialEvent,
        STTFinalEvent,
    ]
    assert [e.text for e in events if isinstance(e, STTFinalEvent)] == [
        "first",
        "second",
    ]
    assert len(captured) == 2


@pytest.mark.unit
async def test_stream_ending_mid_utterance_is_flushed_not_discarded(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([transcript_response("hello")], captured)),
    )

    # No trailing silence: the caller is still talking when the line drops.
    events = await collect(provider, speech(4))

    assert [type(event) for event in events] == [STTPartialEvent, STTFinalEvent]
    assert len(captured) == 1


@pytest.mark.unit
async def test_inbound_chunks_are_reframed_regardless_of_their_size(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([transcript_response("ok")], captured)),
    )

    # 137 bytes is neither a multiple of the 640-byte frame nor of a sample
    # pair boundary at frame level — the provider must buffer across chunks.
    events = await collect(provider, speech(3) + silence(3), chunk_bytes=137)

    assert [type(event) for event in events] == [STTPartialEvent, STTFinalEvent]


@pytest.mark.unit
async def test_preroll_frames_are_prepended_to_the_buffered_utterance(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    with_preroll = dataclasses.replace(audio_settings, vad_preroll_frames=2)
    without_preroll = dataclasses.replace(audio_settings, vad_preroll_frames=0)

    for settings in (with_preroll, without_preroll):
        provider = AzureWhisperSTTProvider(
            settings,
            make_client(recording_handler([transcript_response("hi")], captured)),
        )
        await collect(provider, silence(4) + speech(3) + silence(2))

    # Same speech, but the pre-roll variant posts two extra frames of audio.
    assert len(captured[0].content) > len(captured[1].content)
    assert len(captured[0].content) - len(captured[1].content) == 2 * 640


@pytest.mark.unit
async def test_max_utterance_seconds_force_endpoints_a_non_stopping_caller(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    # 20 frames of 20 ms = 400 ms cap.
    settings = dataclasses.replace(audio_settings, max_utterance_seconds=0.4)
    provider = AzureWhisperSTTProvider(
        settings,
        make_client(
            recording_handler(
                [transcript_response("a"), transcript_response("b")], captured
            )
        ),
    )

    events = await collect(provider, speech(45))

    finals = [event for event in events if isinstance(event, STTFinalEvent)]
    assert len(finals) >= 2
    # Re-armed after the forced endpoint, so a fresh barge-in partial fires.
    assert sum(isinstance(event, STTPartialEvent) for event in events) >= 2


# ---------------------------------------------------------------------------
# Transport: auth, bounding, retry, errors
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_request_targets_the_deployment_and_carries_the_api_key(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([transcript_response("hi")], captured)),
    )

    await collect(provider, speech(3) + silence(2))
    request = captured[0]

    assert request.method == "POST"
    assert "/openai/deployments/whisper/audio/transcriptions" in str(request.url)
    assert request.headers["api-key"] == "secret-key"
    assert b"verbose_json" in request.content
    assert b"RIFF" in request.content


@pytest.mark.unit
async def test_transport_error_is_retried_on_a_longer_budget(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(
            recording_handler(
                [
                    httpx.ConnectError("boom"),
                    transcript_response("recovered"),
                ],
                captured,
            )
        ),
    )

    events = await collect(provider, speech(3) + silence(2))

    assert len(captured) == 2
    finals = [event for event in events if isinstance(event, STTFinalEvent)]
    assert finals[0].text == "recovered"


@pytest.mark.unit
async def test_every_attempt_timing_out_raises_timeout_error(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([httpx.ReadTimeout("slow")], captured)),
    )

    with pytest.raises(TimeoutError):
        await collect(provider, speech(3) + silence(2))

    assert len(captured) == 2


@pytest.mark.unit
async def test_non_2xx_raises_without_leaking_the_response_body(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    body = {"error": {"message": "caller said: my card is 4111111111111111"}}
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([httpx.Response(401, json=body)], captured)),
    )

    with pytest.raises(AudioProviderError) as exc_info:
        await collect(provider, speech(3) + silence(2))

    message = str(exc_info.value)
    assert exc_info.value.status_code == 401
    assert "4111111111111111" not in message
    assert "caller said" not in message
    assert "secret-key" not in message
    # A 401 will fail identically on a retry — one attempt only.
    assert len(captured) == 1


@pytest.mark.unit
async def test_non_json_success_body_is_tolerated_and_yields_no_final(
    audio_settings: AzureAudioSettings,
) -> None:
    provider = AzureWhisperSTTProvider(
        audio_settings,
        make_client(recording_handler([httpx.Response(200, text="not json")], [])),
    )

    events = await collect(provider, speech(3) + silence(2))

    assert [type(event) for event in events] == [STTPartialEvent]
