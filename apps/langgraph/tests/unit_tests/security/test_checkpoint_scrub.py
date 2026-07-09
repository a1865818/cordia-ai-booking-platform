"""Unit tests for the spec-14 checkpoint transcript rules (Inv-12, §5.2)."""

from __future__ import annotations

import pytest

from cordia_agent.checkpoint import SessionCheckpointer
from cordia_agent.state import AgentState


VISA_PAN = "4111111111111111"


def _state(**overrides: object) -> AgentState:
    kwargs: dict = {"tenant_id": "tenant_a", "session_id": "sess_1"}
    kwargs.update(overrides)
    return AgentState(**kwargs)


@pytest.mark.unit
def test_transcript_dropped_when_recording_not_allowed() -> None:
    cp = SessionCheckpointer()
    state = _state(transcript_text="book a table for two", recording_allowed=False)
    cp.save(state)
    stored = cp.load("tenant_a", "sess_1")
    assert stored is not None
    assert stored.transcript_text is None
    # The in-memory state keeps the live turn transcript.
    assert state.transcript_text == "book a table for two"


@pytest.mark.unit
def test_transcript_redacted_when_recording_allowed() -> None:
    cp = SessionCheckpointer()
    state = _state(
        transcript_text=f"my card is {VISA_PAN}",
        recording_allowed=True,
    )
    cp.save(state)
    stored = cp.load("tenant_a", "sess_1")
    assert stored is not None
    assert VISA_PAN not in (stored.transcript_text or "")
    assert "[REDACTED]" in (stored.transcript_text or "")
    assert state.transcript_text == f"my card is {VISA_PAN}"


@pytest.mark.unit
def test_transcript_redacted_even_before_consent_resolves() -> None:
    """recording_allowed=None (gate not run) still never persists a PAN (Inv-12)."""
    cp = SessionCheckpointer()
    state = _state(transcript_text=f"pan {VISA_PAN}")
    cp.save(state)
    stored = cp.load("tenant_a", "sess_1")
    assert stored is not None
    assert VISA_PAN not in (stored.transcript_text or "")


@pytest.mark.unit
def test_clean_transcript_round_trips_unchanged() -> None:
    cp = SessionCheckpointer()
    state = _state(transcript_text="a table for four at seven", recording_allowed=True)
    cp.save(state)
    stored = cp.load("tenant_a", "sess_1")
    assert stored is not None
    assert stored.transcript_text == "a table for four at seven"


@pytest.mark.unit
def test_pan_in_any_captured_field_is_scrubbed_not_just_transcript() -> None:
    """Customer speech lands in captured free-text fields too — a PAN spoken
    into a special request must never be durably stored (Inv-12)."""
    from cordia_agent.state import (
        BookingDraft,
        BookingRequest,
        Channel,
        OperationType,
        SourceInfo,
    )

    cp = SessionCheckpointer()
    draft = BookingDraft(
        tenant_id="tenant_a",
        session_id="sess_1",
        schema_version="v1",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            special_requests=[f"put it on card {VISA_PAN}"],
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    state = _state(draft=draft, recording_allowed=True)
    cp.save(state)
    stored = cp.load("tenant_a", "sess_1")
    assert stored is not None
    assert stored.draft is not None
    assert VISA_PAN not in stored.draft.request.special_requests[0]
    assert "[REDACTED]" in stored.draft.request.special_requests[0]
