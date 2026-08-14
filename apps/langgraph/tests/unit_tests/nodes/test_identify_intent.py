"""Unit tests for the identify_intent node and classify_intent function (02-F).

Verifies representative utterances → each of create/reschedule/modify/cancel/inquire.
"""

from __future__ import annotations

import pytest

from cordia_agent.nodes.identify_intent import classify_intent, identify_intent
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    CustomerRef,
    DialogIntent,
    OperationType,
    SourceInfo,
)


# ---------------------------------------------------------------------------
# classify_intent (pure function tests)
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "text,expected",
    [
        # create
        ("I'd like to book a table for two", DialogIntent.create),
        ("Can I reserve an appointment?", DialogIntent.create),
        ("I need to make a reservation", DialogIntent.create),
        # reschedule
        ("I need to reschedule my appointment", DialogIntent.reschedule),
        ("Can you move my booking to tomorrow?", DialogIntent.reschedule),
        ("I'd like to change time for my booking", DialogIntent.reschedule),
        ("Is there a different time available?", DialogIntent.reschedule),
        # modify
        ("I want to modify the party size", DialogIntent.modify),
        ("Can I update the booking details?", DialogIntent.modify),
        ("I'd like to edit my reservation", DialogIntent.modify),
        # cancel
        ("I want to cancel my booking", DialogIntent.cancel),
        ("Please process a cancellation for me", DialogIntent.cancel),
        ("I need to cancel", DialogIntent.cancel),
        # inquire
        ("What are your opening hours?", DialogIntent.inquire),
        ("When is the next available slot?", DialogIntent.inquire),
        ("Do you have appointments on Monday?", DialogIntent.inquire),
        ("Is the spa open on weekends?", DialogIntent.inquire),
        ("How long does a haircut take?", DialogIntent.inquire),
    ],
)
def test_classify_intent_representative_utterances(
    text: str, expected: DialogIntent
) -> None:
    """Each representative utterance maps to the expected DialogIntent."""
    result = classify_intent(text)
    assert result == expected, (
        f"classify_intent({text!r}) → {result!r}, expected {expected!r}"
    )


@pytest.mark.unit
def test_classify_intent_cancel_precedes_reschedule() -> None:
    """'cancel' takes precedence over 'reschedule' in the same utterance."""
    # "cancel my reschedule" — cancel keyword appears; cancel has higher precedence.
    result = classify_intent("cancel my reschedule request")
    assert result == DialogIntent.cancel


@pytest.mark.unit
def test_classify_intent_reschedule_precedes_modify() -> None:
    """'reschedule' takes precedence over 'change' (modify keyword)."""
    result = classify_intent("I want to reschedule and change the time")
    assert result == DialogIntent.reschedule


@pytest.mark.unit
def test_classify_intent_empty_text_returns_inquire() -> None:
    """Empty text falls through to the default: inquire."""
    result = classify_intent("")
    assert result == DialogIntent.inquire


@pytest.mark.unit
def test_classify_intent_case_insensitive() -> None:
    """Classification is case-insensitive."""
    assert classify_intent("CANCEL MY BOOKING") == DialogIntent.cancel
    assert classify_intent("Book A Table") == DialogIntent.create
    assert classify_intent("RESCHEDULE please") == DialogIntent.reschedule


# ---------------------------------------------------------------------------
# identify_intent node (async)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_identify_intent_sets_dialog_intent() -> None:
    """identify_intent sets dialog_intent on the returned update dict."""
    state = AgentState(transcript_text="I'd like to book a table")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.create


@pytest.mark.unit
async def test_identify_intent_sets_operation_for_booking_intents() -> None:
    """For booking intents, identify_intent also sets the operation."""
    state = AgentState(transcript_text="I need to cancel my appointment")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.cancel
    assert result["operation"] == OperationType.cancel


@pytest.mark.unit
async def test_identify_intent_does_not_set_operation_for_inquire() -> None:
    """For inquire, identify_intent does NOT set operation (None means no booking op)."""
    state = AgentState(transcript_text="What are your opening hours?")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.inquire
    assert "operation" not in result


@pytest.mark.unit
async def test_identify_intent_reschedule_sets_correct_operation() -> None:
    """reschedule intent sets OperationType.reschedule on state."""
    state = AgentState(transcript_text="I want to reschedule my booking")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.reschedule
    assert result["operation"] == OperationType.reschedule


@pytest.mark.unit
async def test_identify_intent_handles_none_transcript() -> None:
    """identify_intent handles None transcript_text gracefully."""
    state = AgentState(transcript_text=None)
    result = await identify_intent(state)
    # Empty text defaults to inquire
    assert result["dialog_intent"] == DialogIntent.inquire


# ---------------------------------------------------------------------------
# Fix C — identify_intent builds an initial BookingDraft when draft is None
#          and session bootstrap fields are present
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_identify_intent_builds_initial_draft_when_session_ready() -> None:
    """Fix C: when draft is None and source/tenant_id/session_id are set, an initial
    BookingDraft is constructed with request.operation == OperationType.create.
    """
    state = AgentState(
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
        tenant_id="tenant_fix_c",
        session_id="sess_fix_c",
        tenant_policy_version="v1",
        draft=None,
        transcript_text="I'd like to book a table",
    )

    result = await identify_intent(state)

    draft = result.get("draft")
    assert draft is not None, (
        "Fix C: identify_intent must construct an initial BookingDraft "
        "when source/tenant_id/session_id are present and draft is None"
    )
    assert isinstance(draft, BookingDraft)
    assert draft.request.operation == OperationType.create, (
        f"Fix C: initial draft.request.operation must be create; got {draft.request.operation!r}"
    )


@pytest.mark.unit
async def test_identify_intent_puts_the_customer_on_the_request_not_only_the_draft() -> (
    None
):
    """The revision is built from ``draft.request`` (read_back passes it straight to
    build_revision as the canonical payload), so a customer kept only on the draft never
    reaches the hashed payload the API receives — the booking would land with no
    notification destination and be unfindable by phone. identify_intent is the only site
    that can set it.
    """
    customer = CustomerRef(customer_id="cust_fix", display_name="A. Guest")
    state = AgentState(
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
        tenant_id="tenant_fix_c",
        session_id="sess_fix_c",
        tenant_policy_version="v1",
        draft=None,
        customer=customer,
        transcript_text="I'd like to book a table",
    )

    result = await identify_intent(state)

    draft = result.get("draft")
    assert draft is not None
    assert draft.request.customer == customer, (
        "the customer must ride on draft.request (the hashed canonical payload), "
        f"not only on the draft; got {draft.request.customer!r}"
    )
    assert draft.customer == customer


@pytest.mark.unit
async def test_identify_intent_defers_draft_when_source_missing() -> None:
    """Fix C control: when source is None, no initial draft is built (deferred)."""
    state = AgentState(
        source=None,
        tenant_id="tenant_fix_c",
        session_id="sess_fix_c",
        draft=None,
        transcript_text="I'd like to book a table",
    )

    result = await identify_intent(state)

    # dialog_intent and operation are still set; draft must be absent.
    assert result["dialog_intent"] == DialogIntent.create
    assert "draft" not in result, (
        "Fix C control: with source=None the draft key must not appear in the result"
    )


# ---------------------------------------------------------------------------
# Fix E — identify_intent revalidates the draft's request on reclassify;
#          an invalid mutation (no target_booking_id) is rejected, leaving the
#          existing draft untouched
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_identify_intent_rejects_reclassify_to_cancel_without_target() -> None:
    """Fix E: reclassifying an existing create draft to cancel without a
    target_booking_id must be rejected — the result must NOT contain a new draft
    with operation=cancel, and must not raise.

    The result is either absent (no "draft" key) or the draft's operation is still
    create (the invalid reclassify was rejected and the draft left untouched).
    """
    existing_draft = BookingDraft(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )

    state = AgentState(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        draft=existing_draft,
        transcript_text="cancel my booking",
    )

    result = await identify_intent(state)

    # Must not propagate an invalid cancel draft.
    returned_draft = result.get("draft")
    if returned_draft is not None:
        assert returned_draft.request.operation == OperationType.create, (
            "Fix E: reclassify to cancel without target_booking_id must be rejected; "
            f"got operation={returned_draft.request.operation!r}"
        )


@pytest.mark.unit
async def test_identify_intent_reclassifies_cancel_when_target_present() -> None:
    """Fix E positive control: reclassify succeeds when target_booking_id is set.

    An existing create draft WITH target_booking_id triggers the revalidation path;
    the result draft's operation must be cancel (revalidation passes).
    """
    existing_draft = BookingDraft(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            target_booking_id="bk_existing_001",
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )

    state = AgentState(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        draft=existing_draft,
        transcript_text="cancel my booking",
    )

    result = await identify_intent(state)

    returned_draft = result.get("draft")
    assert returned_draft is not None, (
        "Fix E positive: reclassify to cancel with target_booking_id must succeed "
        "and return the updated draft"
    )
    assert returned_draft.request.operation == OperationType.cancel, (
        f"Fix E positive: expected operation=cancel; got {returned_draft.request.operation!r}"
    )
