---
date: 2026-07-09T18:55:30+09:30
researcher: Claude (spec-loop Stage 1)
git_commit: 2f592d4
branch: feature/spec-14
repository: Cordia
topic: "Spec 14 — Security, Privacy & Compliance: research + baseline audit"
tags: [research, spec-14, security, privacy, consent, redaction, audit, tenancy]
status: complete
---

# Research: Spec 14 — Security, Privacy & Compliance

**Scope:** implement `docs/specs/14-security-and-privacy.md` — consent gate, untrusted-content
boundary, redaction filter, authorization spine, `audit_events` write path, tenant-scope guard,
secret custody, retention/deletion — across `apps/api` (authoritative), `apps/langgraph`
(foreground), and `libs/shared-schemas` (contracts). Delivery-sequencing Phase 4 (with 08, 09).

## Summary

Spec 14 is a **"complete the seams" spec, not greenfield**. Spec-11/08/12 work already landed
most of the primitives it owns or references: the append-only `audit_events` table + insert-only
repository, consent columns + `consent_state` pg-enum on `conversation_sessions`, the
`ITenantScoped` EF global query filter, `GrantAuthorizationService`, a retention sweeper that
audits every prune, and (langgraph) the prompt-injection harness, signed-`TenantContext`
verifier, and a DTMF-only log-redaction utility explicitly stubbed "aligned with spec 14
RedactionPolicy". What is genuinely missing: the three cross-service Pydantic contracts, the
consent gate (node + API endpoints — no ConversationSession handlers exist at all), a real
PAN/CVV/gov-id redaction filter on both lanes, a Serilog scrubbing enricher, a secret-store
abstraction, the authorization-spine/reason-code discipline for operator endpoints, and the
§27.6 test suite (cross-tenant isolation, forged-caller-ID, prompt-injection fixtures,
redaction, audited-replay).

## Key registry findings (tie-breakers applied)

1. **`audit_events` shape — spec 14 §4.2 prose is STALE; spec 12 DDL wins.** Spec 12 §5.1
   ratifies `event_id, event_type, actor, subject_ref, correlation_id?, operation_id?,
   metadata jsonb (sanitized), occurred_at` — and the live entity
   (`Cordia.Domain/Entities/AuditEvent.cs`) matches it exactly. Spec 14's proposed record
   (ActorType/Action/TargetType/TargetId/ReasonCode/Decision/DataClasses) must NOT become new
   columns; those fields ride inside the sanitized `metadata` jsonb (and the cross-service
   `AuditEventEnvelope` contract). No migration to `audit_events`.
2. **`conversation_sessions` consent columns already exist** (`ConsentState?` (pg-enum
   granted/declined/not_required/alternative_offered), `ConsentPolicyVersion`,
   `JurisdictionPolicyId`, `RecordingAllowed`, `ConsentRecordedAt`, `Version`). D12: writes go
   through the Booking API — gateway creates the row, consent gate updates `consent_*`. **No
   handler/endpoint exists today for either write**; spec 14 owns adding the consent-update path
   (session creation belongs to spec 15/02 but a minimal create seam may be required for the
   consent gate to have a row to update — flag in plan).
3. **`verification_grants` / `TenantContext` are referenced, not owned.** Grant lifecycle =
   spec 08 (`GrantAuthorizationService` exists, do not change semantics). `TenantContext`
   mint/sign = spec 15 (`TenantResolutionMiddleware` is an explicit dev-shim reading
   `X-Tenant-Id`; hardening it to signed contexts is spec 15's job, NOT this spec's).
4. **`consent_state` pg-enum already ratified + mapped** (`Cordia.Domain/Enums/ConsentState.cs`
   with `[PgName]`). The Pydantic `ConsentState` *model* from spec 14 §4.1 would collide in name;
   generated C# lands in `Cordia.Domain.Generated` (different namespace) — tolerable but the plan
   should consider naming the Pydantic outcome field to reuse the existing enum values verbatim.
5. **D13**: spec 14 owns the `audit_events` write-path contract; spec 13 is a narrow telemetry
   appender. Existing writers (notifications, catalog approve/publish, waitlist auto-book
   consent, retention sweeper) already follow `IAuditEventRepository.AddAsync` inside the
   mutating handler's transaction — that is the write-path convention to codify, not replace.

## Detailed findings

### apps/api — what exists (paths)

| Area | Files | State |
|---|---|---|
| Audit | `Cordia.Domain/Entities/AuditEvent.cs`, `Interfaces/IAuditEventRepository.cs` (AddAsync/GetAsync only), `Infrastructure/Repositories/AuditEventRepository.cs`, `Configurations/AuditEventConfiguration.cs`, `IntegrationTests/Persistence/AuditEventPersistenceTests.cs` | Complete per spec-12 DDL; insert-only honored |
| Consent storage | `Entities/ConversationSession.cs`, `Enums/ConsentState.cs` (pg-enum), `Configurations/ConversationSessionConfiguration.cs`, `IntegrationTests/Persistence/ConversationSessionPersistenceTests.cs` | Columns + enum exist; **no handlers/controller** |
| Tenant scope | `Interfaces/ITenantScoped.cs`, `Interfaces/ITenantContext.cs`, `Infrastructure/Services/TenantContext.cs`, `ApplicationDbContext.ApplyTenantFilters` (lines ~84–108, `HasQueryFilter(e => e.TenantId == _tenantContext.TenantId)`), `Api/Middleware/TenantResolutionMiddleware.cs` (X-Tenant-Id dev shim, does not fail closed) | Structural guard exists; **no dedicated cross-tenant isolation test**; sanctioned escape hatch = `IgnoreQueryFilters()` + explicit `TenantId ==` predicate (RetentionSweepHandler, VerificationGrantRepository.ConsumeAsync) |
| Authorization | `Application/Common/Authorization/GrantAuthorizationService.cs` (+ `IGrantAuthorizationService`), `Domain/Exceptions/VerificationRequiredException.cs` → 403 via `Api/Errors/GlobalExceptionHandler.cs` | Grant checks centralised (spec 08); **no reason-code requirement, no operator-role concept; staff endpoints use unauthenticated `X-Actor` header (documented deferral)** |
| Retention | `Infrastructure/Services/Retention/RetentionPolicy.cs` (pure), `RetentionSweepHandler.cs` (idempotency records only; `IgnoreQueryFilters` + explicit tenant; emits `privacy.retention.idempotency_pruned` audit events; "ONLY sanctioned path for physical deletion"), `UnitTests/Retention/RetentionPolicyTests.cs`, `IntegrationTests/Persistence/RetentionSweepTests.cs` | Pattern established; transcript/grant retention flagged open (spec 12 §13) |
| Logging | `Program.cs` `UseSerilog(ReadFrom.Configuration)`; appsettings: console sink only, no enrichers | **No scrubbing enricher/destructuring policy** |
| Secrets | — | **Nothing exists** (no SecretStore/Stripe/provider creds; provider adapters are stubs) |
| Webhooks | `Api/Controllers/NotificationWebhooksController.cs` uses shared-secret header `X-Webhook-Secret` | Pattern exists for "generic webhook auth"; Stripe signature verify = spec 09 (NOT ours) |
| Pipeline | `ApplicationServiceCollectionExtensions.cs` lines ~49–58: Validation → InboxDedup → Handler | Adding a behavior changes global order — blast radius |
| Exceptions | 22 typed exceptions + `GlobalExceptionHandler` switch (400/403/404/409/422, unmapped→500) | Add new cases here (e.g. ConsentRequired) |

Conventions to mirror: mutable POCO entities implementing `ITenantScoped`, composite PK
`(tenant_id, id)`, no-FK loose refs where cross-service, `IEntityTypeConfiguration<T>`,
snake_case global convention, repository-per-aggregate (no generic base), `.AsNoTracking()`
reads, `ExecuteUpdateAsync` guarded writes, audit-append inside the mutating transaction,
`[Trait("Category","Unit")]` Moq tests + Alba integration tests against a real local Postgres
(`AcceptanceApiFixture`, unique tenant IDs per test, `fixture.CreateContext(tenantId)` for
seeding). New pg-mapped enums need `tools/config/ef-enum-mappings.json` + `MapEnum<T>()` in BOTH
`InfrastructureServiceCollectionExtensions.cs` and `AcceptanceApiFixture.CreateContext` (known
duplication gotcha). New `.cs` files UTF-8 with BOM.

### apps/langgraph — what exists

| Area | Files | State |
|---|---|---|
| Untrusted-content boundary | `safety/injection_harness.py` — `TrustLevel` (SYSTEM/TOOL_RESULT/UNTRUSTED), `LabeledContent`, immutable `ToolAllowlist` (`from_tenant_context`), `is_tool_invocation_permitted` (UNTRUSTED ⇒ always False); tests `unit_tests/safety/test_injection_harness.py` | Core boundary built; §8.11 channel-separation labeling exists as primitives |
| Tenant context | `safety/tenant_context.py` — frozen `TenantContext`, verifier registry, `verify_signed_context`; `tools/client.py` refuses calls w/o resolved tenant (`tenant_not_resolved_envelope`) and attaches `X-Tenant-Id`/`X-Correlation-Id`/`X-Grant-Id` headers | Inv-13 foreground half in place |
| Redaction | `streaming/logging_redaction.py` — `REDACTION_TOKEN = "[REDACTED]"` ("placeholder aligned with spec 14 RedactionPolicy.mask_token"), masks only DTMF/OTP digit field names; sole consumer is a test | **No PAN/CVV/gov-id/configured-field detection; not wired into any production emit path** |
| Menu isolation | `menu/extraction_agent.py` — `IsolatedExtractionAgent` with `ToolAllowlist([])` | Spec 11's implementation of the boundary spec 14 asserts — leave as-is; add contract test only if missing |
| Consent | — | **Nothing**: no consent node, no jurisdiction resolution, no `security/` package, no contracts/security.py |
| Graph | `graph.py`: START→resolve_tenant→load_context→identify_intent→… ; consent gate belongs between resolve_tenant and any transcript-persisting work | Insertion point identified |
| Transcript surfaces (redaction targets) | `state.transcript_text` (checkpointed durably via `checkpoint.py` tenant-scoped), streaming `TranscriptPartial/FinalFrame` in `streaming/pipeline.py`, log sites in 6 modules (none currently log transcript text) | Pre-emit hook points enumerated |

Conventions to mirror: `model_config = {"extra": "forbid"}` on wire contracts; `AwareDatetime`;
`str,Enum` for reusable vocab vs `Literal` for one-off; register new models/enums (import + list)
in `tools/scripts/export_pydantic_schemas.py` `MODEL_REGISTRY["models"]` / `ENUM_REGISTRY`
(globally-unique class names); nodes are `async def (state: AgentState) -> dict` partial updates
with `route_after_<node>` + exhaustive path_map; tests use `httpx.MockTransport` +
`set_tool_client` + autouse reset fixture + `@pytest.mark.unit`.

### Type-generation blast radius

New Pydantic contracts (`ConsentState`, `RedactionPolicy`, `AuditEventEnvelope`, plus any new
enums e.g. `NoticeChannel`/`ActorType`) → `bun nx run langgraph:generate-schemas` → `bun nx run
shared-schemas:generate-types` → regenerates `SharedTypes.cs` (consumed by Cordia.Domain) and
TS + Zod (consumed by web). Purely **additive** models do not disturb existing consumers.
`VerificationGrantRef` from spec 14 §4.1 already effectively exists as `VerificationGrantView`
(registered) — reuse, don't duplicate. If the API surface changes (new consent endpoints),
`bun nx run api-contract:generate-types` regenerates the web's `paths` types (additive).

### Prior thoughts (supplementary)

- `reviews/2026-07-03_spec-11-progress-review.md`: C11 (empty consent-evidence authorizes
  auto-book) and C9 (enrollment verification-level gap) remain open — spec-11 scope, NOT spec-14
  scope, but adjacent; don't regress them.
- `plans/2026-07-01_spec-11-implementation.md`: staff actions "audited (`X-Actor`) but not
  role-gated" is a documented deferral (owner/operator role taxonomy = open decision in 08/14).

## Existing Feature Behavior (Baseline Audit)

The regression baseline Stage 6 must diff against. Per touched/adjacent feature: behavior today,
invariants upheld, and whether spec-14 work may change it.

1. **EF global tenant filter (Inv-13)** — every `ITenantScoped` entity gets
   `WHERE tenant_id = @TenantContext.TenantId` on all LINQ queries; background/sweeper code
   escapes via `IgnoreQueryFilters()` + explicit tenant predicate. Covered indirectly by
   persistence tests; no dedicated cross-tenant test. **Must stay the same mechanism**; spec 14
   adds *tests* proving isolation, and its new entities implement `ITenantScoped`. No filter
   semantics change.
2. **TenantResolutionMiddleware** — reads `X-Tenant-Id`, sets scoped `TenantContext`, does NOT
   fail closed (endpoints validate). Spec 15 owns hardening. **Must stay byte-for-byte** (unit
   tests: `TenantResolutionMiddlewareTests`).
3. **Audit write path (D13)** — handlers append `AuditEvent` rows inside their own
   transaction via `IAuditEventRepository.AddAsync`; repository exposes no update/delete;
   `AuditEventConfiguration` has no version token, PK `(tenant_id, event_id)`. Existing writers:
   `RecordDeliveryReceiptCommandHandler` (`notification.delivered` etc.),
   catalog approve/publish handlers, waitlist auto-accept consent, preorder standalone
   conversion, `RetentionSweepHandler` (`privacy.retention.idempotency_pruned`),
   `AuditMissingTerminalNotificationCommandHandler`. **Existing writers must stay green**;
   spec 14 may add a typed helper/writer but must not alter existing rows' event_type/shape.
4. **Verification grants (Inv-11, spec 08)** — `RequireGrantAsync`/`ConsumeGrantAsync` +
   `VerificationRequiredException`→403; used by manage-booking + preorder conversion; unit tests
   `GrantAuthorizationServiceTests`, integration `VerificationEndpointTests`. **Referenced
   contract — byte-for-byte.** Spec 14 consumes it in the authorization-spine tests only.
5. **Retention sweep (Inv-14/15)** — idempotency-records-only sweep; prune requires deadline
   passed AND linked operation terminal (or orphan); every prune audited. Tests:
   `RetentionPolicyTests`, `RetentionSweepTests`. **Intentionally extended** (spec 14 owns
   retention/deletion workflows) — existing idempotency sweep behavior must not change.
6. **ConversationSession persistence** — entity + config + round-trip test only; nothing writes
   it in production code. **Intentionally extended** with consent-update (and possibly minimal
   session-create) handlers per D12 single-writer rule.
7. **MediatR pipeline order** — Validation → InboxDedup → Handler. Any new behavior (e.g. an
   authorization spine) must slot without disturbing inbox-consumer semantics (Inv-5).
   `InboxDeduplicationBehaviorTests` must stay green.
8. **GlobalExceptionHandler** — typed-exception switch; unmapped → 500 with no internals.
   **Additive only** (new cases at the end); `GlobalExceptionHandlerTests` green.
9. **Serilog config** — console sink, config-driven, `UseSerilogRequestLogging` after tenant
   middleware. **Intentionally extended** with a scrubbing enricher/destructuring policy;
   existing log output shape otherwise unchanged.
10. **langgraph injection harness (Inv-1/§8.11)** — UNTRUSTED source can never invoke a tool;
    allowlist immutable, derived from verified TenantContext. Tests:
    `test_injection_harness.py`, `test_tenant_guard.py`. **Must stay the same**; spec 14 adds
    prompt-injection fixture tests exercising it end-to-end (menu/retrieved text), not new
    mechanisms.
11. **Menu extraction isolation (spec 11)** — `IsolatedExtractionAgent` with empty allowlist;
    test `test_extraction_agent.py`. **Byte-for-byte** (spec 14 asserts the boundary; spec 11
    implements it).
12. **DTMF log redaction** — `redact_for_logging` masks digit fields; used only in tests.
    **Intentionally extended/superseded** by the spec-14 redaction filter; the existing
    function's behavior for DTMF fields must be preserved (test `test_dtmf_masking.py` green).
13. **ToolClient gating** — refuses tool calls without resolved tenant; attaches
    correlation/tenant/grant headers; never re-mints operation/idempotency keys (Inv-2); never
    leaks raw response bodies into `customer_hint`. **Byte-for-byte** (tests
    `test_client_tenant_gate.py`, `test_client_envelope_mapping.py`).
14. **Graph topology** — 11 nodes, tested by `test_graph_topology.py`. **Intentionally changed**:
    consent gate node inserted (topology test updates are expected and must be explicit).
15. **Checkpointer** — persists full AgentState (incl. transcript_text) tenant-scoped.
    Consent-declined sessions must not persist transcript per spec 14 §5.2 — **intentionally
    changed** behavior gated on `recording_allowed`.
16. **Release-blocking invariants currently upheld by touched code**: Inv-1 (harness+tools),
    Inv-5 (inbox behavior), Inv-11 (grant service), Inv-12 (job-event schemas carry no
    secrets/PAN; DTMF masking), Inv-13 (EF filter + client gate), Inv-14/15 (retention sweep
    audit trail). None may weaken.

### Existing automated coverage that must stay green

- api unit: Middleware, Errors, ManageBooking (grants), Notifications (redaction/identity),
  Retention, Concurrency. api integration: Persistence (AuditEvent, ConversationSession,
  VerificationGrant, RetentionSweep, ScheduledJob), Messaging (InboxDedup, ScheduledJobPoller,
  StepSweeper), ManageBooking (VerificationEndpoint), Acceptance.
- langgraph unit: safety/, nodes/, tools/, streaming/, menu/, contracts/, test_graph_topology,
  test_checkpoint. langgraph integration: tools-vs-fake-api, streaming e2e.
- web: vitest (untouched unless api-contract regen shifts types — additive only).

## Open decisions spec 14 leaves to the user (Stage 3 gate)

1. Transcript/audio retention periods + jurisdiction→policy mapping (drives consent gate + TTL).
2. `audit_events` / idempotency retention windows vs audit-preservation minimums.
3. Verification factors & risk policy (spec 08 owns lifecycle; spine consumes).
4. Owner/operator role taxonomy (known deferred item repo-wide; X-Actor shim today).
5. Default configured-sensitive-field catalog beyond PAN/CVV/gov-id; tenant-overridable?
