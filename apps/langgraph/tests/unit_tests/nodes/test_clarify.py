"""Unit tests for the clarify node and its pure policy functions (02-G, §8.5).

Covers:
  - Escalation ladder order: decide_clarify_action for attempt 0/1/2/3/4.
  - Ambiguous datetime (starts_at, needs_clarification) forces SPELL_BACK;
    spell-back prompt contains date + local-time + timezone.
  - Draft immutability: clarify NEVER writes any draft field (Inv-1).
  - Attempt increment: clarify increments clarification_attempts[field] by 1;
    a second clarify on the same field escalates the action one rung.
  - Re-capture path: after clarify, capture_slots (the capture engine, not
    clarify) applies the corrected value to the draft.
"""

from __future__ import annotations

from datetime import UTC, datetime, timedelta, timezone

import pytest

from cordia_agent.nodes.clarify import (
    ESCALATION_LADDER,
    ClarifyAction,
    clarify,
    decide_clarify_action,
    render_datetime_spell_back,
)
from cordia_agent.slots.capture import (
    SlotCapture,
    capture_slots,
)
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    CaptureMethod,
    Channel,
    FieldProvenance,
    FieldValidation,
    OperationType,
    SourceInfo,
)


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


def _make_state(draft: BookingDraft | None = None, **kwargs) -> AgentState:
    """Build an AgentState with sensible defaults, overridable via kwargs."""
    defaults: dict = {
        "tenant_id": "tenant_a",
        "session_id": "session_1",
        "transcript_text": "I want to book",
        "transcript_is_final": True,
        "current_turn_id": "turn_001",
        "operation": OperationType.create,
        "draft": draft,
    }
    defaults.update(kwargs)
    return AgentState(**defaults)


# ---------------------------------------------------------------------------
# 1. Escalation ladder order — pure function
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_escalation_ladder_order() -> None:
    """ESCALATION_LADDER must be (ASK_AGAIN, SPELL_BACK, DTMF, ESCALATE_HANDOFF)."""
    assert ESCALATION_LADDER == (
        ClarifyAction.ASK_AGAIN,
        ClarifyAction.SPELL_BACK,
        ClarifyAction.DTMF,
        ClarifyAction.ESCALATE_HANDOFF,
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    "attempt,expected",
    [
        (0, ClarifyAction.ASK_AGAIN),
        (1, ClarifyAction.SPELL_BACK),
        (2, ClarifyAction.DTMF),
        (3, ClarifyAction.ESCALATE_HANDOFF),
        (4, ClarifyAction.ESCALATE_HANDOFF),  # clamped to last rung
    ],
)
def test_decide_clarify_action_non_datetime(
    attempt: int, expected: ClarifyAction
) -> None:
    """Non-datetime, no special validation: escalate purely by attempt (§8.5)."""
    action = decide_clarify_action(
        validation=None,
        stt_confidence=0.3,
        model_confidence=None,
        attempt=attempt,
        is_datetime=False,
    )
    assert action == expected


@pytest.mark.unit
def test_decide_clarify_action_low_confidence_starts_ask_again() -> None:
    """Low STT confidence for a non-datetime field starts at ASK_AGAIN on attempt 0."""
    action = decide_clarify_action(
        validation=None,
        stt_confidence=0.4,  # below LOW_CONFIDENCE_THRESHOLD=0.5
        model_confidence=None,
        attempt=0,
        is_datetime=False,
    )
    assert action == ClarifyAction.ASK_AGAIN


@pytest.mark.unit
def test_decide_clarify_action_invalid_field_starts_ask_again() -> None:
    """Invalid validation for a non-datetime field starts at ASK_AGAIN on attempt 0."""
    action = decide_clarify_action(
        validation=FieldValidation.invalid,
        stt_confidence=None,
        model_confidence=None,
        attempt=0,
        is_datetime=False,
    )
    assert action == ClarifyAction.ASK_AGAIN


@pytest.mark.unit
@pytest.mark.parametrize(
    "attempt,expected",
    [
        (0, ClarifyAction.SPELL_BACK),  # starts at SPELL_BACK rung
        (1, ClarifyAction.SPELL_BACK),  # min(1, 3)==1 → rung 1
        (2, ClarifyAction.DTMF),  # min(2, 3)==2 → rung 2
        (3, ClarifyAction.ESCALATE_HANDOFF),
        (4, ClarifyAction.ESCALATE_HANDOFF),
    ],
)
def test_decide_clarify_action_ambiguous_datetime(
    attempt: int, expected: ClarifyAction
) -> None:
    """Ambiguous datetime (needs_clarification) starts at SPELL_BACK and escalates (§8.5)."""
    action = decide_clarify_action(
        validation=FieldValidation.needs_clarification,
        stt_confidence=None,
        model_confidence=None,
        attempt=attempt,
        is_datetime=True,
    )
    assert action == expected


@pytest.mark.unit
def test_decide_clarify_action_datetime_without_needs_clarification_starts_ask_again() -> (
    None
):
    """is_datetime=True without needs_clarification follows the normal ladder from ASK_AGAIN."""
    action = decide_clarify_action(
        validation=None,
        stt_confidence=None,
        model_confidence=None,
        attempt=0,
        is_datetime=True,
    )
    assert action == ClarifyAction.ASK_AGAIN


# ---------------------------------------------------------------------------
# 2. render_datetime_spell_back — date + local-time + timezone
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_render_datetime_spell_back_utc_plus_10() -> None:
    """Spell-back for UTC+10 contains date, local time, and UTC offset."""
    tz = timezone(timedelta(hours=10))
    dt = datetime(2026, 6, 13, 19, 0, 0, tzinfo=tz)  # 7:00 PM AEST
    result = render_datetime_spell_back(dt)

    # (a) date
    assert "13" in result
    assert "June" in result
    assert "2026" in result
    # (b) local time — 7:00 PM
    assert "7:00 PM" in result
    # (c) timezone — UTC+10:00
    assert "UTC+10:00" in result


@pytest.mark.unit
def test_render_datetime_spell_back_custom_tz_label() -> None:
    """When tz_label is given, it appears in the spell-back instead of the offset."""
    tz = timezone(timedelta(hours=10))
    dt = datetime(2026, 6, 13, 19, 0, 0, tzinfo=tz)
    result = render_datetime_spell_back(dt, tz_label="AEST")

    assert "AEST" in result
    # UTC offset should NOT appear when tz_label is supplied
    assert "UTC+10:00" not in result
    # But date and time still present
    assert "2026" in result
    assert "7:00 PM" in result


@pytest.mark.unit
def test_render_datetime_spell_back_utc() -> None:
    """UTC datetime spell-back contains UTC+00:00."""
    dt = datetime(2026, 6, 13, 9, 30, 0, tzinfo=UTC)
    result = render_datetime_spell_back(dt)

    assert "UTC+00:00" in result
    assert "9:30 AM" in result
    assert "2026" in result


@pytest.mark.unit
def test_render_datetime_spell_back_negative_offset() -> None:
    """Negative UTC offset (e.g. UTC-05:00) renders correctly."""
    tz = timezone(timedelta(hours=-5))
    dt = datetime(2026, 6, 13, 14, 0, 0, tzinfo=tz)
    result = render_datetime_spell_back(dt)

    assert "UTC-05:00" in result
    assert "2:00 PM" in result


# ---------------------------------------------------------------------------
# 3. Draft immutability (Inv-1)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_clarify_does_not_mutate_draft() -> None:
    """clarify NEVER writes any field on state.draft (Inv-1)."""
    draft = _make_draft(operation=OperationType.create)
    draft_json_before = draft.model_dump_json()

    state = _make_state(draft=draft)
    result = await clarify(state)

    # Draft byte-equality
    assert draft.model_dump_json() == draft_json_before, (
        "clarify must not mutate state.draft"
    )
    # Return value must not contain 'draft'
    assert "draft" not in result, (
        "clarify must not include 'draft' in the returned update dict"
    )


@pytest.mark.unit
async def test_clarify_does_not_mutate_draft_with_provenance() -> None:
    """Draft immutability holds even when the draft has starts_at + provenance."""
    tz = timezone(timedelta(hours=10))
    dt = datetime(2026, 6, 20, 14, 0, 0, tzinfo=tz)

    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        party_size=2,
        starts_at=dt,
    )
    # Add provenance with needs_clarification
    draft = draft.model_copy(
        update={
            "field_provenance": {
                "starts_at": FieldProvenance(
                    source_turn_id="turn_x",
                    validation=FieldValidation.needs_clarification,
                    capture_method=CaptureMethod.speech,
                )
            }
        }
    )
    draft_json_before = draft.model_dump_json()

    state = _make_state(draft=draft)
    result = await clarify(state)

    assert draft.model_dump_json() == draft_json_before
    assert "draft" not in result


# ---------------------------------------------------------------------------
# 4. Ambiguous datetime → SPELL_BACK with date+time+timezone
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_clarify_ambiguous_datetime_spells_back_all_three() -> None:
    """Ambiguous starts_at: pending_clarify_action==spell_back, prompt has date+time+tz."""
    tz = timezone(timedelta(hours=10))
    dt = datetime(2026, 6, 13, 19, 0, 0, tzinfo=tz)  # Friday, 13 June 2026 at 7:00 PM

    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        party_size=2,
        starts_at=dt,
    )
    draft = draft.model_copy(
        update={
            "field_provenance": {
                "starts_at": FieldProvenance(
                    source_turn_id="turn_1",
                    validation=FieldValidation.needs_clarification,
                    capture_method=CaptureMethod.speech,
                )
            }
        }
    )

    state = _make_state(draft=draft)
    result = await clarify(state)

    assert result["pending_clarify_action"] == ClarifyAction.SPELL_BACK.value
    prompt = result["pending_clarify_prompt"]
    assert prompt is not None

    # (a) date
    assert "13" in prompt
    assert "June" in prompt
    assert "2026" in prompt
    # (b) local time
    assert "7:00 PM" in prompt
    # (c) timezone
    assert "UTC+10:00" in prompt


# ---------------------------------------------------------------------------
# 5. Attempt increment and action escalation
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_clarify_increments_attempt_count() -> None:
    """clarify increments clarification_attempts[field] by 1 each call."""
    draft = _make_draft(operation=OperationType.create)
    state = _make_state(draft=draft)

    result = await clarify(state)
    field = result["pending_clarify_field"]
    assert result["clarification_attempts"][field] == 1

    # Simulate second call with updated attempts
    state2 = _make_state(
        draft=draft,
        clarification_attempts=result["clarification_attempts"],
    )
    result2 = await clarify(state2)
    assert result2["clarification_attempts"][field] == 2


@pytest.mark.unit
async def test_clarify_escalates_action_on_second_attempt() -> None:
    """Second clarify on the same field escalates the action one rung."""
    draft = _make_draft(operation=OperationType.create)

    # First call: attempt 0 → ASK_AGAIN
    state1 = _make_state(draft=draft)
    result1 = await clarify(state1)
    assert result1["pending_clarify_action"] == ClarifyAction.ASK_AGAIN.value

    field = result1["pending_clarify_field"]

    # Second call: attempt 1 → SPELL_BACK
    state2 = _make_state(
        draft=draft,
        clarification_attempts={field: 1},
    )
    result2 = await clarify(state2)
    assert result2["pending_clarify_action"] == ClarifyAction.SPELL_BACK.value


@pytest.mark.unit
async def test_clarify_escalates_to_escalate_handoff_on_attempt_3() -> None:
    """Attempt 3 on a non-datetime field → ESCALATE_HANDOFF."""
    draft = _make_draft(operation=OperationType.create)
    # Pick the first required field that would be selected
    state = _make_state(draft=draft)
    # First clarify to learn which field is selected
    r = await clarify(state)
    field = r["pending_clarify_field"]

    state_high = _make_state(draft=draft, clarification_attempts={field: 3})
    result = await clarify(state_high)
    assert result["pending_clarify_action"] == ClarifyAction.ESCALATE_HANDOFF.value


# ---------------------------------------------------------------------------
# 6. Re-capture path: capture_slots (engine) writes the corrected field
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_recapture_via_capture_engine_after_clarify() -> None:
    """After clarify, the capture engine (not clarify) applies the corrected starts_at.

    Simulates the clarify → capture_fields loop:
    1. Draft has ambiguous starts_at (needs_clarification provenance).
    2. clarify returns spell-back prompt — does NOT write starts_at.
    3. capture_slots (engine) receives the corrected datetime and applies it.
    4. The corrected draft holds the new starts_at value.
    """
    tz = timezone(timedelta(hours=10))
    old_dt = datetime(2026, 6, 13, 19, 0, 0, tzinfo=tz)
    new_dt = datetime(2026, 6, 14, 10, 0, 0, tzinfo=tz)

    draft = _make_draft(
        operation=OperationType.create,
        service_id="svc_001",
        location_id="loc_001",
        party_size=2,
        starts_at=old_dt,
    )
    draft = draft.model_copy(
        update={
            "field_provenance": {
                "starts_at": FieldProvenance(
                    source_turn_id="turn_1",
                    validation=FieldValidation.needs_clarification,
                    capture_method=CaptureMethod.speech,
                )
            }
        }
    )
    draft_json_before = draft.model_dump_json()

    state = _make_state(draft=draft)
    # Step 1: clarify — must NOT change the draft
    clarify_result = await clarify(state)

    assert clarify_result["pending_clarify_action"] == ClarifyAction.SPELL_BACK.value
    assert draft.model_dump_json() == draft_json_before  # Inv-1 holds

    # Step 2: simulate user providing corrected starts_at; capture engine applies it
    corrected_capture = SlotCapture(
        field="starts_at",
        value=new_dt,
        capture_method=CaptureMethod.speech,
        stt_confidence=0.95,
        model_confidence=0.92,
        normalized_value=str(new_dt),
    )
    corrected_draft = capture_slots(
        draft,
        [corrected_capture],
        source_is_final=True,
        source_turn_id="turn_2",
    )

    # The capture engine wrote the corrected value; clarify never did
    assert corrected_draft.request.starts_at == new_dt
    assert corrected_draft is not draft
    # Original draft still unchanged (capture engine returns a new object)
    assert draft.model_dump_json() == draft_json_before


# ---------------------------------------------------------------------------
# 7. clarify returns pending_clarify_field pointing to a real required field
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_clarify_targets_first_missing_required_field() -> None:
    """clarify selects a missing required-candidate field first (§8.5 selection order)."""
    # Draft missing all required fields
    draft = _make_draft(operation=OperationType.create)
    state = _make_state(draft=draft)

    result = await clarify(state)

    # Must target one of the required candidate fields
    required = {"service_id", "location_id", "starts_at", "party_size"}
    assert result["pending_clarify_field"] in required


@pytest.mark.unit
async def test_clarify_returns_empty_dict_when_no_draft() -> None:
    """clarify returns {} and does not crash when draft is None."""
    state = _make_state(draft=None)
    result = await clarify(state)
    assert result == {}


# ---------------------------------------------------------------------------
# 8. clarify → capture_fields topology (graph-level)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_clarify_capture_fields_topology_edge_exists() -> None:
    """The compiled graph has clarify → capture_fields (and NOT clarify → human_handoff)."""
    from cordia_agent.graph import build_agent_graph

    edges = {(e.source, e.target) for e in build_agent_graph().get_graph().edges}

    assert ("clarify", "capture_fields") in edges, (
        "clarify → capture_fields edge must be present"
    )
    assert ("clarify", "human_handoff") not in edges, (
        "clarify → human_handoff edge must NOT exist (§8.3 topology frozen)"
    )
