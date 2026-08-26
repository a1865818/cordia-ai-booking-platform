---
date: 2026-08-26
researcher: Claude (spec-loop Stage 1 — five parallel agents)
git_commit: 958a168
branch: fix/wave-1-correctness
repository: Cordia
topic: "Wave 1 — outbox lanes and lease (#96), artifact-ref masking (#95), cancellation outcome (#53), CORS/correlation/Swagger (#81/#77/#94-D9), stale gap docs and TODOs (#85/#79)"
tags: [research, baseline-audit, apps/api, apps/langgraph, messaging, inv-12, inv-16, spec-05, spec-06, spec-14, spec-15]
status: complete
issues: [96, 95, 53, 81, 77, 85, 79, 94]
---

# Research — Wave 1: cheap correctness and hygiene

Baseline for the second wave. Unlike Wave 0, this wave changes **production code**.

## Scope

| Lane | Touched | Contract change? |
|---|---|---|
| api — `Cordia.Infrastructure/Messaging` | yes (#96) | no |
| api — `Cordia.Domain/Security` | yes (#95) | no |
| api — `Cordia.Api/Controllers`, `Program.cs` | yes (#53, #81, #77) | see below |
| langgraph — `cordia_agent/security` | yes (#95) | no |
| `tools/config/redaction-test-vectors.json` | yes (#95) | shared corpus, both lanes |
| docs | yes (#85) | — |
| web, shared-schemas | **no** | — |

**Type-generation blast radius: none.** #53's endpoint returns `IActionResult` with an anonymous
object, so `v1.json:4656` has **no `content`** for the 202 — there is nothing generated to break.
No Pydantic contract changes.

**Security-sensitive: yes** — #95 is Inv-12, #77 is correlation provenance. `engineering-security-engineer.md`
applies at planning and review.

---

## Existing Feature Behavior (Baseline Audit)

### B1 — Outbox publisher: sequential dispatch, one shared lease (#96)

`OutboxPublisherHostedService.PublishPendingBatchAsync` (`:45-171`):
claim in its own scope (`:51-76`), rows detached to `ClaimedRow`, scope disposed **before** dispatch;
then a **single sequential `foreach`** (`:83-168`), each row's `await transport.PublishAsync` (`:109`)
fully resolved — success, dead-letter, or leave-unpublished — before the next row is looked at.
No fan-out. The only concurrency in the whole design is multiple API processes racing
`FOR UPDATE SKIP LOCKED`.

**The lanes already exist and are already classified.** `LogicalQueue.cs:55-64` declares
`OrderingRequired` per queue:

| Queue | OrderingRequired |
|---|---|
| `booking.commands`, `booking.reconcile` | **true** |
| `booking.scheduled`, `booking.compensate`, `notification.commands`, `waitlist.commands`, `menu.commands`, `preorder.commands`, `operations.review` | false |

The slow message rides `menu.commands`; the traffic it starves is `booking.commands` and
`notification.commands` — **different queues**. So grouping the claimed batch by queue and running
the *groups* concurrently, while staying sequential *within* a group, removes head-of-line blocking
without weakening any declared ordering guarantee.

**Lease sizing.**
- `ScheduledJobPollerOptions.LeaseSeconds = 30` — `MessagingOptions.cs:87`
- `OutboxPublisherOptions.LeaseSeconds = 30` — `MessagingOptions.cs:208`

Two independent classes, no shared constant. **Only `:208` is the target.**

**The slow call.** `LangGraphOptions.cs:53` `ExtractTimeoutSeconds = 120`, doc-commented: *"The call
runs inside the ingestion handler's open inbox transaction, so every second here is a second a
Postgres transaction stays open."* Both `HttpClient.Timeout` and the client's own linked CTS are set
to the same 120 s.

**`CommandTimeout` is configured nowhere in `apps/api`** — Npgsql's 30 s default stands.

**Issue #96 point 3 is misdiagnosed, confirmed.** `InboxRepository.TryAdmitAsync:41-58` is
`ON CONFLICT (consumer_name, tenant_id, message_id) DO NOTHING` returning `rows == 1`, and
`InboxDeduplicationBehavior:60-78` runs it **before** `next(ct)` — the handler is never invoked on
the duplicate path. Double-billing is impossible.

**Spec position — an OPEN DECISION.** Spec 05 §12 **OD-3**: *"lease = 2× p99 step latency,
heartbeat-renewable for legitimately long ops only"* — the spec never commits to a number. The
shipped flat 30 s derives from no measurement and there is **no renewal mechanism** in the publisher.
Spec 05 never uses the phrase "head-of-line blocking" anywhere.

**Coverage:** `OutboxPublisherTests` (7 tests). None asserts intra-batch ordering.
`PublishPendingBatchAsync_PublishesBothRows_MarksPublished` asserts `count >= 2` because the tick is
cross-tenant.

**Invariant upheld today:** Inv-5 (duplicate-safe consumers) via the inbox. Must not weaken.

### B2 — `source_artifact_ref` is exempt from masking (#95)

`SensitiveDataMask.cs:94-99` — `_controlPlaneKeys` is an **exact-name** allowlist containing
`source_artifact_ref`. `:75-77` states `_ref` is *deliberately not* a suffix exemption, because
`customer_ref` is client-supplied free text. Mirrored byte-for-byte at `redaction.py:184-198`.

**The choke point runs and is a deliberate no-op.** `OutboxRepository.AddAsync:12-19` and
`ScheduledJobRepository.AddAsync:13-19` both call `SanitizeJson`; `WalkNode` sees the exempt key and
returns the value verbatim, unscanned.

**The ref *is* the document.** `InlineMenuArtifactTextResolver.cs:7-27` resolves only `inline:` and
`data:text/plain`; `s3://`/`blob://` throw `MenuArtifactUnresolvableException` and "arrive with #84".
`UploadMenuCommandHandler.cs:40-69` writes `{catalog_id, source_artifact_ref}` into
`outbox_messages.payload`, and its own comment at `:46-62` already says: *"The ref is NOT opaque and
this is NOT bytes-free (#95)… the old comment here asserted 'no secrets/bytes — Inv-12', which is
true of a storage key and false of `inline:`."* Only mitigation today is
`UploadMenuValidator.MaxSourceArtifactRefChars = 1 MiB`.

**Live consumer of the value.** `ApplicationServiceCollectionExtensions.cs:197-206` extracts it back
out; `IngestVendorMenuCommandHandler.cs:98,135` passes it to `scanner.ScanAsync` / `extractor.ExtractAsync`.
Masking replaces only *detected spans*, so ordinary menu prices survive — **but that must be proven,
not assumed**, and `_UNCONDITIONAL_MASK_DIGITS = 50` is a second path needing a vector.

**Shared corpus.** `tools/config/redaction-test-vectors.json` — 21 text vectors + 18
`payload_key_vectors`, including `{ "key": "source_artifact_ref", "expect_exempt": true,
"class": "system-ref-artifact" }`. Consumed by `SensitiveDataMaskParityTests.cs` (.NET) and
`test_redaction_parity_corpus.py` (pytest), both resolving the file by walking up to `nx.json`. A
one-sided edit reds one suite.

**Spec position.** Spec 14:39 (MUST NOT) and 14:272: queue payloads carry *"no transcript text / no
secrets"* (Inv-12). The spec is on the side of removing the exemption.

**Fast-path trap:** `SanitizeJson` bails at `:117` when `CountAsciiDigits(json) < 3`.

### B3 — Cancellation reports a stale outcome (#53)

`BookingOperationsController.cs:167-191`. It **does** guard terminality at `:180` (409), but from a
**stale snapshot** read by a separate `mediator.Send` at `:174`. Between that read and `:189`, the
workflow driver can legally commit `Finalizing → Booked` (edge exists,
`OperationStateMachine.cs:69`). Then:

- `RequestCancellationCommandHandler:41-46` calls `SetCancellationRequestedAsync`, whose
  `WHERE … && !TerminalStatuses.Contains(o.Status)` (`BookingOperationRepository.cs:119-120`)
  matches 0 rows → returns **`false`**.
- `:189` **discards** that bool; `:190` hardcodes `cancellation_pending = true`.

**The handler is already correct and already proven correct** —
`CompensationAndCancellationTests.cs:758-802` asserts `false` for a terminal op.

**Terminal set** (derived, `OperationStateMachine.cs:118-121`): `Booked, Rejected, Expired,
Waitlisted, FailedFinal`.

**Two unrelated `cancellation_pending` concepts** — this operation-level flag, and a
`BookingLifecycleStatus` value in spec 08 for post-booking cancellation. **Do not conflate.**

**Web consumers do not read the boolean.** `features/operations/api.ts:70` is `fetchApi<unknown>`;
`ConsolePage.tsx:997` toasts a static "Cancellation requested" on any 2xx. The console's message is
already honest; the API's body is not.

**Spec position.** Spec 06 failure-mode row 13 (`:342`) requires exposing `cancellation_pending` —
i.e. real pending-ness. Inv-16 (agent states only authoritative outcomes).

**Coverage gap:** `Cancel_TerminalOp_Returns409` seeds terminal *before* the call;
`Cancel_NonTerminalOp_Returns202` never races. Neither exercises the window.

### B4 — CORS, correlation, Swagger (#81, #77, #94 D9)

**CORS** — `Program.cs:162-168`: `WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()`, origins
from `Cors:AllowedOrigins`. Values: `appsettings.json:26-28` → **`[]`**;
`appsettings.Development.json:14-16` → `["http://localhost:4200"]`; `appsettings.Test.json` → no
section, inherits `[]`. **No `AllowCredentials()`.**

So the issue title is technically true but the risk is low: `AllowAnyHeader`/`AllowAnyMethod` only
relax same-origin policy *for already-allowlisted origins*, the production default allowlist is
empty, and credentials are off. This is defence-in-depth, not exposure.
**`CorsPolicyTests` does not exist** — a new test file is needed.

**Correlation** — `CorrelationMiddleware.cs:31-38` **already validates** inbound ids against
`^[A-Za-z0-9_-]{1,64}$` with an explicit log-injection/response-splitting comment. #77's stated
defect is fixed *at the middleware*.

**The live defect is one layer up:** `VerificationsController.cs:47` binds `X-Correlation-Id` via
MVC `[FromHeader]`, bypassing that regex entirely, and `:57` falls back to
`HttpContext.TraceIdentifier` instead of the middleware-minted id. A second, unvalidated path into
audit. `AuditEventSanitizationInterceptor.cs:83-85,106` masks `CorrelationId` only when the value is
*PAN-shaped* — DLP, not validation.

**Spec position.** Spec 15:70, :285 names the gateway the sole correlation originator, minted once
per session and never re-minted; `CorrelationMiddleware` is a sanctioned second mint point for
direct API calls (spec 13:324). `VerificationsController` is an unsanctioned third.

**Swagger** — `Program.cs:337-338` unconditional, and they sit **before** `UseCors` at `:339`, so a
CORS-based gate would not cover them. `OpenApiExport` has **zero readers** in `apps/api` (confirmed);
`export_openapi.mjs` sets it and Wave 0 kept it deliberately as the hook for this gate.

**Pipeline order** (`Program.cs`): `173 UseExceptionHandler → 185 Correlation → 192 TenantContextVerification
→ 208 TenantResolution (conditional) → 336 UseSerilogRequestLogging → 337 UseSwagger → 338 UseSwaggerUI
→ 339 UseCors → 343 UseDefaultFiles → 344 UseStaticFiles → 346 MapControllers → 350 MapFallbackToFile`.

**Coverage:** no CORS test at all. `CorrelationChainContractTests` has one test, happy-path only —
no malformed input, no mint path, no causation header, and it does not touch the
`VerificationsController` path.

### B5 — `docs/backend-gaps.md` is far staler than scoped (#85, #79)

**14 of 16 gaps read as closed against current code**, while the doc still frames them as open
blockers — including its TL;DR ("One gap blocks deployment at all… Two whole screens have no
backend… Five Settings panels persist nothing").

| Verdict | Gaps |
|---|---|
| **Contradicted by code — closed** | G1 (auth: `AuthController.cs` login/session/logout exists), G2 (`IDepositPolicy` = `TenantDepositPolicyProvider` at `InfrastructureServiceCollectionExtensions.cs:208`, **not** `NoDepositPolicy`, which is now dead code; `DepositsController`, `PayoutsController` exist), G3–G16 |
| **Still open — matches doc** | **G17** (no notification-delivery read endpoint), **G18** (no artifact upload) |

Migration `20260813121656_ConsoleCompleteness` landed the same day the doc says it was "Mapped:
2026-08-13" and appears to be what closed most of the list. `docs/frontend-api-wiring.md` §12 gaps 1,
2 and 9 are stale in the same way.

**§13-C names six endpoints the console must never call:** `POST /api/channels/messaging`,
`/api/channels/telephony`, `/api/notification-webhooks/delivery-receipts`,
`/api/conversation-sessions/{id}/transcript-turns`, `/api/conversation-sessions/{id}/consent`,
`/api/Bookings/{id}/modifications`.

**None is called from live console code.** C1–C4 do not appear as strings in `apps/web/src` at all.
C5 appears once — a doc comment in `ConsentBadge.tsx:19`. C6 appears twice — a comment in
`submit.ts:81` and an error-parsing unit test in `client.test.ts:220` that hits a mocked `fetch`.

**There is no automated guard whatsoever.** No lint rule, no CI check, no test would fail if a
console component started calling any of the six tomorrow. §13-C is enforced by prose alone.

**Also legitimate and easily false-positived:** `settings/api.ts:8-10` calls
`GET /api/consent-policies` — a *different* resource (the venue's consent **policy**) from
`POST …/consent` (the guest's consent **state**).

**TODOs — the counts differ from the issue and from the roadmap.** Across `apps/api/src`,
`cordia_agent`, `apps/web/src`: **15 hits total** = **11 false positives** (all `\uXXXX` escape prose
in canonical-hash/JWS code) + **4 real deferred TODOs**, and **zero** `TODO(cordia)` markers:

| File:line | TODO |
|---|---|
| `GetAgentKnowledgeQuery.cs:12` | include open `HandoffWorkItem` rows with no knowledge entry |
| `NotificationWebhooksController.cs:59` | replace shared-secret check with HMAC — **duplicate of #74** |
| `BookingOperationsController.cs:161` | surface `cancellation_pending` in the status projection — **adjacent to #53** |
| `ResolveTenantContextCommandHandler.cs:141` | real bundle → policy-id expansion (spec-15/03) |

The **25 `TODO(cordia)` markers are all in `.github/workflows/`** (17 in `ci-cd.yml`, 8 in
`app-deploy.yml`) and are Azure resource-id / OIDC / app-settings placeholders — they belong to #94,
not #79.

---

## Blast radius

| Item | Disposition |
|---|---|
| `OutboxPublisherHostedService.PublishPendingBatchAsync` | **intentionally changing** (#96) |
| `OutboxPublisherOptions.LeaseSeconds` (`:208`) | **intentionally changing** (#96) |
| `ScheduledJobPollerOptions.LeaseSeconds` (`:87`) | **must stay 30** — different lane |
| `OutboxRepository.ClaimBatchAsync` / the Wave 0 tenant-scoped mirror | **must stay byte-for-byte** |
| `LogicalQueue.cs` registry + `OrderingRequired` values | **must stay** — read, not changed |
| `SensitiveDataMask._controlPlaneKeys` | **intentionally changing** (#95) — one entry |
| `redaction.py` `_CONTROL_PLANE_KEYS` | **intentionally changing** — must stay mirror-identical |
| `redaction-test-vectors.json` | **intentionally changing** — one vector flips + new vectors |
| `_ref` suffix policy, `customer_ref` behaviour | **must stay** |
| `BookingOperationsController` cancel endpoint | **intentionally changing** (#53) |
| `RequestCancellationCommandHandler` | **must stay** — already correct |
| `Program.cs` CORS + Swagger calls | **intentionally changing** (#81, #94 D9) |
| `CorrelationMiddleware` | **must stay** — already validates |
| `VerificationsController` correlation bind | **intentionally changing** (#77) |
| `docs/backend-gaps.md`, `frontend-api-wiring.md` §12 | **intentionally changing** (#85) |
| `apps/web/**` | **no change this wave** |
| `v1.json` | must regenerate identically unless #53 formalises a DTO |

## Coverage that must stay green

`bun nx test api` (1812), `bun nx test-integration api` (670/669), `bun nx lint api`,
`bun nx test langgraph` (1428), `bun nx lint langgraph`. Web unrunnable locally (Bun < 1.4).

## Open questions for Stage 3

1. **Spec 05 OD-3 lease value.** Choosing a number resolves an open decision. Options: raise the
   outbox lease to cover the 120 s extraction; or add renewal; or move extraction off the lane.
2. **How far to take `docs/backend-gaps.md`** now that 14 of 16 gaps are closed, not 2.
3. **Whether #95 masking breaks menu extraction** — must be proven with a real menu vector.
