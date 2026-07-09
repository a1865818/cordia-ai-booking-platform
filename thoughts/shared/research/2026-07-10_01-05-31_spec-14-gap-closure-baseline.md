---
date: 2026-07-10T01:05:31+09:30
researcher: Claude (spec-loop Stage 1, delta)
git_commit: d6270f3
branch: feature/spec-14
repository: Cordia
topic: "Spec 14 gap closure (~30-40% remainder) — delta research + baseline audit"
tags: [research, spec-14, security, retention, audit, operator, secrets, object-store, redaction, harness]
status: complete
---

# Research: Spec 14 gap closure — delta baseline

Extends `2026-07-09_18-55-30_spec-14-security-privacy-baseline.md` (still valid) for the
user-identified remainder. Sources: two fresh code inventories + this session's two review
docs. HEAD `d6270f3` (all suites green: langgraph 787, api 796u/331i).

## Key delta findings

### apps/api

1. **Retention scheduling**: no hosted service calls the sweeps. Established pattern to follow:
   `Messaging:*:Enabled` config gate (7 existing hosted services, e.g. `StepSweeperHostedService`
   line ~124), integration tests disable via config overrides (~22 sites). NO precedent for
   tenant enumeration — existing sweepers run one cross-tenant `IgnoreQueryFilters` query and
   set ambient `ITenantContext.TenantId` per row. Retention sweeps are per-tenant APIs → the
   hosted service must enumerate distinct tenants (union over idempotency_records,
   conversation_sessions, verification_grants) — a NEW pattern, keep it inside the retention
   seam.
2. **Ad hoc audit writers**: 35+ sites construct `AuditEvent` directly across BookingFulfillment
   (24), Waitlist (8), Preorder (17 via `PreorderSagaSupport.Audit` + a private duplicate in
   StartSaga), Notifications (~20), Menu (8), ManageBooking/Verification (3), plus
   `RetentionSweepHandler` which bypasses even the repository (`db.AuditEvents.Add`, and uses
   `audit-{guid}` EventIds vs `evt_{guid}` elsewhere). ALL sites are same-transaction with
   their domain writes (T-uow, T-inbox, or T-save shapes). Two distinct populations:
   **security-decision events** (operator/owner actions, grant issuance, acceptance) where the
   spec-14 envelope matters, and **workflow-progress events** (system actor, state-transition
   trail) where it doesn't. NONE of the ad hoc sites sanitize metadata (Inv-12 gap: only
   `AuditWriter` sanitizes). ~24 EventTypes have zero test assertions (safe to migrate); the
   rest are asserted by named tests (inventory in agent report — EventType/Actor values must
   stay byte-stable or tests updated deliberately).
3. **Operator surfaces**: `IOperatorActionRequest` implemented ONLY by `ReplayDeadLetterCommand`.
   Nine other staff commands qualify (Menu: Upload/Review/Edit/Approve/Publish; Preorder:
   ConvertStandalone/CancelManualReview; ManualRecovery: ResumeWorkflowStep/ResolveManualReview)
   — all plain `IRequest` (inbox-safe), all already carry `Actor`, NONE carry a reason code, and
   controllers apply silent actor defaults (`"owner"`, `"operator"`, `"ops"`) — the opposite of
   §24.5 fail-closed. Waitlist endpoints are customer/owner-shared (`"customer"` default) —
   explicitly NOT operator surfaces; do not touch.
4. **ISecretStore**: zero consumers. Natural first consumers: the webhook shared secret
   (`NotificationWebhooksController` reads `Notifications:WebhookSecret` from IConfiguration,
   constant-time compare — good) and the new object-store encryption key.
5. **Typed exceptions**: `TenantScopeMissingException` / `AuthorizationFailedException` /
   `ConsentRequiredException` absent. Current equivalents: 11 controllers hand-roll
   `Problem(400, "Missing tenant")`; `OperatorActionBehavior` backstop throws
   FluentValidation `ValidationException` (400) where 403 is more truthful.
6. **Queue payload redaction**: outbox rows (`OutboxMessage.Payload`) and `scheduled_jobs`
   payloads persist unsanitized JSON. A blanket `Sanitize` over payload JSON is UNSAFE (would
   corrupt digit-run ids/hashes — the exact checkpoint bug class fixed at d6270f3): needs a
   key-aware JSON walker with control-plane exemptions (C# mirror of Python `redact_payload`).
7. **Object store**: nothing exists; no artifact table in spec 12 (known deferred item) — a
   NO-DDL filesystem-backed encrypted store behind a Domain seam is the only in-bounds shape.

### apps/langgraph

8. **`ToolAllowlist.from_tenant_context` is dead scaffolding** with broken semantics: zero
   production call sites; `allowed_policy_ids` carries policy ids ("policy_a") in ~15 fixtures —
   deriving a tool allowlist from it would deny every real tool. Enforcement point:
   `ToolClient.call_tool` gates at steps 1 (tenant) and 1b (grant); a new step 1c slots in
   before header construction. Client already imports the `_timeouts` tables
   (`ALL_TOOLS` = 19 canonical names available). Envelope constructors to mirror:
   `tenant_not_resolved_envelope` / `verification_required_envelope` in `tools/errors.py`.
9. **Redaction-policy load seam**: `/api/tenant-context` DOES NOT EXIST API-side (faked only in
   langgraph tests); `TenantContextView` has no redaction field and `load_context` drops most
   of its fields. The signed `TenantContext` is a frozen spec-15 claim set (stub-minted only) —
   off limits. BEST seam: `ConsentPolicyView` (spec-14 OWNED contract, real endpoint
   `ConsentPoliciesController`, config source `Redaction:SensitiveFields` already exists for
   the enricher) — consent_gate already runs at session start and can install the policy.
10. **Channel separation**: extraction agent passes a single UNTRUSTED `LabeledContent` straight
    to the model port; no system prompt is assembled anywhere yet (only `clarify.py` renders
    deterministic templates — no LLM mixing). The enforcement artifact is therefore a channel
    assembler (validate labels per channel, raise on UNTRUSTED-in-system) adopted by the
    extraction agent and mandated for future LLM nodes.

## Existing Feature Behavior (Baseline Audit) — delta

Items 1-16 of the 2026-07-09 baseline remain in force. Additional touched/adjacent behavior:

17. **Hosted services** — 7 services, config-gated, non-overlapping responsibilities;
    integration tests disable them. NEW service must follow the gate pattern and default OFF in
    Test env config overrides. Must-stay-the-same: existing services untouched.
18. **Ad hoc audit rows** — EventType/Actor/metadata shapes asserted by the tests inventoried;
    any migration keeps EventType + Actor stable per site unless a test is deliberately updated.
    RetentionSweepHandler's `audit-{guid}` EventId prefix is a live quirk (asserted by nothing);
    normalizing is allowed.
19. **Staff controllers** — MenuController/PreorderController/BookingOperationsController staff
    endpoints currently succeed WITHOUT X-Actor (silent defaults). Making actor+reason mandatory
    is an intentional breaking change to those dev-only endpoints (gate decision needed).
20. **Webhook secret check** — constant-time compare, config-sourced; must keep the
    constant-time property when re-sourcing from ISecretStore.
21. **Outbox/scheduled-jobs payloads** — consumers parse ids/hashes out of payload JSON
    (`ExtractString(payload, "operation_id")` etc. in the ConsumerRegistry) — the sanitizer MUST
    exempt control-plane keys or every consumer breaks. Payload content today is system-built
    (no PAN reaches it in tests) — sanitize must be a no-op for all existing payload fixtures.
22. **ToolClient behavior** — gates, retry rules, headers byte-stable; adding gate 1c must not
    affect any existing test (default allowlist behavior must permit all 19 canonical tools).
23. **ConsentPolicyView consumers** — consent_gate (langgraph) + ConsentPoliciesController
    (API) + generated TS/zod; adding an OPTIONAL `redaction` field is additive for all.
24. **Type-gen blast radius**: ConsentPolicyView change → regen shared-schemas (C#/TS/Zod) +
    api-contract (endpoint response). Additive-optional only.

## Registry position (delta)

All owned/referenced positions unchanged from the prior plan. New assets: `IObjectStore` +
encrypted store (spec 14 owns the storage-control boundary; NO DDL — artifact metadata table is
a tracked deferred item for spec 12); outbox/scheduled-jobs payload sanitization (spec 05 owns
the envelope CONTRACT — we only sanitize free-text VALUES, never shape); `ConsentPolicyView.redaction`
(spec 14 owned). Typed exceptions are spec-14-named (§10). Operator-spine expansion touches
spec-11/06-owned handlers only in their audit/actor plumbing (their business logic byte-stable).
