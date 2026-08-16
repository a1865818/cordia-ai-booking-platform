"""Unit tests for the revision manager (02-I, §8.7 / §8.8 / Inv-10)."""

from __future__ import annotations

from datetime import UTC, datetime

import pytest

from cordia_agent.models.intent import Confirmation, confirmation_binds_revision
from cordia_agent.revisions.manager import (
    build_revision,
    canonical_payload_hash,
    is_material_change,
    supersede_revision,
)
from cordia_agent.state import BookingRequest, CustomerRef, OperationType


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_NOW = datetime(2026, 6, 25, 10, 0, 0, tzinfo=UTC)
_LATER = datetime(2026, 6, 25, 11, 0, 0, tzinfo=UTC)


def _create_request(**overrides: object) -> BookingRequest:
    kwargs: dict[str, object] = {"operation": OperationType.create}
    kwargs.update(overrides)
    return BookingRequest(**kwargs)


def _make_revision(payload: BookingRequest, revision: int = 1) -> object:
    """Build a minimal IntentRevision for testing."""
    return build_revision(
        intent_id="intent_test_001",
        revision=revision,
        canonical_payload=payload,
        schema_version="1.0",
        tenant_policy_version="v1",
        source_session_id="sess_001",
        created_at=_NOW,
    )


# ---------------------------------------------------------------------------
# canonical_payload_hash — determinism
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_canonical_payload_hash_deterministic() -> None:
    """Same payload → same hash (called twice)."""
    payload = _create_request(service_id="svc_1", party_size=2)
    h1 = canonical_payload_hash(payload)
    h2 = canonical_payload_hash(payload)
    assert h1 == h2


@pytest.mark.unit
def test_canonical_payload_hash_different_material_field_different_hash() -> None:
    """Different material field → different hash."""
    p1 = _create_request(service_id="svc_1")
    p2 = _create_request(service_id="svc_2")
    assert canonical_payload_hash(p1) != canonical_payload_hash(p2)


@pytest.mark.unit
def test_canonical_payload_hash_is_hex_string() -> None:
    """Hash is a non-empty hex string (sha256 → 64 chars)."""
    payload = _create_request()
    h = canonical_payload_hash(payload)
    assert isinstance(h, str) and len(h) == 64
    int(h, 16)  # raises ValueError if not valid hex


@pytest.mark.unit
def test_canonical_payload_hash_omitted_optional_resource_id_unchanged() -> None:
    """Absent/None resource_id is omitted by canon.v1, so the hash matches a payload without it."""
    omitted = _create_request(service_id="svc_1", party_size=2)
    explicit_none = _create_request(service_id="svc_1", party_size=2, resource_id=None)
    assert canonical_payload_hash(omitted) == canonical_payload_hash(explicit_none)


# ---------------------------------------------------------------------------
# build_revision
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_build_revision_payload_hash_matches() -> None:
    """build_revision sets payload_hash == canonical_payload_hash(payload)."""
    payload = _create_request(service_id="svc_1", party_size=3)
    rev = build_revision(
        intent_id="intent_001",
        revision=1,
        canonical_payload=payload,
        schema_version="1.0",
        tenant_policy_version="v1",
        source_session_id="sess_001",
        created_at=_NOW,
    )
    assert rev.payload_hash == canonical_payload_hash(payload)


@pytest.mark.unit
def test_build_revision_fields_set_correctly() -> None:
    """build_revision stores all constructor arguments verbatim."""
    payload = _create_request(service_id="svc_A")
    rev = build_revision(
        intent_id="intent_xyz",
        revision=2,
        canonical_payload=payload,
        schema_version="2.0",
        tenant_policy_version="v2",
        source_session_id="sess_abc",
        created_at=_NOW,
        prior_revision=1,
        supersession_reason="test supersession",
    )
    assert rev.intent_id == "intent_xyz"
    assert rev.revision == 2
    assert rev.schema_version == "2.0"
    assert rev.tenant_policy_version == "v2"
    assert rev.source_session_id == "sess_abc"
    assert rev.prior_revision == 1
    assert rev.supersession_reason == "test supersession"


# ---------------------------------------------------------------------------
# is_material_change
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_is_material_change_detects_service_id_change() -> None:
    """Changing service_id is a material change → True."""
    old = _create_request(service_id="svc_1")
    new = _create_request(service_id="svc_2")
    assert is_material_change(old, new) is True


@pytest.mark.unit
def test_is_material_change_detects_location_id_change() -> None:
    """Changing location_id is a material change → True."""
    old = _create_request(location_id="loc_1")
    new = _create_request(location_id="loc_2")
    assert is_material_change(old, new) is True


@pytest.mark.unit
def test_is_material_change_detects_resource_id_change() -> None:
    """Changing resource_id is a material change → True."""
    old = _create_request(resource_id="staff_1")
    new = _create_request(resource_id="staff_2")
    assert is_material_change(old, new) is True


@pytest.mark.unit
def test_is_material_change_detects_party_size_change() -> None:
    """Changing party_size is a material change → True."""
    old = _create_request(party_size=2)
    new = _create_request(party_size=4)
    assert is_material_change(old, new) is True


@pytest.mark.unit
def test_is_material_change_detects_operation_change() -> None:
    """Changing operation (create→reschedule) is a material change → True."""
    old = _create_request()
    new = BookingRequest(
        operation=OperationType.reschedule,
        target_booking_id="booking_001",
    )
    assert is_material_change(old, new) is True


@pytest.mark.unit
def test_is_material_change_special_requests_only_not_material() -> None:
    """Changing only special_requests is NOT a material change → False.

    Rationale: special_requests are free-form annotations that do not define
    the identity of a booking (operation/service/location/time/party/duration).
    A change here alone does not trigger a new revision.
    """
    old = _create_request(service_id="svc_1", special_requests=[])
    new = _create_request(service_id="svc_1", special_requests=["window seat"])
    assert is_material_change(old, new) is False


@pytest.mark.unit
def test_is_material_change_detects_customer_projection() -> None:
    """Projecting the guest onto a request that had none is a material change → True.

    ``read_back`` fills ``request.customer`` from the ambient guest when the request
    carries none, and its comment claims that "counts as a material change below and
    supersedes the revision".  While ``customer`` was absent from ``_MATERIAL_FIELDS``
    that was false: the projection changed the request and left the revision holding
    ``customer=None``, and since the projection only runs while ``request.customer`` is
    None it never fired again.  The two diverged permanently — the guest was dropped
    from the submitted payload and any lane sending ``draft.request`` with the stale
    ``payload_hash`` got a 422.
    """
    old = _create_request(service_id="svc_1")
    new = _create_request(
        service_id="svc_1",
        customer=CustomerRef(phone_e164="+61400000000"),
    )
    assert old.customer is None
    assert is_material_change(old, new) is True


@pytest.mark.unit
def test_is_material_change_detects_customer_replacement() -> None:
    """Booking for a DIFFERENT guest is material — it moves the notification destination."""
    old = _create_request(customer=CustomerRef(phone_e164="+61400000000"))
    new = _create_request(customer=CustomerRef(phone_e164="+61400000001"))
    assert is_material_change(old, new) is True


@pytest.mark.unit
def test_is_material_change_identical_payloads_false() -> None:
    """Identical payloads → no material change → False."""
    p = _create_request(service_id="svc_1", party_size=2)
    assert is_material_change(p, p) is False


# ---------------------------------------------------------------------------
# supersede_revision (Inv-10)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_supersede_revision_increments_revision_number() -> None:
    """supersede_revision → new.revision == current.revision + 1."""
    payload = _create_request(service_id="svc_1")
    current = _make_revision(payload, revision=1)
    new_payload = _create_request(service_id="svc_2")
    new_rev = supersede_revision(
        current, new_payload, reason="changed service", created_at=_LATER
    )
    assert new_rev.revision == current.revision + 1


@pytest.mark.unit
def test_supersede_revision_sets_prior_revision() -> None:
    """supersede_revision → new.prior_revision == current.revision."""
    payload = _create_request(service_id="svc_1")
    current = _make_revision(payload, revision=3)
    new_payload = _create_request(service_id="svc_2")
    new_rev = supersede_revision(
        current, new_payload, reason="reason", created_at=_LATER
    )
    assert new_rev.prior_revision == current.revision


@pytest.mark.unit
def test_supersede_revision_sets_supersession_reason() -> None:
    """supersede_revision → new.supersession_reason == given reason."""
    payload = _create_request(service_id="svc_1")
    current = _make_revision(payload)
    new_payload = _create_request(service_id="svc_2")
    new_rev = supersede_revision(
        current, new_payload, reason="draft_updated", created_at=_LATER
    )
    assert new_rev.supersession_reason == "draft_updated"


@pytest.mark.unit
def test_supersede_revision_new_payload_hash_differs() -> None:
    """supersede_revision → new payload_hash differs from current (different content)."""
    payload = _create_request(service_id="svc_1")
    current = _make_revision(payload)
    new_payload = _create_request(service_id="svc_2")
    new_rev = supersede_revision(
        current, new_payload, reason="changed", created_at=_LATER
    )
    assert new_rev.payload_hash != current.payload_hash


@pytest.mark.unit
def test_supersede_revision_current_not_mutated() -> None:
    """supersede_revision does NOT mutate the current revision (Inv-10 immutability)."""
    payload = _create_request(service_id="svc_1")
    current = _make_revision(payload, revision=1)
    original_revision = current.revision
    original_hash = current.payload_hash

    new_payload = _create_request(service_id="svc_2")
    supersede_revision(current, new_payload, reason="changed", created_at=_LATER)

    # current is unchanged
    assert current.revision == original_revision
    assert current.payload_hash == original_hash
    assert current.prior_revision is None  # was not set before supersession


# ---------------------------------------------------------------------------
# §8.8 binding: confirmation_binds_revision
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_confirmation_binds_revision_true_when_hash_matches() -> None:
    """§8.8: Confirmation built with matching payload_hash → confirmation_binds_revision True."""
    payload = _create_request(service_id="svc_1", party_size=2)
    rev = _make_revision(payload)

    confirmation = Confirmation(
        confirmation_id="conf_001",
        intent_id=rev.intent_id,
        intent_revision=rev.revision,
        intent_payload_hash=rev.payload_hash,  # must match
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=_NOW,
        source_turn_id="turn_001",
        read_back_version="abc123",
        disclosures_presented=["disc_01"],
        verification_level="none",
    )

    assert confirmation_binds_revision(confirmation, rev) is True, (
        "§8.8: confirmation with matching payload_hash must bind the revision"
    )


@pytest.mark.unit
def test_confirmation_does_not_bind_superseded_revision() -> None:
    """§8.8 / Inv-10: after supersession, the old confirmation no longer binds the new revision."""
    payload = _create_request(service_id="svc_1", party_size=2)
    rev1 = _make_revision(payload, revision=1)

    # Record a Confirmation bound to rev1
    confirmation = Confirmation(
        confirmation_id="conf_001",
        intent_id=rev1.intent_id,
        intent_revision=rev1.revision,
        intent_payload_hash=rev1.payload_hash,
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=_NOW,
        source_turn_id="turn_001",
        read_back_version="abc123",
        disclosures_presented=["disc_01"],
        verification_level="none",
    )

    # Customer changes service → supersede
    new_payload = _create_request(service_id="svc_2", party_size=2)
    rev2 = supersede_revision(
        rev1, new_payload, reason="changed_service", created_at=_LATER
    )

    # The old confirmation must NOT bind the new revision (Inv-10 invalidation)
    assert confirmation_binds_revision(confirmation, rev2) is False, (
        "Inv-10: prior confirmation must NOT bind the superseded revision"
    )
