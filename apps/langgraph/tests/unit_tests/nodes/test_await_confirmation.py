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
from langgraph.graph import END

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
# Fix D — is_explicit_confirmation rejects negations + word-boundary match
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    [
        "yes",
        "yes confirm please",
        "go ahead",
        "that's right",
        "correct",
        "okay",
    ],
)
def test_is_explicit_confirmation_fix_d_affirmatives(text: str) -> None:
    """Fix D: extended affirmative set — all must return True."""
    assert is_explicit_confirmation(text) is True, (
        f"Fix D: {text!r} should be an explicit confirmation"
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    [
        "not correct",
        "don't go ahead",
        "no",
        "nope",
        "incorrect",
        "cancel",
        "that's wrong",
        "",
    ],
)
def test_is_explicit_confirmation_fix_d_negations_rejected(text: str) -> None:
    """Fix D: negation-bearing or empty utterances must return False.

    Covers:
    - ``not correct`` — negation token rejects even though "correct" is affirmative.
    - ``don't go ahead`` — negation contraction rejects even though "go ahead" is affirmative.
    - ``incorrect`` — word-boundary prevents "correct" from matching inside "incorrect".
    - ``that's wrong`` — "wrong" is a negation token.
    """
    assert is_explicit_confirmation(text) is False, (
        f"Fix D: {text!r} must NOT be an explicit confirmation"
    )


@pytest.mark.unit
async def test_await_confirmation_negated_phrase_does_not_confirm() -> None:
    """Fix D node-level: 'not correct' on a final transcript with full read-back
    and disclosures must NOT record a Confirmation.
    """
    state = _base_state(
        transcript_text="not correct",
        transcript_is_final=True,
        read_back_interrupted=False,
    )
    result = await await_confirmation(state)
    assert result.get("confirmation") is None or "confirmation" not in result, (
        "Fix D: 'not correct' must NOT produce a Confirmation"
    )


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
async def test_partial_transcript_ends_the_turn() -> None:
    """Inv-1: a partial transcript cannot confirm → the turn ENDs awaiting a reply."""
    state = _base_state(
        transcript_is_final=False,
        transcript_text="yes confirm",
        confirmation=None,
    )
    assert route_after_await_confirmation(state) == END


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
        # A CHANNEL in the assurance field — which is how this got into production. The node
        # now coerces it to the honest floor rather than forwarding a value the API rejects.
        verification_level="sms",
    )
    result = await await_confirmation(state)
    conf = Confirmation.model_validate(result["confirmation"])

    assert conf.read_back_version == "hash_xyz"
    assert conf.disclosures_presented == ["disc_a", "disc_b"]
    assert conf.verification_level == "unverified"


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
async def test_consent_gate_ends_the_turn() -> None:
    """Consent gate: empty disclosures → the turn ENDs (never 'submit_intent')."""
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
    assert route == END


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
async def test_interrupted_ends_the_turn() -> None:
    """§8.9: interrupted read-back → never confirmed, so the turn ENDs."""
    state = _base_state(read_back_interrupted=True, confirmation=None)
    assert route_after_await_confirmation(state) == END


@pytest.mark.unit
async def test_confirmation_from_an_earlier_turn_does_not_resubmit() -> None:
    """A stale Confirmation must not route to submit_intent on a later turn.

    submit_intent routes recoverable failures back to capture_fields, which
    walks forward to await_confirmation again.  Without the turn check the
    confirmation recorded on the ORIGINAL turn would resubmit the identical
    payload for the identical rejection, forever.
    """
    state = _base_state()
    recorded = (await await_confirmation(state)).get("confirmation")
    assert recorded is not None

    later_turn = _base_state(confirmation=recorded, current_turn_id="turn-later")
    assert route_after_await_confirmation(later_turn) == END


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


# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# §8.9: a read-back the customer has not heard cannot be confirmed
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_a_read_back_produced_this_turn_cannot_be_confirmed_this_turn() -> None:
    """An affirmative on the turn that PRODUCED the read-back is answering
    something else.

    read_back runs earlier in the same turn, so on the first turn that renders
    one the customer has not been told anything yet.  The live text run hit
    exactly this: "Yes, that's fine. I'd like to book a table..." answered the
    recording-consent notice and was recorded as a booking confirmation in the
    same breath — submitting a booking that had never been read back.
    """
    state = _base_state(
        read_back_first_presented_turn_id="turn_001",  # == current_turn_id
    )
    result = await await_confirmation(state)

    assert result.get("confirmation") is None
    assert route_after_await_confirmation(_base_state(**result)) == END


@pytest.mark.unit
async def test_the_same_read_back_confirms_on_the_following_turn() -> None:
    """Once the customer has heard it, their "yes" means what it says."""
    state = _base_state(
        read_back_first_presented_turn_id="turn_000",  # an earlier turn
    )
    result = await await_confirmation(state)

    assert result.get("confirmation") is not None
    assert result["confirmation"]["source_turn_id"] == "turn_001"


# ---------------------------------------------------------------------------
# R15 — the assurance vocabulary
# ---------------------------------------------------------------------------


def test_normalise_verification_level_keeps_ratified_members() -> None:
    from cordia_agent.nodes.await_confirmation import _normalise_verification_level

    for level in (
        "unverified",
        "channel_verified",
        "identity_verified",
        "high_assurance",
    ):
        assert _normalise_verification_level(level) == level


def test_normalise_verification_level_coerces_everything_else_to_the_honest_floor() -> (
    None
):
    """The API rejects an off-vocabulary value outright, so forwarding one loses a confirmed
    submission. Recording the honest floor is better than a 400 — and far better than the old
    behaviour, where the API silently mapped it to unverified with nothing saying so."""
    from cordia_agent.nodes.await_confirmation import _normalise_verification_level

    # "none" was this node's own historical sentinel; "sms" is a channel that leaked into the
    # assurance field. Neither is a member.
    for junk in (None, "", "none", "sms", "verified", "IDENTITY-VERIFIED", "  "):
        assert _normalise_verification_level(junk) == "unverified"


def test_normalise_verification_level_is_case_and_space_tolerant() -> None:
    from cordia_agent.nodes.await_confirmation import _normalise_verification_level

    assert _normalise_verification_level("  Identity_Verified  ") == "identity_verified"
