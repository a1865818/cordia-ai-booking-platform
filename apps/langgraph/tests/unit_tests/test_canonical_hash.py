"""Shared cross-language canon.v1 corpus (spec 04 §5, Inv-2/Inv-3).

The expected hashes in ``tools/config/canon-v1-test-vectors.json`` were produced by
the authoritative C# implementation and are asserted there by
``CanonV1GoldenVectorTests`` and in the web app by ``canonicalHash.test.ts``.  A change
in any lane that diverges fails that lane's suite, so the three canonicalisers cannot
silently drift.

This matters because ``POST /api/bookings`` rejects a submission whose client-computed
``confirmation.intent_payload_hash`` does not equal the server's recomputed hash (422
``ConfirmationHashMismatchException``) — a drifted lane cannot book at all.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from cordia_agent.revisions.canonical_hash import (
    CANON_VERSION,
    canonical_hash,
    canonical_json,
)


def _repo_root() -> Path:
    current = Path(__file__).resolve()
    for parent in current.parents:
        if (parent / "nx.json").exists():
            return parent
    raise AssertionError("repo root (nx.json) not found")


_CORPUS = json.loads(
    (_repo_root() / "tools" / "config" / "canon-v1-test-vectors.json").read_text(
        encoding="utf-8"
    )
)
_VECTORS = _CORPUS["vectors"]


def test_corpus_loads() -> None:
    assert _CORPUS["version"] == CANON_VERSION
    assert len(_VECTORS) > 40


@pytest.mark.parametrize(
    ("name", "payload", "expected"),
    [(v["name"], v["payload"], v["expected"]) for v in _VECTORS],
    ids=[v["name"] for v in _VECTORS],
)
def test_golden_vector(name: str, payload: str, expected: str) -> None:
    result = canonical_hash(json.loads(payload))
    assert result.version == CANON_VERSION
    assert result.value == expected, (
        f"{name}: canonical json was {canonical_json(json.loads(payload))}"
    )


def test_equivalence_groups_agree() -> None:
    groups: dict[str, tuple[str, str]] = {}
    for vector in _VECTORS:
        value = canonical_hash(json.loads(vector["payload"])).value
        label = vector["equivalence"]
        if label in groups:
            first_name, first_hash = groups[label]
            assert value == first_hash, (
                f"equivalence '{label}': {vector['name']} vs {first_name}"
            )
        else:
            groups[label] = (vector["name"], value)
    assert groups


# --- properties the corpus alone would not make obvious -----------------------


def test_sorts_keys_and_drops_nulls() -> None:
    assert canonical_json({"z": 1, "a": 2, "m": None}) == '{"a":2,"z":1}'


def test_drops_empty_collections_recursively() -> None:
    assert canonical_json({"a": 1, "b": {}, "c": [], "d": {"e": []}}) == '{"a":1}'


def test_keeps_nulls_inside_arrays() -> None:
    assert canonical_json({"a": [None, 1]}) == '{"a":[1,null]}'


def test_escapes_non_ascii_as_uppercase_hex_unlike_json_dumps() -> None:
    assert canonical_json({"name": "café"}) == '{"name":"caf\\u00E9"}'
    assert json.dumps({"name": "café"}) != canonical_json({"name": "café"})


def test_escapes_html_sensitive_ascii() -> None:
    assert (
        canonical_json({"s": "a<b>c&d'e+f\"g`h"})
        == '{"s":"a\\u003Cb\\u003Ec\\u0026d\\u0027e\\u002Bf\\u0022g\\u0060h"}'
    )


def test_escapes_backslash_as_double_backslash() -> None:
    assert canonical_json({"s": "a\\b"}) == '{"s":"a\\\\b"}'


def test_emits_surrogate_pairs_as_two_escapes() -> None:
    assert canonical_json({"s": "\U0001f600"}) == '{"s":"\\uD83D\\uDE00"}'


def test_collapses_whitespace_preserving_case() -> None:
    assert canonical_json({"s": "  Window   Seat  "}) == '{"s":"Window Seat"}'


def test_nel_is_whitespace_but_bom_is_not() -> None:
    assert canonical_json({"s": "xy"}) == '{"s":"x y"}'
    assert canonical_json({"s": "x﻿y"}) == '{"s":"x\\uFEFFy"}'


def test_integer_forms_canonicalise_identically() -> None:
    assert canonical_json({"n": 500}) == '{"n":500}'
    assert canonical_json({"n": 500.0}) == '{"n":500}'
    assert canonical_json({"n": 5e2}) == '{"n":500}'
    assert canonical_json({"n": -0.0}) == '{"n":0}'


def test_fractions_have_no_exponent() -> None:
    assert canonical_json({"n": 1e-7}) == '{"n":0.0000001}'
    assert canonical_json({"n": 500.5}) == '{"n":500.5}'
    assert canonical_json({"n": -12.25}) == '{"n":-12.25}'


def test_large_integers_have_no_exponent() -> None:
    assert canonical_json({"n": 1e21}) == '{"n":1000000000000000000000}'


def test_timestamp_offset_normalises_to_utc_with_truncation() -> None:
    assert (
        canonical_json({"t": "2026-08-01T20:30:00+02:00"})
        == '{"t":"2026-08-01T18:30:00.000Z"}'
    )
    assert (
        canonical_json({"t": "2026-08-01T18:30:59.9995Z"})
        == '{"t":"2026-08-01T18:30:59.999Z"}'
    )


def test_tristate_markers_and_bare_dates_stay_plain_strings() -> None:
    assert canonical_json({"t": "absent"}) == '{"t":"absent"}'
    assert canonical_json({"d": "2026-06-15"}) == '{"d":"2026-06-15"}'
    assert canonical_json({"t": "2026-08-01T18:30:00"}) == '{"t":"2026-08-01T18:30:00"}'


def test_object_arrays_sort_by_natural_key() -> None:
    unsorted = {
        "lines": [
            {"item_id": "b", "quantity": 1},
            {"item_id": "a", "modifier_id": "z"},
            {"item_id": "a", "modifier_id": "a"},
        ]
    }
    assert canonical_json(unsorted) == (
        '{"lines":[{"item_id":"a","modifier_id":"a"},'
        '{"item_id":"a","modifier_id":"z"},'
        '{"item_id":"b","quantity":1}]}'
    )


def test_none_and_empty_payload_hash_identically() -> None:
    assert canonical_hash(None).value == canonical_hash({}).value
    assert len(canonical_hash(None).value) == 64
