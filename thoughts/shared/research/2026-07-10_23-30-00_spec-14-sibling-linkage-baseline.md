---
date: 2026-07-10T23:30:00+09:30
researcher: claude (fable-5, spec-loop stage 1)
topic: "Spec-14 sibling-spec linkage: role taxonomy, consent-gated transcripts, model-trace sink"
tags: [research, spec-14, spec-08, spec-01, spec-02, spec-13, roles, transcripts, object-store]
status: complete
---

# Research: Spec-14 sibling linkage — delta baseline

Third spec-14 run. Scope: WI1 role taxonomy (§12 open decision, decision-first), WI2
consent-gated transcript producer/reader (first `ConsentRequiredException` thrower), WI3
ModelTraceSink port. Everything builds ON the gap-closure deliverables recorded in
`thoughts/shared/plans/2026-07-10_spec-14-gap-closure.md`.

## Regression contract import (verbatim, per goal statement)

The following are BINDING must-stay-the-same items imported from the prior run:
- Baseline items **1–16** (`2026-07-09_18-55-30_spec-14-security-privacy-baseline.md`) and
  **17–24** (`2026-07-10_01-05-31_spec-14-gap-closure-baseline.md`) remain in force.
- Envelope/outbox shape; inbox consumer semantics (no `IInboxConsumerRequest` gains operator
  semantics); existing hosted services; waitlist customer endpoints; `ToolClient` behavior for
  the existing 19 canonical tools with no allowlist set; every asserted EventType/Actor pair
  except the pre-named WI1 exception below.
- Zero migrations; UTF-8 BOM on new `.cs`; payload sanitizer byte-identical no-op; fail-closed
  defaults; two-population audit norm (workflow-progress audits stay direct writes).
- **Pre-named exception (goal-approved):** `IOperatorActionRequest` gains a role dimension →
  all 10 implementers + their tests change (enumerated in §Delta-3 below).

## Key delta findings

### spec-08 grant machinery (apps/api)

1. `GrantAuthorizationService` (`Cordia.Application/Common/Authorization/GrantAuthorizationService.cs:20-101`;
   interface `IGrantAuthorizationService.cs:7-24`): `RequireGrantAsync(tenant, grantId,
   customerRef, action, targetBookingId?)` → validates via
   `IVerificationGrantRepository.FindValidGrantByIdAsync` (tenant + grant + customer + action +
   target + expiry, SQL-side); null → **deny audit through a fresh DI scope** then
   `VerificationRequiredException` (403). `ConsumeGrantAsync` mirrors with `ConsumeAsync`.
2. **No owner-role path exists anywhere**: `VerificationActionEnum.OwnerOverride` (D5) appears
   only in the pg-enum + `ef-enum-mappings.json`; zero application consumers. WI1's "spec-08
   adoption" therefore = the reader's grant-OR-role alternative built on a shared taxonomy
   helper; `GrantAuthorizationService` itself needs no change.
3. Read-grant consumption pattern to mirror: `GetBookingDetailsQuery.cs:49-75` —
   `RequireGrantAsync(..., VerificationActionEnum.ReadSensitive, bookingId)`, catches
   `VerificationRequiredException` → degraded status (deny already audited in the service),
   success → explicit ALLOW audit `read_sensitive_booking`. Grant id arrives `X-Grant-Id`
   header or `grant_id` query (`BookingsController.cs:48-57`, header wins).

### DLQ replay (10th operator implementer)

4. `ReplayDeadLetterCommand(TenantId, MessageId, ConsumerName, ActorId, ReasonCode)` —
   `Handlers/DeadLetters/ReplayDeadLetterCommand.cs:13-26`; validator NotEmpty ×5; endpoint
   `POST /api/dead-letter-messages/replay` reads `X-Actor` (`DeadLetterMessagesController.cs:15-51`).
   Tests: `IntegrationTests/Messaging/ReplayDeadLetterTests.cs` constructions at
   127,174,214,253,287,329,347,401,453 (+ handler at 61,324,343);
   `UnitTests/Messaging/ReplayDeadLetterValidatorTests.cs`.

### ConversationSessions (the WI2 anchor row — no DDL needed)

5. Entity (`Cordia.Domain/Entities/ConversationSession.cs:7-32`): `TenantId, SessionId,
   Channel, ConsentState?, ConsentPolicyVersion?, JurisdictionPolicyId?, RecordingAllowed?
   (bool?), ConsentRecordedAt?, CustomerRef?, StartedAt, EndedAt?, Version`. `RecordingAllowed`
   is nullable — `!= true` is the fail-closed predicate (absent/unknown/declined all suppress).
6. `ConversationSessionsController` — one route today (`POST {sessionId}/consent`), reads
   `X-Correlation-Id`, delegates to `RecordConsentCommand.FromContract`; D12 single-writer note
   in doc comment. Repository: `GetAsync(tenant, session)` (AsNoTracking, tenant-scoped) +
   `RecordConsentAsync` (ExecuteUpdate, version bump). Handler audits `consent.recorded`
   (ActorType "agent") inside the effect transaction.

### langgraph transcript flow + tool-addition blast radius

7. Transcript persistence today = **checkpointer only** (`checkpoint.py:104-122` scrubs;
   `recording_allowed is False` ⇒ transcript_text nulled in the durable copy). The streaming
   pipeline is a pure in-memory frame pipe; the natural producer seam is
   `pipeline.py:87-97` — the already-scrubbed `TranscriptFinalFrame` branch (`:95`) that
   invokes `_run_agent`. `turn_manager.is_committable()` is the existing committable predicate.
8. `redact_text(text, policy=None) -> str` (`security/redaction.py:121`) — the mandated
   pre-emit scrub for producer content.
9. New-tool pattern (copy the consent pair): `@tool` in `tools/foreground.py` (registration
   list `FOREGROUND_TOOLS`, currently **13**), thin `*_call` + `parse_*` in
   `tools/client_binding.py`, deadline + side-effect class in `tools/_timeouts.py`
   (`ALL_TOOLS = frozenset(TOOL_DEADLINES)`, currently **19**, import-time consistency guard),
   status enum in `tools/status_codes.py`, Pydantic I/O in `tools/contracts.py`, registration
   in `tools/scripts/export_pydantic_schemas.py` `MODEL_REGISTRY`/`ENUM_REGISTRY`.
10. **Named test updates required by any new canonical tool** (the ONLY count assertions):
    `tests/unit_tests/tools/test_client_tenant_gate.py:134-138` (`len(ALL_TOOLS) == 19`) and
    docstring `:12`; `test_client_tool_gate.py:91,107` ("19" comments only);
    `test_foreground_tools.py:63-77` (`EXPECTED_TOOL_NAMES` exact set), `:196-197` (count 13),
    `:236-263` (tool-body inspection vs EXPECTED_TOOL_NAMES), stale module docstring `:4`.
    `test_graph_topology.py` pins EDGES not counts — tool calls inside existing nodes/pipeline
    do NOT touch it.
11. `cordia_agent/observability/` **does not exist** — WI3 creates it greenfield.
12. `GRANT_REQUIRED_TOOLS = {submit_booking_modification, submit_booking_cancellation}` —
    unchanged by this work (the transcript reader is an API/staff surface, not an agent tool).

## Existing Feature Behavior (Baseline Audit)

Items 1–24 imported (above). New/re-confirmed items for THIS run:

25. **OperatorActionBehavior contract** (`Behaviours/OperatorActionBehavior.cs`): no-op for
    non-operator requests (Inv-5 untouched); missing actor/reason → `AuthorizationFailedException`
    (403) BEFORE the handler, no deny-audit; `DomainException` escaping the handler → deny-audit
    via fresh scope + rethrow; ALLOW audit is the handler's job. Pipeline order Validation →
    OperatorAction → InboxDedup → Handler. Tests: `UnitTests/Security/OperatorActionBehaviorTests.cs`.
    WI1 CHANGES this intentionally (role check added after presence checks, deny-audited).
26. **The 10 operator implementers + tests** (pre-named exception): Menu ×5
    (`UploadMenu/ReviewCatalog/EditCatalogItems/ApproveCatalog/PublishCatalog` cmd+validator+handler),
    Preorder ×2 (`ConvertPreorderToStandalone`, `CancelPreorderManualReview`), ManualRecovery ×2
    (`ResumeWorkflowStep`, `ResolveManualReview`), DLQ ×1 (`ReplayDeadLetter`). Controllers:
    MenuController (5 actions), PreorderController (2), BookingOperationsController (2),
    DeadLetterMessagesController (1). Test files: UnitTests/Menu ×4 (Approve/Review/Publish/
    EditCatalogItems), UnitTests/Preorder/PreorderManualResolutionHandlerTests,
    UnitTests/BookingFulfillment ×2 (Resume/Resolve), UnitTests/Security/OperatorActionBehaviorTests,
    UnitTests/Messaging/ReplayDeadLetterValidatorTests, IntegrationTests/Menu ×2
    (MenuLifecycleEndToEnd, PublishImmutability), IntegrationTests/Acceptance/MenuControllerTests,
    IntegrationTests/Messaging/{ManualRecoveryTests, ReplayDeadLetterTests}. EventType/Actor
    pairs stay stable; only constructor arity + headers change.
27. **GrantAuthorizationService/VerificationRequiredException flow** (item 3 above) — MUST STAY
    byte-for-byte: the reader CONSUMES `RequireGrantAsync`, never modifies it. Its deny-audit +
    403 mapping and `GetBookingDetails` degraded-status behavior unchanged.
28. **RecordConsent vertical** — MUST STAY: entity, repo `ExecuteUpdate`, handler audit
    (`consent.recorded`, ActorType agent), controller route, D12 single-writer. WI2 ADDS
    sibling endpoints on the same controller; existing endpoint untouched.
29. **Streaming pipeline scrub-before-yield** (`pipeline.py:34-40,87-97`) — MUST STAY: frames
    already scrubbed before the producer hook sees them; the hook is additive after `:95`,
    fail-open for the pipe (persist failure never blocks the turn) and fail-closed for
    persistence (no consent ⇒ no call).
30. **Checkpointer transcript suppression** (item 7) — MUST STAY: the object-store transcript
    is a parallel consented artifact; checkpoint behavior unchanged.
31. **IObjectStore/EncryptedFileObjectStore** (prior run P6) — additive `ListAsync` only:
    Put/Get/Delete/Sweep semantics, blob layout, traversal guard, 72h raw-audio TTL, sweep
    audits (`privacy.retention.object_pruned`) all byte-stable. Tests:
    UnitTests/Storage/EncryptedFileObjectStoreTests, IntegrationTests/Persistence/RetentionSweeperTests.
32. **Tool gate 1c / tenant gate behavior** — MUST STAY for the existing 19 tools (no-allowlist
    pass pinned by `test_client_tool_gate.py`); new tools extend the tables, never alter
    existing entries' deadlines/classes. Count assertions = named updates (item 10).
33. **Existing api-contract surface** — new endpoints are additive; existing paths unchanged
    (regen required; web consumes none of the new paths).

## Registry position (delta)

- **Owned by spec 14** (may change): authorization spine incl. role check (§3.1 step 2/3),
  `IOperatorActionRequest`/`OperatorActionBehavior`, `IObjectStore` seam, typed security
  exceptions, transcript/audio storage boundary (§4.3), retention values (§12 OD#1/#2 —
  DECISIONS, gate-bound), audit write-path contract (D13).
- **Referenced (must match owner)**: `verification_grants` lifecycle + `VerificationActionEnum`
  (spec 08, D5) — consume `RequireGrantAsync` as-is; `conversation_sessions` writes via Booking
  API only (D12); envelope/queue contracts (spec 05); `TenantContext` resolution (spec 15, D15
  — X-Tenant-Id shim stays); tool I/O contract shape (spec 03); telemetry-plane audit
  permission (spec 13 §4.3 — "who viewed model traces" append is explicitly allowed).
- **Frozen decisions in play**: D5 (verification_action set — use `ReadSensitive`; do NOT
  invent new enum values), D12, D13, D15, D17 (no new tables — object store is "not a table"
  per spec 14 §4.3).
- **Invariants**: Inv-1 (producer persists only), Inv-11 (grant-or-role, never session identity),
  Inv-12 (redact before persist, API-side re-sanitize), Inv-13 (TenantGuard + store path
  isolation), Inv-14 (append-only audit, allow AND deny).

## Type-generation blast radius

New Pydantic models (tools/contracts.py): transcript-turn input/result + model-trace input/
result + 2 status enums → `MODEL_REGISTRY`/`ENUM_REGISTRY` → regen shared-schemas (C# consumed
by new controllers, TS/Zod additive) → api-contract regen (new endpoints). Web consumes nothing
new. Existing generated types untouched except additive.

## Existing automated coverage that must stay green

api: 829 unit / 339 integration (all suites); langgraph: 801; web: 2. Named updates only where
§Delta items 10 & 26 say so.
