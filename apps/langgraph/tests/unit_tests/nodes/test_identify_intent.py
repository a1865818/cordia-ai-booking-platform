"""Unit tests for the identify_intent node and classify_intent function (02-F).

Verifies representative utterances → each of create/reschedule/modify/cancel/inquire.
"""

from __future__ import annotations

import pytest

from cordia_agent.nodes.identify_intent import classify_intent, identify_intent
from cordia_agent.state import AgentState, DialogIntent, OperationType


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
