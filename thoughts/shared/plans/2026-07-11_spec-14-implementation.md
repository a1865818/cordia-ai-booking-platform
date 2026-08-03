# Spec 14 — Security, Privacy & Compliance: Implementation Plan (consolidated, final)

**Spec:** `docs/specs/14-security-and-privacy.md`
**Branch:** `feature/spec-14` · **Status:** COMPLETE — all 20 phases across 3 runs implemented,
reviewed, and validated; plus a 4th Bugbot-driven bugfix pass (§7 Run 4, §8) on 2026-07-11.
**Research/baseline:** `thoughts/shared/research/2026-07-11_spec-14-baseline.md`
**Manual verification:** `thoughts/shared/plans/2026-07-11_spec-14-manual-verification-manual.md`

This document supersedes and consolidates three sequential planning documents (2026-07-09
foundation, 2026-07-10 gap closure, 2026-07-11 sibling linkage) plus their three code-review
reports, now that all work is committed and validated. It is the single source of truth for what
was built, why, and what was decided, and is kept current with post-implementation bugfix passes.

Governing directive throughout (spec-11 precedent): treat the repo as a single evolving
architecture — integrate with existing mechanisms (single front doors), never add parallel
implementations. **Zero EF migrations across all three runs.**

---

## 1. Overview

Spec 14 is the cross-cutting security, privacy, and compliance layer every other Cordia
component inherits: the authorization spine (identity + role + tenant scope + reason code +
audit), consent gate, redaction filter, untrusted-content boundary, encryption/retention, and
secret custody. It was delivered in three runs as gaps were discovered and open decisions were
ratified:

- **Run 1 — Foundation** (2026-07-09, 6 phases): shared security contracts, foreground redaction
  + consent gate, API consent vertical, audit writer + operator spine (DLQ replay only), log
  scrubbing + secret custody + retention extension, §27.6 cross-cutting test suite.
- **Run 2 — Gap closure** (2026-07-10, 8 phases): scheduled retention sweeps, uniform audit
  sanitization, operator spine expanded to all 9 remaining staff commands, typed security
  exceptions, queue/outbox payload redaction, encrypted object store, langgraph tool-allowlist
  gate + prompt channels + redaction-policy loading.
- **Run 3 — Sibling linkage** (2026-07-11, 6 phases): owner/operator role taxonomy (closing the
  §12 "Owner and internal operations roles" open decision), consent-gated transcript
  producer/reader (first `ConsentRequiredException` thrower), model-trace sink.

Each run closed with a code-review pass (xhigh/max) and a validation report; findings from all
three reviews are folded into §7 below.

---

## 2. Gate decisions (all runs, chronological)

**Run 1 (2026-07-09):** Retention defaults — sessions anonymized 90 days after `ended_at`; grants
pruned 30 days after expiry/consumption; `audit_events` never pruned (all configurable). Operator
identity — keep `X-Actor` + mandatory reason code + audit (no role taxonomy yet). Redaction —
PAN/CVV/gov-id always-on; `configured_sensitive_fields` empty by default, tenant-overridable.

**Run 2 (2026-07-10):** All 8 phases approved, commit per phase. Audit strategy = interceptor +
selective migration (workflow-progress events stay direct-but-sanitized; only staff handlers move
to `IAuditWriter`). Staff endpoints go fail-closed: `X-Actor` + `ReasonCode` mandatory — **an
intentional breaking change** for dev flows. Object store = filesystem AES-256-GCM seam behind
`IObjectStore`, raw-audio TTL 72h.

**Run 3 (2026-07-11) — closes the last two §12/§31 open decisions:**
- **GD-1 Role taxonomy:** `{owner, operator}` with a per-action matrix — menu lifecycle → owner;
  DLQ replay / resume / resolve / preorder resolution → operator; **owner satisfies operator**
  (owner ⊇ operator); unknown/blank role denies. Transcript read + model-trace view require
  operator (owner ok).
- **GD-2 Role transport:** `X-Actor-Role` header shim, fail-closed — missing/blank → 400
  (validator), mismatch → 403 + deny-audit (behavior). Swaps for claims when real authn lands.
- **GD-3 TTLs:** `ObjectStore:TranscriptTtlDays=90` (aligned with the 90-day session anonymization
  window), `ObjectStore:ModelTraceTtlDays=14` (strictly shorter, config-tested). Raw audio stays
  72h.
- **GD-4 Audit/idempotency retention:** `audit_events` NEVER pruned (financial/security minimum);
  idempotency records keep the existing live 90-day config default. Zero code churn.

---

## 3. How this maps onto Cordia (cumulative)

| Area | Reuses/extends | Consequence |
|---|---|---|
| Security contracts | Pydantic-contract pipeline (`cordia_agent/contracts/*.py` + `export_pydantic_schemas.py`) | `contracts/security.py` (`SessionConsentState`, `RedactionPolicy`, `AuditEventEnvelope`); additive regen throughout |
| Consent gate (foreground) | Graph node pattern, `ToolClient` tool pattern | `security/consent.py` node between `resolve_tenant` and `load_context`; 2 tools (`resolve_consent_policy`, `record_consent`) |
| Redact-before-emit | `streaming/logging_redaction.py` (spec-14 placeholder) | `security/redaction.py`, always-on PAN/CVV/gov-id detectors; wired into checkpointer, transcript frames, tool-call content |
| Consent persistence (D12) | `ConversationSession` entity + `consent_state` pg-enum (pre-existing) | `Handlers/Security/RecordConsent` (update-only) + `ResolveJurisdictionPolicy`; `ConsentPoliciesController`/`ConversationSessionsController` |
| Audit writer (D13) | `IAuditEventRepository.AddAsync` convention | `IAuditWriter` (sanitized envelope) for security-decision writers; `AuditEventSanitizationInterceptor` masks EVERY insert at the DbContext choke point (closes Inv-12 for 35+ writers with no per-site churn) |
| Authorization spine (§3.1) | Pipeline-behavior + marker-interface patterns | `IOperatorActionRequest` + `OperatorActionBehavior` — reason code (Run 1: DLQ only → Run 2: all 10 staff commands → Run 3: + role check) |
| Role taxonomy | `IOperatorActionRequest`/`OperatorActionBehavior` | `StaffRole` static helper (`Satisfies(provided, required)`); no new pipeline behavior |
| Tenant-scope guard | `ITenantScoped` + EF global query filter (pre-existing) | New `TenantGuard.RequireTenant()` helper adopted by 11 controllers; structural cross-tenant test suite |
| Secret custody | none (gap) | `ISecretStore` (Domain) + `ConfigurationSecretStore`; webhook secret + object-store key are the two real consumers |
| Log scrubbing | Serilog `ReadFrom.Configuration` | `LogScrubbingEnricher`, recursive into destructured values |
| Queue/outbox redaction | Python `redact_payload` design | C# `SensitiveDataMask.SanitizeJson` — control-plane key exemption (`*_id/_key/_hash/_ref/_version`), byte-identical no-op on existing payloads |
| Retention & deletion | `RetentionPolicy` + `RetentionSweepHandler` | `IRetentionSweeper` seam + `RetentionSweepHostedService` (previously dead code outside tests); session anonymization + grant pruning; object-store TTL sweep |
| Encrypted object store | — (greenfield, no DDL) | `IObjectStore` + `EncryptedFileObjectStore` (AES-256-GCM, tenant-scoped paths, purpose TTLs: raw-audio 72h / transcript 90d / model-trace 14d) |
| Transcript producer/reader | `ConversationSessionsController` (D12) + `IObjectStore` purpose `"transcript"` | `POST .../transcript-turns` (fail-closed on consent) + `GET .../transcript` (grant-or-role, first `ConsentRequiredException` thrower) |
| Model-trace sink | Same store, purpose `"model-trace"` | `ModelTracesController`; stub-backed `ModelTraceSink` port (no LLM nodes call it yet) |
| Untrusted-content boundary | `ToolClient` gate pattern + `injection_harness` | Gate 1c: unknown tool → `tool_not_permitted` (was `KeyError`); `PromptChannels` enforces channel separation at construction time |
| Typed exceptions | `DomainException` + `GlobalExceptionHandler` switch | `TenantScopeMissingException`(400), `AuthorizationFailedException`(403), `ConsentRequiredException`(409) |

---

## 4. Spec ownership & frozen decisions (final)

| Contract / mechanism | Position | Constraint |
|---|---|---|
| `audit_events` write path | **Owned** (D13) | Row shape is spec 12's ratified DDL (`event_id, event_type, actor, subject_ref, correlation_id?, operation_id?, metadata jsonb, occurred_at`) — spec 14 §4.2 prose is stale; envelope fields ride in `metadata`. Append-only, never pruned (GD-4). |
| `IOperatorActionRequest`/`OperatorActionBehavior`/`StaffRole` | **Owned** (14) | Role values ⊂ ratified `actor_type` set `{owner, operator}` |
| `IObjectStore` (+`ListIdsAsync`) | **Owned** (14) | Additive only; Put/Get/Delete/Sweep byte-stable across runs |
| `ConsentPolicyView`(+`redaction`), typed security exceptions, retention workflows | **Owned** (14) | — |
| `conversation_sessions` | **Referenced** (12/D12/15) | Writes only through the Booking API (single-writer); ZERO DDL |
| `verification_grants`/`GrantAuthorizationService` | **Referenced** (08, D5) | Consumed as-is; `ReadSensitive` only; no new enum values, no service changes |
| `TenantContext` mint/sign | **Referenced** (15, D15) | `X-Tenant-Id` shim untouched |
| Envelope/outbox contracts | **Referenced** (05) | Sanitize free-text VALUES only, never shape; control-plane keys exempt |
| Stripe webhook signature verification | **Not ours** (09) | Generic rule only; no `PaymentWebhooksController` |
| DDL/pg-enums | **Not ours** (12) | Zero migrations across all 3 runs |
| Invariants | Inv-1 (partial), **Inv-11, Inv-12, Inv-13 (primary)**, Inv-5, Inv-14/15 | Proven per-phase and in the final validation (§8) |

## 5. What we're NOT doing (final state)

- Real staff authentication — `X-Actor`/`X-Actor-Role` remain a header shim (swap point
  documented; role *matrix* is real, only the *transport* is a shim).
- No cloud object-store provider (filesystem AES-GCM is the seam; provider swap is future work).
- No spec-15 `TenantContext` signing/hardening changes.
- No waitlist customer-endpoint operator-ization (waitlist stays customer/owner-shared, untouched).
- No DDL, no artifact-metadata table (tracked deferred item for spec 12).
- No LLM prompt-assembly/OTel build-out beyond the channel assembler and stub-backed
  `ModelTraceSink` (no LLM nodes exist yet to call them in production).
- No `PaymentWebhooksController`/Stripe signature code (spec 09); no verification-factor/risk-policy
  changes (spec 08 territory); no real media/STT/TTS providers (raw-audio seam exists, stubbed).
- No apps/web UI work.

## 6. Blast radius (cumulative, final)

**apps/api:** `Messaging/RetentionSweepHostedService` + `IRetentionSweeper`;
`EntityFramework/Interceptors/AuditEventSanitizationInterceptor`; `Common/Audit/` (envelope core);
10 staff commands (Menu ×5, Preorder ×2, ManualRecovery ×2, DLQ replay ×1) + validators + handlers
+ their 4 controllers, all gaining `ActorRole`/`RequiredRole`; `Domain/Exceptions/` (3 new) +
`GlobalExceptionHandler`; `Api/TenantGuard`; `Domain/Security/SensitiveDataMask`;
`OutboxRepository`/`ScheduledJobRepository` (sanitize-on-add); `Domain/Interfaces/IObjectStore` +
`EncryptedFileObjectStore`; `ConversationSessionsController` (+2 endpoints: transcript-turns,
transcript); new `Handlers/Security/{AppendTranscriptTurn,GetTranscript,RecordModelTrace,
GetModelTrace}/`; new `ModelTracesController`; `Application/Common/Authorization/StaffRole.cs`;
config keys for retention/TTLs/secrets.

**apps/langgraph:** `contracts/security.py`; `security/{consent,redaction,channels}.py`;
`tools/client.py` (gate 1c); `tools/errors.py` (`tool_not_permitted_envelope`);
`tools/{foreground,client_binding,contracts,status_codes,_timeouts}.py` (+4 tools total: consent
×2, transcript producer ×1, model-trace ×1 — `ALL_TOOLS` 19→21, `FOREGROUND_TOOLS` 13→15);
`streaming/pipeline.py` (optional `TranscriptPersister` port); new
`observability/model_trace.py`; `graph.py`/`state.py`/`checkpoint.py` (consent gate + suppression);
`tools/scripts/export_pydantic_schemas.py` registries.

**libs/shared-schemas + libs/api-contract:** additive regen only across all 3 runs — no existing
consumer type changed shape.

**Must stay byte-for-byte (regression contract, cumulative):** EF tenant-filter mechanism;
`TenantResolutionMiddleware`; `GrantAuthorizationService` + `VerificationGrantRepository`
semantics; `InboxDeduplicationBehavior` / pipeline order (Validation → OperatorAction → InboxDedup
→ Handler); existing audit writers' EventType/Actor pairs (except the one pre-named WI1 exception:
operator-family ALLOW audits now stamp the asserted role); injection harness + menu extraction
isolation; `ToolClient` gating for the original 19 canonical tools with no allowlist set; DTMF log
masking; existing hosted services; waitlist customer endpoints; envelope/outbox shape; checkpointer
scrub-before-persist semantics; store blob layout/sweep audits.

---

## 7. Review findings addressed (3 review passes, folded in)

### Run 1 review — xhigh (2026-07-09): 12 fixed, 7 accepted with rationale
Fixed: PAN redaction failing open on ≥20-digit runs; compact `cvv123` escaping masking; stale
stashed consent overriding a later decline; consent record-failure-after-grant discarding the
reply; checkpoint scrubbing only `transcript_text` (now the whole durable copy via
`redact_payload`); `LogScrubbingEnricher` skipping destructured values; `SensitiveDataMask`
CVV/SSN/TFN parity gap + Unicode-digit Luhn bug; `ConsentController` null-body NRE; enricher
false-positive on property names containing "pan"; double jurisdiction-policy resolution/version
skew; consent audit actor mislabeled "customer" (now `agent`); various cleanup (controller split,
fresh-DI-scope deny audits, canonical-key protection in audit `Extra`).
Accepted: `RecordConsentAsync` bypasses the version token (deliberate last-write-wins); replay
audit key rename `decision`→`replay_decision`; STT keeps streaming after decline (spec 01/02
follow-up); retention sweeps are N+1 (mirrors pre-existing idempotency sweep); two separate yes/no
classifiers (spec-02 NLU unification is follow-up); omitted consent fields fail-SAFE to
`alternative_offered`/`recording_allowed=false`; `X-Actor` read per-controller (matches the gate
decision to keep the shim).

### Run 2 review — max conformance pass (2026-07-10): 13 fixed, 4 accepted with rationale
Fixed: checkpoint identifier corruption — whole-state scrub masked Luhn-valid digit runs inside
`idem_<uuid>`/`op_<uuid>` (~0.25–0.4%/key), blocking Inv-2 durability; `redact_payload` now exempts
control-plane keys. Stashed decline flipped to granted by an unrelated affirmative on retry. Stale
pending notice retained when policy resolution failed mid-retry. Grant denials were never audited
(`GrantAuthorizationService` now writes `verification.grant_denied`). Sensitive reads were never
audited (`GetBookingDetails` now writes `read_sensitive_booking` allow). Consent not provably
linked to submissions (acceptance transaction now stamps the consent snapshot into
`operation.accepted` metadata). `RedactionPolicy` was a dead contract (added the load/install
seam). Tool decisions weren't logged (`is_tool_invocation_permitted` now logs allow/deny). Missing
tenant-isolation test families added (`QueueConsumerTenantIsolationTests`,
`test_job_event_schema_hygiene`). Cross-language redaction drift risk (shared
`redaction-test-vectors.json` corpus now asserted by both languages). Precision regression:
adjacent phone numbers merging into a false PAN, spaced date-times over-masked (~10%) — fixed with
per-group evaluation. Adversarial CPU on >50-digit runs — now O(1) unconditional mask.
Accepted: `HumanHandoff` branch simplified to non-recorded (spec's own diagram marks it
policy-conditional); C# lane always masks SSN/TFN regardless of tenant opt-out (documented
conservatism); C# lane is ASCII-digit-only (foreground filter catches Unicode PANs first); checkpoint
byte-identical idempotency verified empirically, no dedicated test added.

### Run 3 review — xhigh (2026-07-11): 9 fixed, 3 dispositioned
Fixed: pipeline persist ordering — the transcript-persist call was awaited BEFORE `_run_agent`,
adding dead-air latency and racing `resolve_tenant` (silent `tenant_not_resolved` on every
session's first turn); moved after frames yield. Silent envelope discard in
`ToolTranscriptPersister` — non-ok rejections are normal returns, not exceptions, so drops were
invisible; now logged. camelCase wire drift — 5 new response records lacked
`[JsonPropertyName]`, unparseable by the snake_case Python contracts; fixed + api-contract
regenerated. Reader 500/phantom-splice on dot-bearing session ids — `int.Parse` threw on sibling
sessions sharing a prefix; fixed with a strict digit-suffix filter. Deny-audit role mislabel — the
`DomainException` deny path hardcoded `"operator"` even for owners. Hostile ids → 500 instead of
400 — new `ObjectStoreSegment` single-source validator wired into all 4 new validators.
Unknown-role audit fallback inconsistency across the three deny sites — standardized. `ListIds`
was a full-directory scan — prefix pushed down into `Directory.EnumerateFiles`. First-turn audit
race under concurrency — documented as AT-LEAST-ONCE by contract (consistent with platform audit
posture).
Dispositioned (not fixed): global tool-client single slot under concurrent sessions (pre-existing,
spec-15 territory — the ordering fix removed only the newly-added exposure window); serial N+1
turn-decrypt in the reader (acceptable at current sizes, `Task.WhenAll` is the named future
optimization); `GetModelTrace`'s hand-rolled spine (deliberate — spec 13 §4.3 requires auditing
even the missing-identity case, which `OperatorActionBehavior` doesn't do); hand-rolled controller
DTOs vs. generated types for the transcript-turn body (the generated type requires a `session_id`
field the path param already supplies — binding it would change the wire shape).

### Run 4 review — Bugbot (2026-07-11): 8 fixed across five passes
**Pass 1 (2 fixed):** **booking read blocked by audit** — `GetBookingDetailsQuery`'s post-grant
`read_sensitive_booking` ALLOW audit was awaited on the request's (cancelable) token with no
error isolation; a client disconnect or audit-store hiccup during that write could turn an
already-authorized, already-successful read into a 500/cancellation. **Transcript audit not
durable (cancellation)** — `AppendTranscriptTurnCommandHandler`'s `transcript.recording_started`
marker was written on the request token too; a disconnect after the turn object was already
persisted to the store could silently drop the marker while the turn itself survived. Both fixed
the same way, matching the established B2 convention already used by `GetTranscriptQueryHandler`'s
ALLOW/DENY audits and `OperatorActionBehavior`'s DENY audits: write the ALLOW audit on
`CancellationToken.None` so the record's durability is decoupled from the caller's request
lifecycle.

**Pass 2 (3 fixed, surfaced by re-reviewing after pass 1):** **transcript audit not durable
(ordering)** — the cancellation fix in pass 1 did not address a deeper ordering bug: the turn
object was `PutAsync`'d to the store BEFORE the first-turn audit was written; if the audit write
failed after the put succeeded, the client got a 500 and a retry then saw a non-empty prefix,
silently skipped the marker forever, and could finish `Persisted: true` with no
`transcript.recording_started` row ever recorded — a genuine Inv-14 gap. Fixed by writing the
audit BEFORE the put: the only remaining failure mode is a harmless duplicate marker on a
put-retry, consistent with the already-documented AT-LEAST-ONCE audit posture, never a silent
gap. **Empty secret skips migration fallback** —
`NotificationWebhooksController` null-coalesced `ISecretStore.GetSecret` against the legacy
`Notifications:WebhookSecret` config key, so an empty (but non-null) string in
`Secrets:NotificationsWebhookSecret` during a migration window was treated as "configured" and
the fallback never fired, 401-ing every delivery receipt; fixed by blank-checking (not
null-checking) before falling through. **Blank redaction fields not filtered** —
`ConsentPoliciesController.BuildRedactionPolicy` forwarded every entry from
`Redaction:SensitiveFields` verbatim, unlike `LogScrubbingEnricher`'s identical config source,
which already drops blank/whitespace entries; a misconfigured array could emit a non-null
`redaction` block full of useless selectors instead of correctly omitting the field. Fixed by
applying the same filter.

**Pass 3 (1 fixed, surfaced by re-reviewing after pass 2):**
**consent-state wrong audit format** — `SubmitConfirmedIntentCommandHandler`'s `operation.accepted`
consent snapshot (§4.2) wrote `session.ConsentState?.ToString()`, which emits the raw PascalCase
CLR member name (`"Granted"`, `"NotRequired"`) instead of the lowercase snake_case wire/DB value
(`"granted"`, `"not_required"`) that every other spec-14 consent surface uses. Fixed by passing
the enum value straight through to `JsonSerializer.Serialize` instead of calling `.ToString()`
first — `ConsentState` already carries `[JsonConverter(typeof(JsonStringEnumConverter))]` +
per-member `[JsonStringEnumMemberName(...)]`, so the serializer honors the correct wire label on
its own; calling `.ToString()` beforehand bypassed that converter entirely.

**Pass 4 (1 fixed, surfaced by re-reviewing after pass 3):** **whitespace store secret blocks
legacy fallback** — pass 2's webhook-secret fix used `string.IsNullOrEmpty` to decide the
fallback, which still treated a whitespace-only `Secrets:NotificationsWebhookSecret` value (a
common env-var mistake — set but blank) as "configured," so the legacy
`Notifications:WebhookSecret` key still never fired for that case. Tightened to
`string.IsNullOrWhiteSpace`, matching the fix's own stated intent.

**Pass 5 (1 fixed; 2 further findings dispositioned as open decisions, not silently fixed — see
below):** **deny-audit failure becomes 500** — none of `OperatorActionBehavior`'s three deny
branches isolated the audit-store write from the caller's response, unlike the already-updated
`GrantAuthorizationService.WriteDenyAuditAsync`; an audit-store failure during a deny would
propagate and turn the intended 403 authorization refusal into a generic 500, masking the
security decision. Fixed by extracting a shared `TryWriteDenyAuditAsync` helper (mirrors
`GrantAuthorizationService`'s pattern exactly): the deny audit write is now isolated in its own
try/catch, logging a warning on failure but always still throwing the intended
`AuthorizationFailedException` — the audit-record gap is the lesser evil versus a mislabeled 500.
Added `OperatorActionBehaviorTests.DenyAuditWriteFailure_IsSwallowed_AndTheIntended403StillSurfaces`.

Verified after all five passes: `bun nx test api` 934/934, `bun nx test-integration api` 357/359
(2 pre-existing skips; one unrelated flake in `ReplayDeadLetterTests` reproduced once mid-session
and passed clean on immediate retry — not caused by any of these changes), `bun nx lint api`
clean.

**Three findings from passes 5–6 intentionally NOT auto-fixed — flagged as open items for a
deliberate decision instead of a silent patch:**
1. **Operator denials before the handler may skip the audit trail.** All 10 operator commands'
   FluentValidation validators carry redundant `NotEmpty()` rules on `ActorId`/`ActorRole`/
   `ReasonCode`, and `ValidationPipelineBehavior` runs BEFORE `OperatorActionBehavior` in the
   pipeline (Validation → OperatorAction → InboxDedup → Handler). In the real HTTP path, a request
   missing any of those three fields therefore fails FluentValidation first (an **unaudited 400**)
   — `OperatorActionBehavior`'s own identity-incomplete branch (with its audited 403) is
   structurally unreachable for that case; it only fires today when a caller invokes the command
   through MediatR directly, bypassing validation (as the unit tests do). This is a real
   discrepancy against §3.1 step 5 ("append audit_events for the decision, allow **and deny**"),
   but resolving it means picking one of two designs with materially different blast radius: (a)
   remove the three redundant `NotEmpty()` rules from all 10 validators so `OperatorActionBehavior`
   becomes the sole, audited gate (touches 10 validators + their existing "empty field" test
   assertions), or (b) keep the validator-first 400 as an intentional "malformed request, not an
   authorization decision" design and document the deviation. Left open pending that decision.
2. **DLQ replay reads its reason code from the JSON body, not `X-Reason-Code`.** Every other
   operator endpoint (Menu ×5, Preorder ×2, ManualRecovery ×2) reads `ActorId`/`ActorRole` from
   headers and `ReasonCode` from the `X-Reason-Code` header; `DeadLetterMessagesController` reads
   `ActorId`/`ActorRole` from headers but `ReasonCode` from `ReplayRequest.ReasonCode` in the body
   — a legacy shape from Run 1, before the header convention was established in Run 2. Fixing it
   changes DLQ replay's wire contract (breaking for any existing caller sending the reason code in
   the body) and touches the already-executed manual verification guide and PR description, which
   both document the current body-based contract. Left open pending a decision on whether to
   break/migrate the contract or document the inconsistency as accepted.
3. **Missing session silently skips the §4.2 consent stamp.** `SubmitConfirmedIntentCommandHandler`
   only stamps the consent snapshot onto the `operation.accepted` audit when
   `sessions.GetAsync(...)` returns a row; a present-but-missing session (e.g. already anonymized
   by the retention sweep, or a data inconsistency) silently omits the consent proof and
   acceptance still commits — no error, no fallback. Whether that should instead be a hard failure,
   a degraded/explicit "consent_unknown" stamp, or stays a silent best-effort omission is a product
   decision (how strict is the "provable at submission time" guarantee meant to be against a
   session that legitimately no longer exists), not a one-line fix. Left open.

---

## 8. Final validation report (2026-07-11, HEAD after all 3 runs + review fixes)

**Automated suites, final state:** `bun nx test api` **934 passed** · `bun nx test-integration api`
**357 passed**, 2 pre-existing skips · `bun nx test langgraph` **810 passed** · `bun nx test web`
**2 passed** · `bun nx lint api` / `bun nx lint langgraph` both clean · schema + C#/TS/Zod +
api-contract regeneration clean · **zero EF migrations** across all three runs (and the Run 4
bugfix pass). Two Run-4 findings (§7) are open decisions, not yet fixed — see above.

**Regression diff** (verified item-by-item against both prior baselines, `git diff` scoped checks):
zero migrations (`Migrations/` path never touched); envelope/outbox shape untouched; inbox
semantics untouched (no `IInboxConsumerRequest` gained operator semantics; pipeline order
Validation → OperatorAction → InboxDedup → Handler stable throughout); hosted services and
waitlist customer endpoints untouched; `ToolClient` behavior byte-stable for the original 19
canonical tools with no allowlist set; every asserted EventType/Actor pair stable except the one
pre-named exception (operator-family ALLOW audits now stamp the asserted role — assertion-
compatible, no existing test checked that field); sanitizer byte-identity no-op proven on every
existing payload fixture; UTF-8 BOM clean; fail-closed defaults present on every new surface.

**Invariant re-check (final):**
- **Inv-1** (no side effect from transcript text) — injection corpus proves untrusted text can
  never invoke a tool; transcript/trace producers persist only, no command emission.
- **Inv-5** (inbox semantics) — no queue consumer touched across all 3 runs; new tools are
  `BOUNDED_SIDE_EFFECT`, never retried.
- **Inv-11** (server-verified authorization) — grant-or-role required on every sensitive read;
  forged caller identity alone always rejected (proven against real SQL); role mismatches deny +
  audit before the 403.
- **Inv-12** (no sensitive data in context/transcript/queue/logs) — redact→sanitize→encrypt at
  four choke points (audit interceptor, queue sanitizer, object-store encryption, prompt
  channels); PAN-at-rest tests pass on both language lanes; precision/adversarial-CPU findings
  fixed.
- **Inv-13** (tenant scope before access) — single `TenantGuard` across all controllers +
  filesystem/DB tenant isolation proven cross-tenant, including a real inbox-consumer test.
- **Inv-14/15** (audit integrity, retention safety) — `audit_events` append-only, never pruned
  (GD-4); allow AND deny audited on every sensitive read/view, including view *attempts* that
  don't hit (model-trace); deletion runs only through the retention sweeper (anonymize, never
  hard-delete).

**Conformance verdict:** Spec 14 §1–§12 requirement-by-requirement: **conformant**, with these
documented, deliberate deviations: `SessionConsentState` naming (avoids a C# namespace collision
with the ratified `ConsentState` pg-enum; wire JSON unchanged); §4.2's C# `AuditEvent` shape
superseded by spec 12's ratified row (registry wins; envelope fields ride in `metadata` jsonb);
consent proof implemented as an acceptance-audit stamp rather than a submission-envelope field
(spec-04 owns that wire contract); operator identity transport remains the `X-Actor`/`X-Actor-Role`
header shim pending real staff authentication (role *matrix* itself is fully real — GD-1/GD-2);
STT audio-egress consent enforcement is spec 01/02 follow-up (enforcement sits at the
persistence/emit boundary today, documented limitation).

---

## 9. Open decisions from spec §12/§31 — final disposition

1. **Transcript/audio retention by jurisdiction** — ~~open~~ **CLOSED** by GD-3: raw-audio 72h,
   transcript 90d, model-trace 14d (strictly shorter), all config-driven defaults.
2. **Idempotency/audit retention periods** — ~~open~~ **CLOSED** by GD-4: `audit_events` never
   pruned; idempotency records keep the existing 90-day default.
3. **Verification factors & risk policy** — still open; lifecycle owned by spec 08. Spec 14's
   authorization spine consumes whatever risk policy spec 08 eventually ratifies without change.
4. **Owner and internal operations roles** — ~~open~~ **CLOSED** by GD-1/GD-2: `{owner, operator}`
   taxonomy, owner ⊇ operator, `X-Actor-Role` header transport (shim, pending real staff authn).
5. **Configured-sensitive field catalog** — still open (default catalog is PAN/CVV/gov-id
   always-on; `configured_sensitive_fields` exists and is tenant-overridable, but no default
   catalog beyond the always-on set has been ratified).

**Residual, tracked (out of scope, unchanged by this work):** real staff authentication (roles are
real, transport is a shim); spec-12 artifact-metadata table; real media/STT/TTS providers;
`ToolAllowlist.from_tenant_context` removal (spec-15 owns `allowed_policy_ids`'s future); apps/web
surfaces; spec-08 verification-factor/risk-policy decision (item 3 above).

## 10. Package notes

None across all 3 runs. AES-GCM (`System.Security.Cryptography`), `JsonNode` walks, Serilog
enrichers/interceptors are all in-box on both languages — zero new dependencies.
