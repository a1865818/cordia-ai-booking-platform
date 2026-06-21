---
date: 2026-06-21T13:54:44+09:30
researcher: quangnguyentechno@gmail.com
git_commit: a8a8d05db04fb3fa37fb312f609870940cf1d4dd
branch: feature/reconnect-01G
repository: Cordia
task: "Issue #13 [01-G] — Reconnect/session re-binding handler"
tags: [implementation-plan, streaming, reconnect, session-recovery, tdd]
status: draft
last_updated: 2026-06-21
last_updated_by: quangnguyentechno@gmail.com
---

# Reconnect / Session Re-binding Handler — Implementation Plan

## Overview

Implement `apps/langgraph/cordia_agent/streaming/reconnect.py` — a pure-behavior reconnect handler that re-establishes a streaming session after media drop or channel re-attach. The handler preserves `session_id`/`correlation_id`/`tenant_id`, never finalizes in-flight turns, invokes a stubbed agent-resume hook, and signals human handoff after repeated failures (§8.12). Follows TDD with deterministic fakes matching the existing streaming module pattern.

## Current State Analysis

### Existing infrastructure (merged):
- `TurnManager` (`streaming/turn_manager.py:38-85`) — per-session seq numbering, dedup, cancellation authority, tool-call ack. Tracks in-flight turn state via `_non_authoritative_turn_ids`.
- `STTAdapter` (`streaming/stt_adapter.py:38-98`) — adapts STT provider → transcript frames. Uses `STTFrameEnvelope` for identity propagation.
- `StreamFrameEnvelope` (`streaming_models.py:17-35`) — Pydantic base carrying `session_id`, `correlation_id`, `tenant_id`, `turn_id`, `causation_id`, `seq`, `ts`.
- `STTProvider` Protocol (`providers/types.py:33-39`) + `FakeSTTProvider` (`providers/fake.py:14-28`) — the established stub pattern.
- `BookingDraft` / `FieldProvenance` / `CanonicalBookingDraft` (`state.py:97-250`) — booking models already implemented.
- `RequestHumanHandoffStatus` (`tools/status_codes.py:86-91`) — tool-result status enum, not a pipeline signal.

### What's missing:
- `reconnect.py` — the handler module itself
- `ReconnectSessionContext` — frozen dataclass carrying identity fields for session rebinding
- Protocol ports — session lookup, agent resume, handoff hooks
- `NormalizedOperationStatus` — stub for agent resume hook return type (spec 06)
- `ReconnectOutcome` — result type for reconnect attempts
- Fakes for all three protocol ports
- Unit tests at `tests/unit_tests/streaming/test_reconnect.py`

### Key Discoveries:
- `TurnManager._next_seq_by_session_id` is a dict keyed by `session_id` — passing the same TurnManager instance preserves seq continuity across reconnect (`turn_manager.py:47-51`)
- `TurnManager.is_authoritative_turn()` already tracks whether a turn was cancelled without material delivery (`turn_manager.py:69-71`) — no new in-flight tracking needed
- The spec module listing expects `reconnect.py` in `streaming/` (`01-streaming-voice-pipeline.md:330`)
- All streaming modules use the pure-behavior pattern: no I/O, no network, protocol ports with fakes (`providers/types.py:33-39`)
- The pipeline explicitly does NOT mint `operation_id` (Inv-2) — it preserves IDs so the agent can do recovery

## Desired End State

After this plan is complete:
1. `apps/langgraph/cordia_agent/streaming/reconnect.py` exists with `ReconnectHandler`, `ReconnectSessionContext`, `ReconnectOutcome`, `NormalizedOperationStatus`, and three Protocol ports
2. `apps/langgraph/cordia_agent/streaming/providers/fake.py` contains fakes for all three reconnect protocols
3. `apps/langgraph/tests/unit_tests/streaming/test_reconnect.py` has comprehensive unit tests covering all invariants
4. `streaming/__init__.py` re-exports `ReconnectHandler`, `ReconnectSessionContext`, `ReconnectOutcome`
5. Schema export pipeline registers new models
6. All tests pass: `bun nx test langgraph`

### Verification:
- `bun nx test langgraph` — all unit tests pass including new reconnect tests
- `bun nx lint langgraph` — ruff check passes
- `bun nx run langgraph:generate-schemas` — new models export cleanly to JSON Schema

## What We're NOT Doing

- Implementing the actual session persistence layer (owned by Booking API, spec 15)
- Implementing the real agent-resume logic (owned by spec 02)
- Implementing real human handoff routing (owned by spec 08)
- Adding `TenantContext` model (spec 15 — separate issue)
- Any network/I/O in the reconnect handler — pure behavior only
- Changing `TurnManager`'s API — the existing interface is sufficient

## Implementation Approach

**TDD (RED-GREEN-REFACTOR):** Write failing tests first that define all reconnect behaviors, then implement the handler and supporting types to make them pass. Three phases:

1. **Phase 1 (RED):** Tests for all reconnect handler behaviors
2. **Phase 2 (GREEN):** Types, protocols, fakes, and `ReconnectHandler` implementation
3. **Phase 3:** Integration into module exports and schema pipeline

### Design Decisions:

| Decision | Choice | Rationale |
|----------|--------|-----------|
| TurnManager ownership | Receive existing instance | Preserves seq monotonicity per-session (spec §5.1) |
| Session lookup return | Lightweight `ReconnectSessionContext` frozen dataclass | Pipeline propagates, doesn't resolve — no need for full `BookingDraft` |
| In-flight turn tracking | Via `TurnManager.is_authoritative_turn()` | Already tracks cancelled turns; no new state needed |
| Failure threshold | Configurable `max_reconnect_attempts` with default 3 | "Repeated reconnect failure" (§8.12) without hardcoding |

---

## Phase 1: Tests (RED)

### Phase 1: Overview
Write comprehensive unit tests at `apps/langgraph/tests/unit_tests/streaming/test_reconnect.py` that define all reconnect handler behaviors. These tests will fail until Phase 2 implements the handler.

### Phase 1: Changes Required

#### 1. Test file
**File**: `apps/langgraph/tests/unit_tests/streaming/test_reconnect.py` (new)
**Changes**: Create test file following established patterns from `test_turn_manager.py` and `test_stt_adapter.py`.

```python
"""Unit tests for the streaming reconnect / session re-binding handler."""

from __future__ import annotations

import pytest

from cordia_agent.streaming.reconnect import (
    NormalizedOperationStatus,
    ReconnectHandler,
    ReconnectOutcome,
    ReconnectSessionContext,
)
from cordia_agent.streaming.turn_manager import TurnManager

# --- Protocols are tested via fakes imported from providers ---
from cordia_agent.streaming.providers import (
    FakeAgentResumeHook,
    FakeHandoffHook,
    FakeSessionLookup,
)


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
async def test_reconnect_with_existing_operation_status() -> None:
    handler = _handler(resume_status=NormalizedOperationStatus.FOUND)
    outcome = await handler.attempt_reconnect(channel_ref="ch_1")
    assert outcome == ReconnectOutcome.RESUMED


@pytest.mark.unit
async def test_reconnect_with_not_found_operation_status() -> None:
    handler = _handler(resume_status=NormalizedOperationStatus.NOT_FOUND)
    outcome = await handler.attempt_reconnect(channel_ref="ch_1")
    assert outcome == ReconnectOutcome.RESUMED
```

### Phase 1: Success Criteria

#### Phase 1: Automated Verification
- [x] Test file exists and is syntactically valid: `python -c "import ast; ast.parse(open('apps/langgraph/tests/unit_tests/streaming/test_reconnect.py').read())"`
- [x] Tests are collected by pytest (will fail with ImportError — expected RED state): `cd apps/langgraph && uv run python -m pytest tests/unit_tests/streaming/test_reconnect.py --collect-only 2>&1 | head -5`

#### Phase 1: Manual Verification
- [x] All test names clearly describe the invariant they verify
- [x] Test patterns match existing `test_turn_manager.py` and `test_stt_adapter.py` conventions

### Phase 1: Discoveries and Notable Information

**Technical Discoveries:**
- `python` is not on PATH in this environment; use `cd apps/langgraph && uv run python` for syntax checks (same as pytest).
- Pytest collection fails with `ModuleNotFoundError: No module named 'cordia_agent.streaming.reconnect'` — expected RED state before Phase 2.

**Implementation Adaptations:**
- Test file created exactly per plan spec at `apps/langgraph/tests/unit_tests/streaming/test_reconnect.py`.
- 22 test functions (25 collected cases with `@pytest.mark.parametrize` on `test_handoff_respects_configurable_max_attempts`).

**Future Considerations:**
- Phase 3 will append a source-inspection test for forbidden network imports (deferred per plan).
- `test_successful_reconnect_resets_failure_counter` mutates `handler._session_lookup` — tests internal state; acceptable for TDD but may warrant a public setter in Phase 2 if preferred.

---

## Phase 2: Core Implementation (GREEN)

### Phase 2: Overview
Implement all types, protocols, fakes, and the `ReconnectHandler` to make all Phase 1 tests pass.

### Phase 2: Changes Required

#### 1. Reconnect handler module
**File**: `apps/langgraph/cordia_agent/streaming/reconnect.py` (new)
**Changes**: Create the reconnect handler with all supporting types.

```python
"""Reconnect / session re-binding handler (spec 01 §6.3, §8.9).

Pure-behavior module — no I/O. External dependencies are protocol ports
with fake implementations for testing.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import TYPE_CHECKING, Protocol

if TYPE_CHECKING:
    from cordia_agent.streaming.turn_manager import TurnManager


class NormalizedOperationStatus(str, Enum):
    """Stub for the agent-resume hook return (spec 06)."""

    FOUND = "found"
    NOT_FOUND = "not_found"


class ReconnectOutcome(str, Enum):
    """Result of a reconnect attempt."""

    RESUMED = "resumed"
    FAILED = "failed"
    HANDOFF_SIGNALLED = "handoff_signalled"


@dataclass(frozen=True, slots=True)
class ReconnectSessionContext:
    """Identity fields for session rebinding — propagated, not resolved (Inv-13)."""

    session_id: str
    correlation_id: str
    tenant_id: str
    turn_id: str


class SessionLookup(Protocol):
    """Port: look up an existing session by channel reference."""

    async def lookup(self, channel_ref: str) -> ReconnectSessionContext | None: ...


class AgentResumeHook(Protocol):
    """Port: signal the agent to resume from persisted draft (§8.9)."""

    async def resume(
        self, context: ReconnectSessionContext
    ) -> NormalizedOperationStatus | None: ...


class HandoffHook(Protocol):
    """Port: signal human handoff on repeated reconnect failure (§8.12)."""

    async def signal(self, context: ReconnectSessionContext) -> None: ...


class ReconnectHandler:
    """Session re-binding handler for the streaming pipeline.

    Re-establishes a streaming session after media drop / channel re-attach
    under the SAME session_id/correlation_id. Never finalizes in-flight turns
    (Inv-1). Never mints operation_id (Inv-2). Signals handoff after
    max_reconnect_attempts consecutive failures (§8.12).
    """

    def __init__(
        self,
        *,
        session_lookup: SessionLookup,
        agent_resume_hook: AgentResumeHook,
        handoff_hook: HandoffHook,
        turn_manager: TurnManager,
        max_reconnect_attempts: int = 3,
    ) -> None:
        self._session_lookup = session_lookup
        self._agent_resume_hook = agent_resume_hook
        self._handoff_hook = handoff_hook
        self._turn_manager = turn_manager
        self._max_reconnect_attempts = max_reconnect_attempts
        self._consecutive_failures = 0
        self._session_context: ReconnectSessionContext | None = None
        self._last_known_context: ReconnectSessionContext | None = None

    @property
    def session_context(self) -> ReconnectSessionContext | None:
        return self._session_context

    @property
    def consecutive_failures(self) -> int:
        return self._consecutive_failures

    async def attempt_reconnect(self, channel_ref: str) -> ReconnectOutcome:
        context = await self._session_lookup.lookup(channel_ref)

        if context is None:
            self._consecutive_failures += 1
            if self._consecutive_failures >= self._max_reconnect_attempts:
                if self._last_known_context is not None:
                    await self._handoff_hook.signal(self._last_known_context)
                return ReconnectOutcome.HANDOFF_SIGNALLED
            return ReconnectOutcome.FAILED

        # Rebind — preserve all identity fields, never mint new IDs
        self._session_context = context
        self._last_known_context = context
        self._consecutive_failures = 0

        # Signal agent to resume (stubbed) — pipeline never mints operation_id
        await self._agent_resume_hook.resume(context)

        return ReconnectOutcome.RESUMED
```

#### 2. Fake implementations
**File**: `apps/langgraph/cordia_agent/streaming/providers/fake.py` (append)
**Changes**: Add fakes for the three reconnect protocol ports after `FakeSTTProvider`.

```python
# --- Reconnect fakes ---

from cordia_agent.streaming.reconnect import (
    NormalizedOperationStatus,
    ReconnectSessionContext,
)


class FakeSessionLookup:
    """Return a scripted session context (or None for lookup failure)."""

    def __init__(self, result: ReconnectSessionContext | None) -> None:
        self.result = result

    async def lookup(self, channel_ref: str) -> ReconnectSessionContext | None:
        return self.result


class FakeAgentResumeHook:
    """Record the resume call and return a scripted status."""

    def __init__(self, result: NormalizedOperationStatus | None) -> None:
        self.result = result
        self.called_with: ReconnectSessionContext | None = None

    async def resume(
        self, context: ReconnectSessionContext
    ) -> NormalizedOperationStatus | None:
        self.called_with = context
        return self.result


class FakeHandoffHook:
    """Record whether handoff was signalled and with what context."""

    def __init__(self) -> None:
        self.was_called = False
        self.called_with: ReconnectSessionContext | None = None

    async def signal(self, context: ReconnectSessionContext) -> None:
        self.was_called = True
        self.called_with = context
```

#### 3. Provider exports
**File**: `apps/langgraph/cordia_agent/streaming/providers/__init__.py`
**Changes**: Add re-exports for the three new fakes.

Add to existing exports:
```python
from cordia_agent.streaming.providers.fake import (
    FakeAgentResumeHook,
    FakeHandoffHook,
    FakeSessionLookup,
)
```

Add to `__all__`:
```python
"FakeAgentResumeHook",
"FakeHandoffHook",
"FakeSessionLookup",
```

### Phase 2: Success Criteria

#### Phase 2: Automated Verification

**Run verification commands from repository root:**
- [x] All unit tests pass: `bun nx test langgraph 2>&1 | tee .tmp/test-langgraph.log` — 24/24 reconnect tests pass; 1 pre-existing failure in `test_schema_export.py` (Phase 3 scope)
- [x] Linting passes: `bun nx lint langgraph`
- [x] No forbidden imports in reconnect module: verify `reconnect.py` does not import `httpx`, `requests`, `socket`, `websocket`, `boto3`, `deepgram`, `google.cloud`, `openai`

#### Phase 2: Manual Verification
- [x] `ReconnectHandler` is a pure-behavior class with no I/O
- [x] All protocol ports follow the `STTProvider` pattern (Protocol class, not ABC)
- [x] Fakes follow the `FakeSTTProvider` pattern (plain classes with scripted behavior)
- [x] `NormalizedOperationStatus` is a `(str, Enum)` matching codebase convention

### Phase 2: Discoveries and Notable Information

**Technical Discoveries:**
- Tests require `handoff_hook.signal()` to be called on every max-failure threshold breach (even when `_last_known_context` is None), not only when last-known context exists — plan pseudocode had conditional call; implementation always signals with `ReconnectSessionContext | None`.
- Ruff TC001 requires `TurnManager` and reconnect types in `TYPE_CHECKING` blocks when only used for annotations (`from __future__ import annotations` already present).

**Implementation Adaptations:**
- `HandoffHook.signal()` protocol accepts `ReconnectSessionContext | None` to support handoff signalling without prior successful reconnect.
- `FakeHandoffHook.signal()` updated to match optional context parameter.
- Ruff RUF022 auto-sorted `__all__` in `providers/__init__.py` (FakeSTTProvider before FakeSessionLookup).

**Future Considerations:**
- Full `bun nx test langgraph` suite has 1 unrelated failure in `test_generated_typescript_exports_streaming_frames_when_available` — addressed in Phase 3 schema pipeline.

---

## Phase 3: Integration

### Phase 3: Overview
Wire the new types into `streaming/__init__.py` exports and the schema export pipeline so they are available to consumers and generate to JSON Schema / C# / TypeScript.

### Phase 3: Changes Required

#### 1. Streaming module exports
**File**: `apps/langgraph/cordia_agent/streaming/__init__.py`
**Changes**: Add re-exports for `ReconnectHandler`, `ReconnectSessionContext`, `ReconnectOutcome`.

```python
from cordia_agent.streaming.reconnect import (
    ReconnectHandler,
    ReconnectOutcome,
    ReconnectSessionContext,
)
```

Add to `__all__`:
```python
"ReconnectHandler",
"ReconnectOutcome",
"ReconnectSessionContext",
```

#### 2. Schema export registration
**File**: `tools/scripts/export_pydantic_schemas.py`
**Changes**: Register `NormalizedOperationStatus` in `ENUM_REGISTRY` and `ReconnectSessionContext` does NOT need registration (it's a frozen dataclass, not a Pydantic model — it stays internal to the pipeline).

However, `NormalizedOperationStatus` and `ReconnectOutcome` are `(str, Enum)` types that should be registered in `ENUM_REGISTRY` for cross-language generation:

```python
# Add import
from cordia_agent.streaming.reconnect import NormalizedOperationStatus, ReconnectOutcome

# Add to ENUM_REGISTRY list
NormalizedOperationStatus,
ReconnectOutcome,
```

#### 3. Source-inspection test update
**File**: `apps/langgraph/tests/unit_tests/streaming/test_reconnect.py` (append)
**Changes**: Add a source-inspection test matching the pattern from `test_stt_adapter.py:199-219`.

```python
from inspect import getsource
import cordia_agent.streaming.reconnect as reconnect_module


@pytest.mark.unit
def test_reconnect_module_does_not_import_network_dependencies() -> None:
    source = getsource(reconnect_module)
    forbidden_tokens = [
        "boto3", "deepgram", "google.cloud",
        "httpx", "openai", "requests", "socket", "websocket",
    ]
    assert not any(token in source for token in forbidden_tokens)
```

### Phase 3: Success Criteria

#### Phase 3: Automated Verification

**Run verification commands from repository root:**
- [x] All unit tests pass: `bun nx test langgraph 2>&1 | tee .tmp/test-langgraph.log` — 159/159 pass (25 reconnect tests including source-inspection)
- [x] Linting passes: `bun nx lint langgraph`
- [x] Schema generation succeeds: `bun nx run langgraph:generate-schemas 2>&1 | tee .tmp/schema-gen.log`
- [x] Type generation succeeds: `bun nx run shared-schemas:generate-types 2>&1 | tee .tmp/type-gen.log`

#### Phase 3: Manual Verification
- [x] `NormalizedOperationStatus` and `ReconnectOutcome` appear in `libs/shared-schemas/schemas/enums/`
- [x] `streaming/__init__.py` exports are importable: `cd apps/langgraph && uv run python -c "from cordia_agent.streaming import ReconnectHandler, ReconnectSessionContext, ReconnectOutcome"`

### Phase 3: Discoveries and Notable Information

**Technical Discoveries:**
- `test_generated_typescript_exports_streaming_frames_when_available` failed before schema/type regeneration due to stale `zod.ts` (`z.string()` vs `z.string().datetime({ offset: true })` for `ts` fields). Running `generate-schemas` then `generate-types` resolves it — no test code changes needed.
- Schema export lists 43 shared defs (up from 41) after adding the two reconnect enums.

**Implementation Adaptations:**
- `ReconnectSessionContext` intentionally excluded from `ENUM_REGISTRY` and `MODEL_REGISTRY` — frozen dataclass, internal to reconnect handler.
- Source-inspection test added at end of `test_reconnect.py` (pattern from `test_stt_adapter.py`).

**Future Considerations:**
- Generated enum schemas are gitignored under `libs/shared-schemas/generated/` — CI must run schema/type generation; committed `schemas/enums/*.json` files are the source artifacts for review.

---

## Testing Strategy

### Unit Tests:
- Successful reconnect preserves session_id, correlation_id, tenant_id
- Seq numbering continues from existing TurnManager (not reset)
- In-flight turns are never finalized on reconnect
- Agent resume hook called with correct session context
- Pipeline never mints operation_id (Inv-2)
- Session not found → FAILED outcome
- Failure counter increments on failed reconnect
- Successful reconnect resets failure counter
- Handoff signalled after max_reconnect_attempts failures (§8.12)
- Handoff NOT signalled before max failures
- Configurable max_reconnect_attempts works with different values
- Handoff receives correct session/correlation/tenant IDs
- Correlation ID identical across reconnect (§25.1)
- Multiple successful reconnects preserve same IDs
- Reconnect with FOUND/NOT_FOUND operation status both succeed
- Source inspection: no forbidden network imports

### Integration Tests:
- Not in scope — reconnect handler is pure behavior with protocol ports

### Manual Testing Steps:
1. Run `bun nx test langgraph` and verify all tests pass
2. Run `bun nx run langgraph:generate-schemas` and verify new enums appear in `libs/shared-schemas/schemas/enums/`

## Performance Considerations

None — the reconnect handler is a lightweight in-memory state machine with no I/O. The protocol ports are async to match the async streaming pipeline convention, but the fakes are synchronous.

## Migration Notes

No migration needed — this is a new module. No existing behavior is changed.

## References

- Research document: `thoughts/shared/research/2026-06-21_13-39-44_reconnect-handler-01G.md`
- Spec §6.3 recovery path: `docs/specs/01-streaming-voice-pipeline.md:211-236`
- Spec §8.9 session recovery: `docs/booking-agent-reliability-design.md:720-729`
- Spec §8.12 human handoff: `docs/booking-agent-reliability-design.md:785-813`
- Spec §25.1 correlation: `docs/booking-agent-reliability-design.md:2073-2087`
- Spec §30 invariants: `docs/booking-agent-reliability-design.md:2364-2386`
- Module listing: `docs/specs/01-streaming-voice-pipeline.md:330`
- TurnManager: `apps/langgraph/cordia_agent/streaming/turn_manager.py:38-85`
- STTProvider protocol pattern: `apps/langgraph/cordia_agent/streaming/providers/types.py:33-39`
- Test patterns: `apps/langgraph/tests/unit_tests/streaming/test_turn_manager.py`, `test_stt_adapter.py`
