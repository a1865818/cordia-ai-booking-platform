"""Unit tests for the Azure model settings resolver.

Every test drives ``settings_from_env`` through monkeypatched environment
variables — no Azure credential and no network is ever involved.
"""

from __future__ import annotations

import pytest

from cordia_agent.llm.config import (
    DEFAULT_API_VERSION,
    DEFAULT_BASE_TIMEOUT_SECONDS,
    AzureModelSettings,
    ModelNotConfigured,
    settings_from_env,
)


_REQUIRED = {
    "AZURE_OPENAI_ENDPOINT": "https://demo.openai.azure.com/",
    "AZURE_OPENAI_API_KEY": "secret-key",
    "AZURE_OPENAI_DEPLOYMENT": "gpt-4.1-mini",
}

_OPTIONAL = (
    "AZURE_OPENAI_API_VERSION",
    "AZURE_OPENAI_MODEL_PROVIDER",
    "AZURE_OPENAI_API_SURFACE",
    "AZURE_OPENAI_REASONING_EFFORT",
    "CORDIA_LLM_BASE_TIMEOUT_SECONDS",
)


@pytest.fixture
def azure_env(monkeypatch: pytest.MonkeyPatch) -> pytest.MonkeyPatch:
    """Install a minimal valid Azure environment, clearing every optional knob.

    Clearing matters: a developer's real ``.env`` is loaded into the process by
    the LangGraph CLI, and a stray ``AZURE_OPENAI_API_SURFACE`` there would
    otherwise silently change what these tests assert.
    """
    for key, value in _REQUIRED.items():
        monkeypatch.setenv(key, value)
    for key in _OPTIONAL:
        monkeypatch.delenv(key, raising=False)
    return monkeypatch


def test_defaults_resolve_to_azure_openai_chat_completions(azure_env) -> None:
    settings = settings_from_env()

    assert settings.endpoint == "https://demo.openai.azure.com/"
    assert settings.api_key == "secret-key"
    assert settings.deployment == "gpt-4.1-mini"
    assert settings.provider == "azure_openai"
    assert settings.api_surface == "chat_completions"
    assert settings.api_version == DEFAULT_API_VERSION
    assert settings.reasoning_effort is None
    assert settings.base_timeout_seconds == DEFAULT_BASE_TIMEOUT_SECONDS


@pytest.mark.parametrize("missing", sorted(_REQUIRED))
def test_missing_required_var_fails_closed(azure_env, missing: str) -> None:
    """Absent credentials raise at startup rather than 401-ing mid-conversation."""
    azure_env.delenv(missing)

    with pytest.raises(ModelNotConfigured, match=missing):
        settings_from_env()


def test_blank_var_is_treated_as_absent(azure_env) -> None:
    """A commented-out .env line often leaves an empty string, not an unset var."""
    azure_env.setenv("AZURE_OPENAI_DEPLOYMENT", "   ")

    with pytest.raises(ModelNotConfigured, match="AZURE_OPENAI_DEPLOYMENT"):
        settings_from_env()


def test_role_suffix_overrides_shared_deployment(azure_env) -> None:
    """Per-role deployments let one process drive cheap + strong models."""
    azure_env.setenv("AZURE_OPENAI_DEPLOYMENT_SLOT_EXTRACTOR", "gpt-4o-mini")

    assert settings_from_env("slot_extractor").deployment == "gpt-4o-mini"
    # The shared value still serves every other role.
    assert settings_from_env("clarify").deployment == "gpt-4.1-mini"
    assert settings_from_env().deployment == "gpt-4.1-mini"


def test_role_falls_back_per_variable_not_all_or_nothing(azure_env) -> None:
    """A role overriding only the deployment still inherits endpoint and key."""
    azure_env.setenv("AZURE_OPENAI_DEPLOYMENT_CLARIFY", "gpt-4.1")

    settings = settings_from_env("clarify")

    assert settings.deployment == "gpt-4.1"
    assert settings.endpoint == _REQUIRED["AZURE_OPENAI_ENDPOINT"]
    assert settings.api_key == _REQUIRED["AZURE_OPENAI_API_KEY"]


def test_azure_ai_provider_is_accepted(azure_env) -> None:
    """Foundry's non-OpenAI catalog is reached via the azure_ai surface."""
    azure_env.setenv("AZURE_OPENAI_MODEL_PROVIDER", "azure_ai")

    assert settings_from_env().provider == "azure_ai"


def test_unknown_provider_rejected(azure_env) -> None:
    azure_env.setenv("AZURE_OPENAI_MODEL_PROVIDER", "bedrock")

    with pytest.raises(ModelNotConfigured, match="invalid provider"):
        settings_from_env()


def test_unknown_api_surface_rejected(azure_env) -> None:
    azure_env.setenv("AZURE_OPENAI_API_SURFACE", "completions")

    with pytest.raises(ModelNotConfigured, match="invalid api_surface"):
        settings_from_env()


def test_responses_surface_requires_recent_api_version(azure_env) -> None:
    """Caught at startup — otherwise it is an opaque 400 on the first turn."""
    azure_env.setenv("AZURE_OPENAI_API_SURFACE", "responses")
    azure_env.setenv("AZURE_OPENAI_API_VERSION", "2024-10-21")

    with pytest.raises(ModelNotConfigured, match="2025-03-01-preview"):
        settings_from_env()


def test_responses_surface_accepts_default_api_version(azure_env) -> None:
    azure_env.setenv("AZURE_OPENAI_API_SURFACE", "responses")
    azure_env.setenv("AZURE_OPENAI_REASONING_EFFORT", "medium")

    settings = settings_from_env()

    assert settings.api_surface == "responses"
    assert settings.reasoning_effort == "medium"


def test_responses_surface_rejected_on_azure_ai_provider(azure_env) -> None:
    """Foundry's inference surface has no Responses API — fail loudly, not at request time."""
    azure_env.setenv("AZURE_OPENAI_MODEL_PROVIDER", "azure_ai")
    azure_env.setenv("AZURE_OPENAI_API_SURFACE", "responses")

    with pytest.raises(ModelNotConfigured, match="only available on"):
        settings_from_env()


def test_timeout_override_is_parsed(azure_env) -> None:
    azure_env.setenv("CORDIA_LLM_BASE_TIMEOUT_SECONDS", "2.5")

    assert settings_from_env().base_timeout_seconds == 2.5


def test_non_numeric_timeout_rejected(azure_env) -> None:
    azure_env.setenv("CORDIA_LLM_BASE_TIMEOUT_SECONDS", "soon")

    with pytest.raises(ModelNotConfigured, match="must be a number"):
        settings_from_env()


def test_non_positive_timeout_rejected(azure_env) -> None:
    azure_env.setenv("CORDIA_LLM_BASE_TIMEOUT_SECONDS", "0")

    with pytest.raises(ModelNotConfigured, match="must be > 0"):
        settings_from_env()


def test_settings_are_frozen_and_hashable(azure_env) -> None:
    """Hashability is load-bearing: the factory memoises clients by settings."""
    settings = settings_from_env()

    assert hash(settings) == hash(settings_from_env())
    with pytest.raises(Exception):  # noqa: B017 - dataclasses raise FrozenInstanceError
        settings.deployment = "other"  # type: ignore[misc]


def test_direct_construction_validates_too() -> None:
    """The guard lives in __post_init__, so it holds for non-env callers."""
    with pytest.raises(ModelNotConfigured, match="invalid provider"):
        AzureModelSettings(
            endpoint="https://demo.openai.azure.com/",
            api_key="k",
            api_version=DEFAULT_API_VERSION,
            deployment="d",
            provider="openai",  # type: ignore[arg-type]
        )
