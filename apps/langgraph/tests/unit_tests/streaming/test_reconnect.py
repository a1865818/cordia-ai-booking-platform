"""Unit tests for the streaming reconnect / session re-binding handler."""

from __future__ import annotations

from inspect import getsource

import pytest

import cordia_agent.streaming.reconnect as reconnect_module
from cordia_agent.contracts.operation_status import NormalizedOperationStatus

# --- Protocols are tested via fakes imported from providers ---
from cordia_agent.streaming.providers import (
    FakeAgentResumeHook,
    FakeHandoffHook,
    FakeSessionLookup,
)
from cordia_agent.streaming.reconnect import (
    ReconnectHandler,
    ReconnectOutcome,
    ReconnectSessionContext,
)
from cordia_agent.streaming.turn_manager import TurnManager


# ---------------------------------------------------------------------------
# Shared fixtures
# ---------------------------------------------------------------------------

SESSION_CONTEXT = ReconnectSessionContext(
    session_id="session_1",
    correlation_id="correlation_1",
    tenant_id="tenant_1",
    turn_id="turn_1",
)


def _handler(
    *,
    session_context: ReconnectSessionContext | None = SESSION_CONTEXT,
    turn_manager: TurnManager | None = None,
    max_reconnect_attempts: int = 3,
    resume_status: NormalizedOperationStatus | None = None,
) -> ReconnectHandler:
    """Build a ReconnectHandler with deterministic fakes."""
    return ReconnectHandler(
        session_lookup=FakeSessionLookup(result=session_context),
        agent_resume_hook=FakeAgentResumeHook(result=resume_status),
        handoff_hook=FakeHandoffHook(),
        turn_manager=turn_manager or TurnManager(),
        max_reconnect_attempts=max_reconnect_attempts,
    )


# ---------------------------------------------------------------------------
# Happy-path: successful reconnect
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_successful_reconnect_returns_resumed_outcome() -> None:
    handler = _handler()
    outcome = await handler.attempt_reconnect(channel_ref="ch_1")
    assert outcome == ReconnectOutcome.RESUMED


@pytest.mark.unit
async def test_reconnect_preserves_session_id() -> None:
    handler = _handler()
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.session_context is not None
    assert handler.session_context.session_id == "session_1"


@pytest.mark.unit
async def test_reconnect_preserves_correlation_id() -> None:
    handler = _handler()
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.session_context is not None
    assert handler.session_context.correlation_id == "correlation_1"


@pytest.mark.unit
async def test_reconnect_preserves_tenant_id() -> None:
    handler = _handler()
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.session_context is not None
    assert handler.session_context.tenant_id == "tenant_1"


@pytest.mark.unit
async def test_reconnect_never_mints_new_session_id() -> None:
    handler = _handler()
    await handler.attempt_reconnect(channel_ref="ch_1")
    # The session_id must be the SAME as what the lookup returned, not a new UUID
    assert handler.session_context is not None
    assert handler.session_context.session_id == SESSION_CONTEXT.session_id


# ---------------------------------------------------------------------------
# Seq continuity via TurnManager
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_seq_continues_from_existing_turn_manager() -> None:
    tm = TurnManager()
    # Simulate 5 frames already processed before drop
    for _ in range(5):
        tm.next_seq("session_1")

    handler = _handler(turn_manager=tm)
    await handler.attempt_reconnect(channel_ref="ch_1")
    # Post-reconnect, next seq should be 5 (continuing, not reset to 0)
    assert tm.next_seq("session_1") == 5


@pytest.mark.unit
async def test_reconnect_does_not_reset_seq_counter() -> None:
    tm = TurnManager()
    tm.next_seq("session_1")  # seq 0
    tm.next_seq("session_1")  # seq 1

    handler = _handler(turn_manager=tm)
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert tm.next_seq("session_1") == 2


# ---------------------------------------------------------------------------
# In-flight turn: never finalize
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_reconnect_does_not_finalize_in_flight_turn() -> None:
    tm = TurnManager()
    handler = _handler(turn_manager=tm)
    await handler.attempt_reconnect(channel_ref="ch_1")
    # turn_1 should still be authoritative (not cancelled/finalized by reconnect)
    assert tm.is_authoritative_turn("turn_1")


# ---------------------------------------------------------------------------
# Agent resume hook
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_agent_resume_hook_called_with_session_context() -> None:
    resume_hook = FakeAgentResumeHook(result=None)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
        max_reconnect_attempts=3,
    )
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert resume_hook.called_with == SESSION_CONTEXT


@pytest.mark.unit
async def test_agent_resume_hook_receives_preserved_ids() -> None:
    resume_hook = FakeAgentResumeHook(result=None)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
        max_reconnect_attempts=3,
    )
    await handler.attempt_reconnect(channel_ref="ch_1")
    ctx = resume_hook.called_with
    assert ctx is not None
    assert ctx.session_id == "session_1"
    assert ctx.correlation_id == "correlation_1"


@pytest.mark.unit
async def test_resume_hook_never_mints_operation_id() -> None:
    """Inv-2: pipeline preserves IDs but never creates operation_id."""
    resume_hook = FakeAgentResumeHook(result=None)
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=SESSION_CONTEXT),
        agent_resume_hook=resume_hook,
        handoff_hook=FakeHandoffHook(),
        turn_manager=TurnManager(),
        max_reconnect_attempts=3,
    )
    await handler.attempt_reconnect(channel_ref="ch_1")
    # The context passed to the resume hook should have no operation_id
    ctx = resume_hook.called_with
    assert ctx is not None
    assert not hasattr(ctx, "operation_id")


# ---------------------------------------------------------------------------
# Failure tracking and handoff
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_session_not_found_returns_failed() -> None:
    handler = _handler(session_context=None)
    outcome = await handler.attempt_reconnect(channel_ref="ch_unknown")
    assert outcome == ReconnectOutcome.FAILED


@pytest.mark.unit
async def test_failure_counter_increments_on_failed_reconnect() -> None:
    handler = _handler(session_context=None)
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.consecutive_failures == 1
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.consecutive_failures == 2


@pytest.mark.unit
async def test_successful_reconnect_resets_failure_counter() -> None:
    handler = _handler(session_context=None, max_reconnect_attempts=5)
    await handler.attempt_reconnect(channel_ref="ch_1")
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.consecutive_failures == 2

    # Now fix the session lookup to succeed
    handler._session_lookup = FakeSessionLookup(result=SESSION_CONTEXT)
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.consecutive_failures == 0


@pytest.mark.unit
async def test_handoff_signalled_after_max_failures() -> None:
    handoff = FakeHandoffHook()
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=None),
        agent_resume_hook=FakeAgentResumeHook(result=None),
        handoff_hook=handoff,
        turn_manager=TurnManager(),
        max_reconnect_attempts=3,
    )
    await handler.attempt_reconnect(channel_ref="ch_1")
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert not handoff.was_called
    outcome = await handler.attempt_reconnect(channel_ref="ch_1")
    assert outcome == ReconnectOutcome.HANDOFF_SIGNALLED
    assert handoff.was_called


@pytest.mark.unit
async def test_handoff_not_signalled_before_max_failures() -> None:
    handoff = FakeHandoffHook()
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=None),
        agent_resume_hook=FakeAgentResumeHook(result=None),
        handoff_hook=handoff,
        turn_manager=TurnManager(),
        max_reconnect_attempts=3,
    )
    await handler.attempt_reconnect(channel_ref="ch_1")
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert not handoff.was_called


@pytest.mark.unit
@pytest.mark.parametrize("max_attempts", [1, 2, 5])
async def test_handoff_respects_configurable_max_attempts(max_attempts: int) -> None:
    handoff = FakeHandoffHook()
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=None),
        agent_resume_hook=FakeAgentResumeHook(result=None),
        handoff_hook=handoff,
        turn_manager=TurnManager(),
        max_reconnect_attempts=max_attempts,
    )
    for _ in range(max_attempts - 1):
        await handler.attempt_reconnect(channel_ref="ch_1")
    assert not handoff.was_called
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handoff.was_called


@pytest.mark.unit
async def test_handoff_receives_session_context_ids() -> None:
    """Handoff package must include tenant/session/correlation IDs (§8.12)."""
    ctx = ReconnectSessionContext(
        session_id="s_handoff",
        correlation_id="c_handoff",
        tenant_id="t_handoff",
        turn_id="turn_handoff",
    )
    handoff = FakeHandoffHook()
    handler = ReconnectHandler(
        session_lookup=FakeSessionLookup(result=None),
        agent_resume_hook=FakeAgentResumeHook(result=None),
        handoff_hook=handoff,
        turn_manager=TurnManager(),
        max_reconnect_attempts=1,
    )
    # Pre-set the last known context so handoff can reference it
    handler._last_known_context = ctx
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handoff.called_with is not None
    assert handoff.called_with.session_id == "s_handoff"
    assert handoff.called_with.correlation_id == "c_handoff"
    assert handoff.called_with.tenant_id == "t_handoff"


# ---------------------------------------------------------------------------
# Correlation continuity
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_correlation_id_identical_across_reconnect() -> None:
    """§25.1: correlation_id must survive reconnect unchanged."""
    handler = _handler()
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.session_context is not None
    assert handler.session_context.correlation_id == SESSION_CONTEXT.correlation_id


# ---------------------------------------------------------------------------
# Edge cases
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_multiple_successful_reconnects_preserve_same_ids() -> None:
    handler = _handler()
    await handler.attempt_reconnect(channel_ref="ch_1")
    await handler.attempt_reconnect(channel_ref="ch_1")
    assert handler.session_context is not None
    assert handler.session_context.session_id == "session_1"
    assert handler.session_context.correlation_id == "correlation_1"


@pytest.mark.unit
async def test_reconnect_with_recovered_operation_status() -> None:
    handler = _handler(resume_status=NormalizedOperationStatus.ACCEPTED)
    outcome = await handler.attempt_reconnect(channel_ref="ch_1")
    assert outcome == ReconnectOutcome.RESUMED


@pytest.mark.unit
async def test_reconnect_with_no_recovered_operation_status() -> None:
    handler = _handler(resume_status=None)
    outcome = await handler.attempt_reconnect(channel_ref="ch_1")
    assert outcome == ReconnectOutcome.RESUMED


# ---------------------------------------------------------------------------
# Pure-behavior: no network I/O
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_reconnect_module_does_not_import_network_dependencies() -> None:
    source = getsource(reconnect_module)
    forbidden_tokens = [
        "boto3",
        "deepgram",
        "google.cloud",
        "httpx",
        "openai",
        "requests",
        "socket",
        "websocket",
    ]
    assert not any(token in source for token in forbidden_tokens)
