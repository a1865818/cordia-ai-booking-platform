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

import json
from datetime import UTC, datetime, timedelta, timezone

import httpx
import pytest

from cordia_agent.nodes.clarify import (
    ASK_PROMPTS,
    ESCALATION_LADDER,
    ClarifyAction,
    clarify,
    decide_clarify_action,
    field_label,
    is_options_request,
    render_datetime_spell_back,
)
from cordia_agent.safety.tenant_context import TenantContext
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
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

#: What the seeded tenant declares.  clarify now LISTS these instead of asking
#: the caller to guess at a vocabulary only the venue knows, so the node needs a
#: ToolClient in every test that reaches a resource field.
_DECLARED: dict[str, list[dict]] = {
    "service": [
        {
            "resource_id": "breakfast",
            "kind": "service",
            "name": "Breakfast",
            "aliases": [],
        },
        {
            "resource_id": "lunch",
            "kind": "service",
            "name": "Lunch sitting",
            "aliases": [],
        },
        {
            "resource_id": "dinner",
            "kind": "service",
            "name": "Dinner sitting",
            "aliases": [],
        },
    ],
    "location": [
        {
            "resource_id": "fitzroy",
            "kind": "location",
            "name": "Fitzroy dining room",
            "aliases": [],
        },
        {
            "resource_id": "rooftop",
            "kind": "location",
            "name": "Rooftop terrace",
            "aliases": [],
        },
    ],
}

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


def install_resources_client(*, fail: bool = False) -> None:
    """Install a ToolClient that lists _DECLARED, or fails when *fail*."""

    def handle(request: httpx.Request) -> httpx.Response:
        if fail:
            return httpx.Response(503, content=b'{"status":"unavailable"}')
        kind = request.url.params.get("kind") or "service"
        matches = _DECLARED.get(kind, [])
        return httpx.Response(
            200,
            content=json.dumps(
                {"status": "listed", "matches": matches, "resolved": None}
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


@pytest.fixture(autouse=True)
def _resources_client() -> None:
    install_resources_client()
    yield
    reset_tool_client()


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


def _only_service_missing() -> BookingDraft:
    """A draft whose sole gap is the sitting, so clarify targets ``service_id``.

    The ask order is when, how many, where, service — so anything vaguer than
    this lands on an earlier field.
    """
    return _make_draft(
        operation=OperationType.create,
        starts_at=datetime(2026, 9, 20, 19, 0, tzinfo=UTC),
        party_size=4,
        location_id="fitzroy",
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
    """Second clarify on the same field escalates the action one rung.

    ``service_id`` is a RESOURCE field, so its ladder is offer, offer, human —
    a keypad rung for a value like "dinner" was never usable.
    """
    draft = _only_service_missing()

    # First call: attempt 0 → OFFER_OPTIONS (the tenant's set is closed)
    state1 = _make_state(draft=draft)
    result1 = await clarify(state1)
    assert result1["pending_clarify_action"] == ClarifyAction.OFFER_OPTIONS.value

    field = result1["pending_clarify_field"]

    # Second call: attempt 1 → offer once more before involving a human
    state2 = _make_state(
        draft=draft,
        clarification_attempts={field: 1},
    )
    result2 = await clarify(state2)
    assert result2["pending_clarify_action"] == ClarifyAction.OFFER_OPTIONS.value

    # Third call: attempt 2 → a human, NOT a keypad
    state3 = _make_state(draft=draft, clarification_attempts={field: 2})
    result3 = await clarify(state3)
    assert result3["pending_clarify_action"] == ClarifyAction.ESCALATE_HANDOFF.value


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
def test_clarify_ends_the_turn() -> None:
    """clarify ENDs the turn — it never loops back and never escalates directly.

    The original topology drew clarify → capture_fields.  That edge re-ran the
    extractor over the transcript that had just failed to produce the field, so
    it produced the same draft and returned to clarify until GraphRecursionError.
    A clarifying question can only be answered by the customer, on a later turn.
    """
    from langgraph.graph import END

    from cordia_agent.graph import build_agent_graph

    edges = {(e.source, e.target) for e in build_agent_graph().get_graph().edges}

    assert ("clarify", END) in edges, "clarify → END edge must be present"
    assert ("clarify", "capture_fields") not in edges, (
        "clarify → capture_fields is the cycle this replaced"
    )
    assert ("clarify", "human_handoff") not in edges, (
        "clarify → human_handoff edge must NOT exist (§8.3 topology frozen)"
    )


# ---------------------------------------------------------------------------
# What the caller is actually asked for
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "field,label",
    [
        ("location_id", "location"),
        ("service_id", "service"),
        ("starts_at", "date and time"),
        ("party_size", "number of people"),
    ],
)
def test_field_label_is_speakable(field: str, label: str) -> None:
    """The default underscore-strip asks the caller for "the location id" — a
    column name read down a phone line."""
    assert field_label(field) == label


@pytest.mark.unit
def test_an_unmapped_field_still_gets_a_label() -> None:
    assert field_label("some_new_field") == "some new field"


@pytest.mark.unit
async def test_clarify_never_says_id_to_the_caller() -> None:
    """Guards every rung, not just the first: SPELL_BACK and DTMF interpolate the
    same label, and both were reachable with "location id" in them."""
    draft = _make_draft(
        service_id="dinner",
        starts_at=datetime(2026, 9, 20, 19, 0, tzinfo=UTC),
        party_size=2,
    )

    for attempt in range(4):
        state = _make_state(draft, clarification_attempts={"location_id": attempt})
        prompt = (await clarify(state))["pending_clarify_prompt"]
        # The invariant is the absence of the column name, not the presence of a
        # label: the natural ask ("And whereabouts would you like to sit?")
        # rightly never says "location" at all.
        assert " id" not in prompt
        assert "location_id" not in prompt


@pytest.mark.unit
async def test_the_first_question_is_not_which_room() -> None:
    """Alphabetical order put location_id first, so an empty draft opened with
    "could you tell me the location?" before asking what or when. Asking for the
    service first was also wrong once the sitting became derivable from the time:
    the first thing to establish is WHEN."""
    state = _make_state(_make_draft())

    result = await clarify(state)

    assert result["pending_clarify_field"] == "starts_at"


@pytest.mark.unit
@pytest.mark.parametrize(
    "present,expected",
    [
        ({}, "starts_at"),
        ({"starts_at": datetime(2026, 9, 20, tzinfo=UTC)}, "party_size"),
        (
            {"starts_at": datetime(2026, 9, 20, tzinfo=UTC), "party_size": 4},
            "location_id",
        ),
        (
            {
                "starts_at": datetime(2026, 9, 20, tzinfo=UTC),
                "party_size": 4,
                "location_id": "fitzroy",
            },
            "service_id",
        ),
    ],
)
async def test_required_fields_are_asked_when_how_many_where_then_service(
    present: dict, expected: str
) -> None:
    """The order a person takes a reservation in — and the service last, because
    by then the time usually settles it without asking."""
    state = _make_state(_make_draft(**present))

    assert (await clarify(state))["pending_clarify_field"] == expected


# ---------------------------------------------------------------------------
# The reported call: "what kind of service do you have?" -> keypad
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_the_first_ask_for_a_service_lists_the_services() -> None:
    """The caller cannot guess a vocabulary only the venue knows. The tenant's
    set is closed and published, so read it out."""
    state = _make_state(draft=_only_service_missing())

    prompt = (await clarify(state))["pending_clarify_prompt"]

    assert "Breakfast" in prompt
    assert "Lunch sitting" in prompt
    assert "Dinner sitting" in prompt


@pytest.mark.unit
async def test_a_named_resource_is_never_asked_for_by_keypad() -> None:
    """You cannot type "dinner" on a number pad. Every rung, both resource
    fields — this is the line the reported call actually ended on."""
    for field, present in (
        (
            "service_id",
            {
                "party_size": 2,
                "location_id": "fitzroy",
                "starts_at": datetime(2026, 9, 20, 19, 0, tzinfo=UTC),
            },
        ),
        (
            "location_id",
            {
                "service_id": "dinner",
                "party_size": 2,
                "starts_at": datetime(2026, 9, 20, 19, 0, tzinfo=UTC),
            },
        ),
    ):
        for attempt in range(5):
            state = _make_state(
                draft=_make_draft(operation=OperationType.create, **present),
                clarification_attempts={field: attempt},
            )
            result = await clarify(state)
            assert result["pending_clarify_action"] != ClarifyAction.DTMF.value
            assert "keypad" not in result["pending_clarify_prompt"]
            assert " id" not in result["pending_clarify_prompt"]


@pytest.mark.unit
async def test_asking_what_the_options_are_is_not_a_failed_attempt() -> None:
    """The reported spiral: three reasonable questions, three escalations. A
    question answers nothing but fails at nothing."""
    state = _make_state(
        draft=_only_service_missing(),
        transcript_text="What kind of service do you have?",
        clarification_attempts={"service_id": 0},
    )

    result = await clarify(state)

    assert result["clarification_attempts"]["service_id"] == 0


@pytest.mark.unit
async def test_a_real_non_answer_still_escalates() -> None:
    """The counter must still move for an actual failure to answer, or the
    ladder never reaches a human."""
    state = _make_state(
        draft=_only_service_missing(),
        transcript_text="mmm",
        clarification_attempts={"service_id": 1},
    )

    result = await clarify(state)

    assert result["clarification_attempts"]["service_id"] == 2


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    [
        "What kind of service do you have?",
        "Can you list? Because I don't know any service.",
        "what are my options",
        "like what?",
    ],
)
def test_options_requests_are_recognised(text: str) -> None:
    assert is_options_request(text) is True


@pytest.mark.unit
@pytest.mark.parametrize("text", ["dinner please", "seven pm", "four people"])
def test_a_plain_answer_is_not_an_options_request(text: str) -> None:
    assert is_options_request(text) is False


@pytest.mark.unit
async def test_an_unreachable_tenant_list_asks_openly_rather_than_inventing() -> None:
    """Naming a service the tenant never declared is the same fabrication the
    resolver exists to prevent."""
    install_resources_client(fail=True)
    state = _make_state(draft=_only_service_missing())

    result = await clarify(state)

    assert result["pending_clarify_prompt"] == ASK_PROMPTS["service_id"]
    assert "Breakfast" not in result["pending_clarify_prompt"]


@pytest.mark.unit
async def test_a_keypad_field_still_offers_the_keypad() -> None:
    """Party size IS expressible on a keypad; the rung stays for fields that can
    use it."""
    state = _make_state(
        draft=_make_draft(
            operation=OperationType.create,
            service_id="dinner",
            location_id="fitzroy",
            starts_at=datetime(2026, 9, 20, 19, 0, tzinfo=UTC),
        ),
        clarification_attempts={"party_size": 2},
    )

    result = await clarify(state)

    assert result["pending_clarify_field"] == "party_size"
    assert result["pending_clarify_action"] == ClarifyAction.DTMF.value


# ---------------------------------------------------------------------------
# Listening, not counting
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_answering_a_different_question_is_not_a_failed_attempt() -> None:
    """The caller was asked for a time and said "there'll be six of us". That is
    progress. Counting it as a failure is how the next line became "tap the date
    and time in on your keypad"."""
    state = _make_state(
        draft=_make_draft(operation=OperationType.create, party_size=6),
        transcript_text="There'll be six of us",
        draft_advanced=True,
        clarification_attempts={"starts_at": 2},
    )

    result = await clarify(state)

    assert result["pending_clarify_field"] == "starts_at"
    assert result["clarification_attempts"]["starts_at"] == 2
    assert result["pending_clarify_action"] == ClarifyAction.ASK_AGAIN.value


@pytest.mark.unit
async def test_saying_nothing_usable_still_escalates() -> None:
    """The ladder must still reach a human, or a stuck call never gets one."""
    state = _make_state(
        draft=_make_draft(operation=OperationType.create),
        transcript_text="mmm",
        draft_advanced=False,
        clarification_attempts={"starts_at": 2},
    )

    result = await clarify(state)

    assert result["clarification_attempts"]["starts_at"] == 3


@pytest.mark.unit
async def test_a_date_is_never_asked_for_on_a_keypad() -> None:
    """There is no format the caller could guess, and nobody has ever said this
    to someone booking a table."""
    for attempt in range(5):
        state = _make_state(
            draft=_make_draft(operation=OperationType.create),
            clarification_attempts={"starts_at": attempt},
        )
        result = await clarify(state)

        assert result["pending_clarify_action"] != ClarifyAction.DTMF.value
        assert "keypad" not in result["pending_clarify_prompt"]


@pytest.mark.unit
async def test_a_date_is_never_asked_to_be_spelled_when_none_was_captured() -> None:
    """Spell-back reads a CAPTURED value back. With nothing captured there is
    nothing to spell, and "spell the date and time out" is unanswerable."""
    state = _make_state(
        draft=_make_draft(operation=OperationType.create),
        clarification_attempts={"starts_at": 1},
    )

    prompt = (await clarify(state))["pending_clarify_prompt"]

    assert "spell" not in prompt.lower()


# ---------------------------------------------------------------------------
# Defect 1 — a mutation with no target asks for the booking reference
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_cancel_without_a_target_asks_for_the_reference() -> None:
    """ "I need to cancel my booking" has to become a question, not a dead end.

    target_booking_id was in neither operation's REQUIRED_CANDIDATE_FIELDS, so
    the capture gate passed with no target and clarify was never asked to
    produce this question.
    """
    draft = BookingDraft(
        tenant_id="tenant_clarify_target",
        session_id="sess_clarify_target",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.cancel),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    state = AgentState(
        tenant_id="tenant_clarify_target",
        session_id="sess_clarify_target",
        draft=draft,
        operation=OperationType.cancel,
        transcript_text="I need to cancel my booking",
    )

    result = await clarify(state)

    assert result["pending_clarify_field"] == "target_booking_id"
    assert result["pending_clarify_prompt"] == ASK_PROMPTS["target_booking_id"]
