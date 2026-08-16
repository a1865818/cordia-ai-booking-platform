"""Unit tests for the Azure ``gpt-4o-mini-tts`` streaming synthesis provider.

Every request is served by an ``httpx.MockTransport`` whose response body is a
tracked async generator, so the tests can assert not only what was yielded but
whether the upstream body kept being produced — which is the difference between
"stopped yielding" and "aborted the transfer".
"""

from __future__ import annotations

import dataclasses
import json
from typing import TYPE_CHECKING, Any

import httpx
import pytest

from cordia_agent.streaming.providers.audio_config import AudioProviderError
from cordia_agent.streaming.providers.azure_tts import AzureTtsProvider
from cordia_agent.streaming.providers.tts_port import TTSCancellationToken
from tests._stubs.audio_sink_fake import FailingAudioSink, FakeAudioSink
from tests.unit_tests.streaming.conftest import make_client


if TYPE_CHECKING:
    from collections.abc import AsyncIterator, Callable, Sequence

    from cordia_agent.streaming.providers.audio_config import AzureAudioSettings


class BodyProbe:
    """Tracks how much of a streamed response body was pulled before closing.

    ``produced`` grows only as the provider actually consumes the body, and
    ``closed_after`` records how many chunks had been pulled at the moment the
    response was first closed — which is how these tests tell "aborted the
    transfer" apart from "drained it and then stopped yielding".
    """

    def __init__(self, chunks: Sequence[bytes]) -> None:
        self._chunks = list(chunks)
        self.produced: list[bytes] = []
        self.close_calls = 0
        self.closed_after: int | None = None

    def note_close(self) -> None:
        """Record a close, remembering how far the body had been pulled."""
        self.close_calls += 1
        if self.closed_after is None:
            self.closed_after = len(self.produced)

    def stream(self) -> Any:
        async def generate() -> AsyncIterator[bytes]:
            for chunk in self._chunks:
                self.produced.append(chunk)
                yield chunk

        return generate()


class ProbedResponse(httpx.Response):
    """``httpx.Response`` that reports every ``aclose`` back to its probe."""

    def __init__(self, probe: BodyProbe, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self._probe = probe

    async def aclose(self) -> None:
        self._probe.note_close()
        await super().aclose()


def audio_handler(
    probe: BodyProbe, captured: list[httpx.Request]
) -> Callable[[httpx.Request], httpx.Response]:
    """Build a handler streaming *probe*'s chunks and recording requests."""

    def handle(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        request.read()
        return ProbedResponse(probe, status_code=200, content=probe.stream())

    return handle


def _provider(
    settings: AzureAudioSettings,
    handler: Callable[[httpx.Request], httpx.Response],
    sink: Any,
) -> AzureTtsProvider:
    return AzureTtsProvider(settings, make_client(handler), sink)


# ---------------------------------------------------------------------------
# Streaming happy path
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_each_audio_chunk_is_written_to_the_sink_and_its_ref_yielded(
    audio_settings: AzureAudioSettings,
) -> None:
    probe = BodyProbe([b"aaaa", b"bbbb", b"cccc"])
    sink = FakeAudioSink()
    provider = _provider(audio_settings, audio_handler(probe, []), sink)

    refs = [
        ref
        async for ref in provider.synthesize(
            "Table for two.", turn_id="turn_1", chunk_seq=2
        )
    ]

    assert refs == [
        "tts://fake/turn_1/2/0",
        "tts://fake/turn_1/2/1",
        "tts://fake/turn_1/2/2",
    ]
    assert sink.written_bytes == b"aaaabbbbcccc"
    assert probe.close_calls >= 1


@pytest.mark.unit
async def test_chunk_metadata_identifies_turn_ordering_and_audio_format(
    audio_settings: AzureAudioSettings,
) -> None:
    probe = BodyProbe([b"aaaa", b"bbbb"])
    sink = FakeAudioSink()
    provider = _provider(audio_settings, audio_handler(probe, []), sink)

    async for _ in provider.synthesize("Hi.", turn_id="turn_9", chunk_seq=4):
        pass

    metadata = [meta for _, meta in sink.writes]
    assert [meta.frame_seq for meta in metadata] == [0, 1]
    assert all(meta.turn_id == "turn_9" for meta in metadata)
    assert all(meta.chunk_seq == 4 for meta in metadata)
    # 24 kHz PCM is the native output; PSTN's 8 kHz mulaw gap is the sink's.
    assert all(meta.content_type == "audio/pcm" for meta in metadata)
    assert all(meta.sample_rate_hz == 24000 for meta in metadata)


@pytest.mark.unit
async def test_first_ref_is_yielded_before_the_body_finishes_generating(
    audio_settings: AzureAudioSettings,
) -> None:
    probe = BodyProbe([b"aaaa"] * 20)
    sink = FakeAudioSink()
    provider = _provider(audio_settings, audio_handler(probe, []), sink)

    stream = provider.synthesize("One. Two.", turn_id="turn_1", chunk_seq=0)
    first = await anext(stream)

    # Time-to-first-chunk: the caller hears audio while the rest is still
    # being generated upstream.
    assert first == "tts://fake/turn_1/0/0"
    assert len(probe.produced) < 20
    await stream.aclose()


@pytest.mark.unit
async def test_request_targets_the_deployment_with_voice_and_format(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = _provider(
        audio_settings, audio_handler(BodyProbe([b"aaaa"]), captured), FakeAudioSink()
    )

    async for _ in provider.synthesize("Hi.", turn_id="t", chunk_seq=0):
        pass

    request = captured[0]
    body = json.loads(request.content)
    assert request.method == "POST"
    assert "/openai/deployments/gpt-4o-mini-tts/audio/speech" in str(request.url)
    assert request.headers["api-key"] == "secret-key"
    assert body == {
        "model": "gpt-4o-mini-tts",
        "input": "Hi.",
        "voice": "alloy",
        "response_format": "pcm",
    }


@pytest.mark.unit
async def test_output_format_is_configurable(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    settings = dataclasses.replace(
        audio_settings, tts_response_format="wav", tts_sample_rate_hz=8000
    )
    sink = FakeAudioSink()
    provider = _provider(settings, audio_handler(BodyProbe([b"aaaa"]), captured), sink)

    async for _ in provider.synthesize("Hi.", turn_id="t", chunk_seq=0):
        pass

    assert json.loads(captured[0].content)["response_format"] == "wav"
    assert sink.writes[0][1].content_type == "audio/wav"
    assert sink.writes[0][1].sample_rate_hz == 8000


@pytest.mark.unit
async def test_blank_text_never_reaches_the_network(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    provider = _provider(
        audio_settings, audio_handler(BodyProbe([b"aaaa"]), captured), FakeAudioSink()
    )

    refs = [ref async for ref in provider.synthesize("   ", turn_id="t", chunk_seq=0)]

    assert refs == []
    assert captured == []


# ---------------------------------------------------------------------------
# Cancellation — the mechanism that makes barge-in audible
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_cancellation_before_the_call_never_opens_a_request(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []
    cancellation = TTSCancellationToken()
    cancellation.cancel()
    provider = _provider(
        audio_settings, audio_handler(BodyProbe([b"aaaa"]), captured), FakeAudioSink()
    )

    refs = [
        ref
        async for ref in provider.synthesize(
            "Hi.", turn_id="t", chunk_seq=0, cancellation=cancellation
        )
    ]

    assert refs == []
    assert captured == []


@pytest.mark.unit
async def test_cancellation_mid_stream_aborts_the_in_flight_response(
    audio_settings: AzureAudioSettings,
) -> None:
    probe = BodyProbe([b"aaaa"] * 50)
    sink = FakeAudioSink()
    cancellation = TTSCancellationToken()
    provider = _provider(audio_settings, audio_handler(probe, []), sink)

    refs: list[str] = []
    async for ref in provider.synthesize(
        "Your booking is confirmed.",
        turn_id="turn_1",
        chunk_seq=0,
        cancellation=cancellation,
    ):
        refs.append(ref)
        cancellation.cancel()

    # Stopped yielding...
    assert len(refs) == 1
    assert len(sink.writes) == 1
    # ...AND tore the in-flight transfer down at that exact point rather than
    # draining 50 chunks of audio nobody will hear.
    assert probe.close_calls >= 1
    assert probe.closed_after is not None
    assert probe.closed_after <= 2
    assert len(probe.produced) <= 2


@pytest.mark.unit
async def test_consumer_abandoning_the_stream_also_closes_the_response(
    audio_settings: AzureAudioSettings,
) -> None:
    probe = BodyProbe([b"aaaa"] * 50)
    provider = _provider(audio_settings, audio_handler(probe, []), FakeAudioSink())

    stream = provider.synthesize("Hello there.", turn_id="t", chunk_seq=0)
    await anext(stream)
    await stream.aclose()

    # aclosing() in synthesize propagates the close through to the response.
    assert probe.close_calls >= 1
    assert probe.closed_after is not None
    assert probe.closed_after <= 2


# ---------------------------------------------------------------------------
# Bounding, retry, errors
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_transport_error_before_any_audio_is_retried(
    audio_settings: AzureAudioSettings,
) -> None:
    probe = BodyProbe([b"aaaa"])
    captured: list[httpx.Request] = []

    def handle(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        if len(captured) == 1:
            raise httpx.ConnectError("boom")
        return httpx.Response(200, content=probe.stream())

    provider = _provider(audio_settings, handle, FakeAudioSink())

    refs = [ref async for ref in provider.synthesize("Hi.", turn_id="t", chunk_seq=0)]

    assert len(captured) == 2
    assert refs == ["tts://fake/t/0/0"]


@pytest.mark.unit
async def test_every_attempt_failing_raises_timeout_error(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []

    def handle(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        raise httpx.ReadTimeout("slow")

    provider = _provider(audio_settings, handle, FakeAudioSink())

    with pytest.raises(TimeoutError):
        async for _ in provider.synthesize("Hi.", turn_id="t", chunk_seq=0):
            pass

    assert len(captured) == 2


@pytest.mark.unit
async def test_non_2xx_raises_without_leaking_the_response_body(
    audio_settings: AzureAudioSettings,
) -> None:
    captured: list[httpx.Request] = []

    def handle(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        return httpx.Response(400, json={"error": {"message": "secret-key rejected"}})

    provider = _provider(audio_settings, handle, FakeAudioSink())

    with pytest.raises(AudioProviderError) as exc_info:
        async for _ in provider.synthesize("Hi.", turn_id="t", chunk_seq=0):
            pass

    assert exc_info.value.status_code == 400
    assert "secret-key" not in str(exc_info.value)
    assert "rejected" not in str(exc_info.value)
    assert len(captured) == 1


@pytest.mark.unit
async def test_sink_failure_propagates_rather_than_yielding_a_bogus_ref(
    audio_settings: AzureAudioSettings,
) -> None:
    probe = BodyProbe([b"aaaa", b"bbbb"])
    provider = _provider(
        audio_settings, audio_handler(probe, []), FailingAudioSink(fail_on_write=0)
    )

    with pytest.raises(RuntimeError, match="media plane unavailable"):
        async for _ in provider.synthesize("Hi.", turn_id="t", chunk_seq=0):
            pass
