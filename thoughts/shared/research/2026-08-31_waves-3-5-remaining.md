---
date: 2026-08-31
researcher: Claude (spec-loop Stage 1 — six parallel Sonnet agents, each premise-verifying)
git_commit: 855e73d
branch: feat/waves-3-5-remaining
repository: Cordia
topic: "Waves 3–5 — security & config (#80 #75 #73 #74 #71), authn/authz & console (#72 #83), artifact storage (#84), and the wave-2 follow-ups (#102 #105 #106 #108)"
tags: [research, baseline-audit, apps/api, apps/web, security, inv-1, inv-11, inv-12, inv-13, spec-08, spec-12, spec-14, spec-15]
status: complete
issues: [80, 75, 73, 74, 71, 72, 83, 84, 102, 105, 106, 108]
---

# Research — Waves 3–5

## Method, and why it is this shape

Waves 1 and 2 established the governing fact about this backlog: **ten of the twelve issues addressed
so far did not describe the defect that was actually present**, and twice the filed remedy would have
made things worse if implemented literally. So every issue here was researched as *locate, then
adversarially verify the premise*, by six parallel agents told to assume nothing in the issue text is
true until confirmed against the code.

That paid for itself again. **The single largest item in the backlog (#72, budgeted at 38 h across
three PRs and the roadmap's only breaking change) is mostly already built**, and two more filed
remedies would break production if implemented as written.

Where two agents disagreed, the conflict was adjudicated against the code, not averaged — see
[The asserted-identity gap](#the-asserted-identity-gap) below.

## Headline: premise verdicts

| Issue | Verdict | The real scope |
|---|---|---|
| **#72** authn/authz | 🔴 **MOSTLY FALSE** | Login, JWS sessions, DB revocation, actor precedence and a role matrix over ~35 handlers all exist. The real gap is narrower and live: the role check never verifies the identity behind it. |
| **#80** mutable tenant | ⚠️ **REAL_BUT_MISDESCRIBED** | Interface segregation already blocks handlers. **The filed remedy breaks production** — a write-once setter throws on every dead-letter replay. |
| **#75** unconditional stubs | ⚠️ **REAL, and unimplementable as written** | 6 of 8 ports confirmed. But an environment gate breaks ~20 integration tests, and 5 of the 6 have no real implementation to fall back to. |
| **#71** verification code | ⚠️ **AS_DESCRIBED, and worse** | `IsProduction()` is a literal string match, so Staging silently uses the public pepper. And there is **no delivery channel at all**. |
| **#73** committed secrets | ⚠️ **AS_DESCRIBED, materiality overstated** | Exactly one genuinely usable credential. Blind deletion crashes the host and breaks a cross-language byte-coupling. |
| **#74** webhook HMAC | ✅ **AS_DESCRIBED** | Comparison is already constant-time. The real gap is replay, and the fix has a raw-body trap. |
| **#83** notification state | ✅ **AS_DESCRIBED** | Cheapest real item. Everything downstream exists; only a read path is missing. |
| **#84** artifact upload | 🟥 **AS_DESCRIBED but BLOCKED** | The artifact metadata table is owned by no spec, and the only object store is single-node. |
| **#102 #105 #106 #108** | ✅ confirmed | Small and contained, except #108 — where **my own filed issue undercounted the blast radius by 2.4×**. |

---

## The asserted-identity gap

**This is the most important finding, and it is a live privilege escalation.**

Two agents produced conflicting accounts, so it was settled by reading the code:

- `OperatorActionBehavior<TRequest,TResponse>(IServiceScopeFactory scopeFactory)` — that is its
  **only** dependency. It checks that `ActorId`, `ActorRole` and `ReasonCode` are non-empty, then
  `StaffRole.Satisfies(op.ActorRole, op.RequiredRole)`. It never asks whether the identity was
  *proven*.
- `ActorBinder.Resolve` returns the raw `X-Actor` / `X-Actor-Role` headers whenever no verified
  operator session is present. No environment gate.
- `AssertedActorOptions` — the class that exists **precisely** to gate this class of assertion, whose
  own doc comment says *"anyone who knows a booking id can send `X-Actor-Role: operator` and
  reschedule someone else's booking"* — is correctly defaulted OFF outside Development, and is
  consulted by exactly **two** handlers: `SubmitConfirmedIntentCommandHandler:324` and
  `ValidateBookingChangeQueryHandler:47`. Neither is `OperatorActionBehavior`.

So the gate exists, is correctly configured, and **is not wired to the operator-command path**.
`X-Actor: anyone` + `X-Actor-Role: owner` + `X-Reason-Code: x` passes on ~35 operator commands —
menu publish, deposit refunds, dead-letter replay, manual-review resolution — in every environment.

This is the same defect family as Waves 0–2: not a missing guard, a guard that cannot fire.

---

## Existing Feature Behavior (Baseline Audit)

The regression baseline. Anything here that changes without a plan phase saying it should is a
regression.

### Authentication and authorization (#72)

**What exists and works.** `POST /api/auth/login` verifies email+password via
`IPasswordHasher<Operator>`, mints an HS256 `opctx+jws` (`JwsOperatorSessionSigner`), and persists an
`operator_sessions` row. `TenantContextVerificationMiddleware.BindOperatorSessionAsync` loads that
row on **every** request except the logout path and 401s if missing or revoked.
`ActorBinder.Resolve` makes a verified session win over the header shims. `OperatorActionBehavior`
enforces `StaffRole.Satisfies` across ~35 `IOperatorActionRequest` handlers, failing closed with a
403 and a DENY audit.

**Pipeline order** (`Program.cs`): `UseExceptionHandler` → migrate → `CorrelationMiddleware` →
`TenantContextVerificationMiddleware` → `TenantResolutionMiddleware` → `UseSerilogRequestLogging` →
Swagger (dev-gated) → `UseCors` → `UseDefaultFiles`/`UseStaticFiles` → `MapControllers` →
`MapFallbackToFile`. There is **no** `UseAuthentication`/`UseAuthorization`, and nothing produces a
`ClaimsPrincipal` — the auth model is custom middleware + a MediatR behaviour, deliberately.

**Two planes on the same controllers.** The agent carries `tenantctx+jws` (tenant only, never an
operator identity); the console carries `opctx+jws`. Both hit `BookingsController`,
`BookingOperationsController`, `CatalogController`, `VerificationsController`,
`ConsentPoliciesController`. A blanket `[Authorize]` kills the agent's customer lane; a blanket
`[AllowAnonymous]` leaves `/schedule` and `/attendance` open. **Only per-action gating can separate
them**, which is what `IOperatorActionRequest` already does.

**Genuinely ungated reads:** `CustomersController` (PII, masked but returned to any tenant-scoped
caller), `PayoutsController`, `ReportingController`, `DepositsController.List`. None injects
`IActorContext`; none implements `IOperatorActionRequest`.

**No operator provisioning exists.** `IOperatorRepository.AddAsync` has no production caller, no
migration contains `InsertData`, and the only real operator row comes from
`apps/api/scripts/seed-dev-tenant.sql`. Making login mandatory without a provisioning path is an
irreversible console lockout.

### Tenant context (#80)

`ITenantContext` / `IActorContext` expose **get-only** properties; the mutable setters live only on
the concrete `Infrastructure.Services.TenantContext` / `ActorContext`. DI registers both, so ordinary
Application handlers and repositories genuinely cannot mutate the tenant — only middleware, three
hosted services, `PostgresMessageTransport`, a design-time factory, and ~20 test sites reach the
concrete type.

**The filed remedy breaks production.** `DeadLetterMessagesController:45` →
`ReplayDeadLetterCommandHandler:112` → `PostgresMessageTransport.PublishAsync:30` re-stamps the
tenant inside a request scope the verification middleware already stamped. A strictly
write-once-throw setter throws on every dead-letter replay. (The three hosted services each open a
fresh scope per row, so only this synchronous in-request path collides.)

### Stub registrations (#75)

Six ports genuinely unconditional: `IBookingProviderAdapter`, `IVerificationService`,
`IValidationPolicy`, `IArtifactScanner`, `IPreorderItemReservation`, `IExternalInventoryAdapter`.
Two already resolved: `IDepositPolicy` resolves to the real `TenantDepositPolicyProvider`
(`NoDepositPolicy` is dead code registered nowhere), and `IIsolatedMenuExtractor` is config-gated
with `ValidateOnStart` failing closed outside Development.

**Two things make the literal fix wrong.** `AddInfrastructure(services, config, environment = null)`
documents that `null` reads as *a deployment, not dev* — deliberately — and ~20 of 23 integration
test files call it with no environment argument. And five of the six ports have **no real
implementation**, so gating them does not harden production; it stops production resolving them at
all.

**The correct pattern already exists in the same file**: `IIsolatedMenuExtractor` is gated on a
config *value* (`LangGraph:UseStubExtractor`, defaulting to the real client), not sniffed from
`IHostEnvironment`.

### Verification codes (#71)

`VerificationCode.DevelopmentCode = "000000"` is hashed into **every** challenge, for every tenant
and customer. `IsProduction()` is a literal `== "Production"` match on `ASPNETCORE_ENVIRONMENT`, so
**Staging, QA, or any custom environment name silently uses the committed public pepper**.

The code is never delivered anywhere — `RequestVerificationCommandHandler` never calls the
notification pipeline, and `SmsNotificationAdapter`/`EmailNotificationAdapter` are documented stubs
with no network I/O. Spec 08 §332 defers OTP delivery to spec 10, which is itself a stub.

Net effect, proven by an existing test (`VerificationEndpointTests:82`): **anyone who knows a booking
id can self-verify by posting `{"code":"000000"}`** and receive a real `VerificationGrant`.

There is also **no attempt counter and no rate limiting** — `IVerificationChallengeRepository` has no
method for it. Adding one needs a migration.

### Secrets (#73)

Exactly one genuinely usable credential is committed: `appsettings.json:7`'s
`Password=localdev`, in the **base** file, byte-identical to what `docker-compose.yml` provisions.
The `Secrets:*` values in Development/Test decode to literal strings saying they are not real
secrets.

`ISecretStore` exists but `ConfigurationSecretStore` just wraps `IConfiguration.GetSection("Secrets")`
— it is not a vault, so anything under `Secrets:` in a committed file *is* what the store returns.

**Two landmines.** `Secrets:TenantContextSigningKeys` is byte-coupled to
`apps/langgraph/.env.example:269` — same kid, same plaintext — so changing one side alone breaks
agent↔API JWS verification *silently* (wrong-signature, not missing-config). And
`JwsTenantContextSigner`'s constructor calls `GetRequiredSecret` and throws; it is a singleton, so a
blanket strip crashes the host at first resolution.

**Materiality correction:** the Development/Test files are copied into the image by default SDK glob
behaviour, but only *loaded* when `ASPNETCORE_ENVIRONMENT` is literally `Development`/`Test`. The
accurate framing is "they ship in the image and would be live if environment resolution were ever
misconfigured, and nothing catches that" — not "they are live in production today".

Startup validation exists but covers **only** `LangGraphOptions`. Nothing validates the DB password,
webhook secret, signing keys or object-store key against insecure defaults. No secret-scanning hook
exists in `.pre-commit-config.yaml`.

### Notification webhooks (#74)

`NotificationWebhooksController:59-84` compares a shared secret using
`CryptographicOperations.FixedTimeEquals` with a dummy compare on length mismatch — **the comparison
is already timing-safe**; that part of the issue's framing is unfair to the code. `Secrets:` wins
over the legacy `Notifications:WebhookSecret`, a precedence pinned by tests T8/T9.

The real gap is **replay**: no timestamp or nonce at the HTTP layer. App-level idempotency makes
replaying a *previously-seen* receipt harmless, but a leaked secret can still forge a **first-time**
receipt that flips a still-`Accepted` notification to `Delivered`.

**Two traps for the fix.** `JwsTenantContextSigner` is the right canonicalisation exemplar but its
wire shape is a JWS token, not a webhook signature header — reusing it needs a deliberate design
call. And HMAC-over-body needs the **raw unparsed bytes**, which `[FromBody]` consumes; request
buffering must be enabled or the signature check is computed over the wrong input.

### Console notification state (#83)

The `notifications` table, an eight-value `NotificationState` enum, and every state-transition
repository method exist. What does not: any `[HttpGet]` for notifications anywhere, and any
list-by-operation method on `INotificationRepository`. `BookingDetailDrawer.tsx` renders only a
status chip. `forbiddenEndpoints.test.ts:157-164` already anticipates `GET /api/notifications` and
asserts the §13-C pattern will not false-positive on it.

### Artifact upload (#84)

`source_artifact_ref` is whatever string the operator types; `UploadMenuValidator` imposes only a
1 MiB length cap, no scheme restriction. `InlineMenuArtifactTextResolver` accepts `inline:` and
`data:text/plain` and documents that object-storage schemes "arrive with #84, behind this same port".
`IArtifactScanner` is a stub flagging only refs containing the literal string `"malware"`.

**Blocked on two things that are not implementation.** The artifact metadata/retention table is owned
by **no spec** — `docs/specs/README.md:208-209` says so explicitly, and spec 12 owns all DDL. And
`EncryptedFileObjectStore` is a **local filesystem** store whose own comments document a lock-free
TOCTOU on sweep and single-writer semantics; menu ingestion runs through the in-process outbox, so
under more than one replica the pod that accepts the upload is not necessarily the pod that ingests
it.

**Correction to an earlier assumption of mine:** #84 does *not* let the Wave 1 `source_artifact_ref`
masking special-case be retired. Its acceptance criteria retire the *console's* typed-ref field, not
the *API's* acceptance of `inline:`/`data:`, so a direct caller can still submit a document inline.

### Wave-2 follow-ups (#102 #105 #106 #108)

- **#102** — `lease_expires_at` already exists on `OutboxMessage` since the original job-queue
  migration, so **no migration**. `MarkPublishedAsync` is the exact update shape a `RenewLeaseAsync`
  would reuse. The difficulty is design: the slow call is *inside* the opaque
  `IMessageTransport.PublishAsync`, so renewing between rows does not help the first slow row.
- **#105** — `IsOpen(` has exactly one call site (`FinalizeBookingCommandHandler:502`).
  `InMemoryProviderCircuitBreaker` has a dependency-free constructor and is already DI-registered.
  The "stay in `Reconciling`, return `Wait`" branch to route into already exists at `:454-463` from
  the Wave 2 #55 fix. Open question is the circuit **key**: `IsOpen` is keyed only on
  `(providerName, providerAccount)`, so reusing it conflates "cannot create" with "cannot read".
- **#106** — the stale clause is `docs/specs/07-capacity-and-holds.md:365`. `CapacityPrimitiveResolver.Resolve`
  ignores all three parameters and returns `FirstParty`; one call site. A table needs a migration,
  with `TenantDepositPolicy` as precedent. `CapacityPrimitiveResolverTests` **asserts the hardcoded
  behaviour**, so those tests must be rewritten, not merely kept green.
- **#108** — **my own issue text undercounted the blast radius.** I wrote "~20 `TryTransitionStatusAsync`
  call sites, small enough to audit"; it is **49 across 16 files**, including deposit events, preorder
  saga and alternatives handling that have nothing to do with dispatch. Every one reads a fresh
  version then transitions, and this codebase treats a 0-row result as "not an error, re-drive" — so
  false collisions from an out-of-band bump would be **silent**. That settles the design toward the
  advisory-lock alternative, which touches two decision points and no schema.

---

## Test coverage that must stay green

| Area | Suites |
|---|---|
| Auth | `WebOriginAuthTests`, `ActorBinderWiringTests`, `ActorBinderTests`, `OperatorActionBehaviorTests`, `MutationAuthorizationTests`, `WaitlistAuthorizationTests`, `SubmitConfirmedIntentMutationAuthorizationTests`, `GrantAuthorizationServiceTests`, `JwsOperatorSessionSignerTests`, `OperatorPersistenceTests` |
| Tenancy | `TenantResolutionMiddlewareTests`, `TenantContextVerificationMiddlewareTests`, `PostgresMessageTransportTests`, `QueueConsumerTenantIsolationTests`, plus ~20 integration sites assigning `TenantContext.TenantId` directly |
| Infrastructure | ~20 integration files calling `AddInfrastructure(config)` with no environment argument |
| Verification | `VerificationEndpointTests` (**hard-codes `"000000"`** — breaks the moment the code is random), `VerificationGrantPersistenceTests`, `GrantScopeTests` |
| Webhooks | `NotificationWebhookTests` T1–T9 (T8/T9 pin the secret precedence) |
| Notifications | `NotificationPersistenceTests`, retry/reconcile/redaction unit suites |
| Menu | `MenuControllerTests` (accepts `s3://`), `MenuIngestionTests`, `MenuArtifactRedactionTests`, `SensitiveDataMaskDataUriTests`, `redaction-test-vectors.json` |
| Wave-2 | `DispatchFenceTests`, `FinalizeBookingCommandHandlerTests`, `CompensateOperationTests`, `ReconcileOperationCommandHandlerTests`, `OutboxPublisherTests`, `OutboxLaneTests`, `CapacityPrimitiveResolverTests` |
| Web | `forbiddenEndpoints.test.ts`, `lifecycle.test.tsx` |

Baseline at `855e73d`: **1869 unit**, **704 integration / 0 failed / 1 pre-existing skip**.

## Type-generation blast radius

- **#83** adds a new route ⇒ `libs/api-contract/openapi/v1.json` regenerates, consumed by web.
- **#84** would add the API's **first** `multipart/form-data` body — a content type the exporter has
  never emitted from this codebase.
- Everything else is internal: no Pydantic contract and no controller signature changes, so
  `v1.json` must regenerate **byte-identical**.
