"""Unit tests for the Azure audio settings resolver.

Every test drives ``audio_settings_from_env`` through monkeypatched environment
variables — no Azure credential and no network is ever involved.
"""

from __future__ import annotations

import pytest

from cordia_agent.llm.config import ModelNotConfigured
from cordia_agent.streaming.providers.audio_config import (
    DEFAULT_AUDIO_API_VERSION,
    DEFAULT_AUDIO_BASE_TIMEOUT_SECONDS,
    DEFAULT_REALTIME_API_VERSION,
    DEFAULT_REALTIME_SEND_CHUNK_BYTES,
    DEFAULT_REALTIME_SILENCE_DURATION_MS,
    DEFAULT_REALTIME_TRANSCRIPTION_MODEL,
    DEFAULT_STT_DEPLOYMENT,
    DEFAULT_TTS_DEPLOYMENT,
    AudioNotConfigured,
    AzureAudioSettings,
    audio_settings_from_env,
)


_REQUIRED = {
    "AZURE_OPENAI_ENDPOINT": "https://demo.openai.azure.com/",
    "AZURE_OPENAI_API_KEY": "secret-key",
}

_OPTIONAL = (
    "CORDIA_AUDIO_API_VERSION",
    "CORDIA_AUDIO_STT_DEPLOYMENT",
    "CORDIA_AUDIO_TTS_DEPLOYMENT",
    "CORDIA_AUDIO_TTS_VOICE",
    "CORDIA_AUDIO_TTS_RESPONSE_FORMAT",
    "CORDIA_AUDIO_TTS_SAMPLE_RATE_HZ",
    "CORDIA_AUDIO_TTS_CHUNK_BYTES",
    "CORDIA_AUDIO_INPUT_SAMPLE_RATE_HZ",
    "CORDIA_AUDIO_VAD_FRAME_MS",
    "CORDIA_AUDIO_VAD_ENERGY_THRESHOLD",
    "CORDIA_AUDIO_VAD_ONSET_FRAMES",
    "CORDIA_AUDIO_VAD_HANGOVER_FRAMES",
    "CORDIA_AUDIO_VAD_PREROLL_FRAMES",
    "CORDIA_AUDIO_MAX_UTTERANCE_SECONDS",
    "CORDIA_AUDIO_IDLE_TIMEOUT_SECONDS",
    "CORDIA_AUDIO_FALLBACK_ASR_CONFIDENCE",
    "CORDIA_AUDIO_BASE_TIMEOUT_SECONDS",
    "CORDIA_AUDIO_REALTIME_API_VERSION",
    "CORDIA_AUDIO_REALTIME_MODEL",
    "CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS",
    "CORDIA_AUDIO_REALTIME_SEND_CHUNK_BYTES",
)


@pytest.fixture
def audio_env(monkeypatch: pytest.MonkeyPatch) -> pytest.MonkeyPatch:
    """Install a minimal valid audio environment, clearing every optional knob.

    Clearing matters: a developer's real ``.env`` is loaded into the process by
    the LangGraph CLI, and a stray override there would otherwise silently
    change what these tests assert.
    """
    for key, value in _REQUIRED.items():
        monkeypatch.setenv(key, value)
    for key in _OPTIONAL:
        monkeypatch.delenv(key, raising=False)
        monkeypatch.delenv(f"{key}_PSTN", raising=False)
    return monkeypatch


@pytest.mark.unit
def test_defaults_resolve_to_the_deployed_whisper_and_tts_names(audio_env) -> None:
    settings = audio_settings_from_env()

    assert settings.endpoint == "https://demo.openai.azure.com/"
    assert settings.api_key == "secret-key"
    assert settings.api_version == DEFAULT_AUDIO_API_VERSION
    assert settings.stt_deployment == DEFAULT_STT_DEPLOYMENT == "whisper"
    assert settings.tts_deployment == DEFAULT_TTS_DEPLOYMENT == "gpt-4o-mini-tts"
    assert settings.tts_response_format == "pcm"
    assert settings.tts_sample_rate_hz == 24000
    assert settings.base_timeout_seconds == DEFAULT_AUDIO_BASE_TIMEOUT_SECONDS


@pytest.mark.unit
def test_missing_endpoint_and_key_fail_closed_naming_both(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("AZURE_OPENAI_ENDPOINT", raising=False)
    monkeypatch.delenv("AZURE_OPENAI_API_KEY", raising=False)

    with pytest.raises(AudioNotConfigured) as exc_info:
        audio_settings_from_env()

    message = str(exc_info.value)
    assert "AZURE_OPENAI_ENDPOINT" in message
    assert "AZURE_OPENAI_API_KEY" in message


@pytest.mark.unit
def test_audio_not_configured_is_catchable_as_model_not_configured(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("AZURE_OPENAI_ENDPOINT", raising=False)
    monkeypatch.delenv("AZURE_OPENAI_API_KEY", raising=False)

    with pytest.raises(ModelNotConfigured):
        audio_settings_from_env()


@pytest.mark.unit
def test_deployment_names_are_env_overridable(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_STT_DEPLOYMENT", "whisper-au")
    audio_env.setenv("CORDIA_AUDIO_TTS_DEPLOYMENT", "tts-hd")

    settings = audio_settings_from_env()

    assert settings.stt_deployment == "whisper-au"
    assert settings.tts_deployment == "tts-hd"


@pytest.mark.unit
def test_role_suffix_overrides_shared_value(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_TTS_RESPONSE_FORMAT", "pcm")
    audio_env.setenv("CORDIA_AUDIO_TTS_RESPONSE_FORMAT_PSTN", "wav")

    assert audio_settings_from_env().tts_response_format == "pcm"
    assert audio_settings_from_env("pstn").tts_response_format == "wav"


@pytest.mark.unit
def test_unsupported_response_format_fails_closed(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_TTS_RESPONSE_FORMAT", "mulaw")

    with pytest.raises(AudioNotConfigured, match="tts_response_format"):
        audio_settings_from_env()


@pytest.mark.unit
def test_non_numeric_knob_fails_closed(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_VAD_ENERGY_THRESHOLD", "loud")

    with pytest.raises(AudioNotConfigured, match="must be a number"):
        audio_settings_from_env()


@pytest.mark.unit
def test_non_integer_frame_size_fails_closed(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_INPUT_SAMPLE_RATE_HZ", "44100")
    audio_env.setenv("CORDIA_AUDIO_VAD_FRAME_MS", "1")

    with pytest.raises(AudioNotConfigured, match="whole samples"):
        audio_settings_from_env()


@pytest.mark.unit
@pytest.mark.parametrize("confidence", ["-0.1", "1.1"])
def test_out_of_range_fallback_confidence_fails_closed(
    audio_env, confidence: str
) -> None:
    audio_env.setenv("CORDIA_AUDIO_FALLBACK_ASR_CONFIDENCE", confidence)

    with pytest.raises(AudioNotConfigured, match="fallback_asr_confidence"):
        audio_settings_from_env()


@pytest.mark.unit
def test_derived_urls_and_frame_size(audio_env) -> None:
    settings = audio_settings_from_env()

    assert settings.transcription_url() == (
        "https://demo.openai.azure.com/openai/deployments/whisper/"
        f"audio/transcriptions?api-version={DEFAULT_AUDIO_API_VERSION}"
    )
    assert settings.speech_url() == (
        "https://demo.openai.azure.com/openai/deployments/gpt-4o-mini-tts/"
        f"audio/speech?api-version={DEFAULT_AUDIO_API_VERSION}"
    )
    # 16 kHz * 20 ms = 320 samples * 2 bytes.
    assert settings.vad_frame_bytes == 640
    assert settings.tts_content_type == "audio/pcm"


@pytest.mark.unit
def test_realtime_defaults_match_the_verified_deployment(audio_env) -> None:
    settings = audio_settings_from_env()

    assert settings.realtime_api_version == DEFAULT_REALTIME_API_VERSION
    assert settings.realtime_api_version == "2025-04-01-preview"
    assert settings.realtime_transcription_model == (
        DEFAULT_REALTIME_TRANSCRIPTION_MODEL
    )
    assert settings.realtime_transcription_model == "gpt-realtime-whisper"
    assert settings.realtime_silence_duration_ms == (
        DEFAULT_REALTIME_SILENCE_DURATION_MS
    )
    assert settings.realtime_send_chunk_bytes == DEFAULT_REALTIME_SEND_CHUNK_BYTES


@pytest.mark.unit
def test_realtime_transcription_url_matches_the_verified_querystring(
    audio_env,
) -> None:
    settings = audio_settings_from_env()

    # Verified live: wss, no trailing slash, and NO `deployment` parameter —
    # supplying one is answered with HTTP 400 OperationNotSupported.
    assert settings.realtime_transcription_url() == (
        "wss://demo.openai.azure.com/openai/realtime"
        f"?api-version={DEFAULT_REALTIME_API_VERSION}&intent=transcription"
    )


@pytest.mark.unit
def test_realtime_knobs_are_env_overridable(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_REALTIME_API_VERSION", "2099-01-01-preview")
    audio_env.setenv("CORDIA_AUDIO_REALTIME_MODEL", "whisper-next")
    audio_env.setenv("CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS", "800")
    audio_env.setenv("CORDIA_AUDIO_REALTIME_SEND_CHUNK_BYTES", "3200")

    settings = audio_settings_from_env()

    assert settings.realtime_transcription_model == "whisper-next"
    assert settings.realtime_silence_duration_ms == 800
    assert settings.realtime_send_chunk_bytes == 3200
    assert "api-version=2099-01-01-preview" in settings.realtime_transcription_url()


@pytest.mark.unit
def test_non_positive_realtime_knobs_fail_closed(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS", "0")

    with pytest.raises(AudioNotConfigured, match="realtime_silence_duration_ms"):
        audio_settings_from_env()


@pytest.mark.unit
def test_the_idle_timeout_is_unset_by_default(audio_env) -> None:
    # Silence on a phone call is a normal state, not a fault: with nothing
    # configured the realtime stream waits for the caller indefinitely rather
    # than killing a pre-warmed session out from under a quiet line.
    settings = audio_settings_from_env()

    assert settings.idle_timeout_seconds is None


@pytest.mark.unit
def test_the_idle_timeout_is_env_overridable(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_IDLE_TIMEOUT_SECONDS", "45.5")

    assert audio_settings_from_env().idle_timeout_seconds == 45.5


@pytest.mark.unit
def test_a_non_positive_idle_timeout_fails_closed(audio_env) -> None:
    # "No bound" is spelled by leaving the variable unset; 0 would mean "time
    # out at once", which is only ever a typo.
    audio_env.setenv("CORDIA_AUDIO_IDLE_TIMEOUT_SECONDS", "0")

    with pytest.raises(AudioNotConfigured, match="idle_timeout_seconds"):
        audio_settings_from_env()


@pytest.mark.unit
def test_a_non_numeric_idle_timeout_fails_closed(audio_env) -> None:
    audio_env.setenv("CORDIA_AUDIO_IDLE_TIMEOUT_SECONDS", "forever")

    with pytest.raises(AudioNotConfigured, match="CORDIA_AUDIO_IDLE_TIMEOUT_SECONDS"):
        audio_settings_from_env()


@pytest.mark.unit
def test_settings_are_frozen_and_hash_by_value() -> None:
    first = AzureAudioSettings(endpoint="https://e/", api_key="k")
    second = AzureAudioSettings(endpoint="https://e/", api_key="k")

    assert first == second
    assert hash(first) == hash(second)
    with pytest.raises(AttributeError):
        first.tts_voice = "echo"  # type: ignore[misc]


@pytest.mark.unit
def test_auth_header_is_the_api_key_header() -> None:
    settings = AzureAudioSettings(endpoint="https://e/", api_key="secret")

    assert settings.auth_headers() == {"api-key": "secret"}
