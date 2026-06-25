"""E2E test: Inv-1 committability -- partial transcripts must never mutate the draft.

Wires the REAL capture_fields node with REAL slot-capture engine (a deterministic
fake extractor registered for the test) and asserts:

  Inv-1a: TranscriptPartialFrame (transcript_is_final=False) -> capture_fields ->
          draft UNCHANGED, ZERO tool calls on the fake API transport.

  Inv-1b: TranscriptFinalFrame (transcript_is_final=True) -> capture_fields ->
          draft IS mutated (slot applied).

  Envelope continuity: partial and final frames share the same correlation_id.

Spec refs: spec-01 section 6.3, spec-02 Inv-1, section 8.4.
"""

from __future__ import annotations

import pytest

from cordia_agent.nodes.capture_fields import capture_fields
from cordia_agent.slots.capture import SlotCapture, register_slot_extractor
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    CaptureMethod,
    Channel,
    OperationType,
    SourceInfo,
)
from tests.integration_tests.streaming.conftest import (
    CORRELATION_ID,
    SESSION_ID,
    TENANT_ID,
    make_final_frame,
    make_partial_frame,
    requires_spec02,
)


# ---------------------------------------------------------------------------
# Guard
# ---------------------------------------------------------------------------

requires_spec02()


# ---------------------------------------------------------------------------
# Deterministic fake slot extractor
# ---------------------------------------------------------------------------


class _ServiceIdExtractor:
    """Always returns a single SlotCapture for service_id = 'svc_test_001'."""

    def extract(self, text: str) -> list[SlotCapture]:
        if not text:
            return []
        return [
            SlotCapture(
                field="service_id",
                value="svc_test_001",
                capture_method=CaptureMethod.speech,
                stt_confidence=0.9,
                model_confidence=0.85,
                normalized_value="svc_test_001",
            )
        ]


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _make_draft() -> BookingDraft:
    return BookingDraft(
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _make_state(
    *,
    transcript_text: str,
    transcript_is_final: bool,
    draft: BookingDraft | None = None,
) -> AgentState:
    return AgentState(
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        draft=draft or _make_draft(),
        transcript_text=transcript_text,
        transcript_is_final=transcript_is_final,
        current_turn_id="turn_e2e_01i",
        correlation_id=CORRELATION_ID,
    )


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_partial_frame_causes_no_draft_mutation(fake_api_handler) -> None:
    """Inv-1a: TranscriptPartialFrame -> capture_fields -> draft unchanged, ZERO tool calls."""
    register_slot_extractor(_ServiceIdExtractor())

    partial = make_partial_frame("I want a haircut")
    state = _make_state(
        transcript_text=partial.text,
        transcript_is_final=False,
    )
    original_request_json = state.draft.request.model_dump_json()

    result = await capture_fields(state)

    # Draft must be unchanged (or absent from result dict -- both are acceptable)
    updated_draft = result.get("draft")
    if updated_draft is not None:
        assert updated_draft.request.model_dump_json() == original_request_json, (
            "Inv-1: partial transcript must not mutate draft; "
            f"got service_id={updated_draft.request.service_id!r}"
        )

    # No tool calls: POST /api/bookings count must be 0
    post_calls = [r for r in fake_api_handler.requests if r.method.upper() == "POST"]
    assert len(post_calls) == 0, (
        f"Inv-1: partial transcript must not trigger any tool calls; "
        f"got {len(post_calls)} POST request(s)"
    )


@pytest.mark.integration
async def test_final_frame_mutates_draft(fake_api_handler) -> None:
    """Inv-1b: TranscriptFinalFrame -> capture_fields -> draft IS mutated."""
    register_slot_extractor(_ServiceIdExtractor())

    final = make_final_frame("I want a haircut")
    state = _make_state(
        transcript_text=final.text,
        transcript_is_final=True,
    )

    result = await capture_fields(state)

    updated_draft = result.get("draft")
    assert updated_draft is not None, (
        "Inv-1b: final transcript should produce an updated draft"
    )
    assert updated_draft.request.service_id == "svc_test_001", (
        f"Inv-1b: draft must reflect captured slot; "
        f"got service_id={updated_draft.request.service_id!r}"
    )


@pytest.mark.integration
async def test_partial_and_final_frames_share_correlation_id() -> None:
    """Envelope continuity: partial and final frames carry the same correlation_id."""
    partial = make_partial_frame("I want a haircut")
    final = make_final_frame("I want a haircut")

    assert partial.correlation_id == CORRELATION_ID, (
        f"partial.correlation_id mismatch: {partial.correlation_id!r}"
    )
    assert final.correlation_id == CORRELATION_ID, (
        f"final.correlation_id mismatch: {final.correlation_id!r}"
    )
    assert partial.correlation_id == final.correlation_id, (
        "Envelope continuity: partial and final frames must share correlation_id"
    )


@pytest.mark.integration
async def test_partial_frame_followed_by_final_frame_mutates_once(
    fake_api_handler,
) -> None:
    """Inv-1 sequence: partial -> no mutation, final -> mutation. One clean pass."""
    register_slot_extractor(_ServiceIdExtractor())

    draft = _make_draft()

    # Step 1: partial -- no mutation
    partial_state = _make_state(
        transcript_text="I want",
        transcript_is_final=False,
        draft=draft,
    )
    partial_result = await capture_fields(partial_state)
    after_partial = partial_result.get("draft", draft)
    assert after_partial.request.service_id is None, (
        f"After partial: service_id should still be None; "
        f"got {after_partial.request.service_id!r}"
    )

    # Step 2: final -- mutation
    final_state = _make_state(
        transcript_text="I want a haircut",
        transcript_is_final=True,
        draft=after_partial,
    )
    final_result = await capture_fields(final_state)
    after_final = final_result.get("draft")
    assert after_final is not None
    assert after_final.request.service_id == "svc_test_001", (
        f"After final: service_id should be 'svc_test_001'; "
        f"got {after_final.request.service_id!r}"
    )

    # Throughout: no POSTs to the booking API
    post_calls = [r for r in fake_api_handler.requests if r.method.upper() == "POST"]
    assert len(post_calls) == 0
