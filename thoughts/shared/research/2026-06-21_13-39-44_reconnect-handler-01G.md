---
date: 2026-06-21T13:39:44+09:30
researcher: QuangNguyen2609
git_commit: a8a8d05db04fb3fa37fb312f609870940cf1d4dd
branch: feature/reconnect-01G
repository: Cordia
topic: "Issue #13 [01-G] — Reconnect/session re-binding handler research"
tags: [research, codebase, streaming, reconnect, session-recovery]
status: complete
last_updated: 2026-06-21
last_updated_by: QuangNguyen2609
---

# Research: Issue #13 [01-G] — Reconnect / Session Re-binding Handler

**Date**: 2026-06-21T13:39:44+09:30
**Researcher**: QuangNguyen2609
**Git Commit**: a8a8d05db04fb3fa37fb312f609870940cf1d4dd
**Branch**: feature/reconnect-01G
**Repository**: Cordia

## Research Question

What codebase patterns, contracts, invariants, and dependencies need to be understood before implementing `apps/langgraph/cordia_agent/streaming/reconnect.py` (issue #13)?

## Summary

The reconnect handler is a new streaming sub-component that re-establishes a streaming session after media drop/channel re-attach. It must:
1. Reuse the **same** `session_id` and `correlation_id` (never mint new IDs)
2. Never finalize an in-flight turn on reconnect
3. Invoke an agent-resume hook (stubbed) with preserved IDs — pipeline never mints `operation_id`
4. Maintain `correlation_id` continuity across all post-reconnect frames
5. Raise a handoff signal after repeated reconnect failures (§8.12)

All existing streaming components follow a consistent pattern: pure-behavior dataclass/Pydantic modules with deterministic fake providers for testing.

## Detailed Findings

### 1. Existing Streaming Module Structure

**Location:** `apps/langgraph/cordia_agent/streaming/`

| File | Purpose | Pattern |
|------|---------|---------|
| `__init__.py` | Re-exports `STTAdapter`, `STTFrameEnvelope`, `StreamFrame`, `TurnManager`, `is_committable` | Flat public API |
| `turn_manager.py` | Pure dataclass. Per-session seq numbering, dedup, cancellation authority, tool-call ack | No I/O, stateful in-memory |
| `stt_adapter.py` | Adapts `STTProvider` events → transcript frames. Uses `STTFrameEnvelope` for identity propagation | Async generator, envelope pattern |
| `providers/types.py` | Protocol `STTProvider` + event dataclasses (`STTPartialEvent`, `STTFinalEvent`) | Frozen dataclasses, port abstraction |
| `providers/fake.py` | Deterministic fake yielding scripted events | No real dependencies |

**Key pattern:** All streaming modules are **pure behavior** — no network, no database. External dependencies are protocol ports with fake implementations for testing.

### 2. Identity Fields: session_id, correlation_id

**Defined in:** `apps/langgraph/cordia_agent/streaming_models.py:17-36` — `StreamFrameEnvelope` (Pydantic BaseModel)

```python
class StreamFrameEnvelope(BaseModel):
    model_config = {"extra": "forbid"}
    type: StreamFrameType
    tenant_id: str          # Inv-13: propagated, not resolved
    session_id: str         # One call/conversation
    turn_id: str            # One customer/agent contribution
    correlation_id: str     # Begins at channel ingress (§25.1)
    causation_id: str | None = None
    seq: int                # Monotonic per-session ordering
    ts: AwareDatetime
```

**Propagation pattern (STTAdapter):** `STTFrameEnvelope` dataclass carries `tenant_id`, `session_id`, `turn_id`, `correlation_id`, `causation_id` — all stamped onto every frame the adapter produces (`stt_adapter.py:63-94`).

**§25.1 rule:** A single `correlation_id` begins at channel ingress and appears in everything — conversation traces, tool calls, intent revisions, operations, outbox/inbox, broker messages, notifications, handoff.

### 3. Dependencies: Issues #7 (01-A) and #9 (01-C)

Both are **CLOSED** — their code is already merged:

- **#7 [01-A]:** Created `StreamFrameEnvelope` + all 9 frame variant Pydantic models in `streaming_models.py`, the `StreamFrameType` enum in `state.py`, and registered them in the export pipeline.
- **#9 [01-C]:** Created `TurnManager` (`turn_manager.py`) with seq ordering, dedup (`is_already_seen`/`record_seen`), cancellation authority (`handle_cancellation`/`is_authoritative_turn`), and tool-call ack (`ack_tool_call`).

### 4. Stub Contracts Referenced

The issue references stubs that the reconnect handler needs:

| Stub | Source | What it represents | How reconnect uses it |
|------|--------|--------------------|-----------------------|
| **15 TenantContext** | Spec 15 (tenant-resolution-and-gateway) | Opaque `tenant_id` bound at channel ingress | Rebound on reconnected session — pipeline propagates, never resolves |
| **04 GetOperationStatus / POST /api/bookings** | Spec 03/04 (tool contracts / submission) | Agent's recovery hook to check if an operation was already submitted | Pipeline calls a **stubbed agent-resume hook** — never calls the API directly (Inv-2) |
| **06 NormalizedOperationStatus** | Spec 06 (booking-workflow) | Status projection returned by resume hook | Stubbed return value for the fake resume hook |

**Current codebase has:** `GetTenantContextStatus` enum in `tools/status_codes.py:12-16`, `RequestHumanHandoffStatus` in `tools/status_codes.py:86-91`. No `NormalizedOperationStatus` or `TenantContext` model exists yet — these need stubs.

### 5. Invariants

From `docs/booking-agent-reliability-design.md` §30:

- **Inv-1:** No booking side effect originates from transcript text alone. → Reconnect must NOT finalize in-flight turns.
- **Inv-2:** Every side-effecting request has a stable operation ID and idempotency key. → Reconnect preserves IDs for agent recovery; pipeline never mints `operation_id` or resubmits.
- **Inv-13:** Tenant scope is established before customer/knowledge/menu/booking data is accessed. → `tenant_id` is propagated on every frame, not resolved by the pipeline.

### 6. Handoff Pattern

**§8.12 triggers** include "repeated platform failure" and "customer request for a person." The handoff package contains: tenant/session/correlation IDs, current draft, field states, summary, transcript pointers, verification state, operation status, recommended next action.

**Existing code:** `RequestHumanHandoffStatus` enum with `CREATED`, `EXISTING`, `ROUTING_UNAVAILABLE` — this is a tool-result status code, not a pipeline signal. The reconnect handler needs a **stubbed handoff hook** (a callable/protocol) that receives the session context.

### 7. Test Patterns

**Location:** `apps/langgraph/tests/unit_tests/streaming/`

| Pattern | Example |
|---------|---------|
| Shared `ENVELOPE_FIELDS` dict | `test_turn_manager.py:20-27` |
| Factory helpers (`_transcript_final(**overrides)`) | `test_turn_manager.py:30-87` |
| `@pytest.mark.unit` on every test | All test files |
| `async def test_*` with `asyncio_mode = auto` | `test_stt_adapter.py` |
| Deterministic fakes (FakeSTTProvider) | `test_stt_adapter.py:39-50` |
| Parametrized edge cases | `test_turn_manager.py:90-125`, `test_stt_adapter.py:133-148` |
| No real I/O or network in unit tests | Enforced by source inspection test (`test_stt_adapter.py:198-219`) |

**pytest config:** `apps/langgraph/pytest.ini` — `asyncio_mode = auto`, markers: `unit`, `integration`, `e2e`.

### 8. Spec Sections Governing Reconnect

**§8.9 Session recovery** (`docs/booking-agent-reliability-design.md:720-729`):
- Checkpoint draft after every material slot update
- Never assume interrupted read-back was confirmed
- Resume from persisted draft, state what remains incomplete
- Query platform by `operation_id` before resubmitting
- If submission may have succeeded but response lost, recover existing operation

**§8.12 Human handoff** (`docs/booking-agent-reliability-design.md:785-813`):
- Repeated reconnect failures → handoff signal
- Handoff package: tenant/session/correlation IDs, draft, field states, summary, etc.

**§25.1 Correlation** (`docs/booking-agent-reliability-design.md:2073-2087`):
- Single `correlation_id` from channel ingress through all traces, tool calls, operations, notifications, handoff

**Spec 01 §6.3 recovery path** (`docs/specs/01-streaming-voice-pipeline.md:211-236`):
- State diagram: Streaming → MediaLost → Reconnecting → Resumed (same session_id + correlation_id)
- Pipeline does NOT finalize in-flight turn
- Agent recovers by `operation_id` (Inv-2); pipeline only preserves IDs
- MediaLost → Handoff on repeated reconnect failure

### 9. Pydantic Model Patterns

All models in the langgraph app follow:
- `model_config = {"extra": "forbid"}` (closed schemas)
- `Field(description="...")` on every field
- Spec/invariant references in descriptions where applicable
- `str` enums for discriminators (`StreamFrameType`, `TriState`, etc.)
- Union types with `union_mode="left_to_right"` for TriState|str patterns
- Frozen dataclasses for pure-data types (`@dataclass(frozen=True, slots=True)`)

### 10. Booking Models & Field Provenance

**Already implemented in `apps/langgraph/cordia_agent/state.py`:**

- `BookingRequest` (state.py:97-150): operation, service_id, location_id, starts_at, party_size, duration_minutes, special_requests, target_booking_id, target_booking_version. Uses `TriState | str | None` with `union_mode="left_to_right"`.
- `FieldProvenance` (state.py:153-174): source_turn_id, stt_confidence, model_confidence, capture_method, normalized_value, validation. Internal-only, stripped on submit.
- `BookingDraft` (state.py:219-250): tenant_id, session_id, schema_version, tenant_policy_version, customer, request, source, field_provenance. Has `to_canonical()` method.
- `CanonicalBookingDraft` (state.py:199-216): Same as BookingDraft minus field_provenance.
- Supporting: `TriState`, `OperationType`, `CaptureMethod`, `FieldValidation`, `Channel`, `CustomerRef`, `SourceInfo`.

All registered in `tools/scripts/export_pydantic_schemas.py` MODEL_REGISTRY and ENUM_REGISTRY.

## Code References

- `apps/langgraph/cordia_agent/streaming_models.py:17-36` — StreamFrameEnvelope (identity fields)
- `apps/langgraph/cordia_agent/streaming/turn_manager.py:38-85` — TurnManager (seq, dedup, cancellation)
- `apps/langgraph/cordia_agent/streaming/stt_adapter.py:27-35` — STTFrameEnvelope (propagation envelope)
- `apps/langgraph/cordia_agent/state.py:97-250` — BookingRequest, FieldProvenance, BookingDraft, CanonicalBookingDraft
- `apps/langgraph/cordia_agent/tools/status_codes.py:86-91` — RequestHumanHandoffStatus
- `apps/langgraph/tests/unit_tests/streaming/test_turn_manager.py` — Test patterns
- `apps/langgraph/tests/unit_tests/streaming/test_stt_adapter.py` — Async test patterns with fakes
- `docs/specs/01-streaming-voice-pipeline.md:211-236` — Reconnect state diagram
- `docs/booking-agent-reliability-design.md:720-729` — §8.9 Session recovery
- `docs/booking-agent-reliability-design.md:785-813` — §8.12 Human handoff
- `docs/booking-agent-reliability-design.md:2073-2087` — §25.1 Correlation
- `docs/booking-agent-reliability-design.md:2364-2386` — §30 Architecture Invariants

## Architecture Insights

1. **Pipeline is stateless/pure:** The streaming pipeline never calls APIs, never writes to DB, never resolves tenant context. It only propagates IDs and produces frames. The agent (spec 02) owns draft checkpointing and operation recovery.

2. **Stub-driven development:** The codebase uses protocol ports (like `STTProvider`) with fake implementations for testing. The reconnect handler should follow this pattern — define protocols for the session-lookup hook, agent-resume hook, and handoff hook, then provide fakes.

3. **Envelope propagation pattern:** Identity fields flow through `STTFrameEnvelope` → frame construction. The reconnect handler should receive the existing session context (session_id, correlation_id, tenant_id) and rebind them, not create new ones.

4. **TurnManager is the seq authority:** Post-reconnect frames must continue using the same TurnManager (or at minimum, the same session's seq counter must not reset).

5. **No operation_id in pipeline:** The pipeline explicitly does NOT mint operation IDs (Inv-2). It preserves session_id + correlation_id so the agent can do operation recovery. The agent-resume hook is the boundary.

## Open Questions

1. **TurnManager continuity:** Should the reconnect handler receive the existing TurnManager instance (preserving seq state), or should seq numbering restart? The spec says frames carry monotonic seq per session — suggests the TurnManager should survive reconnect.

2. **Session lookup mechanism:** The spec says `conversation_sessions` table exists but is owned by the Booking API. The reconnect handler needs a session-lookup stub that returns the existing session's IDs. What should the stub's return type look like?

3. **In-flight turn tracking:** The handler must know if a turn was in-flight at drop time to avoid finalizing it. Is this tracked in TurnManager or does the reconnect handler need its own state?
