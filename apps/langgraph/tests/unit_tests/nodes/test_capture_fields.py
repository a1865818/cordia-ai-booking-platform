"""Unit tests for the slot-capture engine and the capture_fields node (02-F, §8.4).

Covers:
  - UnknownSlotFieldError on unknown field names
  - Declared-only slot capture + FieldProvenance recording
  - Inv-1: partial transcripts (source_is_final=False) leave draft unchanged;
    final transcripts apply captures
  - TriState values do NOT count as concrete (required_candidate_fields_present)
  - route_after_capture_fields routing logic
  - Material-update → checkpoint persistence (set_session_checkpointer)
"""

from __future__ import annotations

import json
from datetime import UTC, datetime

import httpx
import pytest

from cordia_agent.checkpoint import (
    SessionCheckpointer,
    reset_session_checkpointer,
    set_session_checkpointer,
)
from cordia_agent.nodes.capture_fields import (
    POLICY_BLOCK_KEY,
    capture_fields,
    route_after_capture_fields,
)
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.slots.capture import (
    SlotCapture,
    UnknownSlotFieldError,
    capture_slots,
    clear_slot_extractor,
    register_slot_extractor,
    required_candidate_fields_present,
)
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    CaptureMethod,
    Channel,
    OperationType,
    SourceInfo,
    TriState,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from tests._stubs.read_tools_fake import FakeSlotExtractor, make_service_id_capture


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _make_draft(
    operation: OperationType = OperationType.create,
    tenant_id: str = "tenant_a",
    session_id: str = "session_1",
    **request_kwargs,
) -> BookingDraft:
    """Build a minimal valid BookingDraft with optional request overrides."""
    return BookingDraft(
        tenant_id=tenant_id,
        session_id=session_id,
        schema_version="1.0",
        tenant_policy_version="v2",
        request=BookingRequest(operation=operation, **request_kwargs),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _make_state(**kwargs) -> AgentState:
    """Build an AgentState with sensible defaults, overridable via kwargs."""
    defaults = {
        "tenant_id": "tenant_a",
        "session_id": "session_1",
        "transcript_text": "I'd like to book a table",
        "transcript_is_final": True,
        "current_turn_id": "turn_001",
        "operation": OperationType.create,
    }
    defaults.update(kwargs)
    return AgentState(**defaults)


def _make_capture(
    field: str,
    value,
    *,
    capture_method: CaptureMethod = CaptureMethod.speech,
    stt_confidence: float = 0.95,
    model_confidence: float = 0.90,
) -> SlotCapture:
    return SlotCapture(
        field=field,
        value=value,
        capture_method=capture_method,
        stt_confidence=stt_confidence,
        model_confidence=model_confidence,
        normalized_value=str(value),
    )


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _reset_ports():
    """Reset slot extractor and checkpointer after each test."""
    yield
    clear_slot_extractor()
    reset_session_checkpointer()


# ---------------------------------------------------------------------------
# 1. Reject unknown fields
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_capture_slots_rejects_unknown_field() -> None:
    """capture_slots raises UnknownSlotFieldError for a field not in CAPTURABLE_FIELDS."""
    draft = _make_draft()
    bad_capture = _make_capture("favourite_colour", "red")

    with pytest.raises(UnknownSlotFieldError, match="favourite_colour"):
        capture_slots(draft, [bad_capture], source_is_final=True)


@pytest.mark.unit
def test_capture_slots_rejects_unknown_field_mixed_with_valid() -> None:
    """Unknown field in a mixed list still raises before any mutation (fail-fast)."""
    draft = _make_draft()
    captures = [
        _make_capture("service_id", "svc_001"),
        _make_capture("unknown_field", "value"),
    ]

    with pytest.raises(UnknownSlotFieldError, match="unknown_field"):
        capture_slots(draft, captures, source_is_final=True)


# ---------------------------------------------------------------------------
# 2. Declared-only capture + provenance
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_capture_slots_writes_declared_fields_and_records_provenance() -> None:
    """Capturing service_id and party_size writes ONLY those fields with provenance."""
    draft = _make_draft()
    captures = [
        _make_capture(
            "service_id", "svc_001", stt_confidence=0.95, model_confidence=0.90
        ),
        _make_capture("party_size", 4, stt_confidence=0.88, model_confidence=0.85),
    ]

    updated = capture_slots(
        draft, captures, source_is_final=True, source_turn_id="turn_01"
    )

    assert updated.request.service_id == "svc_001"
    assert updated.request.party_size == 4
    # Other fields unchanged
    assert updated.request.location_id is None
    assert updated.request.starts_at is None

    # Provenance recorded for each captured field
    assert "service_id" in updated.field_provenance
    assert "party_size" in updated.field_provenance

    prov_svc = updated.field_provenance["service_id"]
    assert prov_svc.source_turn_id == "turn_01"
    assert prov_svc.stt_confidence == 0.95
    assert prov_svc.model_confidence == 0.90
    assert prov_svc.capture_method == CaptureMethod.speech
    assert prov_svc.normalized_value == "svc_001"

    prov_party = updated.field_provenance["party_size"]
    assert prov_party.source_turn_id == "turn_01"
    assert prov_party.stt_confidence == 0.88


@pytest.mark.unit
def test_capture_slots_does_not_mutate_input_draft() -> None:
    """capture_slots always returns a new BookingDraft; input is never mutated."""
    draft = _make_draft()
    original_request_json = draft.request.model_dump_json()

    captures = [_make_capture("service_id", "svc_001")]
    updated = capture_slots(draft, captures, source_is_final=True)

    # Input draft unchanged
    assert draft.request.model_dump_json() == original_request_json
    # Updated draft is a new object
    assert updated is not draft
    assert updated.request.service_id == "svc_001"


# ---------------------------------------------------------------------------
# 3. Inv-1: partial transcripts never mutate the draft
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_capture_slots_inv1_partial_returns_unchanged_draft() -> None:
    """Inv-1: source_is_final=False returns the draft completely unchanged (byte-equal)."""
    draft = _make_draft()
    original_json = draft.model_dump_json()

    captures = [_make_capture("service_id", "svc_001")]
    result = capture_slots(draft, captures, source_is_final=False)

    # Draft returned is the exact same object
    assert result is draft
    # JSON is byte-equal
    assert result.model_dump_json() == original_json


@pytest.mark.unit
def test_capture_slots_inv1_final_applies_captures() -> None:
    """Inv-1 positive: source_is_final=True DOES apply the captures."""
    draft = _make_draft()
    captures = [_make_capture("service_id", "svc_final")]

    result = capture_slots(draft, captures, source_is_final=True)

    assert result.request.service_id == "svc_final"


@pytest.mark.unit
def test_capture_slots_inv1_both_paths_compared() -> None:
    """Assert both paths side-by-side: partial leaves unchanged, final applies."""
    draft = _make_draft()
    captures = [_make_capture("service_id", "svc_x")]

    partial_result = capture_slots(draft, captures, source_is_final=False)
    final_result = capture_slots(draft, captures, source_is_final=True)

    # Partial: unchanged
    assert partial_result.request.model_dump_json() == draft.request.model_dump_json()
    # Final: applied
    assert final_result.request.service_id == "svc_x"


# ---------------------------------------------------------------------------
# 4. required_candidate_fields_present + TriState gate
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_required_fields_absent_returns_false() -> None:
    """Draft missing party_size → required_candidate_fields_present is False."""
    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        starts_at="2026-07-01T10:00:00+00:00",
        # party_size intentionally absent (None)
    )
    assert required_candidate_fields_present(draft) is False


@pytest.mark.unit
def test_required_fields_tristate_does_not_count() -> None:
    """A TriState sentinel (e.g. party_size=unknown) is NOT a concrete value."""
    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        starts_at="2026-07-01T10:00:00+00:00",
        party_size=TriState.unknown,  # TriState, NOT a concrete integer
    )
    assert required_candidate_fields_present(draft) is False


@pytest.mark.unit
def test_required_fields_all_concrete_returns_true() -> None:
    """All required fields for create → required_candidate_fields_present True."""
    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        starts_at="2026-07-01T10:00:00+00:00",
        party_size=2,
    )
    assert required_candidate_fields_present(draft) is True


@pytest.mark.unit
def test_required_fields_tristate_declined_does_not_count() -> None:
    """TriState.declined is also not a concrete value."""
    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        starts_at="2026-07-01T10:00:00+00:00",
        party_size=TriState.declined,
    )
    assert required_candidate_fields_present(draft) is False


# ---------------------------------------------------------------------------
# 5. route_after_capture_fields routing
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_route_clarify_when_draft_is_none() -> None:
    """route_after_capture_fields → 'clarify' when draft is None."""
    state = _make_state(draft=None)
    assert route_after_capture_fields(state) == "clarify"


@pytest.mark.unit
def test_route_clarify_when_fields_missing() -> None:
    """route_after_capture_fields → 'clarify' when party_size is missing."""
    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        starts_at="2026-07-01T10:00:00+00:00",
    )
    state = _make_state(draft=draft, operation=OperationType.create)
    assert route_after_capture_fields(state) == "clarify"


@pytest.mark.unit
def test_route_check_candidates_when_all_required_present() -> None:
    """All required create fields present + no grant → 'check_candidates'."""
    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        starts_at="2026-07-01T10:00:00+00:00",
        party_size=2,
    )
    state = _make_state(
        draft=draft,
        operation=OperationType.create,
        verification_grant_id=None,
    )
    assert route_after_capture_fields(state) == "check_candidates"


@pytest.mark.unit
def test_route_verify_identity_for_cancel_without_grant() -> None:
    """cancel without verification_grant_id → 'verify_identity'."""
    # cancel requires a target_booking_id so we build a valid cancel draft
    draft = BookingDraft(
        tenant_id="tenant_a",
        session_id="session_1",
        schema_version="1.0",
        tenant_policy_version="v2",
        request=BookingRequest(
            operation=OperationType.cancel,
            target_booking_id="bk_001",
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    state = _make_state(
        draft=draft,
        operation=OperationType.cancel,
        verification_grant_id=None,
    )
    assert route_after_capture_fields(state) == "verify_identity"


@pytest.mark.unit
def test_route_read_back_for_cancel_with_grant() -> None:
    """cancel WITH verification_grant_id → 'read_back'."""
    draft = BookingDraft(
        tenant_id="tenant_a",
        session_id="session_1",
        schema_version="1.0",
        tenant_policy_version="v2",
        request=BookingRequest(
            operation=OperationType.cancel,
            target_booking_id="bk_001",
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    state = _make_state(
        draft=draft,
        operation=OperationType.cancel,
        verification_grant_id="grant_xyz",
    )
    assert route_after_capture_fields(state) == "read_back"


# ---------------------------------------------------------------------------
# 6. Material-update → checkpoint test
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_capture_fields_node_checkpoints_material_update() -> None:
    """capture_fields nodes checkpoints the updated state after a material change.

    Flow:
      1. Register a fresh SessionCheckpointer via set_session_checkpointer.
      2. Register a FakeSlotExtractor that returns a service_id capture.
      3. Build a state with tenant_id + session_id + draft (no service_id yet).
      4. await capture_fields(state).
      5. Assert the checkpointer now holds the post-update state (load returns it).
    """
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)

    captures = [make_service_id_capture("svc_checkpointed")]
    register_slot_extractor(FakeSlotExtractor(captures))

    draft = _make_draft(operation=OperationType.create)
    state = AgentState(
        tenant_id="tenant_a",
        session_id="session_1",
        draft=draft,
        transcript_text="book a table",
        transcript_is_final=True,
        current_turn_id="turn_cp",
        operation=OperationType.create,
    )

    result = await capture_fields(state)

    # Node should have returned the updated draft
    updated_draft = result.get("draft")
    assert updated_draft is not None
    assert updated_draft.request.service_id == "svc_checkpointed"

    # Checkpointer should hold the state with the updated draft
    loaded = cp.load("tenant_a", "session_1")
    assert loaded is not None
    assert loaded.draft is not None
    assert loaded.draft.request.service_id == "svc_checkpointed"


@pytest.mark.unit
async def test_capture_fields_node_no_checkpoint_when_no_change() -> None:
    """capture_fields does NOT checkpoint when the draft is unchanged (no material update)."""
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)

    # Extractor returns no captures → no change to draft
    register_slot_extractor(FakeSlotExtractor([]))

    draft = _make_draft(operation=OperationType.create)
    state = AgentState(
        tenant_id="tenant_a",
        session_id="session_1",
        draft=draft,
        transcript_text="hello",
        transcript_is_final=True,
        operation=OperationType.create,
    )

    await capture_fields(state)

    # Nothing should have been checkpointed
    loaded = cp.load("tenant_a", "session_1")
    assert loaded is None


@pytest.mark.unit
async def test_capture_fields_node_no_checkpoint_when_partial() -> None:
    """capture_fields does NOT checkpoint when transcript is partial (Inv-1)."""
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)

    captures = [make_service_id_capture("svc_partial")]
    register_slot_extractor(FakeSlotExtractor(captures))

    draft = _make_draft(operation=OperationType.create)
    state = AgentState(
        tenant_id="tenant_a",
        session_id="session_1",
        draft=draft,
        transcript_text="book",
        transcript_is_final=False,  # partial — must NOT mutate or checkpoint
        operation=OperationType.create,
    )

    result = await capture_fields(state)

    # Draft should not have changed (Inv-1)
    updated = result.get("draft")
    assert updated is draft or (
        updated is not None and updated.request.service_id is None
    )

    # Nothing checkpointed
    loaded = cp.load("tenant_a", "session_1")
    assert loaded is None


@pytest.mark.unit
async def test_capture_fields_node_skip_when_draft_none() -> None:
    """capture_fields returns empty dict and does not crash when draft is None."""
    register_slot_extractor(FakeSlotExtractor([]))

    state = AgentState(
        tenant_id="tenant_a",
        session_id="session_1",
        draft=None,
        transcript_text="anything",
        transcript_is_final=True,
    )

    result = await capture_fields(state)
    assert result == {}


# ---------------------------------------------------------------------------
# Fix B — route_after_capture_fields follows draft.request.operation, NOT
#          state.operation (the two can diverge after identify_intent)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_route_follows_draft_operation_not_state_operation() -> None:
    """Fix B: router reads draft.request.operation, not state.operation.

    Build a state where state.operation is ``create`` (would route to
    check_candidates) but draft.request.operation is ``cancel`` with a
    target_booking_id present and no verification_grant_id.  The router
    MUST follow the draft and return ``"verify_identity"``.
    """
    cancel_draft = BookingDraft(
        tenant_id="tenant_a",
        session_id="session_1",
        schema_version="1.0",
        tenant_policy_version="v2",
        request=BookingRequest(
            operation=OperationType.cancel,
            target_booking_id="bk_diverge_001",
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )

    state = _make_state(
        draft=cancel_draft,
        # state.operation diverges — intentionally set to create.
        operation=OperationType.create,
        verification_grant_id=None,
    )

    route = route_after_capture_fields(state)

    assert route == "verify_identity", (
        "Fix B: router must follow draft.request.operation (cancel → verify_identity), "
        f"not state.operation (create → check_candidates); got {route!r}"
    )


# ---------------------------------------------------------------------------
# The policy gate (§6.1)
#
# "A table for 400 people at 7pm" used to sail straight through to the
# availability lookup, which answers a capacity question and has no opinion on
# the size of a party or on whether the venue is open.
# ---------------------------------------------------------------------------

_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_a",
    channel=Channel.voice,
    channel_endpoint_id="ep_1",
    configuration_version="v1",
    correlation_id="corr_1",
    session_id="session_1",
    allowed_agent_policy_ids=["p"],
    allowed_tool_policy_ids=["p"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)

_SITTINGS = [
    {"resource_id": "breakfast", "kind": "service", "name": "Breakfast", "aliases": []},
    {"resource_id": "lunch", "kind": "service", "name": "Lunch", "aliases": []},
    {"resource_id": "dinner", "kind": "service", "name": "Dinner", "aliases": []},
]

TOO_BIG = "That's a big group! For anything over 20 the team will sort it out with you."


def _install_platform(*, validation: dict, tracked: set[str]) -> list[str]:
    """Serve the policy gate and the capacity probes; record the paths called."""
    called: list[str] = []

    def handle(request: httpx.Request) -> httpx.Response:
        called.append(request.url.path)
        if "booking-drafts/validate" in request.url.path:
            return httpx.Response(200, content=json.dumps(validation).encode())
        if "tenant-resources" in request.url.path:
            return httpx.Response(
                200,
                content=json.dumps(
                    {"status": "listed", "matches": _SITTINGS, "resolved": None}
                ).encode(),
            )
        service_id = json.loads(request.content.decode())["draft"]["request"][
            "service_id"
        ]
        return httpx.Response(
            200,
            content=json.dumps(
                {
                    "mode": "live" if service_id in tracked else "informational",
                    "candidates": [],
                    "hold": None,
                }
            ).encode(),
        )

    set_tool_client(
        ToolClient(
            httpx.AsyncClient(
                transport=httpx.MockTransport(handle), base_url="http://test"
            ),
            tenant_context=_TENANT_CONTEXT,
        )
    )
    return called


def _valid_response() -> dict:
    return {
        "status": "valid",
        "valid": True,
        "code": "valid",
        "customer_message": "",
        "recoverable_fields": [],
        "suggested_action": None,
        "policy_version": "v2",
    }


def _refused_response(message: str = TOO_BIG) -> dict:
    return {
        "status": "party_size_unsupported",
        "valid": False,
        "code": "party_size_unsupported",
        "customer_message": message,
        "recoverable_fields": ["party_size"],
        "suggested_action": "request_human_handoff",
        "policy_version": "v2",
    }


def _timed_draft() -> BookingDraft:
    return _make_draft(
        operation=OperationType.create,
        starts_at=datetime(2026, 9, 15, 9, 0, tzinfo=UTC),
    )


@pytest.fixture
def _reset_client():
    yield
    reset_tool_client()


@pytest.mark.unit
async def test_a_party_of_400_is_refused_in_the_venues_own_words(_reset_client) -> None:
    called = _install_platform(validation=_refused_response(), tracked={"dinner"})
    register_slot_extractor(FakeSlotExtractor([_make_capture("party_size", 400)]))

    result = await capture_fields(_make_state(draft=_timed_draft()))

    assert result[POLICY_BLOCK_KEY] == TOO_BIG
    # And the capacity question is never asked: a refused draft has no business
    # holding a table while the caller is told it cannot have one.
    assert not any("availability" in path for path in called)


@pytest.mark.unit
async def test_a_draft_the_venue_accepts_is_not_blocked(_reset_client) -> None:
    _install_platform(validation=_valid_response(), tracked={"dinner"})
    register_slot_extractor(FakeSlotExtractor([_make_capture("party_size", 4)]))

    result = await capture_fields(_make_state(draft=_timed_draft()))

    assert result[POLICY_BLOCK_KEY] is None
    assert result["draft"].request.service_id == "dinner"


@pytest.mark.unit
async def test_a_time_no_sitting_covers_offers_the_sittings_we_do(
    _reset_client,
) -> None:
    """The venue is open, but nothing is being served then.

    derive_sitting found no sitting and the agent went on to ask which ROOM the
    caller wanted, which is not the answer to "a table at 3am".
    """
    _install_platform(validation=_valid_response(), tracked=set())
    register_slot_extractor(FakeSlotExtractor([_make_capture("party_size", 4)]))

    result = await capture_fields(_make_state(draft=_timed_draft()))

    assert result[POLICY_BLOCK_KEY] == (
        "We're not serving at that time, sorry. "
        "We do Breakfast, Lunch, and Dinner. Which would suit?"
    )


@pytest.mark.unit
async def test_a_draft_with_nothing_to_judge_does_not_call_the_gate(
    _reset_client,
) -> None:
    """The gate rules on a party size and a start time and on nothing else."""
    called = _install_platform(validation=_valid_response(), tracked={"dinner"})
    register_slot_extractor(FakeSlotExtractor([make_service_id_capture("svc_1")]))

    await capture_fields(_make_state(draft=_make_draft()))

    assert called == []


@pytest.mark.unit
def test_a_refused_draft_routes_to_clarify_not_to_availability() -> None:
    """Without this, "a table for 400 at 7pm" has every required field and goes
    on to be priced, held and read back."""
    complete = _make_draft(
        operation=OperationType.create,
        service_id="svc_1",
        location_id="loc_1",
        starts_at=datetime(2026, 9, 15, 9, 0, tzinfo=UTC),
        party_size=400,
    )
    state = _make_state(draft=complete)
    assert route_after_capture_fields(state) == "check_candidates"

    # model_copy carries the channel capture_fields writes. AgentState declares
    # it in the same change that teaches clarify to speak it; until then this is
    # how the router's own behaviour is pinned.
    refused = state.model_copy(update={POLICY_BLOCK_KEY: TOO_BIG})

    assert route_after_capture_fields(refused) == "clarify"
