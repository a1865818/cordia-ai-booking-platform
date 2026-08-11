"""canon.v1 canonicalization + cross-language parity (spec 04 §5, Inv-1/Inv-2).

The fixtures in :data:`PARITY_CASES` are duplicated verbatim — payload AND
expected digest — in
``apps/api/test/Cordia.Tests/UnitTests/Idempotency/CanonicalHashCrossLanguageParityTests.cs``.
If ``cordia_agent/revisions/canonical.py`` or
``Cordia.Infrastructure.Services.CanonicalRequestHasher`` drifts, exactly one of
the two suites goes red — which is the point: a silent divergence means every
submission is rejected at the §8.8 hash gate.
"""

from __future__ import annotations

import pytest

from cordia_agent.revisions.canonical import (
    CANONICALIZER_VERSION,
    canonical_json,
    canonical_payload_hash,
)


# (label, payload, canonical json, sha-256 hex) — mirrored in the C# suite.
PARITY_CASES = [
    pytest.param(
        {
            "operation": "create",
            "service_id": "svc_001",
            "location_id": "loc_001",
            "starts_at": "2026-08-10T19:00:00Z",
            "party_size": 4,
            "duration_minutes": None,
            "special_requests": ["high chair"],
            "preorder": None,
            "target_booking_id": None,
            "target_booking_version": None,
        },
        '{"location_id":"loc_001","operation":"create","party_size":4,'
        '"service_id":"svc_001","special_requests":["high chair"],'
        '"starts_at":"2026-08-10T19:00:00.000Z"}',
        "9efa756fc92721ac095d700d7467e30161781146f53f7295a24db182009ec45e",
        id="real_agent_submission",
    ),
    pytest.param(
        {
            "starts_at": "2026-06-15T20:30:00.9999+10:00",
            "requested_at": "2026-06-15T10:30:00.000Z",
        },
        '{"requested_at":"2026-06-15T10:30:00.000Z",'
        '"starts_at":"2026-06-15T10:30:00.999Z"}',
        "3d639d40fbfb2c2593276fa26c93b42592b99ce406b56539b13d0cbc651c2960",
        id="offset_folding_and_sub_ms_truncation",
    ),
    pytest.param(
        {"note": "café &  bar <b>  ", "emoji": "\U0001f600"},
        '{"emoji":"\\uD83D\\uDE00","note":"caf\\u00E9 \\u0026 bar \\u003Cb\\u003E"}',
        "04bf740fcec16aa06f12e2b8cd5b1f67f09c614763825ac57dbdb665ca9d619d",
        id="dotnet_default_escaping",
    ),
    pytest.param(
        {"a": 500, "b": 500.0, "c": 500.5, "d": 0},
        '{"a":500,"b":500,"c":500.5,"d":0}',
        "30397cf077921abd5085b51d9f0c78ffb2eebf3d1ebe86acf46246b59a1a2ed8",
        id="number_normalization",
    ),
    pytest.param(
        {
            "lines": [
                {"item_id": "b", "modifier_id": "2"},
                {"item_id": "a", "modifier_id": "1"},
            ],
            "tags": ["z", "a"],
            "empty": [],
        },
        '{"lines":[{"item_id":"a","modifier_id":"1"},'
        '{"item_id":"b","modifier_id":"2"}],"tags":["a","z"]}',
        "bf1d43afd874fef6a23e7e4cd31f1664cbc3b2a13e0266f7bd84b419070b92e8",
        id="list_ordering_and_empty_omission",
    ),
]


@pytest.mark.unit
@pytest.mark.parametrize(("payload", "expected_json", "expected_hash"), PARITY_CASES)
def test_canon_v1_parity_with_dotnet(
    payload: dict, expected_json: str, expected_hash: str
) -> None:
    """Both the canonical form and the digest must match the C# hasher exactly."""
    assert canonical_json(payload) == expected_json
    assert canonical_payload_hash(payload) == expected_hash


@pytest.mark.unit
def test_absent_and_explicit_null_hash_identically() -> None:
    """§5: absent and explicit null are the same canonical form."""
    assert canonical_payload_hash({"a": 1, "b": None}) == canonical_payload_hash({"a": 1})


@pytest.mark.unit
def test_key_order_does_not_affect_the_hash() -> None:
    """§5: object key order is not preserved from the wire."""
    assert canonical_payload_hash({"b": 2, "a": 1}) == canonical_payload_hash(
        {"a": 1, "b": 2}
    )


@pytest.mark.unit
def test_whitespace_is_trimmed_and_collapsed_but_case_is_preserved() -> None:
    """§5: NFC + trim + collapse, and identifiers are NOT lowercased."""
    assert canonical_json({"n": "  Jane   Doe  "}) == '{"n":"Jane Doe"}'
    assert canonical_payload_hash({"n": "Svc_001"}) != canonical_payload_hash(
        {"n": "svc_001"}
    )


@pytest.mark.unit
def test_null_payload_hashes_as_the_empty_object() -> None:
    """A null/empty payload is deterministic, matching the API's behaviour."""
    assert canonical_json(None) == "{}"
    assert canonical_payload_hash(None) == canonical_payload_hash({})


@pytest.mark.unit
def test_nulls_inside_arrays_are_preserved() -> None:
    """Only object properties drop nulls; array elements keep them."""
    assert canonical_json({"a": [1, None]}) == '{"a":[1,null]}'


@pytest.mark.unit
def test_canonicalizer_version_label() -> None:
    """The version label is stored on the API's idempotency record (§5)."""
    assert CANONICALIZER_VERSION == "canon.v1"
