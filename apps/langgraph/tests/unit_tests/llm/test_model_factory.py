"""Unit tests for the Azure chat-model factory.

Clients are constructed for real (that is the point — a broken kwarg
combination must fail here, not on the first customer turn), but no request is
ever issued, so the suite stays offline.
"""

from __future__ import annotations

import pytest

from cordia_agent.llm.config import DEFAULT_API_VERSION, AzureModelSettings
from cordia_agent.llm.factory import (
    _foundry_endpoint,
    get_chat_model,
    load_chat_model,
    reset_model_cache,
)


def _settings(**overrides) -> AzureModelSettings:
    base = {
        "endpoint": "https://demo.openai.azure.com/",
        "api_key": "secret-key",
        "api_version": DEFAULT_API_VERSION,
        "deployment": "gpt-4.1-mini",
    }
    return AzureModelSettings(**(base | overrides))


@pytest.fixture(autouse=True)
def _clear_cache():
    reset_model_cache()
    yield
    reset_model_cache()


@pytest.fixture
def azure_env(monkeypatch: pytest.MonkeyPatch) -> pytest.MonkeyPatch:
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://demo.openai.azure.com/")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "secret-key")
    monkeypatch.setenv("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini")
    for key in (
        "AZURE_OPENAI_API_VERSION",
        "AZURE_OPENAI_MODEL_PROVIDER",
        "AZURE_OPENAI_API_SURFACE",
        "AZURE_OPENAI_REASONING_EFFORT",
        "CORDIA_LLM_BASE_TIMEOUT_SECONDS",
    ):
        monkeypatch.delenv(key, raising=False)
    return monkeypatch


# ---------------------------------------------------------------------------
# Provider routing — every Foundry deployment must be reachable
# ---------------------------------------------------------------------------


def test_azure_openai_provider_builds_azure_chat_openai() -> None:
    model = load_chat_model(_settings())

    assert type(model).__name__ == "AzureChatOpenAI"
    assert model.deployment_name == "gpt-4.1-mini"


def test_azure_ai_provider_builds_foundry_client() -> None:
    """Non-OpenAI catalog models (Llama, Mistral, Phi) route here."""
    model = load_chat_model(
        _settings(
            provider="azure_ai",
            endpoint="https://demo.services.ai.azure.com/",
            deployment="Llama-3.3-70B-Instruct",
        )
    )

    assert type(model).__name__ == "AzureAIOpenAIApiChatModel"


def test_responses_surface_sets_responses_kwargs() -> None:
    model = load_chat_model(
        _settings(
            deployment="o4-mini", api_surface="responses", reasoning_effort="medium"
        )
    )

    assert model.use_responses_api is True
    assert model.reasoning == {"effort": "medium", "summary": "auto"}


def test_chat_completions_surface_passes_flat_reasoning_effort() -> None:
    """The flat kwarg is valid here and must NOT be nested."""
    model = load_chat_model(_settings(reasoning_effort="low"))

    assert model.reasoning_effort == "low"
    assert getattr(model, "reasoning", None) is None


def test_none_valued_kwargs_are_dropped() -> None:
    """An unset optional must leave the provider default, not null it."""
    model = load_chat_model(_settings())

    assert model.reasoning_effort is None
    assert model.max_tokens is None


def test_overrides_win_over_derived_kwargs() -> None:
    model = load_chat_model(_settings(), max_tokens=256, temperature=0.0)

    assert model.max_tokens == 256
    assert model.temperature == 0.0


# ---------------------------------------------------------------------------
# Foundry endpoint normalisation
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("configured", "expected"),
    [
        # A bare project endpoint gains the current OpenAI-compatible route.
        (
            "https://demo.services.ai.azure.com",
            "https://demo.services.ai.azure.com/openai/v1",
        ),
        (
            "https://demo.services.ai.azure.com/",
            "https://demo.services.ai.azure.com/openai/v1",
        ),
        # Already-versioned endpoints pass through untouched...
        (
            "https://demo.services.ai.azure.com/openai/v1",
            "https://demo.services.ai.azure.com/openai/v1",
        ),
        # ...including the older inference route and custom gateways.
        ("https://gateway.example.com/models", "https://gateway.example.com/models"),
        ("https://router.example.com/v1", "https://router.example.com/v1"),
    ],
)
def test_foundry_endpoint_normalisation(configured: str, expected: str) -> None:
    assert _foundry_endpoint(configured) == expected


def test_bare_foundry_endpoint_emits_no_sdk_warning(recwarn) -> None:
    """The SDK warns on a version-less endpoint; normalisation must prevent it."""
    load_chat_model(
        _settings(provider="azure_ai", endpoint="https://demo.services.ai.azure.com")
    )

    assert not [w for w in recwarn if "version segment" in str(w.message)]


# ---------------------------------------------------------------------------
# get_chat_model — env resolution + memoisation
# ---------------------------------------------------------------------------


def test_get_chat_model_reads_environment(azure_env) -> None:
    model = get_chat_model()

    assert model.deployment_name == "gpt-4.1-mini"


def test_identical_settings_reuse_one_client(azure_env) -> None:
    """Memoised so each turn reuses one connection pool."""
    assert get_chat_model() is get_chat_model()


def test_role_override_yields_a_distinct_client(azure_env) -> None:
    azure_env.setenv("AZURE_OPENAI_DEPLOYMENT_SLOT_EXTRACTOR", "gpt-4o-mini")

    extractor = get_chat_model("slot_extractor")
    default = get_chat_model()

    assert extractor is not default
    assert extractor.deployment_name == "gpt-4o-mini"
    assert default.deployment_name == "gpt-4.1-mini"


def test_env_change_invalidates_the_cache(azure_env) -> None:
    """Settings hash by value, so a changed deployment cannot hit a stale client."""
    first = get_chat_model()
    azure_env.setenv("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1")
    second = get_chat_model()

    assert first is not second
    assert second.deployment_name == "gpt-4.1"


def test_overrides_bypass_the_shared_cache(azure_env) -> None:
    """A one-off kwarg must never poison the client other callers share."""
    shared = get_chat_model()
    one_off = get_chat_model(max_tokens=128)

    assert one_off is not shared
    assert get_chat_model() is shared
