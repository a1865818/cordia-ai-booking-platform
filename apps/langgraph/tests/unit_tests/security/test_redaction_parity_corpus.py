"""Shared cross-language redaction corpus (spec 14, Inv-12).

The same vectors are asserted against the C# `SensitiveDataMask` by
`SensitiveDataMaskParityTests` in apps/api — a change to either filter that
diverges from the shared corpus fails one suite or the other, so the two
lanes cannot silently drift.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from cordia_agent.security.redaction import redact_payload, redact_text


def _repo_root() -> Path:
    current = Path(__file__).resolve()
    for parent in current.parents:
        if (parent / "nx.json").exists():
            return parent
    raise AssertionError("repo root (nx.json) not found")


_CORPUS = json.loads(
    (_repo_root() / "tools" / "config" / "redaction-test-vectors.json").read_text(
        encoding="utf-8"
    )
)
_VECTORS = _CORPUS["vectors"]
_PAYLOAD_KEY_VECTORS = _CORPUS["payload_key_vectors"]


@pytest.mark.unit
@pytest.mark.parametrize("vector", _VECTORS, ids=[v["class"] for v in _VECTORS])
def test_shared_corpus_vector(vector: dict) -> None:
    masked = "[REDACTED]" in redact_text(vector["input"])
    assert masked == vector["expect_masked"], (
        f"{vector['class']}: expected expect_masked={vector['expect_masked']} "
        f"for {vector['input']!r}"
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    "vector",
    _PAYLOAD_KEY_VECTORS,
    ids=[v["class"] for v in _PAYLOAD_KEY_VECTORS],
)
def test_shared_payload_key_vector(vector: dict) -> None:
    """Pins the structured-payload control-plane exemption (including the
    exact-name system-minted ``*_ref`` allowlist) to the shared corpus: an
    exemption added or removed in only ONE lane fails that lane's suite, so
    the C# and Python lists cannot silently drift."""
    pan = "4111111111111111"
    out = redact_payload({vector["key"]: pan})
    exempt = out[vector["key"]] == pan
    assert exempt == vector["expect_exempt"], (
        f"{vector['class']}: expected expect_exempt={vector['expect_exempt']} "
        f"for key {vector['key']!r}"
    )
