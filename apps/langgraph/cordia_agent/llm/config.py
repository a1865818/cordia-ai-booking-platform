"""Environment-driven Azure model settings (spec 02 model seam).

One ``AzureModelSettings`` describes how to reach ONE Azure AI Foundry
deployment.  Every value is read from the environment so no credential is ever
committed, and the whole test suite runs offline by never constructing one.

Two provider surfaces are supported so that **any** deployment in a Foundry
project is reachable:

``azure_openai``
    The Azure-OpenAI-compatible surface (``https://<res>.openai.azure.com/``)
    served by ``langchain-openai``.  Use for OpenAI-family deployments —
    gpt-4.1, gpt-4o, gpt-4o-mini, o-series.

``azure_ai``
    The Foundry inference surface (``<endpoint>/openai/v1``) served by
    ``langchain-azure-ai``.  Use for everything else in the catalog — Llama,
    Mistral, Phi, Cohere, DeepSeek — which is not exposed on the OpenAI route.

Per-role deployment overrides let one process drive several deployments (e.g. a
cheap mini model for slot extraction, a stronger one for clarification) without
a second settings object: ``AZURE_OPENAI_DEPLOYMENT_<ROLE>`` wins over the
shared ``AZURE_OPENAI_DEPLOYMENT`` when present.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from typing import Literal


type ModelProvider = Literal["azure_openai", "azure_ai"]
type ApiSurface = Literal["chat_completions", "responses"]

VALID_PROVIDERS: frozenset[str] = frozenset({"azure_openai", "azure_ai"})
VALID_API_SURFACES: frozenset[str] = frozenset({"chat_completions", "responses"})

# Default api-version: new enough for the Responses API surface, and accepted by
# the Chat Completions surface too, so a single default serves both.
DEFAULT_API_VERSION = "2025-04-01-preview"

# Per-attempt model deadline.  Deliberately far tighter than a coding agent's
# budget: the foreground agent is conversational (spec 03 §6.6 puts every TOOL
# under 400 ms), so a model call that has not returned in 8 s has already blown
# the turn and should be retried on a longer budget rather than waited on.
DEFAULT_BASE_TIMEOUT_SECONDS = 8.0

# Minimum api-version accepting the Azure OpenAI Responses API.
_MIN_RESPONSES_API_DATE = "2025-03-01-preview"
_API_VERSION_DATE_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})")


class ModelNotConfigured(RuntimeError):
    """Raised when the Azure environment is absent or internally inconsistent.

    Fails closed at construction time so a misconfigured deployment surfaces at
    startup rather than as a 400 mid-conversation.
    """


@dataclass(frozen=True, slots=True)
class AzureModelSettings:
    """Resolved connection settings for one Azure AI Foundry deployment.

    Attributes:
        endpoint: Resource endpoint.  For ``azure_openai`` this is the
            ``https://<resource>.openai.azure.com/`` form from the resource's
            *Keys and Endpoint* blade; for ``azure_ai`` it is the Foundry
            project endpoint (a version segment is appended by the factory
            when absent — see ``factory._foundry_endpoint``).
        api_key: Azure API key.  Never logged.
        api_version: Azure REST api-version.
        deployment: The **deployment** name created in Foundry — not the
            underlying model name (they often differ).
        provider: Which client surface to build (see module docstring).
        api_surface: ``chat_completions`` (default) or ``responses``.  The
            latter is required by some reasoning deployments and demands
            ``api_version >= 2025-03-01-preview``.
        reasoning_effort: Optional reasoning budget for reasoning models;
            ``None`` leaves the provider default untouched.
        base_timeout_seconds: First-attempt deadline for
            ``ainvoke_with_timeout``.
    """

    endpoint: str
    api_key: str
    api_version: str
    deployment: str
    provider: ModelProvider = "azure_openai"
    api_surface: ApiSurface = "chat_completions"
    reasoning_effort: str | None = None
    base_timeout_seconds: float = DEFAULT_BASE_TIMEOUT_SECONDS

    def __post_init__(self) -> None:
        """Validate the combination, failing closed on anything unusable."""
        if self.provider not in VALID_PROVIDERS:
            raise ModelNotConfigured(
                f"invalid provider {self.provider!r}; expected one of "
                f"{sorted(VALID_PROVIDERS)} "
                "(set AZURE_OPENAI_MODEL_PROVIDER)"
            )
        if self.api_surface not in VALID_API_SURFACES:
            raise ModelNotConfigured(
                f"invalid api_surface {self.api_surface!r}; expected one of "
                f"{sorted(VALID_API_SURFACES)} (set AZURE_OPENAI_API_SURFACE)"
            )
        if self.api_surface == "responses":
            if self.provider != "azure_openai":
                raise ModelNotConfigured(
                    "api_surface='responses' is only available on "
                    "provider='azure_openai'; Foundry's azure_ai inference "
                    "surface does not expose the Responses API"
                )
            _require_responses_api_version(self.api_version)
        if self.base_timeout_seconds <= 0:
            raise ModelNotConfigured(
                f"base_timeout_seconds must be > 0 (got {self.base_timeout_seconds!r})"
            )


def _require_responses_api_version(api_version: str) -> None:
    """Raise if *api_version* predates the Responses API cutoff.

    Args:
        api_version: The configured Azure REST api-version string.

    Raises:
        ModelNotConfigured: if the version is unparseable or too old.  Checked
            eagerly so the failure lands at startup, not as a mid-turn 400.
    """
    match = _API_VERSION_DATE_RE.match(api_version or "")
    if match is None or match.group(1) < _MIN_RESPONSES_API_DATE[:10]:
        raise ModelNotConfigured(
            f"api_surface='responses' requires api_version >= "
            f"{_MIN_RESPONSES_API_DATE}, got {api_version!r}; raise "
            "AZURE_OPENAI_API_VERSION"
        )


def _env(name: str) -> str | None:
    """Return a stripped environment value, treating blank as absent."""
    value = os.environ.get(name, "").strip()
    return value or None


def settings_from_env(role: str | None = None) -> AzureModelSettings:
    """Build :class:`AzureModelSettings` from the process environment.

    Reads ``AZURE_OPENAI_ENDPOINT``, ``AZURE_OPENAI_API_KEY``,
    ``AZURE_OPENAI_API_VERSION``, ``AZURE_OPENAI_DEPLOYMENT``,
    ``AZURE_OPENAI_MODEL_PROVIDER``, ``AZURE_OPENAI_API_SURFACE``,
    ``AZURE_OPENAI_REASONING_EFFORT`` and
    ``CORDIA_LLM_BASE_TIMEOUT_SECONDS``.

    When *role* is given, every knob may be overridden per role by suffixing
    the variable with ``_<ROLE>`` upper-cased — e.g. ``role="slot_extractor"``
    checks ``AZURE_OPENAI_DEPLOYMENT_SLOT_EXTRACTOR`` before falling back to
    the shared ``AZURE_OPENAI_DEPLOYMENT``.  This is how one process drives a
    cheap extraction deployment alongside a stronger clarification one.

    Args:
        role: Optional logical role selecting per-role environment overrides.

    Returns:
        A validated :class:`AzureModelSettings`.

    Raises:
        ModelNotConfigured: if endpoint, key or deployment is missing, if the
            timeout is not a positive number, or if the provider/surface
            combination is invalid.
    """
    suffix = f"_{role.upper()}" if role else ""

    def read(name: str) -> str | None:
        return (_env(f"{name}{suffix}") if suffix else None) or _env(name)

    endpoint = read("AZURE_OPENAI_ENDPOINT")
    api_key = read("AZURE_OPENAI_API_KEY")
    deployment = read("AZURE_OPENAI_DEPLOYMENT")

    missing = [
        name
        for name, value in (
            ("AZURE_OPENAI_ENDPOINT", endpoint),
            ("AZURE_OPENAI_API_KEY", api_key),
            ("AZURE_OPENAI_DEPLOYMENT", deployment),
        )
        if not value
    ]
    if missing:
        role_hint = f" for role {role!r}" if role else ""
        raise ModelNotConfigured(
            f"Azure model not configured{role_hint}: missing "
            f"{', '.join(missing)}. See apps/langgraph/.env.example."
        )

    raw_timeout = read("CORDIA_LLM_BASE_TIMEOUT_SECONDS")
    if raw_timeout is None:
        base_timeout = DEFAULT_BASE_TIMEOUT_SECONDS
    else:
        try:
            base_timeout = float(raw_timeout)
        except ValueError as exc:
            raise ModelNotConfigured(
                f"CORDIA_LLM_BASE_TIMEOUT_SECONDS must be a number, got {raw_timeout!r}"
            ) from exc

    # asserted non-None by the `missing` guard above
    assert endpoint is not None and api_key is not None and deployment is not None

    return AzureModelSettings(
        endpoint=endpoint,
        api_key=api_key,
        api_version=read("AZURE_OPENAI_API_VERSION") or DEFAULT_API_VERSION,
        deployment=deployment,
        provider=read("AZURE_OPENAI_MODEL_PROVIDER") or "azure_openai",  # type: ignore[arg-type]
        api_surface=read("AZURE_OPENAI_API_SURFACE") or "chat_completions",  # type: ignore[arg-type]
        reasoning_effort=read("AZURE_OPENAI_REASONING_EFFORT"),
        base_timeout_seconds=base_timeout,
    )
