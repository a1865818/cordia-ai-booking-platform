---
date: 2026-07-11T00:00:00+09:30
researcher: Claude (spec-loop, consolidated)
topic: "Spec 14 — Security, Privacy & Compliance: consolidated research + baseline audit"
tags: [research, spec-14, security, privacy, consent, redaction, audit, tenancy, roles, transcripts, object-store]
status: complete
---

# Research: Spec 14 — Security, Privacy & Compliance (consolidated, final)

**Scope:** implement `docs/specs/14-security-and-privacy.md` — consent gate, untrusted-content
boundary, redaction filter, authorization spine, `audit_events` write path, tenant-scope guard,
secret custody, retention/deletion, owner/operator role taxonomy, consent-gated transcripts, and
the model-trace sink — across `apps/api` (authoritative), `apps/langgraph` (foreground), and
`libs/shared-schemas` (contracts).

This document supersedes and consolidates three sequential research/baseline documents produced
across the feature's three implementation runs (2026-07-09 foundation, 2026-07-10 gap-closure
delta, 2026-07-11 sibling-linkage delta). It records what existed *before* each run started and
the baseline behavior that had to stay green — now presented as one coherent picture of the
finished feature's foundations. See `thoughts/shared/plans/2026-07-11_spec-14-implementation.md`
for what was actually built, decided, and validated.

## Summary

Spec 14 was a **"complete the seams" spec, not greenfield.** Prior spec-11/08/12 work had already
landed most of the primitives it owns or references: the append-only `audit_events` table +
insert-only repository, consent columns + `consent_state` pg-enum on `conversation_sessions`, the
`ITenantScoped` EF global query filter, `GrantAuthorizationService`, a retention sweeper that
audits every prune, and (langgraph) the prompt-injection harness, signed-`TenantContext` verifier,
and a DTMF-only log-redaction utility explicitly stubbed "aligned with spec 14 RedactionPolicy."

What was genuinely missing, discovered across three research passes as work progressed and open
decisions were ratified:
- **Run 1 gap:** the three cross-service Pydantic contracts; the consent gate (node + API
  endpoints — no `ConversationSession` handlers existed at all); a real PAN/CVV/gov-id redaction
  filter on both lanes; a Serilog scrubbing enricher; a secret-store abstraction; the
  authorization-spine/reason-code discipline for operator endpoints; the §27.6 test suite.
- **Run 2 gap:** no hosted service scheduled the retention sweeps (dead code outside tests); 35+
  ad-hoc audit-write sites bypassed sanitization; only DLQ replay had reason-code discipline (9
  other staff commands used silent actor defaults); `ISecretStore` had zero consumers; no typed
  security exceptions; outbox/scheduled-job payloads persisted unsanitized JSON; no object store
  existed at all.
- **Run 3 gap:** no role taxonomy (only the `X-Actor` identity shim); no transcript
  producer/reader endpoints (checkpointer-only persistence, no encrypted object-store artifact);
  no model-trace sink; `ToolAllowlist.from_tenant_context` was dead/broken scaffolding.

## Key registry findings (tie-breakers applied, all runs)

1. **`audit_events` shape — spec 14 §4.2 prose is STALE; spec 12 DDL wins.** Spec 12 §5.1 ratifies
   `event_id, event_type, actor, subject_ref, correlation_id?, operation_id?, metadata jsonb
   (sanitized), occurred_at` — matching the live entity exactly. Spec 14's proposed record
   (ActorType/Action/TargetType/TargetId/ReasonCode/Decision/DataClasses) rides inside the
   sanitized `metadata` jsonb via the cross-service `AuditEventEnvelope` contract — never new
   columns. No migration to `audit_events`, ever, across all 3 runs.
2. **`conversation_sessions` consent columns already existed** before any spec-14 work
   (`ConsentState?` pg-enum, `ConsentPolicyVersion`, `JurisdictionPolicyId`, `RecordingAllowed`,
   `ConsentRecordedAt`, `Version`). D12: writes go through the Booking API — the channel gateway
   creates the row, the consent gate updates `consent_*`. No handler/endpoint existed for either
   write until Run 1 (consent) and Run 3 (transcript sidecars keyed off the same row, still zero
   DDL).
3. **`verification_grants`/`TenantContext` are referenced, not owned.** Grant lifecycle = spec 08
   (`GrantAuthorizationService`, unchanged in semantics across all 3 runs).
   `VerificationActionEnum.OwnerOverride` (D5) existed only in the pg-enum with zero application
   consumers — Run 3's role taxonomy is therefore a *parallel* grant-or-role alternative on the
   transcript reader, not a consumer of `OwnerOverride`.
4. **`consent_state` pg-enum already ratified + mapped** (`Cordia.Domain/Enums/ConsentState.cs`
   with `[PgName]`). The Pydantic model from spec 14 §4.1 would collide in name — resolved by
   renaming it `SessionConsentState` (wire JSON unchanged; generated C# lands in a different
   namespace so the collision was tolerable but the rename removed ambiguity permanently).
5. **D13**: spec 14 owns the `audit_events` write-path *contract*; spec 13 is a narrow telemetry
   appender. Existing writers (notifications, catalog approve/publish, waitlist auto-book consent,
   retention sweeper) already followed `IAuditEventRepository.AddAsync` inside the mutating
   handler's transaction — every run codified/extended this convention, never replaced it.
6. **Two audit populations, discovered in Run 2:** *security-decision events* (operator/owner
   actions, grant issuance, acceptance) where the spec-14 envelope matters, and *workflow-progress
   events* (system actor, state-transition trail) where it doesn't. Run 2's resolution: an
   interceptor sanitizes metadata on EVERY insert (closing Inv-12 for both populations at one
   choke point); only the security-decision population migrated to `IAuditWriter`.
7. **Role taxonomy had no home until Run 3.** `IOperatorActionRequest` (Run 1) carried only
   `ActorId`/`ReasonCode`; Run 3 added `RequiredRole`/`ActorRole` to the same interface rather than
   introducing a parallel authorization mechanism — `StaffRole.Satisfies` is the entire new
   surface.

## Detailed findings by area

### apps/api — state at each run's start

| Area | Files | State before spec-14 work / before the relevant run |
|---|---|---|
| Audit | `AuditEvent.cs`, `IAuditEventRepository.cs` (AddAsync/GetAsync only), `AuditEventRepository.cs`, `AuditEventConfiguration.cs` | Complete per spec-12 DDL, insert-only honored, from the start |
| Consent storage | `ConversationSession.cs`, `Enums/ConsentState.cs`, `ConversationSessionConfiguration.cs` | Columns + enum existed pre-Run-1; zero handlers/controller |
| Tenant scope | `ITenantScoped.cs`, `ITenantContext.cs`, `TenantContext.cs`, `ApplicationDbContext.ApplyTenantFilters`, `TenantResolutionMiddleware.cs` (X-Tenant-Id dev shim, doesn't fail closed) | Structural guard existed; no dedicated cross-tenant isolation test until Run 1 Phase 6 / Run 2 max-review finding #9 |
| Authorization | `GrantAuthorizationService.cs`+`IGrantAuthorizationService.cs`, `VerificationRequiredException.cs`→403 | Grant checks centralized (spec 08); no reason-code requirement, no role concept until Run 3 |
| Retention | `RetentionPolicy.cs`, `RetentionSweepHandler.cs` (idempotency-only pre-Run-1; sessions+grants added Run 1; no scheduler until Run 2) | Pattern established; scheduling was the Run-2 gap |
| Logging | `Program.cs` `UseSerilog(ReadFrom.Configuration)`, console sink only | No scrubbing enricher until Run 1 |
| Secrets | — | Nothing existed until Run 1's `ISecretStore`; zero consumers until Run 2 (webhook, object-store key) |
| Webhooks | `NotificationWebhooksController.cs`, `X-Webhook-Secret` shared-secret header | Pattern existed pre-spec-14; re-sourced through `ISecretStore` in Run 2 |
| Pipeline | `ApplicationServiceCollectionExtensions.cs`: Validation → InboxDedup → Handler | `OperatorAction` slotted in at Run 1 (Validation → OperatorAction → InboxDedup) |
| Exceptions | 22 typed exceptions + `GlobalExceptionHandler` switch (400/403/404/409/422, unmapped→500) | 3 spec-14 exceptions added across Runs 1–2 |
| Object store | — | Did not exist until Run 2 (`IObjectStore`/`EncryptedFileObjectStore`); Run 3 added `ListIdsAsync` |
| Role taxonomy | — | Did not exist until Run 3 (`StaffRole`) |

Conventions mirrored throughout: mutable POCO entities implementing `ITenantScoped`, composite PK
`(tenant_id, id)`, `IEntityTypeConfiguration<T>`, snake_case, repository-per-aggregate,
`.AsNoTracking()` reads, `ExecuteUpdateAsync` guarded writes, audit-append inside the mutating
transaction, `[Trait("Category","Unit")]` Moq tests + Alba integration tests against real local
Postgres. New pg-mapped enums need `tools/config/ef-enum-mappings.json` + `MapEnum<T>()` in both
`InfrastructureServiceCollectionExtensions.cs` and `AcceptanceApiFixture.CreateContext`. New `.cs`
files UTF-8 with BOM.

### apps/langgraph — state at each run's start

| Area | Files | State |
|---|---|---|
| Untrusted-content boundary | `safety/injection_harness.py` (`TrustLevel`, `LabeledContent`, immutable `ToolAllowlist`, `is_tool_invocation_permitted`) | Core boundary existed pre-spec-14; `from_tenant_context` was dead/broken scaffolding (discovered Run 2 — `allowed_policy_ids` carries policy ids, not tool names); Run 2 added the live enforcement gate (1c); Run 2 also added `PromptChannels` (channel separation) |
| Tenant context | `safety/tenant_context.py`, `tools/client.py` (tenant/grant header attachment) | Inv-13 foreground half in place pre-spec-14 |
| Redaction | `streaming/logging_redaction.py` (`REDACTION_TOKEN`, DTMF-only) | No PAN/CVV/gov-id detection until Run 1's `security/redaction.py`; policy-loading seam (`ConsentPolicyView.redaction`) added Run 2 |
| Menu isolation | `menu/extraction_agent.py` (`IsolatedExtractionAgent`, empty allowlist) | Pre-existing spec-11 implementation; spec 14 only asserts the boundary via tests + `PromptChannels` adoption (Run 2) |
| Consent | — | Nothing existed until Run 1 (`security/consent.py` node, 2 tools) |
| Transcript producer | Checkpointer-only persistence (`checkpoint.py`) | No object-store producer until Run 3 (`TranscriptPersister` port + `append_transcript_turn` tool) |
| Model-trace sink | — | Did not exist until Run 3 (`observability/model_trace.py`, stub-backed) |
| Graph | `graph.py`: linear pipeline | Consent gate node inserted Run 1 between `resolve_tenant` and `load_context` |

Conventions mirrored throughout: `model_config = {"extra": "forbid"}` on wire contracts,
`AwareDatetime`, `str,Enum` for reusable vocab vs `Literal` for one-off, registration in
`tools/scripts/export_pydantic_schemas.py` `MODEL_REGISTRY`/`ENUM_REGISTRY`, nodes as
`async def (state) -> dict` partial updates with `route_after_<node>`, tests via
`httpx.MockTransport` + `set_tool_client` + autouse reset fixture.

### Type-generation blast radius (cumulative)

New Pydantic contracts across all 3 runs (`SessionConsentState`, `RedactionPolicy`,
`AuditEventEnvelope`, transcript-turn input/result, model-trace input/result, plus status enums) →
`bun nx run langgraph:generate-schemas` → `bun nx run shared-schemas:generate-types` →
`SharedTypes.cs` (consumed by `Cordia.Domain`) + TS/Zod (consumed by web) — purely additive every
time, no existing consumer's shape changed. New API endpoints (consent, transcript, model-trace) →
`bun nx run api-contract:generate-types` — additive; web consumes none of the new paths.

## Existing Feature Behavior (Baseline Audit) — full, numbered, cumulative

The regression baseline every run's validation diffed against. Items 1–16 established Run 1;
17–24 added by Run 2; 25–33 added by Run 3. All items below were confirmed intact (or
intentionally, explicitly changed) in the final validation report.

1. **EF global tenant filter (Inv-13)** — every `ITenantScoped` entity gets
   `WHERE tenant_id = @TenantContext.TenantId`; background/sweeper code escapes via
   `IgnoreQueryFilters()` + explicit tenant predicate. **Mechanism never changed**; spec 14 only
   added tests proving isolation and new entities implementing the marker.
2. **`TenantResolutionMiddleware`** — reads `X-Tenant-Id`, sets scoped `TenantContext`, does not
   fail closed itself (endpoints validate via the new `TenantGuard`, Run 2). **Byte-for-byte.**
3. **Audit write path (D13)** — handlers append `AuditEvent` rows inside their own transaction via
   `IAuditEventRepository.AddAsync`; no update/delete exists. **Existing writers stayed green**
   through the interceptor (Run 2) and the `IAuditWriter` migration (security-decision population
   only).
4. **Verification grants (Inv-11, spec 08)** — `RequireGrantAsync`/`ConsumeGrantAsync` +
   `VerificationRequiredException`→403. **Referenced, byte-for-byte** across all 3 runs; Run 3's
   transcript reader consumes it as one authorization branch alongside the new role branch.
5. **Retention sweep (Inv-14/15)** — idempotency-records-only originally; **intentionally
   extended** with session anonymization + grant pruning (Run 1) and scheduling + object-store TTL
   sweep (Run 2); existing idempotency-sweep behavior never changed.
6. **`ConversationSession` persistence** — entity + config + round-trip test only pre-Run-1;
   **intentionally extended** with consent-update handlers (Run 1) and transcript sidecar
   read/write (Run 3) — the row itself is still never created by spec-14 code (D12).
7. **MediatR pipeline order** — Validation → InboxDedup → Handler pre-Run-1; **intentionally
   extended** to Validation → OperatorAction → InboxDedup → Handler (Run 1), then the
   `OperatorAction` step gained a role check (Run 3) — inbox-consumer semantics (Inv-5) never
   disturbed at any point.
8. **`GlobalExceptionHandler`** — typed-exception switch, unmapped→500. **Additive only**, every
   run.
9. **Serilog config** — console sink, config-driven. **Intentionally extended** with the
   scrubbing enricher (Run 1); existing log shape otherwise unchanged.
10. **langgraph injection harness (Inv-1/§8.11)** — UNTRUSTED source can never invoke a tool.
    **Byte-for-byte mechanism**; Run 2 added the live enforcement gate (1c) and `PromptChannels`
    as *additional* layers, never replacing the harness.
11. **Menu extraction isolation (spec 11)** — `IsolatedExtractionAgent`, empty allowlist.
    **Byte-for-byte**; spec 14 only asserts it via tests and (Run 2) `PromptChannels` adoption.
12. **DTMF log redaction** — masks digit fields, test-only consumer pre-Run-1. **Superseded** by
    the spec-14 redaction filter (Run 1); DTMF-specific behavior preserved and tested throughout.
13. **`ToolClient` gating** — refuses calls without resolved tenant; never re-mints
    operation/idempotency keys (Inv-2). **Byte-for-byte** for the original 19 canonical tools with
    no allowlist set, across all 3 runs (new tools only extend the tables).
14. **Graph topology** — pre-Run-1: 11 nodes. **Intentionally changed**: consent gate inserted
    (Run 1); no further topology changes in Runs 2–3 (new tool calls happen inside existing
    nodes/pipeline, not new nodes).
15. **Checkpointer** — persists full `AgentState` tenant-scoped. **Intentionally changed**:
    consent-declined sessions don't persist transcript text (Run 1); the max-review pass (Run 2)
    found the scrub was `transcript_text`-only and extended it to the whole durable copy via
    `redact_payload`.
16. **Release-blocking invariants upheld from the start**: Inv-1 (harness+tools), Inv-5 (inbox
    behavior), Inv-11 (grant service), Inv-12 (job-event schemas carry no secrets/PAN; DTMF
    masking), Inv-13 (EF filter + client gate), Inv-14/15 (retention sweep audit trail). **None
    weakened by any run** — all strengthened per the final validation's invariant re-check.
17. **Hosted services** — 7 services pre-Run-2, config-gated, non-overlapping, integration tests
    disable them. `RetentionSweepHostedService` (Run 2) followed the identical gate pattern;
    the other 7 stayed untouched throughout.
18. **Ad hoc audit rows** — 35+ direct-construction sites discovered in Run 2 across
    BookingFulfillment/Waitlist/Preorder/Notifications/Menu/ManageBooking, none sanitizing
    metadata. **EventType/Actor pairs stayed stable** at every migrated site through the
    interceptor + selective `IAuditWriter` migration.
19. **Staff controllers** — pre-Run-2: succeeded WITHOUT `X-Actor` (silent defaults `"owner"`/
    `"operator"`/`"ops"`). **Intentionally broken** (Run 2, approved gate decision) — now
    fail-closed; Run 3 added the role dimension on top.
20. **Webhook secret check** — constant-time compare, config-sourced pre-Run-2. **Property
    preserved** through the Run-2 `ISecretStore` re-sourcing (store value wins over legacy config).
21. **Outbox/scheduled-jobs payloads** — consumers parse ids/hashes out of payload JSON
    (`ExtractString(payload, "operation_id")`, etc.). The Run-2 sanitizer exempts control-plane
    keys — **proven a no-op on every existing payload fixture**, and the max-review pass (Run 2)
    caught and fixed the identical corruption class inside the checkpoint scrubber.
22. **`ToolClient` behavior** — gates/retry/headers byte-stable at every run for the original 19
    tools; Run 2's gate 1c and Run 3's 2 new tools only extend the tables.
23. **`ConsentPolicyView` consumers** — `consent_gate` (langgraph) + `ConsentPoliciesController`
    (API) + generated TS/Zod. The Run-2 `redaction` field addition was additive-optional for all.
24. **Type-gen blast radius** — every run's contract additions regenerated C#/TS/Zod + api-contract
    additively; no existing consumer's shape ever changed.
25. **`OperatorActionBehavior` contract** (pre-Run-3) — no-op for non-operator requests (Inv-5);
    missing actor/reason → 403 before the handler, no deny-audit; `DomainException` escaping the
    handler → deny-audit via fresh scope + rethrow; ALLOW audit is the handler's job. **Run 3
    intentionally extended** this with a role check after the presence checks, deny-audited the
    same way.
26. **The 10 operator implementers + their tests** (pre-named Run-3 exception) — Menu ×5, Preorder
    ×2, ManualRecovery ×2, DLQ ×1. Constructor arity + headers changed (role added); EventType/
    Actor pairs stayed stable at every site.
27. **`GrantAuthorizationService`/`VerificationRequiredException` flow** — **byte-for-byte**
    through Run 3: the transcript reader *consumes* `RequireGrantAsync`, never modifies it.
28. **`RecordConsent` vertical** (Run 1) — entity, repo `ExecuteUpdate`, handler audit, controller
    route, D12 single-writer rule. **Byte-for-byte** through Run 3; the transcript endpoints are
    additive siblings on the same controller.
29. **Streaming pipeline scrub-before-yield** (`pipeline.py`) — frames scrubbed before any producer
    hook sees them (pre-Run-3 property, confirmed still true after the Run-3 persister hook was
    added — the xhigh review's ordering fix reinforced rather than violated this).
30. **Checkpointer transcript suppression** — the Run-3 object-store transcript is a parallel
    consented artifact; checkpoint behavior itself unchanged.
31. **`IObjectStore`/`EncryptedFileObjectStore`** (Run 2) — Put/Get/Delete/Sweep semantics, blob
    layout, traversal guard, 72h raw-audio TTL, sweep audits all stayed byte-stable through Run
    3's additive `ListIdsAsync`.
32. **Tool gate 1c / tenant gate behavior** (Run 2) — byte-stable for the original 19 tools through
    Run 3's 2 new tools, which only extend the tables.
33. **api-contract surface** — every run's new endpoints additive; existing paths never changed
    shape; web consumes none of the spec-14 paths.

### Existing automated coverage that had to stay green (final tally)

api unit: 934 (was 706 before Run 1) · api integration: 357 (was ~318 before Run 1) · langgraph
unit: 810 (was 706 before Run 1) · web: 2 (untouched throughout). Figures include the Run 4
Bugbot bugfix pass (2026-07-11, see plan §7/§8): two ALLOW-audit writes
(`GetBookingDetailsQuery`, `AppendTranscriptTurnCommandHandler`) moved from the cancelable request
token to `CancellationToken.None`; the transcript-turn audit-before-put reordering (closing a
silent-audit-gap window); `NotificationWebhooksController`'s blank/whitespace-secret migration
fallback; `ConsentPoliciesController`'s blank-field filtering; `SubmitConfirmedIntentCommandHandler`'s
consent-state wire-format fix (enum passed through to the JSON converter instead of
`.ToString()`); and `OperatorActionBehavior`'s deny-audit-failure isolation (+1 new test). Two
Run-4 findings remain open decisions (not fixed) — see plan §7.

## Open decisions this research surfaced (final disposition — see plan §9 for detail)

1. Transcript/audio retention by jurisdiction — **closed** (plan GD-3).
2. `audit_events`/idempotency retention windows — **closed** (plan GD-4).
3. Verification factors & risk policy — **still open** (spec 08 owns lifecycle).
4. Owner/operator role taxonomy — **closed** (plan GD-1/GD-2).
5. Default configured-sensitive-field catalog beyond PAN/CVV/gov-id — **still open.**
