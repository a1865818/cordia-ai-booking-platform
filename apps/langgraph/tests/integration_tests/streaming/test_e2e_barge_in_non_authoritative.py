"""E2E test: Inv-16 / section 8.9 -- barge-in interrupted read-back is never authoritative.

Wires the REAL BargeInController + TurnManager + TTSChunker (streaming components)
with the REAL await_confirmation node (agent component).

Scenario:
  1. Render a read-back (set read_back_version + a current_revision on agent state).
  2. Use BargeInController.caller_speech_started to interrupt it -- emits
     BargeInFrame + TurnCancelledFrame (material_content_delivered=False).
  3. Map the TurnCancelledFrame -> read_back_interrupted=True on agent state (the
     streaming->agent contract per section 8.9).
  4. Feed an explicit final "yes" via await_confirmation.
  5. Assert NO Confirmation is recorded (interrupted read-back is never authoritative).
  6. Assert the barge-in / turn.cancelled frames carry the session's correlation_id.

Spec refs: spec-01 section 8.9, spec-02 Inv-16, section 8.9.
"""

from __future__ import annotations

import datetime

import pytest

from cordia_agent.nodes.await_confirmation import await_confirmation
from cordia_agent.revisions.manager import build_revision
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
    StreamFrameType,
)
from cordia_agent.streaming import BargeInController, TurnManager
from cordia_agent.streaming.providers.fake_tts import FakeTTSProvider
from cordia_agent.streaming.tts_chunker import TTSChunker
from cordia_agent.streaming_models import AgentTextDeltaFrame
from tests.integration_tests.streaming.conftest import (
    _TS,
    CORRELATION_ID,
    SESSION_ID,
    TENANT_ID,
    TURN_ID,
    make_partial_frame,
    requires_spec02,
)


# ---------------------------------------------------------------------------
# Guard
# ---------------------------------------------------------------------------

requires_spec02()


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_READ_BACK_VERSION = "rbv_e2e_01i_hash"
_READ_BACK_TEXT = "I'd like to book a haircut at 10am. Can you confirm?"
_NOW = datetime.datetime(2026, 6, 25, 10, 0, 0, tzinfo=datetime.UTC)


def _make_draft() -> BookingDraft:
    return BookingDraft(
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


# A VALID IntentRevision (built via the real revision manager) so await_confirmation
# can parse it and read its payload_hash — required for the positive control below.
_FAKE_REVISION = build_revision(
    intent_id="intent_e2e_01i",
    revision=1,
    canonical_payload=_make_draft().request,
    schema_version="1.0.0",
    tenant_policy_version="v1",
    source_session_id=SESSION_ID,
    created_at=_NOW,
).model_dump(mode="json")


def _make_interrupted_state(transcript_text: str, *, is_final: bool) -> AgentState:
    """Build agent state simulating a barge-in-interrupted read-back."""
    return AgentState(
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        draft=_make_draft(),
        read_back_version=_READ_BACK_VERSION,
        read_back_text=_READ_BACK_TEXT,
        read_back_interrupted=True,  # section 8.9: interrupted flag set by streaming layer
        disclosures_presented=["disclosure_01i"],  # consent gate passes
        current_revision=_FAKE_REVISION,
        transcript_text=transcript_text,
        transcript_is_final=is_final,
        current_turn_id=TURN_ID,
        correlation_id=CORRELATION_ID,
    )


def _make_agent_delta(delta: str, *, seq: int = 0) -> AgentTextDeltaFrame:
    return AgentTextDeltaFrame(
        type=StreamFrameType.AGENT_TEXT_DELTA,
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        turn_id=TURN_ID,
        correlation_id=CORRELATION_ID,
        causation_id=None,
        seq=seq,
        ts=_TS,
        delta=delta,
    )


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_barge_in_emits_correct_frame_types() -> None:
    """BargeInController.caller_speech_started emits BargeInFrame + TurnCancelledFrame."""
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=1),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    # Simulate an active TTS turn by processing one delta frame (starts synthesis)
    synthesis = chunker.process_frame(_make_agent_delta(_READ_BACK_TEXT))
    await anext(synthesis)  # consume first audio frame to activate the turn

    # Trigger barge-in with a caller partial
    caller_partial = make_partial_frame("wait")
    result = controller.caller_speech_started(caller_partial)

    assert len(result.frames) == 2, (
        f"Expected 2 frames (BargeIn + TurnCancelled); got {len(result.frames)}"
    )
    assert result.frames[0].type == StreamFrameType.BARGE_IN_DETECTED
    assert result.frames[1].type == StreamFrameType.TURN_CANCELLED
    assert result.cancelled_turn_id == TURN_ID


@pytest.mark.integration
async def test_barge_in_turn_cancelled_is_non_authoritative() -> None:
    """Section 8.9: TurnCancelledFrame with material_content_delivered=False -> non-authoritative."""
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=1),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_make_agent_delta(_READ_BACK_TEXT))
    await anext(synthesis)

    caller_partial = make_partial_frame("wait")
    result = controller.caller_speech_started(caller_partial)

    cancellation = result.frames[1]
    assert cancellation.material_content_delivered is False
    assert not turn_manager.is_authoritative_turn(TURN_ID), (
        "After barge-in, the cancelled turn must be non-authoritative"
    )


@pytest.mark.integration
async def test_await_confirmation_records_no_confirmation_when_interrupted() -> None:
    """Inv-16: interrupted read-back -> await_confirmation returns no Confirmation
    even with an explicit final 'yes'.
    """
    # State with read_back_interrupted=True and explicit "yes" transcript
    state = _make_interrupted_state("yes confirm please", is_final=True)

    result = await await_confirmation(state)

    # Must NOT record a Confirmation
    confirmation = result.get("confirmation")
    assert confirmation is None, (
        "Inv-16: barge-in interrupted read-back must NEVER produce a Confirmation; "
        f"got: {confirmation!r}"
    )


@pytest.mark.integration
async def test_positive_control_not_interrupted_records_confirmation() -> None:
    """Positive control: the SAME state with read_back_interrupted=False (and a valid
    revision + disclosures + explicit final 'yes') DOES record a Confirmation.

    This proves the no-confirm in the interrupted test is CAUSED by the interrupt
    (Inv-16/§8.9), not by an invalid revision or a missing gate elsewhere.
    """
    state = _make_interrupted_state("yes confirm please", is_final=True)
    state = state.model_copy(update={"read_back_interrupted": False})

    result = await await_confirmation(state)

    confirmation = result.get("confirmation")
    assert confirmation is not None, (
        "Positive control: a non-interrupted, consented, explicit final confirmation "
        "MUST record a Confirmation (else the interrupt test is vacuous)"
    )
    # The recorded confirmation must bind the revision's payload_hash (§8.8).
    assert confirmation["intent_payload_hash"] == _FAKE_REVISION["payload_hash"]


@pytest.mark.integration
async def test_await_confirmation_not_interrupted_no_disclosures_no_confirmation() -> (
    None
):
    """Sanity: read_back_interrupted=False but no disclosures -> no confirmation (consent gate)."""
    state = AgentState(
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        draft=_make_draft(),
        read_back_version=_READ_BACK_VERSION,
        read_back_interrupted=False,
        disclosures_presented=[],  # consent NOT satisfied
        current_revision=_FAKE_REVISION,
        transcript_text="yes",
        transcript_is_final=True,
        current_turn_id=TURN_ID,
        correlation_id=CORRELATION_ID,
    )

    result = await await_confirmation(state)

    # consent gate fails -> no confirmation
    assert result.get("confirmation") is None


@pytest.mark.integration
async def test_barge_in_frames_carry_session_correlation_id() -> None:
    """Correlation continuity: BargeIn and TurnCancelled frames carry the session correlation_id."""
    turn_manager = TurnManager()
    chunker = TTSChunker(
        provider=FakeTTSProvider(refs_per_text_chunk=1),
        turn_manager=turn_manager,
    )
    controller = BargeInController(tts_chunker=chunker, turn_manager=turn_manager)

    synthesis = chunker.process_frame(_make_agent_delta(_READ_BACK_TEXT))
    await anext(synthesis)

    caller_partial = make_partial_frame("wait")
    result = controller.caller_speech_started(caller_partial)

    for frame in result.frames:
        assert frame.correlation_id == CORRELATION_ID, (
            f"Frame {frame.type.value} carries wrong correlation_id: "
            f"{frame.correlation_id!r} != {CORRELATION_ID!r}"
        )


@pytest.mark.integration
async def test_interrupted_read_back_explicit_yes_partial_no_confirmation() -> None:
    """Inv-1 + Inv-16 combined: partial 'yes' on interrupted read-back -> no confirmation."""
    state = _make_interrupted_state("yes", is_final=False)  # partial, not final

    result = await await_confirmation(state)

    assert result.get("confirmation") is None, (
        "Partial transcript on interrupted read-back must not produce Confirmation"
    )
