"""Unit tests for the await_confirmation node (02-I, §8.8 / §8.9 / Inv-1).

Covers:
- Inv-1: partial transcript (not final) → NO Confirmation recorded.
- Happy-path: transcript.final + explicit confirmation + disclosures + not interrupted
  → Confirmation recorded; payload_hash binding correct (§8.8).
- Consent gate (§5.5 / §14): empty disclosures_presented → REFUSED, routes back.
- §8.9 interrupted: read_back_interrupted=True → NOT confirmed.
- Inv-10 supersession: after material change, old confirmation no longer binds new revision.
"""

from __future__ import annotations

from datetime import UTC, datetime

import pytest

from cordia_agent.models.intent import confirmation_binds_revision
from cordia_agent.nodes.await_confirmation import (
    await_confirmation,
    consent_satisfied,
    is_explicit_confirmation,
    route_after_await_confirmation,
)
from cordia_agent.revisions.manager import build_revision, supersede_revision
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_NOW = datetime(2026, 6, 25, 10, 0, 0, tzinfo=UTC)
_LATER = datetime(2026, 6, 25, 11, 0, 0, tzinfo=UTC)


def _make_draft(service_id: str = "svc_1", party_size: int = 2) -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_t",
        session_id="sess_t",
        schema_version="1.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            service_id=service_id,
            party_size=party_size,
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _make_revision_dict(draft: BookingDraft) -> dict:
    rev = build_revision(
        intent_id="intent_001",
        revision=1,
        canonical_payload=draft.request,
        schema_version=draft.schema_version,
        tenant_policy_version=draft.tenant_policy_version,
        source_session_id=draft.session_id,
        created_at=_NOW,
    )
    return rev.model_dump(mode="json")


def _base_state(**overrides: object) -> AgentState:
    """Build a minimal AgentState set up for the happy-path."""
    draft = _make_draft()
    revision_dict = _make_revision_dict(draft)
    defaults: dict[str, object] = {
        "draft": draft,
        "transcript_text": "yes, confirm",
        "transcript_is_final": True,
        "read_back_version": "somehash_abc123",
        "read_back_interrupted": False,
        "disclosures_presented": ["disc_01"],
        "current_revision": revision_dict,
        "current_intent_id": "intent_001",
        "current_turn_id": "turn_001",
        "verification_level": "none",
    }
    defaults.update(overrides)
    return AgentState(**defaults)


# ---------------------------------------------------------------------------
# is_explicit_confirmation helper tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    ["yes", "confirm", "that's right", "correct", "go ahead", "Yes please", "YES"],
)
def test_is_explicit_confirmation_affirmatives(text: str) -> None:
    """Affirmative phrases → True."""
    assert is_explicit_confirmation(text) is True


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    ["no", "nope", "cancel", "change it", "wait", ""],
)
def test_is_explicit_confirmation_negatives(text: str) -> None:
    """Non-affirmative or empty text → False."""
    assert is_explicit_confirmation(text) is False


# ---------------------------------------------------------------------------
# consent_satisfied helper tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_consent_satisfied_with_disclosures() -> None:
    """Non-empty disclosures_presented → consent satisfied."""
    assert consent_satisfied(["disc_01"]) is True


@pytest.mark.unit
def test_consent_not_satisfied_empty() -> None:
    """Empty disclosures_presented → consent NOT satisfied (§5.5 / §14)."""
    assert consent_satisfied([]) is False


# ---------------------------------------------------------------------------
# Inv-1: partial transcript cannot confirm
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_partial_transcript_cannot_confirm() -> None:
    """Inv-1: transcript NOT final with 'yes confirm' → NO Confirmation recorded."""
    state = _base_state(
        transcript_is_final=False,  # partial transcript
        transcript_text="yes confirm",
    )
    result = await await_confirmation(state)
    # No confirmation should be recorded (either empty dict or missing key)
    assert result.get("confirmation") is None or "confirmation" not in result, (
        "Inv-1: partial transcript must NOT produce a Confirmation"
    )


@pytest.mark.unit
async def test_partial_transcript_routes_to_capture_fields() -> None:
    """Inv-1: partial transcript → router returns 'capture_fields'."""
    state = _base_state(
        transcript_is_final=False,
        transcript_text="yes confirm",
        confirmation=None,
    )
    assert route_after_await_confirmation(state) == "capture_fields"


# ---------------------------------------------------------------------------
# Happy-path: explicit confirmation on final transcript
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_explicit_confirmation_recorded() -> None:
    """transcript.final + explicit confirmation + disclosures → Confirmation recorded."""
    state = _base_state()
    result = await await_confirmation(state)

    assert result.get("confirmation") is not None, (
        "Happy-path: Confirmation must be recorded"
    )
    assert result.get("last_confirmed_revision") == 1


@pytest.mark.unit
async def test_confirmation_payload_hash_binding() -> None:
    """§8.8: recorded Confirmation.intent_payload_hash == current revision's payload_hash."""
    from cordia_agent.models.intent import Confirmation, IntentRevision

    state = _base_state()
    result = await await_confirmation(state)

    assert result.get("confirmation") is not None

    conf = Confirmation.model_validate(result["confirmation"])
    rev = IntentRevision.model_validate(state.current_revision)

    assert conf.intent_payload_hash == rev.payload_hash, (
        "§8.8: Confirmation.intent_payload_hash must equal the revision payload_hash"
    )
    assert confirmation_binds_revision(conf, rev) is True, (
        "§8.8: confirmation_binds_revision must be True for a freshly recorded Confirmation"
    )


@pytest.mark.unit
async def test_confirmation_fields_persisted() -> None:
    """Recorded Confirmation carries read_back_version, disclosures, and verification_level."""
    from cordia_agent.models.intent import Confirmation

    state = _base_state(
        read_back_version="hash_xyz",
        disclosures_presented=["disc_a", "disc_b"],
        verification_level="sms",
    )
    result = await await_confirmation(state)
    conf = Confirmation.model_validate(result["confirmation"])

    assert conf.read_back_version == "hash_xyz"
    assert conf.disclosures_presented == ["disc_a", "disc_b"]
    assert conf.verification_level == "sms"


@pytest.mark.unit
async def test_confirmed_state_routes_to_submit_intent() -> None:
    """After recording a Confirmation, router returns 'submit_intent'."""
    state = _base_state()
    result = await await_confirmation(state)
    # Build new state with recorded confirmation
    updated_state = _base_state(**{"confirmation": result.get("confirmation")})
    assert route_after_await_confirmation(updated_state) == "submit_intent"


# ---------------------------------------------------------------------------
# §5.5 / §14 consent gate: empty disclosures → refused
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_consent_gate_empty_disclosures_refused() -> None:
    """Consent gate: empty disclosures_presented → Confirmation REFUSED (§5.5 / §14)."""
    state = _base_state(disclosures_presented=[])
    result = await await_confirmation(state)
    # Confirmation must NOT be recorded
    assert result.get("confirmation") is None, (
        "§5.5/§14: empty disclosures must refuse to record a Confirmation"
    )


@pytest.mark.unit
async def test_consent_gate_routes_to_capture_fields() -> None:
    """Consent gate: empty disclosures → routes back (not 'submit_intent')."""
    state = _base_state(disclosures_presented=[], confirmation=None)
    result = await await_confirmation(state)
    # Apply the result (confirmation=None) to state and check router
    updated = _base_state(
        disclosures_presented=[],
        confirmation=result.get("confirmation"),
    )
    route = route_after_await_confirmation(updated)
    assert route != "submit_intent", (
        "§5.5/§14: consent gate failure must NOT route to submit_intent"
    )
    assert route == "capture_fields"


# ---------------------------------------------------------------------------
# §8.9 interrupted: read_back_interrupted=True → never confirmed
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_interrupted_read_back_not_confirmed() -> None:
    """§8.9: read_back_interrupted=True → NOT confirmed even with explicit final confirmation."""
    state = _base_state(read_back_interrupted=True)
    result = await await_confirmation(state)
    assert result.get("confirmation") is None or "confirmation" not in result, (
        "§8.9: interrupted read-back must never produce a Confirmation"
    )


@pytest.mark.unit
async def test_interrupted_routes_to_capture_fields() -> None:
    """§8.9: interrupted read-back → router returns 'capture_fields'."""
    state = _base_state(read_back_interrupted=True, confirmation=None)
    assert route_after_await_confirmation(state) == "capture_fields"


# ---------------------------------------------------------------------------
# Inv-10: material change invalidates prior confirmation
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_material_change_invalidates_confirmation() -> None:
    """Inv-10: after material change → supersede_revision; old confirmation no longer binds new rev.

    Simulates the scenario where:
    1. A Confirmation is recorded for revision 1 (svc_1).
    2. The customer changes service_id to svc_2 (material change).
    3. supersede_revision produces revision 2 with a new payload_hash.
    4. The old Confirmation's intent_payload_hash != new revision's → binds False.
    """
    from cordia_agent.models.intent import Confirmation

    # Step 1: record a Confirmation for rev 1
    draft1 = _make_draft(service_id="svc_1")
    rev1 = build_revision(
        intent_id="intent_001",
        revision=1,
        canonical_payload=draft1.request,
        schema_version="1.0",
        tenant_policy_version="v1",
        source_session_id="sess_t",
        created_at=_NOW,
    )
    state1 = AgentState(
        draft=draft1,
        transcript_text="yes confirm",
        transcript_is_final=True,
        read_back_version="rbv_1",
        read_back_interrupted=False,
        disclosures_presented=["disc_01"],
        current_revision=rev1.model_dump(mode="json"),
        current_intent_id="intent_001",
        current_turn_id="turn_001",
        verification_level="none",
    )
    result1 = await await_confirmation(state1)
    assert result1.get("confirmation") is not None, (
        "Pre-condition: confirmation must be recorded"
    )

    conf1 = Confirmation.model_validate(result1["confirmation"])
    assert confirmation_binds_revision(conf1, rev1) is True, "Pre-condition: binds rev1"

    # Step 2: customer changes service → material change → supersede
    draft2 = _make_draft(service_id="svc_2")
    rev2 = supersede_revision(
        rev1,
        draft2.request,
        reason="changed_service",
        created_at=_LATER,
    )

    # Step 3: old Confirmation must NOT bind the new revision (Inv-10 invalidation)
    assert confirmation_binds_revision(conf1, rev2) is False, (
        "Inv-10: prior confirmation must NOT bind the superseded revision"
    )

    # Step 4: a fresh Confirmation for rev2 DOES bind it
    state2 = AgentState(
        draft=draft2,
        transcript_text="yes go ahead",
        transcript_is_final=True,
        read_back_version="rbv_2",
        read_back_interrupted=False,
        disclosures_presented=["disc_01"],
        current_revision=rev2.model_dump(mode="json"),
        current_intent_id="intent_001",
        current_turn_id="turn_002",
        verification_level="none",
    )
    result2 = await await_confirmation(state2)
    assert result2.get("confirmation") is not None, (
        "Fresh confirmation must be recorded for rev2"
    )

    conf2 = Confirmation.model_validate(result2["confirmation"])
    assert confirmation_binds_revision(conf2, rev2) is True, (
        "Fresh Confirmation must bind the new revision"
    )
