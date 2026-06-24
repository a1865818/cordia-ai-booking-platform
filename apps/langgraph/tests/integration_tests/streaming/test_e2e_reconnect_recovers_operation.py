"""E2E test: Inv-2 / section 25.1 -- reconnect recovers existing operation without minting.

Wires the REAL ReconnectHandler + ReconnectSessionContext with the REAL
SessionCheckpointer, SpecTwoAgentResumeHook, and fake 04 API transport.

Scenario:
  1. Checkpoint an AgentState carrying a known operation_id + idempotency_key.
  2. Build a ReconnectHandler with:
     - SessionLookup returning the SAME (session_id, correlation_id, tenant_id, turn_id).
     - SpecTwoAgentResumeHook backed by the real SessionCheckpointer +
       get_operation_status_call (the fake API serves the known op_id).
  3. Call attempt_reconnect(channel_ref).
  4. Assert: ReconnectOutcome.RESUMED, recovered_op_id == checkpointed op_id,
     submit_count == 0 (no POST /api/bookings during recovery).
  5. Assert correlation_id continuity: the resumed ReconnectSessionContext
     carries the original correlation_id.

Spec refs: spec-01 section 25.1, spec-02 Inv-2, section 8.9.
"""

from __future__ import annotations

import datetime

import pytest

from cordia_agent.checkpoint import SessionCheckpointer, set_session_checkpointer
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.streaming.providers.fake import (
    FakeHandoffHook,
    FakeSessionLookup,
)
from cordia_agent.streaming.reconnect import (
    ReconnectHandler,
    ReconnectOutcome,
    ReconnectSessionContext,
)
from cordia_agent.streaming.turn_manager import TurnManager
from tests.integration_tests.streaming.conftest import (
    _KNOWN_OP_ID,
    CHANNEL_REF,
    CORRELATION_ID,
    SESSION_ID,
    TENANT_ID,
    TURN_ID,
    SpecTwoAgentResumeHook,
    requires_spec02,
)


# ---------------------------------------------------------------------------
# Guard
# ---------------------------------------------------------------------------

requires_spec02()


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_NOW = datetime.datetime(2026, 6, 25, 10, 0, 0, tzinfo=datetime.UTC)
_KNOWN_IDEM_KEY = "idem_e2e_01i_reconnect"

SESSION_CONTEXT = ReconnectSessionContext(
    session_id=SESSION_ID,
    correlation_id=CORRELATION_ID,
    tenant_id=TENANT_ID,
    turn_id=TURN_ID,
)


def _make_draft() -> BookingDraft:
    return BookingDraft(
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            service_id="svc_reconnect_001",
            location_id="loc_reconnect_001",
            starts_at=datetime.datetime(2026, 7, 10, 9, 0, tzinfo=datetime.UTC),
            party_size=2,
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _build_checkpointed_state(cp: SessionCheckpointer) -> AgentState:
    """Checkpoint an AgentState with the known operation_id + idempotency_key."""
    state = AgentState(
        tenant_id=TENANT_ID,
        session_id=SESSION_ID,
        draft=_make_draft(),
        operation_id=_KNOWN_OP_ID,
        idempotency_key=_KNOWN_IDEM_KEY,
        correlation_id=CORRELATION_ID,
    )
    cp.checkpoint_material_update(state)
    return state


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_reconnect_returns_resumed_outcome(fake_api_handler) -> None:
    """Inv-2: ReconnectHandler returns RESUMED when a session + checkpoint exist."""
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)
    _build_checkpointed_state(cp)

    resume_hook = SpecTwoAgentResumeHook(cp)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
    )

    outcome = await handler.attempt_reconnect(CHANNEL_REF)

    assert outcome == ReconnectOutcome.RESUMED, f"Expected RESUMED; got {outcome!r}"


@pytest.mark.integration
async def test_reconnect_recovers_existing_operation_id(fake_api_handler) -> None:
    """Inv-2: resume hook recovers the EXISTING operation_id -- no new mint."""
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)
    _build_checkpointed_state(cp)

    resume_hook = SpecTwoAgentResumeHook(cp)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
    )

    await handler.attempt_reconnect(CHANNEL_REF)

    assert resume_hook.recovered_op_id == _KNOWN_OP_ID, (
        f"Inv-2: expected recovered_op_id={_KNOWN_OP_ID!r}; "
        f"got {resume_hook.recovered_op_id!r}"
    )


@pytest.mark.integration
async def test_reconnect_does_not_submit_post_booking(fake_api_handler) -> None:
    """Inv-2: reconnect recovery must NOT call POST /api/bookings (no new operation minted)."""
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)
    _build_checkpointed_state(cp)

    resume_hook = SpecTwoAgentResumeHook(cp)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
    )

    await handler.attempt_reconnect(CHANNEL_REF)

    post_count = fake_api_handler.submit_count
    assert post_count == 0, (
        f"Inv-2: reconnect recovery must NOT submit; got submit_count={post_count}"
    )


@pytest.mark.integration
async def test_reconnect_correlation_id_continuity(fake_api_handler) -> None:
    """Section 25.1: ReconnectSessionContext.correlation_id after reconnect == original."""
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)
    _build_checkpointed_state(cp)

    resume_hook = SpecTwoAgentResumeHook(cp)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
    )

    await handler.attempt_reconnect(CHANNEL_REF)

    assert handler.session_context is not None
    assert handler.session_context.correlation_id == CORRELATION_ID, (
        f"Section 25.1: correlation_id must be continuous across reconnect; "
        f"got {handler.session_context.correlation_id!r}"
    )


@pytest.mark.integration
async def test_resume_hook_context_carries_original_ids(fake_api_handler) -> None:
    """The context passed to the resume hook carries the original session + tenant ids."""
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)
    _build_checkpointed_state(cp)

    resume_hook = SpecTwoAgentResumeHook(cp)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
    )

    await handler.attempt_reconnect(CHANNEL_REF)

    ctx = resume_hook.resume_context
    assert ctx is not None
    assert ctx.session_id == SESSION_ID
    assert ctx.tenant_id == TENANT_ID
    assert ctx.correlation_id == CORRELATION_ID


@pytest.mark.integration
async def test_reconnect_no_checkpoint_returns_resumed_no_op_id(
    fake_api_handler,
) -> None:
    """Inv-2: when no checkpoint exists, resume_hook returns None (no op to recover).

    The ReconnectHandler still returns RESUMED (it is the session lookup that
    controls the outcome, not the resume hook return value).
    """
    cp = SessionCheckpointer()  # empty -- no checkpoint
    set_session_checkpointer(cp)

    resume_hook = SpecTwoAgentResumeHook(cp)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
    )

    outcome = await handler.attempt_reconnect(CHANNEL_REF)

    # Still RESUMED (session was found); but no op_id recovered
    assert outcome == ReconnectOutcome.RESUMED
    assert resume_hook.recovered_op_id is None
    assert fake_api_handler.submit_count == 0
