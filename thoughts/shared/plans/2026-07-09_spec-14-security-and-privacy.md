# Spec 14 — Security, Privacy & Compliance: Implementation Plan

**Date:** 2026-07-09 · **Branch:** `feature/spec-14` (at `2f592d4`, even with main)
**Spec:** `docs/specs/14-security-and-privacy.md` · **Research/baseline:**
`thoughts/shared/research/2026-07-09_18-55-30_spec-14-security-privacy-baseline.md`
**Status:** COMPLETE — all 6 phases implemented, verified, and validated (see Validation Report)

Governing directive (spec-11 precedent): treat the repo as a single evolving architecture —
integrate with existing mechanisms (single front doors), never add parallel implementations.

**Stage-3 gate decisions (user-approved 2026-07-09):** plan approved, all 6 phases, commit per
phase. Retention defaults: sessions anonymized 90 days after `ended_at`; grants pruned 30 days
after expiry/consumption; `audit_events` never pruned — all configurable. Operator identity:
keep `X-Actor` + mandatory reason code + audit (no role taxonomy). Redaction: PAN/CVV/gov-id
always-on; `configured_sensitive_fields` empty by default, tenant-overridable.

## Overview

Spec 14 completes the cross-cutting security seams the earlier specs scaffolded. Six phases:
(1) shared Pydantic security contracts, (2) foreground redaction filter + consent gate node,
(3) API consent vertical (jurisdiction policy + RecordConsent + audit), (4) authorization
spine + typed audit writer + reason-coded operator replay, (5) log scrubbing + secret custody
+ retention extension, (6) the §27.6 cross-cutting security test suite. **Zero EF migrations**
— every table/column/pg-enum spec 14 needs is already ratified and migrated (spec 12 owns DDL;
we add none).

## How this maps onto Cordia

| New concept (spec 14) | Existing Cordia artifact it reuses/extends | Consequence |
|---|---|---|
| `ConsentState` / `RedactionPolicy` / `AuditEventEnvelope` contracts | Pydantic-contract pipeline: `cordia_agent/contracts/*.py` + `export_pydantic_schemas.py` registries → C#/TS/Zod | New `contracts/security.py`; additive regen; never hand-edit generated files |
| Consent gate (foreground) | Graph node pattern (`async def (state)->dict` + `route_after_*`), inserted after `resolve_tenant`; tool pattern in `tools/foreground.py` via `ToolClient` | New `security/consent.py` node + 2 tools (`resolve_consent_policy`, `record_consent`); topology test updated deliberately |
| Redact-before-emit | `streaming/logging_redaction.py` (self-described spec-14 placeholder) | New `security/redaction.py` with always-on PAN/CVV/gov-id detectors; `logging_redaction` delegates to it (DTMF behavior preserved); wired into checkpointer + transcript frames |
| Consent persistence (D12) | `ConversationSession` entity + `consent_state` pg-enum (already migrated); CQRS trio pattern (`Handlers/<Feature>/`); audit-append-in-txn convention | New `Handlers/Security/RecordConsent` (update-only; session creation stays spec 15's) + `Handlers/Security/ResolveJurisdictionPolicy`; thin `ConsentController` |
| Audit writer (D13) | `IAuditEventRepository.AddAsync` convention already used by 6 writers; spec-12 ratified row shape | New `IAuditWriter` (Application/Common/Audit) that builds sanitized `metadata` jsonb (actor_type, reason_code, decision, data_classes) — **no schema change**; existing writers untouched |
| Authorization spine (§3.1) | Pipeline-behavior pattern (`ValidationPipelineBehavior`), marker-interface pattern (`IInboxConsumerRequest`) | New `IOperatorActionRequest` marker + `OperatorActionBehavior` (reason-code + audit); applied to new spec-14 commands and retrofitted to DLQ replay only |
| Tenant-scope guard | `ITenantScoped` + EF global query filter (exists) | No mechanism change — spec 14 adds the structural cross-tenant test (§27.6) and marks its features' reads/writes with existing conventions |
| Secret custody | none (gap) | New `ISecretStore` (Domain) + config-backed Infrastructure impl; adapters keep receiving config via DI; enricher guarantees non-serialization |
| Log scrubbing | Serilog `ReadFrom.Configuration` in Program.cs | New `Infrastructure/Security/LogScrubbingEnricher` + destructuring policy registered in code; PAN/CVV/secret patterns masked |
| Retention & deletion | `RetentionPolicy` + `RetentionSweepHandler` ("only sanctioned deletion path") | Extend with session anonymization + expired-grant pruning, each audited; idempotency sweep untouched |
| Prompt-injection boundary | `safety/injection_harness.py` + `IsolatedExtractionAgent` (exist) | No mechanism change — add §27.6 end-to-end fixture tests only |

## Spec ownership & frozen decisions

| Contract / mechanism | Position | Constraint |
|---|---|---|
| `audit_events` write path | **OWNED** (D13) | Row shape is spec 12's ratified DDL (`event_id,event_type,actor,subject_ref,correlation_id?,operation_id?,metadata,occurred_at`) — spec 14 §4.2 prose is stale; extras go in `metadata` jsonb. Append-only preserved. |
| `ConsentState`/`RedactionPolicy`/`AuditEventEnvelope` Pydantic | **OWNED** | New; additive registration. Pydantic model name `ConsentState` coexists with C# enum `Cordia.Domain.Enums.ConsentState` (different namespaces; alias if ambiguous). |
| `conversation_sessions` consent columns | **REFERENCED** (02 table / 15 creator / D12 writes-via-API) | Update `consent_*` only, through the API; RecordConsent is update-only — row creation remains spec 15's. Columns already exist; no DDL. |
| `verification_grants` / `VerificationGrant` | **REFERENCED** (08) | Validate/consume via existing `IGrantAuthorizationService` byte-for-byte. |
| `TenantContext` mint/sign | **REFERENCED** (15, D15) | `TenantResolutionMiddleware` shim untouched. |
| Foreground tool allowlist / tool I/O style | **REFERENCED** (03) | New consent tools mirror existing tool contracts exactly (ToolResultEnvelope, status enums). |
| `consent_state` pg-enum | **REFERENCED** (12 DDL) | Values granted/declined/not_required/alternative_offered — reused verbatim in the Pydantic contract. |
| Stripe webhook signature verification | **NOT OURS** (09) | We state the generic rule only; no `PaymentWebhooksController`. |
| DDL / pg-enums | **NOT OURS** (12) | Zero migrations in this plan. |
| Invariants in play | Inv-1 (partial), **Inv-11, Inv-12, Inv-13** (primary), Inv-14/15 (audit/retention discipline) | §27.6 tests prove each. |

## What we're NOT doing

- No `PaymentWebhooksController` / Stripe signature code (spec 09).
- No verification-grant lifecycle changes, factors, or risk policy (spec 08).
- No signed-`TenantContext` mint/verify hardening of the middleware (spec 15).
- No new tables, columns, or pg-enums; no EF migrations (spec 12).
- No owner/operator **role taxonomy** or authentication system — staff actions keep the
  documented `X-Actor` shim, now hardened with mandatory reason codes + audit (open decision,
  known deferred item).
- No object-store/encrypted-transcript infrastructure (no object store exists in the repo;
  DB-at-rest encryption is deployment config, noted in docs only).
- No notification-consent changes (`IConsentGate` is notification opt-in — a different concept;
  untouched).
- No fixing of spec-11 review residuals (C9/C11) — adjacent, tracked separately.
- No apps/web UI work (operations console has no spec).

## Blast radius (scope boundary for Stage 4)

**Intentionally changed:**
- `cordia_agent/contracts/security.py` (new), `security/` package (new: `consent.py`,
  `redaction.py`), `graph.py` + `nodes/__init__.py` (consent node), `state.py` (additive consent
  fields), `checkpoint.py` (respect `recording_allowed`), `streaming/logging_redaction.py`
  (delegate to new detectors; DTMF semantics preserved), `tools/foreground.py` +
  `tools/client_binding.py` + `tools/contracts.py` (2 new tools + I/O models),
  `tools/scripts/export_pydantic_schemas.py` (registrations), `test_graph_topology.py` (+1 node).
- api new files under `Handlers/Security/`, `Common/Audit/`, `Behaviours/OperatorActionBehavior.cs`,
  `Api/Controllers/ConsentController.cs`, `Infrastructure/Security/` (enricher, secret store),
  `Infrastructure/Services/Retention/` (extension), `Domain/Interfaces/ISecretStore.cs`,
  `Domain/Exceptions/` (ConsentRequired/SessionNotFound as needed), `GlobalExceptionHandler`
  (additive cases), `Program.cs` (enricher registration), `ApplicationServiceCollectionExtensions`
  (behavior registration), `DeadLetterMessagesController` + `ReplayDeadLetterCommandHandler`
  (reason-code + audit retrofit).
- Type-gen consumers: `SharedTypes.cs`, TS `index.ts`/`zod.ts` regenerate (additive);
  api-contract regen after new endpoints (additive).

**Must stay byte-for-byte / behavior-identical** (from baseline audit §1–13,16):
EF tenant filter mechanism; `TenantResolutionMiddleware`; existing audit writers' rows;
`GrantAuthorizationService`; idempotency retention sweep; pipeline order for inbox consumers
(Validation → InboxDedup → Handler for `IInboxConsumerRequest`); existing exception mappings;
injection harness + menu extraction isolation; `ToolClient` gating; DTMF masking output;
existing job-event schemas; `IConsentGate` (notifications).

Anything not listed above is out of bounds; if a phase needs it, stop and re-plan.

## Cross-cutting requirements

- **Inv-13**: all new entities/queries tenant-scoped (no new entities planned; reads/writes go
  through existing scoped context); Phase 6 adds the structural cross-tenant test.
- **Inv-11**: no new mutation bypasses grants/roles; consent recording is session-scoped and
  tenant-gated (it *establishes* consent, it is not a booking mutation; validated + audited).
- **Inv-12**: redaction always-on for PAN/CVV; secrets only via `ISecretStore`; audit metadata
  sanitized; scrubbing enricher on every log line.
- **Inv-5**: `OperatorActionBehavior` must no-op for `IInboxConsumerRequest` traffic; pipeline
  registered Validation → OperatorAction → InboxDedup (operator commands are never inbox
  consumers; assert with a test).
- **Terminal-path discipline**: consent gate fail-closed — no policy/session ⇒ non-recorded path,
  never silent recording.
- **Files**: new `.cs` UTF-8 with BOM; one `nx` command at a time; tee test output to `.tmp/`.
- **Packages**: **none new** (regex-based detectors; Serilog enricher is a plain class).

## Phases

### Phase 1 — Shared security contracts (langgraph → C#/TS/Zod)

New `cordia_agent/contracts/security.py` (`extra="forbid"`, `AwareDatetime`):
`ConsentState` (consent_policy_version, jurisdiction_policy_id, outcome Literal matching the
pg-enum values, recording_allowed, captured_at, notice_channel Literal), `RedactionPolicy`
(mask_pan/mask_cvv/mask_government_id defaults True, configured_sensitive_fields,
mask_token="[REDACTED]"), `AuditEventEnvelope` (audit_event_id, tenant_id, correlation_id,
causation_id?, actor_type Literal, actor_id, action, target_type, target_id, reason_code?,
decision Literal, data_classes, occurred_at). Reuse `VerificationGrantView` — do NOT add
`VerificationGrantRef`. Register in `MODEL_REGISTRY["models"]`; regenerate schemas + types.
TDD: unit tests for round-trip/forbid/defaults first.

Automated verification:
- [x] `bun nx test langgraph` (706 passed)
- [x] `bun nx lint langgraph`
- [x] `bun nx run langgraph:generate-schemas` then `bun nx run shared-schemas:generate-types` (clean)
- [x] `bun nx build api` (Build succeeded, 0 errors; ConsentState lands in `Cordia.Domain.Generated` — no ambiguity, no file imports both namespaces today)

Discoveries and notable information:

- **Blast-radius addition (pre-existing defect, approved rationale):** `nodes/clarify.py` used
  glibc-only `%-d`/`%-I` strftime flags, crashing 7 tests on Windows ("Invalid format string")
  and blocking every `bun nx test langgraph` green-check. Fixed with portable equivalents
  (`dt.day`, 12-hour via `dt.hour % 12 or 12`) — output byte-identical on Linux; committed
  separately as a `fix:`. Unrelated to spec 14 (merged in PR #65).
- quicktype maps Pydantic `Literal[...]` to plain C# `string` properties, so the generated
  `ConsentState` class carries `Outcome` as `string` — the ratified pg-enum stays the only
  closed C# enum (`Cordia.Domain.Enums.ConsentState`).
- `VerificationGrantRef` from spec 14 §4.1 was deliberately not added; `VerificationGrantView`
  (already registered) is the grant reference contract.

### Phase 2 — Foreground redaction filter + consent gate node

`security/redaction.py`: `redact_text` / `redact_payload` with always-on detectors — PAN
(13–19 digit runs incl. separator-tolerant + Luhn), CVV-in-context, government-id patterns
(SSN-style, AU TFN-style), plus `RedactionPolicy.configured_sensitive_fields` key masking;
`logging_redaction.py` delegates (DTMF tests stay green). Wire pre-emit: checkpointer scrubs
`transcript_text` (always) and drops it entirely when `recording_allowed` is False;
`TranscriptFinalFrame` text scrubbed in the pipeline before yield.

`security/consent.py`: `consent_gate` node after `resolve_tenant` — calls
`resolve_consent_policy` tool; if notice required, emits notice + captures outcome; calls
`record_consent` tool (Booking API); declined ⇒ `recording_allowed=False` non-recorded path;
policy-resolution failure ⇒ fail-closed non-recorded. New tools in `tools/foreground.py` +
typed bindings + status enums (registered). State: additive consent fields on `AgentState`.
Graph/topology tests updated. TDD throughout.

Automated verification:
- [x] `bun nx test langgraph` (731 passed; +25 new security tests)
- [x] `bun nx lint langgraph`
- [x] `bun nx run langgraph:generate-schemas` + `bun nx run shared-schemas:generate-types` (5 new schema files; `bun nx build api` re-verified green)

Discoveries and notable information:

- Consent gate implemented as `consent_gate` between `resolve_tenant` and `load_context` with
  `route_after_consent_gate` → `load_context` | `END` (END = awaiting the customer's reply to
  the emitted notice). Declined consent maps to `alternative_offered` and **continues the
  conversation non-recorded** (§5.2 Active_NonRecorded) — no human_handoff edge needed.
- The customer's consent reply is read from `transcript_text` via a small yes/no classifier
  (decline wins on ambiguity, "no problem" is a grant) — no change to the strict
  `AgentInputState` contract was needed.
- Two new foreground tools (`resolve_consent_policy` PURE_READ, `record_consent`
  BOUNDED_SIDE_EFFECT) — tool tables now 19 entries, FOREGROUND_TOOLS 13; count-pinning tests
  updated deliberately.
- Checkpointer scrubs on persist only (in-memory state keeps the live transcript): PAN/CVV/
  gov-id always redacted; transcript dropped entirely when `recording_allowed is False`.
  `recording_allowed is None` (gate unresolved) still redacts — Inv-12 holds pre-consent.
- LangGraph resolves router type hints at runtime — `AgentState` must be imported for real in
  router modules, not under `TYPE_CHECKING`.
- `tests/unit_tests/contracts/test_preorder.py` had pre-existing ruff-format drift (surfaced by
  `lint langgraph`); reformatted in this phase's commit.

### Phase 3 — API consent vertical

`Handlers/Security/ResolveJurisdictionPolicy/` (query → `jurisdiction_policy_id`,
`consent_policy_version`, `notice_required`, `notice_text`, resolved by a new
`IJurisdictionPolicyProvider` Infrastructure service, config-backed, **fail-closed default =
notice required**). `Handlers/Security/RecordConsent/` (command: tenant + session + outcome +
policy ids → version-guarded update of `consent_*` on the existing row; **update-only**, typed
`ConversationSessionNotFoundException` → 404; appends `consent.recorded` audit event in the
same transaction via `IAuditWriter` from Phase 4 — or directly `IAuditEventRepository` if sequenced
first). Thin `ConsentController`: `GET /api/consent-policies`, `POST
/api/conversation-sessions/{sessionId}/consent`. FluentValidation on all inputs. Alba
integration tests incl. cross-tenant (tenant B cannot record consent on A's session) and
duplicate-post (version token) cases. `bun nx run api-contract:generate-types` after.

Automated verification:
- [x] `bun nx test api` (745 passed; +12 new security unit tests)
- [x] `bun nx lint api`
- [x] `bun nx test-integration api` (318 passed, 2 pre-existing skips)
- [x] `bun nx run api-contract:generate-types` (clean; openapi/v1.json updated)

Discoveries and notable information:

- **Name-collision correction to Phase 1:** the Pydantic `ConsentState` model was renamed
  `SessionConsentState`. The generated C# class collided with the ratified pg-enum
  `Cordia.Domain.Enums.ConsentState` inside EF migration Designer files (which import both
  namespaces and must never be edited). Wire JSON is unchanged — only the type name moved.
- The controller binds/returns the **generated** contracts (`SessionConsentState`,
  `ConsentPolicyView`, `ConsentRecordedResult`) directly, so agent↔API wire drift is impossible.
- `RecordConsent` is deliberately re-recordable (reconnect re-runs the gate, spec 14 §8) —
  last-write-wins with a version bump; integration test proves version increments.
- Postgres jsonb normalizes `audit_events.metadata` formatting — tests must parse JSON, not
  substring-match.
- **Blast-radius addition (pre-existing flake):** `ScheduledJobPollerTests` (2 tests) compared
  raw 100ns-tick `DateTimeOffset.UtcNow` against the pg-truncated 6-digit value — fails whenever
  the 7th fractional digit is non-zero. Added a `TruncateToMicroseconds` helper in that test
  file only. Unrelated to spec 14.
- Local Postgres (docker compose `cordia-postgres`) was not running; started it for the
  integration suites.

### Phase 4 — Audit writer + operator-action spine + reason-coded replay

`Application/Common/Audit/IAuditWriter` + impl: builds spec-12-shaped rows, packs
actor_type/reason_code/decision/data_classes/causation_id into sanitized `metadata` jsonb
(scrub PAN/CVV before serialize), `event_type = action`, `Actor` = actor_id, `SubjectRef` =
target. `IOperatorActionRequest` marker (ActorId, ReasonCode) + `OperatorActionBehavior`
(pipeline: Validation → OperatorAction → InboxDedup): rejects empty reason code
(ValidationException), appends allow-decision audit after handler success and deny audit on
typed authorization failures; asserts no-op for non-marker requests. Retrofit **only** DLQ
replay: `ReplayDeadLetterCommand` gains ActorId+ReasonCode (controller reads `X-Actor` +
`reason_code`), emits `operator.dlq_replayed` audit. Unit tests (Moq) for writer/behavior;
integration test proving replay writes the audit row and inbox consumers are unaffected.

Automated verification:
- [x] `bun nx test api` (762 passed; +17 new audit/behavior tests)
- [x] `bun nx lint api`
- [x] `bun nx test-integration api` (318 passed, 2 pre-existing skips)

Discoveries and notable information:

- **Allow/deny audit split:** the behavior writes only the DENY audit (effect rolled back, so a
  standalone denial record is truthful); the ALLOW audit is the handler's job inside the
  effect's transaction — an audit must never claim an effect that didn't commit. Documented on
  `IOperatorActionRequest`.
- DLQ replay already had a well-designed publish-before-mark audit; the retrofit only changed
  its identity (Actor: "system" → operator's `X-Actor`) and added reason_code + the spec-14
  metadata envelope via `IAuditWriter`. Event type `dead_letter.replayed` unchanged.
- Unexpected (non-Domain) exceptions are deliberately NOT audited as deny — the
  GlobalExceptionHandler still logs them; auditing an unknown crash as a "deny decision" would
  be untruthful.
- `AuditMetadataSanitizer` is a C# port of the Python PAN detector (separator-tolerant + Luhn,
  non-Luhn runs kept) so both lanes mask identically.
- Pipeline order is now Validation → OperatorAction → InboxDedup; the behavior no-ops for
  non-marker requests, proven by `NonOperatorRequest_PassesThroughUntouched` and the untouched
  `InboxDeduplicationBehaviorTests`.
- xUnit's `xUnit1051` analyzer (enforced by `dotnet format`) requires
  `TestContext.Current.CancellationToken` on awaited calls in tests.

### Phase 5 — Log scrubbing + secret custody + retention extension

`Infrastructure/Security/LogScrubbingEnricher` (Serilog `ILogEventEnricher` masking PAN/CVV/
credential-pattern property values + a destructuring guard) registered in `Program.cs`
alongside `ReadFrom.Configuration`; unit tests feed PAN/secret-bearing log events and assert
masked output. `Domain/Interfaces/ISecretStore.cs` + `Infrastructure/Security/
ConfigurationSecretStore.cs` (config/env-backed; values never in `ToString`/exceptions;
DI-registered; adapters keep their current config until a real provider needs it — plumbing +
tests only). Retention: extend `RetentionPolicy` + `RetentionSweepHandler` with (a) session
anonymization — ended sessions past window ⇒ `customer_ref` nulled, `privacy.retention.
session_anonymized` audit; (b) expired-and-consumed verification-grant pruning past window ⇒
`privacy.retention.grant_pruned` audit. Windows from configuration with the defaults approved
at the Stage-3 gate. Idempotency sweep untouched (regression tests re-run).

Automated verification:
- [x] `bun nx test api` (779 passed; +17 new tests)
- [x] `bun nx lint api`
- [x] `bun nx test-integration api` (320 passed, 2 pre-existing skips)

Discoveries and notable information:

- **Layering correction:** the PAN sanitizer moved to `Cordia.Domain/Security/SensitiveDataMask`
  (pure, dependency-free) because Application → Infrastructure means Infrastructure cannot use
  Application types; and the Serilog enricher lives in `Cordia.Api/Logging` (only Cordia.Api
  references Serilog — no new package added, matching "Api owns Program.cs/Serilog").
- Serilog enrichers cannot rewrite an attached `Exception` object; the compensating controls
  (documented on the enricher) are the `ISecretStore` never-in-message contract and the
  GlobalExceptionHandler's generic 500 for unmapped exceptions. `SecretNotFoundException` is
  deliberately unmapped.
- Session retention ANONYMIZES (`customer_ref` → null, row + consent evidence preserved) —
  never hard-deletes (§24.3); grant pruning only touches consumed/expired grants; audit_events
  is never pruned. Windows: 90d/30d defaults in `RetentionPolicy`, caller-overridable.
- No scheduled caller exists for any retention sweep (pre-existing: the idempotency sweep has
  none either) — wiring sweeps to `scheduled_jobs` is spec 05/12 follow-up work, not spec 14's.

### Phase 6 — §27.6 cross-cutting security test suite

api integration: **structural cross-tenant isolation test** (reflects over the EF model,
asserts every `ITenantScoped` entity carries the tenant query filter; plus a live two-tenant
read probe on representative aggregates); forged-caller-ID mutation rejected (no grant ⇒ 403);
expired/wrong-action/wrong-booking/wrong-tenant grants rejected (extend
`GrantAuthorizationServiceTests`); no-secret-in-envelope test (serialize representative job
envelopes + a thrown exception chain, assert no credential/PAN patterns). langgraph: prompt-
injection fixtures (menu/retrieved text with embedded instructions ⇒ no privileged tool call,
allowlist unchanged — through `is_tool_invocation_permitted` and the extraction agent);
consent-before-recording (recording_allowed=False ⇒ checkpoint contains no transcript);
redaction ingress/egress corpus test (PAN/CVV/gov-id/configured fields masked in checkpoint,
frames, and logs). Full-lane regression run.

Automated verification:
- [x] `bun nx test api` (779 passed)
- [x] `bun nx test-integration api` (330 passed, 2 pre-existing skips)
- [x] `bun nx test langgraph` (741 passed)
- [x] `bun nx lint api` and `bun nx lint langgraph` (both green, run separately)
- [x] `bun nx test web` (2 passed, regression only)

Discoveries and notable information:

- **Phase-2 leftover closed here:** transcript-frame scrubbing in the streaming pipeline
  (`_scrub_transcript_frame` before yield) had been promised in Phase 2 but not wired; done in
  this phase with a test proving PANs never leave in `TranscriptPartial/FinalFrame.text`.
- The structural isolation test found exactly one non-`ITenantScoped` mapped entity:
  `TenantChannelEndpoint` — correct by design (spec 15 tenant-RESOLUTION registry, looked up
  before tenant scope exists) and now on the test's justified allowlist, which acts as the
  merge gate for future unscoped entities.
- Grant scope failures (expired/wrong-action/wrong-booking/wrong-tenant/consumed/forged
  caller identity) are proven against the REAL repository SQL — the pre-existing unit suite
  only mocked it.
- The prompt-injection corpus exercises `is_tool_invocation_permitted` + the empty extraction
  allowlist + redaction end-to-end; ToolAllowlist immutability asserted structurally (no
  add/extend/update API).

## Manual verification checklist (post-implementation)

- [ ] Start `bun run dev`; open a session via the dev harness; confirm consent notice appears
      before any transcript-bearing turn; decline ⇒ conversation continues, checkpoint has no
      transcript (regression check for baseline #15).
- [ ] POST a DLQ replay without `reason_code` ⇒ 400; with it ⇒ 202 + `operator.dlq_replayed`
      row in `audit_events`.
- [ ] Grep live logs for a test PAN uttered in a dev conversation ⇒ only `[REDACTED]`.
- [ ] Regression: run an existing booking submission end-to-end (baseline #13 ToolClient
      unchanged; acceptance flow unaffected).
- [ ] Regression: notification delivery receipt still writes its existing audit event shape
      (baseline #3).

## Validation Report (Stage 6, 2026-07-09)

**Automated checks (final run, HEAD = 8ee3701):** api unit 779 ✓ · api integration 330 ✓
(2 pre-existing skips) · api lint ✓ · langgraph unit 741 ✓ · langgraph lint ✓ · web 2 ✓ ·
schema + C#/TS/Zod + api-contract regeneration clean · zero EF migrations (as planned).

### Regression diff against the Stage-1 baseline audit

Every **must-stay-byte-for-byte** item verified with `git diff 2f592d4..HEAD` — EMPTY diff for:
`TenantResolutionMiddleware`, `ApplicationDbContext` (tenant-filter mechanism),
`GrantAuthorizationService` + `VerificationGrantRepository`, `InboxDeduplicationBehavior`,
`AuditEvent` entity + insert-only repository, `safety/` (injection harness + TenantContext),
`menu/` (extraction isolation), `tools/client.py` (ToolClient gating). Their existing suites
are green in the final run.

**Intentionally changed** items match the plan exactly:
baseline #6 ConversationSession (consent update handlers added, update-only per D12);
#7 pipeline order (OperatorAction inserted, no-op for non-marker requests — inbox tests green);
#8 GlobalExceptionHandler (one additive 404 case); #9 Serilog (+scrubbing enricher);
#12 DTMF masking (delegates to redact_text, original outputs preserved — test green);
#14 graph topology (+consent_gate, topology test updated); #15 checkpointer (scrub/suppress).

**Release-blocking invariant re-check:** Inv-1 (injection corpus: untrusted text can never
invoke a tool), Inv-5 (record_consent is BOUNDED_SIDE_EFFECT/no-retry; inbox dedup untouched),
Inv-11 (grant scope failures proven against real SQL; forged identity finds no grant),
Inv-12 (redaction on transcript/checkpoint/frames/logs/audit-metadata in both lanes),
Inv-13 (structural filter test + live two-tenant probe), Inv-14/15 (audit append-only
untouched; replay preserves history and is now reason-coded + operator-attributed).

**Explained deviations (all documented in phase discoveries):** Pydantic model renamed
`ConsentState`→`SessionConsentState` (C# collision with the ratified pg-enum in immutable
migration designers; wire JSON unchanged); enricher lives in `Cordia.Api/Logging` not
Infrastructure (Serilog reference lives in Api; no new packages); three blast-radius additions
for pre-existing environment defects (`clarify.py` strftime portability, exporter LF newlines,
`ScheduledJobPollerTests` timestamp precision). No unexplained deviations. **Verdict: clean.**
