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

from cordia_agent.security.redaction import redact_text


def _repo_root() -> Path:
    current = Path(__file__).resolve()
    for parent in current.parents:
        if (parent / "nx.json").exists():
            return parent
    raise AssertionError("repo root (nx.json) not found")


_VECTORS = json.loads(
    (_repo_root() / "tools" / "config" / "redaction-test-vectors.json").read_text(
        encoding="utf-8"
    )
)["vectors"]


@pytest.mark.unit
@pytest.mark.parametrize("vector", _VECTORS, ids=[v["class"] for v in _VECTORS])
def test_shared_corpus_vector(vector: dict) -> None:
    masked = "[REDACTED]" in redact_text(vector["input"])
    assert masked == vector["expect_masked"], (
        f"{vector['class']}: expected expect_masked={vector['expect_masked']} "
        f"for {vector['input']!r}"
    )
