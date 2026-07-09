# Spec 14 — Gap Closure Plan (remaining ~30-40%)

**Date:** 2026-07-10 · **Branch:** `feature/spec-14` (HEAD `d6270f3`, all suites green)
**Spec:** `docs/specs/14-security-and-privacy.md` · **Baseline:**
`thoughts/shared/research/2026-07-10_01-05-31_spec-14-gap-closure-baseline.md` (+ the
2026-07-09 baseline, still in force)
**Status:** IN PROGRESS

Governing directive unchanged: integrate with existing mechanisms; no parallel implementations;
zero DDL (spec 12 owns; the artifact-metadata table stays a tracked deferred item).

**Stage-3 gate decisions (user-approved 2026-07-10):** all 8 phases, commit per phase.
Audit strategy = interceptor + selective migration (workflow-progress events stay
direct-but-sanitized; only staff handlers move to IAuditWriter). Staff endpoints go
fail-closed: X-Actor + reasonCode mandatory (breaking for dev flows — intentional).
Object store = filesystem AES-256-GCM seam behind IObjectStore, raw-audio TTL 72h.

## How this maps onto Cordia

| Gap | Reuses/extends | Consequence |
|---|---|---|
| Retention not scheduled | Hosted-service pattern (`Messaging:*:Enabled` gate ×7 precedents) + `RetentionSweepHandler` | New `IRetentionSweeper` seam + `RetentionSweepHostedService`; tenant enumeration (new, kept inside the seam); tests disable via config like the other 7 |
| Audit inconsistency | `AuditWriter` + EF interceptor pattern (SaveChanges hooks) | **Tier A:** `AuditEventSanitizationInterceptor` masks Metadata on EVERY AuditEvent insert (one choke point — closes Inv-12 across all 35+ writers with no per-site churn). **Tier B:** shared `AuditEnvelope.Build` core; only security-decision writers migrate to `IAuditWriter` (operator/staff in P3); workflow-progress events stay direct-but-sanitized (documented norm) |
| Operator spine narrow | `IOperatorActionRequest` + `OperatorActionBehavior` (proven on DLQ replay) | 9 staff commands gain ReasonCode + marker; controllers drop silent actor defaults (fail-closed); handler allow-audits go through `IAuditWriter` |
| ISecretStore unused | `ConfigurationSecretStore` + webhook constant-time compare | Webhook secret + object-store key resolved via `ISecretStore` (2 real consumers) |
| No object store | — (greenfield seam) | Domain `IObjectStore` + `EncryptedFileObjectStore` (AES-256-GCM, key via `GetRequiredSecret`, tenant-scoped paths, TTL metadata sidecars, sweep wired into the retention service). NO DDL |
| Typed exceptions missing | `DomainException` + `GlobalExceptionHandler` switch | `TenantScopeMissingException` (→400, thrown by a shared `TenantGuard`), `AuthorizationFailedException` (→403, OperatorActionBehavior backstop), `ConsentRequiredException` (→409, defined+mapped; consumer documented as spec-02 recording flow) |
| Queue payloads unredacted | `SensitiveDataMask` + Python `redact_payload` design | C# `SensitiveDataMask.SanitizeJson` (JsonNode walk, control-plane key exemptions `*_id/_key/_hash/_ref/_version`) applied in `OutboxRepository.Add*` + `ScheduledJobRepository` add path |
| Harness not wired | `ToolClient` gate pattern (steps 1/1b) + `injection_harness` | Step 1c: canonical-tool check (ALL_TOOLS) always; optional tenant `ToolAllowlist` via explicit `set_tool_allowlist` (NOT derived from `allowed_policy_ids` — semantics documented as broken scaffolding); `tool_not_permitted` envelope mirrors existing constructors |
| Redaction policy not loaded | `ConsentPolicyView` (spec-14 owned) + `Redaction:SensitiveFields` config | Optional `redaction: RedactionPolicy` on ConsentPolicyView; API serves it from config; consent_gate installs via `set_redaction_policy` at session start |
| No channel-separation enforcement | `LabeledContent`/`TrustLevel` | `security/channels.py`: `PromptChannels` validating trust-per-channel (raises on UNTRUSTED in system); extraction agent adopts it; mandated for future LLM nodes |

## Spec ownership & frozen decisions (delta)

Owned: audit write path (D13), `ConsentPolicyView` (+`redaction`), object-store control,
typed security exceptions, retention workflows. Referenced byte-for-byte: envelope/outbox
CONTRACT (05 — we sanitize free-text values only, never shape; control-plane keys exempt so
consumer id-extraction keeps working), signed `TenantContext` (15 — frozen, untouched),
grants (08), workflow handlers' business logic (06/10/11 — only their audit/actor plumbing
changes), DDL (12 — none). Invariants: Inv-11/12/13 primary; Inv-2/5 must not regress
(payload sanitizer id-exemptions; inbox consumers never get operator semantics).

## What we're NOT doing

- No role taxonomy / real authn (X-Actor + reason code stays the approved shim).
- No migration of workflow-progress audit events to IAuditWriter (documented two-population
  norm; interceptor gives them Inv-12).
- No cloud object-store provider (filesystem AES-GCM impl is the seam; provider swap later).
- No spec-15 TenantContext changes; no waitlist customer-endpoint operator-ization.
- No DDL, no artifact-metadata table (tracked deferred item).
- No LLM prompt-assembly build-out beyond the channel assembler (no LLM nodes exist yet).

## Blast radius

**Intentionally changed (api):** new `Messaging/RetentionSweepHostedService` + `IRetentionSweeper`
seam + MessagingOptions section; `EntityFramework/Interceptors/AuditEventSanitizationInterceptor`
(+ DbContext registration); `Common/Audit/` (envelope core extraction); 9 staff command records +
validators + handlers (Menu×5, Preorder ManualResolution×2, ManualRecovery×2) + their three
controllers (actor defaults removed, reasonCode added) + their unit/integration tests;
`PreorderSagaSupport.Audit` + StartSaga helper (route through envelope core, keep EventTypes);
`Domain/Exceptions/` (3 new) + `GlobalExceptionHandler` (additive cases) + `Api/TenantGuard`
helper adopted by controllers with hand-rolled tenant checks; `NotificationWebhooksController`
(secret via ISecretStore w/ config fallback); `Domain/Security/SensitiveDataMask.SanitizeJson`;
`OutboxRepository` + `ScheduledJobRepository` (sanitize on add); new `Domain/Interfaces/IObjectStore`
+ `Infrastructure/Storage/EncryptedFileObjectStore` + DI + retention wiring; appsettings Test
overrides for the new hosted service.
**Intentionally changed (langgraph):** `tools/client.py` (gate 1c) + `tools/errors.py`
(`tool_not_permitted_envelope`) + tests; `security/channels.py` (new) + `menu/extraction_agent.py`
(adopt channels) + tests; `contracts/security.py` untouched — `ConsentPolicyView` in
`tools/contracts.py` gains optional `redaction`; `security/consent.py` installs policy;
exporter regen (schemas → C#/TS/Zod) + api-contract regen.
**Must stay byte-for-byte:** all items 1–16 of the 2026-07-09 baseline + delta items 17–24;
envelope/outbox shape; inbox consumer semantics; existing hosted services; waitlist endpoints;
ToolClient behavior for all 19 canonical tools with no allowlist set; every asserted
EventType/Actor pair (unless the plan names the test update).

## Cross-cutting

Zero migrations; UTF-8 BOM on new `.cs`; one nx command at a time; **no new packages**
(AES-GCM = `System.Security.Cryptography`); inbox consumers never `IOperatorActionRequest`;
payload sanitizer must be a no-op on every existing payload fixture (id/hash preservation
tests); fail-closed defaults throughout (missing object-store key → typed error on use;
missing actor/reason → reject).

## Phases

### Phase 1 — Retention scheduling (api)
`IRetentionSweeper` (Domain seam: `SweepAllTenantsAsync(now, ct)`), Infrastructure impl
wrapping `RetentionSweepHandler` + distinct-tenant enumeration (union of the three swept
tables, `IgnoreQueryFilters`), `RetentionSweepHostedService` (gate `Messaging:RetentionSweep:Enabled`,
`IntervalHours` default 24, initial delay, per-tenant try/catch + logging). Object-store sweep
hook added in P6. Tests: unit (enumeration + per-tenant fan-out with mocks), integration
(seed 2 tenants' expired rows → one `SweepAllTenantsAsync` call prunes both + audits), config
gate off in the ~existing Alba overrides that would otherwise race (add key only where needed).

Automated verification:
- [x] `bun nx test api` (796) · `bun nx lint api` · `bun nx test-integration api` (332)

Discoveries and notable information:

- `PollingLoop.RunAsync` + `SweepOnceAsync`-public-for-tests mirrors StepSweeper exactly; the
  new service adds an `InitialDelaySeconds` (300) so a boot doesn't immediately run a
  cross-tenant scan — no precedent needed changing.
- Tenant enumeration = distinct union over the three swept tables (`IgnoreQueryFilters`),
  inside the `IRetentionSweeper` seam as planned. Hosted services only run under a Host, so
  the mediator-container tests (`BuildContainer`) needed no new config keys — only
  appsettings.Test.json gained `RetentionSweep:Enabled=false`.

### Phase 2 — Uniform audit sanitization + envelope core (api)
`AuditEventSanitizationInterceptor` (`SaveChangesInterceptor`: every Added `AuditEvent` gets
`Metadata = SensitiveDataMask.Sanitize(Metadata)`; also normalize empty CorrelationId → null)
registered on the DbContext (both prod and test fixture contexts). Extract
`AuditEnvelope.Build(AuditRecord) : AuditEvent` static core from AuditWriter; AuditWriter
delegates. `PreorderSagaSupport.Audit` + StartSaga's private helper route through the core
where shapes allow WITHOUT changing their EventType/Actor/metadata keys (else stay direct —
interceptor covers Inv-12 regardless). Document the two-population norm on IAuditWriter.
Tests: interceptor masks a PAN planted in any writer's metadata (pick an untested EventType
site), ids/hashes in metadata survive, existing audit assertions all green.

Automated verification:
- [x] `bun nx test api` (796) · `bun nx lint api` · `bun nx test-integration api` (333)

Discoveries and notable information:

- Interceptor registered in `ApplicationDbContext.OnConfiguring` (not DI) so every context —
  production, both test fixtures, design-time — gets it with zero extra wiring.
- `PreorderSagaSupport.Audit`/StartSaga helper were deliberately NOT routed through
  `AuditEnvelope.Build`: forcing the envelope keys into their metadata would churn asserted
  shapes for zero Inv-12 gain (the interceptor already covers them). The two-population norm
  is documented on `IAuditWriter`/`AuditWriter`.
- Skipped the planned empty-CorrelationId→null normalization — some writers legitimately set
  non-null empties and no consumer distinguishes; not worth the assertion churn.

### Phase 3 — Operator spine expansion (api)
Add `ReasonCode` + `IOperatorActionRequest` to: UploadMenu, ReviewCatalog, EditCatalogItems,
ApproveCatalog, PublishCatalog, ConvertPreorderToStandalone, CancelPreorderManualReview,
ResumeWorkflowStep, ResolveManualReview. Validators: ActorId/Actor + ReasonCode NotEmpty.
Controllers: drop silent actor defaults; read reasonCode (body field or query per endpoint
shape); missing → 400 via validator/behavior. Handlers: allow-audits move to `IAuditWriter`
(EventTypes unchanged; envelope adds actor_type=owner|operator, reason_code, decision=allow,
data_classes). OperatorActionBehavior backstop throws `AuthorizationFailedException` (403)
instead of ValidationException (mapped in P4 — sequence: introduce exception first here,
mapping lands same phase to keep tests green). Update the affected unit/integration tests
(inventoried; ~15 files).

Automated verification:
- [x] `bun nx test api` (796 passed) · `bun nx lint api` · `bun nx test-integration api` (335 passed, 2 skipped) · `bun nx run api-contract:generate-types`

Discoveries and notable information:

- Reason code is transported as an **`X-Reason-Code` header** on all nine staff endpoints
  (menu ×5, preorder ×2, booking-operations resume/resolve) — consistent with the X-Actor
  shim, keeps body-less endpoints body-less; the plan's "body field or query" wording is
  superseded.
- `AuthorizationFailedException` (→403, `Cordia.Domain.Exceptions`) landed in THIS phase
  including the GlobalExceptionHandler mapping (the plan's "mapping lands same phase" note);
  OperatorActionBehavior's backstop now throws it instead of FluentValidation
  `ValidationException`. In practice the per-command validators fire first (pipeline order
  Validation → OperatorAction), so missing headers surface as **400** at the HTTP layer and
  the 403 backstop covers commands whose validators don't guard actor/reason.
- `ResumeWorkflowStepCommand` / `ResolveManualReviewCommand` had **no validators at all** —
  added `ResumeWorkflowStepValidator` / `ResolveManualReviewValidator` (tenant, operation,
  replayCommandId, actor, reason code).
- Silent actor defaults removed: Menu `"owner"`, Preorder `"operator"`, BookingOperations
  `"ops"` — absent X-Actor now flows through empty and fails closed.
- `ConvertPreorderToStandalone` keeps its OperationId in the envelope; `AuditRecord` already
  carried an `OperationId` field so no envelope change was needed.
- Fail-closed regression tests added: `Approve_MissingActorAndReasonHeaders_IsRejected`
  (MenuControllerTests) and `Resolve_MissingActorAndReasonHeaders_IsRejected`
  (ManualRecoveryEndpointTests), both asserting rejection AND no state advance.
- 403 added to `ProducesResponseType` on all nine endpoints (OpenAPI contract regenerated).
- Test-file churn was 10 files (not ~15): 4 Menu unit test files were already retrofitted in
  Goal-1 work; PreorderController has no HTTP-level test file.

### Phase 4 — Secrets consumption + typed exceptions (api)
`NotificationWebhooksController` resolves the shared secret via
`ISecretStore.GetSecret("NotificationsWebhookSecret")` falling back to the legacy
`Notifications:WebhookSecret` config key (constant-time compare preserved; tests extended for
both sources). New exceptions: `TenantScopeMissingException` (→400) thrown by new
`TenantGuard.RequireTenant(ITenantContext)` adopted by the controllers that hand-roll the
check; `AuthorizationFailedException` (→403; used by OperatorActionBehavior from P3);
`ConsentRequiredException` (→409; defined + mapped + documented consumer). GlobalExceptionHandler
cases + tests; controller 400 bodies keep title "Missing tenant".

Automated verification:
- [x] `bun nx test api` (802 passed) · `bun nx lint api` · `bun nx test-integration api` (337 passed, 2 skipped)

Discoveries and notable information:

- `TenantGuard` lives in **`Cordia.Domain.Security`** (next to `SensitiveDataMask`) — it
  depends only on `ITenantContext` + the Domain exception, so no Api-layer placement needed.
  All 31 hand-rolled `if (IsNullOrWhiteSpace(tenant.TenantId)) return Problem(400…)` sites
  across 11 controllers were replaced with `TenantGuard.RequireTenant(tenant);` — wire shape
  proven identical by a body-asserting unit test (title "Missing tenant", detail
  "X-Tenant-Id header is required.").
- Webhook secret precedence: `Secrets:NotificationsWebhookSecret` (ISecretStore) **wins over**
  the legacy `Notifications:WebhookSecret`; when the store value is set the legacy value no
  longer authenticates. Covered by two new Alba tests (store-only + precedence) that build
  dedicated hosts; constant-time compare untouched.
- `ConsentRequiredException(requiredScope, subjectRef)` → 409 "Consent required"; consumers
  documented in the exception XML doc (consent-gated transcript/recording reads, spec 13
  session surfaces) — first throwing call site arrives with the Phase-6/7 object-store and
  consent wiring.
- No existing test asserted the hand-rolled 400 body beyond status, so the exception-path
  migration was assertion-compatible; 5 MissingTenant endpoint tests keep passing unchanged.

### Phase 5 — Queue payload sanitization (api)
`SensitiveDataMask.SanitizeJson(string json)` — System.Text.Json.Nodes walk mirroring Python
`redact_payload`: control-plane key exemption (`*_id/_key/_hash/_ref/_version` + `id/seq/ts`),
`Sanitize` on other string values, structure/shape untouched, invalid JSON returned as
`Sanitize(raw)`. Applied in `OutboxRepository.AddAsync/AddRangeAsync` and the scheduled-job
add path. Tests: PAN planted in a payload free-text field masked; operation_id/payload_hash
digit-run values preserved; every existing outbox/scheduled-job fixture byte-identical through
the sanitizer (no-op proof); consumer id-extraction round-trip.

Automated verification:
- [x] `bun nx test api` (815 passed) · `bun nx lint api` · `bun nx test-integration api` (337 passed, 2 skipped)

Discoveries and notable information:

- `SanitizeJson` returns the **original string instance** when no value needed masking (a
  `changed` flag tracked through the walk) — the byte-identity no-op guarantee is structural,
  not dependent on `ToJsonString()` reproducing the input formatting. Byte-identity covers
  whitespace/formatting too (asserted with a spaced-JSON vector).
- The control-plane exemption applies only to a string DIRECTLY under the key — an object
  nested under an `*_id` key is still walked, exactly matching `redact_payload`'s
  `isinstance(item, str)` pass-through (pinned by a test).
- Invalid JSON masks wholesale via `Sanitize(raw)` (fail closed), matching the plan.
- Strongest no-op proof: the full 337-test integration suite (publisher ticks, saga
  outcomes, inbox dedup, consumer id-extraction) runs THROUGH the sanitizing add paths
  unchanged — plus 7 representative payload-shape vectors asserted byte-identical in unit
  tests. Interpolated raw string literals containing `}}` next to `{{…}}` interpolations
  need the `$$$`/`{{{…}}}` form (CS9007).

### Phase 6 — Encrypted object store (api)
`Domain/Interfaces/IObjectStore.cs` (Put/Get/Delete/SweepExpiredAsync; tenant-scoped ids,
purpose tag e.g. "raw-audio"/"transcript", optional TTL) +
`Infrastructure/Storage/EncryptedFileObjectStore.cs`: AES-256-GCM per object (fresh nonce,
tag stored), key = `ISecretStore.GetRequiredSecret("ObjectStoreEncryptionKey")` (base64,
resolved lazily — missing key fails on USE with `SecretNotFoundException`, not at startup),
root from `ObjectStore:Root` config, layout `{root}/{tenant}/{purpose}/{id}.bin` + sidecar
`.meta.json` (created_at, expires_at, purpose), path traversal guarded (id/tenant sanitized).
TTL defaults: `ObjectStore:RawAudioTtlHours` = 72 (gate decision). SweepExpired wired into
`IRetentionSweeper` (P1 service). DI registration. Tests: round-trip, tenant path isolation,
wrong/absent key behavior, TTL sweep deletes expired + audits `privacy.retention.object_pruned`,
traversal attempts rejected.

Automated verification:
- [ ] `bun nx test api` · `bun nx lint api` · `bun nx test-integration api`

Discoveries and notable information:

- _(left blank for the implementer)_

### Phase 7 — langgraph wiring (harness, channels, policy load)
(a) ToolClient gate 1c: unknown tool name → `tool_not_permitted` envelope (replaces today's
KeyError); explicit `set_tool_allowlist(ToolAllowlist)` narrows further (deny → same envelope,
decision logged via `is_tool_invocation_permitted`); `from_tenant_context` docstring marked
broken-scaffolding/deprecated. (b) `security/channels.py`: `PromptChannels` +
`ChannelSeparationError`; extraction agent builds channels (system empty today) and passes the
validated untrusted span; future-LLM mandate documented. (c) `ConsentPolicyView.redaction:
RedactionPolicy | None` (additive): API `ConsentPoliciesController`/provider serve it from
`Redaction:SensitiveFields` config; `consent_gate` calls `set_redaction_policy` when present
(first pass + stashed-policy reuse); regen schemas/types + api-contract. Tests: gate 1c
(unknown tool, allowlist deny, all-19 pass with none set), channels (UNTRUSTED-in-system
raises), policy install (consent gate sets registry; reset in teardown), API returns redaction
from config.

Automated verification:
- [ ] `bun nx test langgraph` · `bun nx lint langgraph` · `bun nx run langgraph:generate-schemas` + `bun nx run shared-schemas:generate-types` · `bun nx build api` · `bun nx test api` · `bun nx test-integration api` · `bun nx run api-contract:generate-types`

Discoveries and notable information:

- _(left blank for the implementer)_

### Phase 8 — Full validation + regression diff
All-lane suites; regression diff against BOTH baseline docs (items 1–24); invariant re-check
(Inv-2: payload/checkpoint ids intact; Inv-5: inbox untouched; Inv-11/12/13 strengthened);
validation report appended here.

Automated verification:
- [ ] `bun nx test api` · `bun nx test-integration api` · `bun nx test langgraph` · `bun nx lint api` · `bun nx lint langgraph` · `bun nx test web`

Discoveries and notable information:

- _(left blank for the implementer)_

## Manual verification checklist

- [ ] Enable `Messaging:RetentionSweep` in dev; observe one sweep cycle log line per tenant.
- [ ] POST a menu approve without X-Actor ⇒ 400 (was silent "owner" default) — intentional break.
- [ ] Put/Get an object via a scratch endpoint-less harness (unit covers it); verify file on
      disk is ciphertext (no plaintext PAN greppable).
- [ ] Regression: DLQ replay unchanged; booking submission e2e unchanged.

## Package notes

None. AES-GCM, JsonNodes, interceptors are all in-box.
