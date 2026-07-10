# Spec-14 sibling linkage — role taxonomy, consent-gated transcripts, model-trace sink

**Anchor:** `docs/specs/14-security-and-privacy.md` §3.1 / §5.1–5.2 / §4.3 / §12.
**Baseline:** `thoughts/shared/research/2026-07-10_23-30-00_spec-14-sibling-linkage-baseline.md`
(imports items 1–24 from the two prior baselines + adds items 25–33).
**Prior deliverables built on (not rebuilt):** `IObjectStore`/`EncryptedFileObjectStore`,
`IAuditWriter`/`AuditEnvelope`, `OperatorActionBehavior`/`IOperatorActionRequest`,
`ConsentRequiredException`, `TenantGuard`, `consent_gate`+`set_redaction_policy`,
`RetentionSweeper` — see `thoughts/shared/plans/2026-07-10_spec-14-gap-closure.md`.

Governing directive: integrate with existing mechanisms (single front doors), never add
parallel implementations. **Zero migrations** — transcripts/model traces key off the existing
`conversation_sessions` row + object-store sidecars.

## Gate decisions (Stage 3 — RESOLVED BEFORE ANY CODE)

- **GD-1 Role taxonomy (APPROVED 2026-07-11):** `{owner, operator}` with per-action matrix —
  menu lifecycle → owner; DLQ replay / resume / resolve / preorder resolution → operator;
  **owner satisfies operator** (owner ⊇ operator); unknown/blank role denies. Transcript read +
  model-trace view require operator (owner ok). Closes spec 14 §12 "Owner and internal
  operations roles" + spec 08 §12 item 2 + README deferred item.
- **GD-2 Role transport (APPROVED):** `X-Actor-Role` header shim, fail-closed — missing/blank →
  400 (validator), mismatch → 403 + deny-audit (behavior). Swaps for claims when real authn lands.
- **GD-3 TTLs (APPROVED):** `ObjectStore:TranscriptTtlDays=90` (aligned with the 90d session
  anonymization window), `ObjectStore:ModelTraceTtlDays=14` (strictly shorter, config-tested).
  Raw audio stays 72h.
- **GD-4 Audit/idempotency retention (APPROVED — closes §12 OD#2):** `audit_events` NEVER
  pruned (financial/security minimum; revisit only on a concrete compliance cap); idempotency
  records keep the existing live 90d config default. Zero code churn; ratified here + in the
  validation report.

## How this maps onto Cordia

| New concept | Reuses / extends | Consequence |
|---|---|---|
| Role taxonomy + check | `IOperatorActionRequest` + `OperatorActionBehavior` (spec-14-owned spine) | One new `StaffRole` static helper in `Application/Common/Authorization`; no new pipeline behavior |
| Role header | Existing `X-Actor` shim pattern in 4 staff controllers | `X-Actor-Role` header, fail-closed like actor/reason |
| Role deny audit | `IAuditWriter` fresh-scope deny pattern (GrantAuthorizationService precedent) | Behavior writes deny-audit before throwing `AuthorizationFailedException` |
| Transcript producer endpoint | `ConversationSessionsController` (D12 single-writer surface) + `IObjectStore` purpose `"transcript"` | `POST /api/conversation-sessions/{id}/transcript-turns`; fail-closed on `RecordingAllowed != true` |
| Transcript reader | spec-08 `RequireGrantAsync(ReadSensitive)` **as-is** OR role path; `ConsentRequiredException` (first thrower); `IAuditWriter` allow+deny | `GET /api/conversation-sessions/{id}/transcript` |
| Turn assembly | `IObjectStore` — **additive `ListIdsAsync`** (spec-14-owned seam) | Per-turn objects `{sessionId}.{seq:D6}`, lexicographic order |
| Producer agent-side | Consent-pair tool pattern (`foreground.py`/`client_binding.py`/`_timeouts.py`/`status_codes.py`/`contracts.py`) + pipeline seam `pipeline.py:95` | New canonical tool `append_transcript_turn` + optional `TranscriptPersister` port on `Pipeline` |
| Model-trace sink | Same tool pattern + same store (purpose `"model-trace"`) | New tool `record_model_trace`; new `cordia_agent/observability/model_trace.py` port (stub-backed) |
| Trace-view audit | spec 13 §4.3 telemetry-plane append permission | `model_trace.viewed` audit on GET, allow AND deny |
| Retention | Existing `RetentionSweeper` sweep-by-`expires_at` (purpose-agnostic) | TTLs set at `PutAsync` from config; zero sweeper changes |

## Spec ownership & frozen decisions

| Contract / mechanism | Position | Constraint |
|---|---|---|
| `IOperatorActionRequest`, `OperatorActionBehavior`, `StaffRole` | **Owned** (14) | Role values ⊂ ratified `actor_type` set {owner, operator} |
| `IObjectStore` (+`ListIdsAsync`) | **Owned** (14) | Additive only; Put/Get/Delete/Sweep byte-stable |
| Transcript/model-trace endpoints + audits | **Owned** (14; 13 §4.3 permits the trace-view append) | allow AND deny audited; data_classes per §5.1 |
| `RequireGrantAsync` / `VerificationActionEnum` | **Referenced** (08, D5) | Consume as-is; `ReadSensitive` only; NO new enum values; no service changes |
| `conversation_sessions` | **Referenced** (12/D12) | Read + object sidecars only; writes stay behind Booking API; ZERO DDL (D17) |
| Queue/envelope contracts | **Referenced** (05) | Untouched — transcript content NEVER in a queue payload (Inv-12) |
| `TenantContext` | **Referenced** (15, D15) | X-Tenant-Id shim unchanged |
| Tool I/O envelope | **Referenced** (03) | New tools follow the exact envelope/deadline/side-effect-class pattern |
| Invariants | Inv-1, Inv-11, Inv-12, Inv-13, Inv-14 | See Cross-cutting |

## Blast radius (Stage-4 boundary — nothing outside this list)

**Intentionally changing (api):**
- `Common/Authorization/IOperatorActionRequest.cs` (+`RequiredRole`, +`ActorRole`), new
  `Common/Authorization/StaffRole.cs`; `Behaviours/OperatorActionBehavior.cs` (role check +
  deny-audit); the **10 pre-named commands** + their validators + the **4 controllers**
  (Menu ×5 actions, Preorder ×2, BookingOperations ×2, DeadLetterMessages ×1) gain
  `X-Actor-Role`; `Cordia.Domain/Interfaces/IObjectStore.cs` (+`ListIdsAsync`) +
  `EncryptedFileObjectStore` (impl); `ConversationSessionsController` (+2 endpoints); new
  handlers `Handlers/Security/AppendTranscriptTurn/`, `Handlers/Security/GetTranscript/`,
  `Handlers/Security/RecordModelTrace/`, `Handlers/Security/GetModelTrace/`; new
  `ModelTracesController`; config keys `ObjectStore:TranscriptTtlDays`,
  `ObjectStore:ModelTraceTtlDays` (values per GD-3).
- **Named test updates (api):** the item-26 list — UnitTests/Menu ×4, PreorderManualResolutionHandlerTests,
  ResumeWorkflowStepCommandHandlerTests, ResolveManualReviewCommandHandlerTests,
  OperatorActionBehaviorTests, ReplayDeadLetterValidatorTests, MenuLifecycleEndToEndTests,
  PublishImmutabilityTests, MenuControllerTests, ManualRecoveryTests, ReplayDeadLetterTests
  (constructor arity + `X-Actor-Role` headers; EventType/Actor assertions unchanged).

**Intentionally changing (langgraph):**
- `tools/contracts.py` (+4 models), `tools/status_codes.py` (+2 enums), `tools/_timeouts.py`
  (+2 entries: 19→21), `tools/foreground.py` (+2 tools: 13→15), `tools/client_binding.py`
  (+2 call/parse pairs), `streaming/pipeline.py` (optional `TranscriptPersister` port hook at
  the `TranscriptFinalFrame` branch), new `observability/model_trace.py`,
  `tools/scripts/export_pydantic_schemas.py` registries.
- **Named test updates (langgraph):** `test_client_tenant_gate.py` (19→21 + docstring),
  `test_client_tool_gate.py` ("19" comments), `test_foreground_tools.py`
  (EXPECTED_TOOL_NAMES/count/docstring/body-inspection).

**Type-generation consumers:** shared-schemas regen (C# for the 4 new models — consumed by the
new controllers/handlers; TS/Zod additive, web unaffected); api-contract regen (new endpoints).

**Must stay byte-for-byte (regression contract):** items 1–24 + 27–33 of the baseline;
envelope/outbox shape; inbox semantics; hosted services; waitlist endpoints; existing 19 tools'
gate/retry/header behavior; `GrantAuthorizationService`; RecordConsent vertical; checkpointer
scrubbing; store blob layout/sweep audits; sanitizer no-op; every asserted EventType/Actor pair.

## Phases

### Phase 1 — WI1: role taxonomy on the operator spine (api)
Per GD-1/GD-2. `StaffRole` helper (`Owner`/`Operator` constants + `Satisfies(provided,
required)`: owner satisfies both; operator satisfies operator only; anything else denies).
`IOperatorActionRequest` += `string RequiredRole` (static per command: menu ×5 → owner;
preorder ×2, resume/resolve, DLQ replay → operator) and `string ActorRole` (from header).
`OperatorActionBehavior`: after the existing presence backstop, `!StaffRole.Satisfies(ActorRole,
RequiredRole)` → **deny-audit via fresh scope** (Decision deny, ReasonCode preserved, Extra
`{provided_role, required_role}`) + `AuthorizationFailedException` (403). Validators += NotEmpty
on ActorRole (missing → 400, matching actor/reason). Controllers += `X-Reason-Code`-style
`[FromHeader("X-Actor-Role")]`, pass `?? string.Empty`. Handlers' ALLOW audits keep their
EventTypes; `ActorType` now reflects the provided role. TDD: behavior role-check tests first.
Update the item-26 test files (arity + headers + one new deny-403 test per controller family).

Automated verification:
- [x] `bun nx test api` (847 passed) · `bun nx lint api` · `bun nx test-integration api` (340 passed, 2 skipped) · `bun nx run api-contract:generate-types`

Discoveries and notable information:
- The behavior's role-mismatch deny-audit uses the SAME fresh-scope pattern as its
  DomainException deny and `GrantAuthorizationService` — three deny writers, one idiom.
  An unrecognized asserted role is recorded with `ActorType: "operator"` (the ratified set
  can't carry arbitrary strings) and the raw value in `Extra.provided_role`.
- Operator-family handlers (preorder ×2, resume/resolve, DLQ replay) now stamp
  `ActorType: command.ActorRole` on ALLOW audits, since an owner may legitimately perform
  them (owner ⊇ operator). Menu handlers keep the hardcoded `"owner"` (only owners pass).
  EventTypes/Actor columns unchanged — assertion-compatible with every existing test.
- Test churn matched the pre-named exception exactly: 12 compile-touched files + 2
  HTTP-header files (MenuControllerTests, ManualRecoveryTests endpoint block). One
  batch-replace pitfall: `"rc_test")` also matched Alba's `WithRequestHeader(...)` lines in
  ManualRecoveryTests — corrupted 2 lines, caught by the compiler, fixed into proper
  separate `X-Actor-Role` headers.
- New coverage: `StaffRoleTests` (matrix), behavior role-deny (audit + 403 + handler-never-
  ran), owner-satisfies-operator pass, `Approve_WithOperatorRole_Returns403_AndDoesNotAdvance`
  (HTTP, asserts no state advance + deny on the trail), DLQ validator role rule.
- Environment note: stray `Cordia.Api` processes (leaked by earlier OpenAPI exports) lock
  build outputs on Windows — kill `Get-Process Cordia.Api` before build/test when MSB3027
  "file locked" appears.

### Phase 2 — WI2a(api): store listing + transcript producer endpoint
`IObjectStore.ListIdsAsync(tenantId, purpose, prefix, ct)` → sorted object ids;
`EncryptedFileObjectStore` impl (allow-list validates tenant/purpose; prefix validated with the
same segment rule minus length; enumerate `{root}/{tenant}/{purpose}/{prefix}*.bin`).
`AppendTranscriptTurnCommand(TenantId, SessionId, Seq, TurnId, Speaker, Text)` + validator; handler:
session via `IConversationSessionRepository.GetAsync` → null ⇒ 404; `RecordingAllowed != true`
⇒ **persist nothing**, return `persisted=false` (§5.2 "transcript suppressed" — the producer
path is not an error; the READER is where consent absence throws); else defense-in-depth
`SensitiveDataMask.Sanitize(Text)` and `PutAsync(tenant, "transcript", $"{sessionId}.{Seq:D6}",
json{turn_id,speaker,text,ts}, ttl: TranscriptTtlDays)` (GD-3). No per-turn audit row
(rationale: the turn IS the consented data plane; `consent.recorded` + sidecars provide the
trail — one `transcript.recording_started` audit on the session's FIRST persisted turn instead).
Endpoint `POST /api/conversation-sessions/{sessionId}/transcript-turns` (TenantGuard; agent-plane
write, no grant — mirrors the consent endpoint's trust model). Tests: fail-closed (absent/false/
null consent ⇒ no object), happy path round-trip, idempotent overwrite on same seq, cross-tenant
isolation, PAN-in-text masked at rest.

Automated verification:
- [x] `bun nx test api` (857 passed) · `bun nx lint api` · `bun nx test-integration api` (345 passed, 2 skipped) · `bun nx run api-contract:generate-types`

Discoveries and notable information:
- GD-3 TTLs are applied as **purpose defaults inside `EncryptedFileObjectStore.PutAsync`**
  (extending the existing raw-audio precedent) rather than resolved by the handler — the
  Application layer never reads config, and every producer of a sensitive purpose inherits
  the fail-closed default automatically. One pre-existing store test intentionally updated:
  "transcript gets no implicit TTL" became "purposes WITHOUT a GD-3 default get none"
  (asserted with a neutral `attachment` purpose) + a new strictly-shorter trace-vs-transcript
  assertion.
- The Alba test host needed object-store config: `appsettings.Test.json` gains
  `ObjectStore:Root=.tmp/objectstore-test` + a clearly-labeled TEST-ONLY base64 AES key under
  `Secrets:ObjectStoreEncryptionKey` (the store's lazy key resolution otherwise 500s on first
  producer call in integration).
- `ListIdsAsync` validates the prefix with the same allow-list char class as full segments
  but permits empty (list-all); ids only, never content.
- Producer suppression outcome is HTTP 200 `persisted=false` — kept deliberately non-erroring
  per §5.2 (the pipeline must not treat consent-suppression as a failure and retry it).
- Recurring Windows footgun confirmed: EVERY `api-contract:generate-types` run (including the
  pre-commit hook) leaks a live `Cordia.Api` process that locks bin outputs — kill
  `Get-Process Cordia.Api` before the next build/test.

### Phase 3 — WI2b(api): consent-gated transcript reader (§3.1 spine, first ConsentRequired thrower)
`GetTranscriptQuery(TenantId, SessionId, GrantId?, ActorId?, ActorRole?, ReasonCode?)`. Handler
order = §3.1: (1) tenant already guarded at controller; (2/3) **grant path** — `GrantId`
present ⇒ `RequireGrantAsync(tenant, grantId, session.CustomerRef ?? "", ReadSensitive,
targetBookingId: null)` (deny audited inside the service, 403 propagates); **role path** —
else require ActorId + ReasonCode + `StaffRole.Satisfies(ActorRole, Operator)`, failure ⇒
deny-audit (`read_sensitive_transcript`, decision deny) + `AuthorizationFailedException`;
neither ⇒ same deny + 403 (Inv-11: session identity alone NEVER reads). (4) consent:
`RecordingAllowed != true` ⇒ deny-audit (reason `consent_absent`) + **`ConsentRequiredException
("transcript", sessionId)`** → 409. (5) assemble via `ListIdsAsync` prefix `{sessionId}.` +
`GetAsync` each (expired ⇒ skipped by read-time expiry); ALLOW audit via `IAuditWriter`
(`action=read_sensitive_transcript`, `TargetType=conversation_session`,
`DataClasses=["pii","transcript"]`, ActorType = role or "customer" for grant path).
Endpoint `GET /api/conversation-sessions/{sessionId}/transcript` (TenantGuard; `X-Grant-Id`
header/`grant_id` query per BookingsController precedent; `X-Actor`/`X-Actor-Role`/
`X-Reason-Code` for staff). Tests: grant happy path; expired/wrong-tenant grant 403; role happy
path (operator + owner); wrong role 403 + deny audit; no-credentials 403; consent declined 409
+ deny audit; consent never-recorded 409; allow audit row shape; cross-tenant 404/isolation;
GlobalExceptionHandler already maps ConsentRequired → 409 (prior run).

Automated verification:
- [x] `bun nx test api` (866 passed) · `bun nx lint api` · `bun nx test-integration api` (350 passed, 2 skipped) · `bun nx run api-contract:generate-types`

Discoveries and notable information:
- Authorization deliberately runs BEFORE the consent check so an unauthorized caller never
  learns the session's consent state (403 first, 409 only for authorized readers).
- The grant path audits `ActorType: "customer"` / `ActorId: session.CustomerRef` (the grant
  proves the customer is acting); a session without a `CustomerRef` simply cannot be
  grant-read — `RequireGrantAsync` denies against the empty ref and 403s, spec-08 machinery
  untouched.
- The reader sorts turns by seq ITSELF rather than trusting the store's id sort — a mock
  returning unsorted ids exposed the implicit dependency; transcript meaning depends on
  order, so the reader owns the guarantee.
- jsonb-normalization pitfall re-confirmed: audit `Metadata` must be parsed, never
  substring-matched (`"decision": "allow"` carries a space in Postgres).
- The `transcript.recording_started` + `read_sensitive_transcript` (allow AND deny) audit
  population all flow through `IAuditWriter`; the §5.1 diagram is now implemented end-to-end
  (consent → producer → grant-or-role read → minimized redacted projection → audit).

### Phase 4 — WI2a(langgraph): producer tool + pipeline hook (stub-proven)
`TranscriptTurnInput(session_id, seq, turn_id, speaker, text)` /
`TranscriptTurnPersisted(session_id, seq, persisted)` in `tools/contracts.py` (+
`AppendTranscriptTurnStatus` in status_codes) + registries. Tool `append_transcript_turn`
(BOUNDED_SIDE_EFFECT, deadline 0.15, POST the Phase-2 endpoint) + `client_binding` pair.
**Text passes `redact_text` BEFORE the tool call** (never raw — Inv-12; API re-sanitizes).
`Pipeline` gains optional `transcript_persister` port (protocol: `persist(frame) -> None`)
invoked fire-and-forget on the `TranscriptFinalFrame` branch AFTER scrub; default `None` = today's
behavior byte-for-byte. Provided impl `ToolTranscriptPersister` consults `recording_allowed`
(supplied by the hosting layer from agent state — fail-closed None/False ⇒ no call) and never
raises into the pipe. Stub producer test: fake port asserts (a) called only with scrubbed final
frames when allowed, (b) never called when consent absent/declined, (c) a raising persister
doesn't break the turn. Named count-test updates (19→20, 13→14). Regen schemas + types.

Automated verification:
- [x] `bun nx test langgraph` (807 passed) · `bun nx lint langgraph` · `bun nx run langgraph:generate-schemas` + `bun nx run shared-schemas:generate-types` · `bun nx build api` · `bun nx test api` (866)

Discoveries and notable information:
- **Latent scrub-vs-agent split surfaced**: `pipeline.run` yields the SCRUBBED frame but hands
  the RAW final to `_run_agent` (in-boundary, intentional). The persister hook therefore
  captures the scrubbed copy explicitly and never sees the raw frame — pinned by a test that
  feeds a PAN through fake STT and asserts `[REDACTED]` at the persister.
- Two distinct fail postures documented on the port: fail-CLOSED for persistence (consent
  unknown/false ⇒ no call; server re-enforces authoritatively) and fail-OPEN for the pipe
  (a raising persister is swallowed + logged; the caller's turn always completes).
- `transcript_persister=None` default keeps the pipeline byte-identical (regression item 29
  upheld; pinned by test).
- Named count updates landed exactly as enumerated: `ALL_TOOLS` 19→20,
  `FOREGROUND_TOOLS`/`EXPECTED_TOOL_NAMES` 13→14, stale "19" comments refreshed. The
  tool-gate no-op loop iterates `ALL_TOOLS` so it extends automatically.
- Exporter registry gotcha: blanket text replace duplicated `TranscriptTurn*` into
  `MODEL_REGISTRY` (import block AND registry share the `ToolResultEnvelope,` anchor) —
  `validate_no_duplicates` would have caught it at regen; fixed pre-regen.

### Phase 5 — WI3: model-trace sink (api + langgraph, stub-backed)
API: `RecordModelTraceCommand(TenantId, TraceId, SessionId?, Kind[prompt|output], Content)` →
sanitize + `PutAsync(tenant, "model-trace", traceId, ..., ttl: ModelTraceTtlDays)` (GD-3;
STRICTLY shorter than transcript TTL, enforced by a config-validation test); no consent gate
(traces are telemetry, not recordings) but NEVER app-logged. `GetModelTraceQuery` — staff role
path ONLY (no grant analog; require ActorId+ActorRole satisfies Operator+ReasonCode) → allow/deny
audit `model_trace.viewed` (`DataClasses=["model_trace"]`; the spec-13 §4.3 telemetry-plane
append). `ModelTracesController` (`POST /api/model-traces`, `GET /api/model-traces/{traceId}`).
Python: `cordia_agent/observability/model_trace.py` — `ModelTraceSink` protocol +
`ApiModelTraceSink` (tool `record_model_trace`, content through `redact_text`) + `NullSink`
default; NOTHING calls it in production yet (no LLM nodes) — port + tests only. Contracts +
registries + named count updates (20→21, 14→15). Regen all.

Automated verification:
- [x] `bun nx test api` (872 passed) · `bun nx lint api` · `bun nx test-integration api` (354 passed, 2 skipped) · `bun nx test langgraph` (810 passed) · `bun nx lint langgraph` · regen schemas/types + api-contract

Discoveries and notable information:
- Trace WRITES are deliberately un-audited (high-volume telemetry data plane); every VIEW is
  audited (`model_trace.viewed`) **including views of traces that don't exist** — spec 13
  §4.3's "who viewed model traces" wants the attempt, not just the hit (pinned by a test
  asserting the allow audit with `found=false` in Extra).
- The GET has NO grant analog (telemetry has no customer subject) — staff-role-only, the one
  §3.1-spine surface where step 3's grant alternative is intentionally absent.
- `ApiModelTraceSink` is triple-redundant on Inv-12 (agent redact → sink redact → API
  sanitize) and swallows its own failures — telemetry never breaks the model call it
  observes (spec 13 fail-open for the business path). `NullModelTraceSink` is the default;
  nothing invokes the sink in production (no LLM nodes yet) — stub-backed as scoped.
- Cross-tenant trace read proven 404 via the store's tenant path isolation (Inv-13) — no
  DB row exists to filter, the FILESYSTEM path is the isolation boundary.
- Final tool counts: `ALL_TOOLS` 21, `FOREGROUND_TOOLS` 15.

### Phase 6 — Stage-6 validation + explicit regression diff
Full lanes + item-by-item re-check of the regression contract: baseline items 1–24 and 25–33;
envelope/outbox untouched (`git diff` scoped check); no `IInboxConsumerRequest` change; hosted
services untouched; waitlist endpoints untouched; existing-19-tool behavior (tool-gate no-op
test still green); every EventType/Actor pair outside the named exception; sanitizer no-op
vectors; zero migrations (`Migrations/` diff empty); BOM check on new `.cs`. Validation report
appended here; invariant re-check (Inv-1/5/11/12/13/14).

Automated verification:
- [ ] `bun nx test api` · `bun nx test-integration api` · `bun nx test langgraph` · `bun nx lint api` · `bun nx lint langgraph` · `bun nx test web`

Discoveries and notable information:
- _(implementer)_

## Cross-cutting requirements
- **Inv-13**: every new endpoint behind `TenantGuard`; store paths tenant-isolated (existing);
  cross-tenant tests per phase. **Inv-11**: reader/trace-view require grant-or-role; session or
  customer identity alone NEVER authorizes; forged-caller test included. **Inv-12**: producer
  content `redact_text`'d agent-side AND `SensitiveDataMask.Sanitize`'d API-side; transcript
  content never enters queue payloads/logs/exceptions (object IDS are fine); PAN-at-rest test.
  **Inv-1**: producer persists only — no command emission, no state mutation beyond the object.
  **Inv-14/§24.3**: allow AND deny audited; deletion only via `RetentionSweeper` TTL sweep.
  **Inv-5**: no queue consumers touched. Fail-closed everywhere (missing role/actor/reason/
  consent/grant ⇒ reject or suppress, never default-allow).
- Zero migrations; UTF-8 BOM; FluentValidation sole required-field enforcement; one nx command
  at a time; tee to `.tmp/`.

## What we're NOT doing (carried forward + this run)
Real staff authentication (X-Actor-Role is a shim, same trust model as X-Actor); workflow-
progress audit migration; cloud object-store providers; spec-15 TenantContext changes; waitlist
operator-ization; DDL / artifact-metadata table; real STT/TTS/media providers; LLM prompt-
assembly or OTel build-out (WI3 is port+API only); apps/web UI; channel-callback transport; DR;
spec-08 verification-factor/risk-policy decision; jurisdiction retention ENGINE (values only,
config-driven per GD-3/GD-4); audio producer endpoint (raw-audio seam exists; media plane is
stubbed — transcripts only this run); `GrantAuthorizationService` internals (referenced, not
owned); `OwnerOverride` grant consumption (stays a spec-08 deferral — the role path here is the
§3.1 "owner/operator role" alternative, not a grant type).

## Package notes
None. No new NuGet/uv/npm dependencies.
