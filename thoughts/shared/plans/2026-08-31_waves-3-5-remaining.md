---
date: 2026-08-31
planner: Claude (spec-loop Stage 2)
branch: feat/waves-3-5-remaining
base: main @ 855e73d
repository: Cordia
topic: "Waves 3–5 — the remaining backlog in one PR"
tags: [plan, apps/api, apps/web, security, inv-1, inv-11, inv-12, inv-13, spec-08, spec-12, spec-14, spec-15]
status: approved
issues: [72, 80, 105, 71, 73, 106, 83, 75, 74, 102, 108]
research: thoughts/shared/research/2026-08-31_waves-3-5-remaining.md
---

# Plan — Waves 3–5

Fourteen phases, one commit each, on one branch. Ordered **security-first**, so the highest-severity
item lands first and the wave degrades gracefully if it is stopped early.

## What research changed before a line was written

| Issue | The plan does NOT do what the issue asks, because |
|---|---|
| **#72** | It is **mostly already built** — login, JWS sessions, DB revocation, actor precedence and a role matrix over ~35 handlers exist. No ASP.NET auth stack is added, no `[Authorize]` rollout, no breaking change. The real gap is that the role check never verifies the identity behind it. |
| **#80** | The filed remedy (write-once setter that throws) **breaks production**: dead-letter replay re-stamps the tenant inside an already-stamped request scope. Same-value tolerance instead. |
| **#75** | An environment-keyed gate breaks ~20 integration tests, and five of six ports have no real implementation. Config-flag opt-in with a loud startup warning instead (**decision D1**). |
| **#71** | Worse than filed: `IsProduction()` is a literal string match, so Staging uses the public pepper. And there is no delivery channel at all, so a naive fix breaks verification for everyone. |
| **#73** | Blind deletion crashes the host (`JwsTenantContextSigner` throws on a missing key, and it is a singleton) and breaks a byte-coupling with `apps/langgraph/.env.example`. Startup validation instead of deletion. |
| **#74** | The comparison is already constant-time. The real gap is replay, and HMAC-over-body needs raw bytes `[FromBody]` consumes (**decision D2**). |
| **#108** | The version-bump option touches **49** call sites, not the ~20 the issue (which I wrote) claimed, and false collisions would be silent. Advisory lock instead (**decision D3**). |
| **#84** | **Out of scope — genuinely blocked.** The artifact metadata table is owned by no spec (`docs/specs/README.md:208-209`) and spec 12 owns all DDL; `EncryptedFileObjectStore` is single-node by its own comments. |

## Decisions taken at the approval gate

| # | Decision |
|---|---|
| **D1** | **#75** — config-flag opt-in per stub, defaulting to stub-on so nothing breaks, with a loud startup warning outside Development listing every active stub. Not refuse-to-boot. |
| **D2** | **#74** — `X-Signature` HMAC over the raw body with request buffering, plus a timestamp window for replay. Rewrites tests T1–T7. |
| **D3** | **#108** — `pg_advisory_xact_lock` over the two decision points. No schema change. |
| **D4** | Scope — Tier A + B + the decided Tier C. #84 excluded. |

## How this maps onto Cordia

| New concept | Existing artifact it extends | Consequence |
|---|---|---|
| "Was this actor proven?" | `IActorContext.ActorId` (set only from a verified `opctx+jws`) + `AssertedActorOptions` | `OperatorActionBehavior` consults the gate that already exists for exactly this — it is wired, not invented |
| Gating ungated reads | `IOperatorActionRequest` + `OperatorActionBehavior` | Four queries adopt the existing interface; no new mechanism |
| Tenant mutation discipline | `ITenantContext` get-only interface already in place | A `SetTenant` that tolerates a same-value repeat; the interface keeps doing the rest |
| Stub visibility | `LangGraph:UseStubExtractor` + `ValidateOnStart` | Same shape per port; the pattern is copied, not designed |
| Insecure-default refusal | `InfrastructureServiceCollectionExtensions` `.ValidateOnStart()` (today only `LangGraphOptions`) | Extended to the DB password, webhook secret, signing keys |
| Webhook integrity | `JwsTenantContextSigner`'s canonicalisation discipline | Signature shape is a webhook header, not a JWS token — deliberate divergence |
| Reconcile circuit guard | `IProviderCircuitBreaker` (registered, dependency-free) + the `Wait` branch from Wave 2 #55 | One ctor param and a guard routing into a branch that exists |
| Dispatch/cancel mutual exclusion | `IUnitOfWork` transactions already wrapping both sites | `pg_advisory_xact_lock` inside them; no schema |

## Spec ownership & frozen decisions

| Contract | Owner | This wave |
|---|---|---|
| `verification_action` enum, grants | **spec 08** | Referenced. No enum label added. |
| Operator-action role matrix (GD-1/GD-2) | **spec 14 §24.5** | Referenced — enforcement is wired, the matrix is unchanged |
| Tenant resolution / `TenantContext` | **spec 15** | Referenced |
| All DDL / pg-enums | **spec 12** | **Two additive migrations** (verification attempt counter; capacity-primitive table). No enum labels. |
| Notification state enum | **spec 10** | Referenced — read-only exposure |
| Artifact metadata table | **unowned** | **Why #84 is out of scope** |

**Invariants in play:** Inv-11 (mutations require server-verified authorization — Phase 1 is the
direct fix), Inv-13 (tenant scope), Inv-12 (no card data in logs — the startup warning must not print
secret values), Inv-1 (no side effect from unverified assertion).

---

## Phases

Each is one commit. Automated verification per phase: `bun nx test api` + `bun nx lint api` green,
and every guard **mutation-tested** — broken on purpose, must red exactly its own test, with a paired
negative test staying green.

### Phase 1 — #72a: `OperatorActionBehavior` must require a proven identity

`ActorBinder` returns raw `X-Actor*` headers when no session exists, and the behaviour never asks
whether the identity was proven. Inject `IActorContext` + `IOptions<AssertedActorOptions>`; when the
actor is unproven and `AllowAssertedStaffRole` is false, deny with the existing 403 + DENY audit.
**Highest severity in the backlog.**

### Phase 2 — #72b: gate the four ungated reads
`GetCustomerQuery`, `ListCustomersQuery`, `ListDepositsQuery`, and the `Reporting`/`Payouts` queries
adopt `IOperatorActionRequest`. Controllers thread actor/role/reason as their gated siblings do.

### Phase 3 — #80: encapsulate tenant mutation
`SetTenant(...)` that **tolerates a same-value repeat**; remove the public setter. Rewrite the ~20
test assignment sites mechanically. `PostgresMessageTransport`'s re-stamp must keep working.

### Phase 4 — #105: circuit guard on the §16.2 ladder
Inject `IProviderCircuitBreaker`; an open circuit routes into the existing "unresolved, stay in
`Reconciling`, return `Wait`" branch. Use a **distinct circuit key suffix for lookups**, so "cannot
create" does not suppress the reads that would resolve it.

### Phase 5 — #71a: fail closed on the pepper
`IsProduction()` becomes "not literally Development ⇒ treat as production". Staging stops using the
committed pepper.

### Phase 6 — #73a: refuse insecure defaults at startup
Extend the existing `ValidateOnStart` pattern to the DB connection string, webhook secret and signing
keys: outside Development, a known-insecure committed default fails startup. **Never log the value.**

### Phase 7 — #106a: the stale spec-07 clause
One clause at `docs/specs/07-capacity-and-holds.md:365`.

### Phase 8 — #83: notification delivery state
Repository list-by-operation, MediatR query + DTO, `GET` route, drawer rendering. **Regenerates
`v1.json`** — the only phase that legitimately does.

### Phase 9 — #71b: real codes + attempt counter *(migration)*
CSPRNG code; a Development/Test-only retrieval path gated exactly like `AllowHeaderShim`; attempt
counter + lockout on `verification_challenges`. `VerificationEndpointTests` is rewritten to read the
generated code rather than assume `"000000"`.

### Phase 10 — #106b: capacity-primitive table *(migration)*
Per-`(tenant, provider)` table modelled on `TenantDepositPolicy`; resolver becomes scoped.
`CapacityPrimitiveResolverTests` is **rewritten** — it currently asserts the gap.

### Phase 11 — #75: per-stub config flags + loud warning (D1)
Each remaining stub behind its own flag, defaulting to stub-on. Outside Development, a single loud
startup warning enumerating every active stub.

### Phase 12 — #74: webhook HMAC + replay window (D2)
`X-Signature` over the raw body with buffering enabled, plus a timestamp tolerance. T1–T7 rewritten.

### Phase 13 — #102: outbox lease renewal
`RenewLeaseAsync` (no migration — `lease_expires_at` exists), renewed around the transport call.

### Phase 14 — #108: advisory lock (D3)
`pg_advisory_xact_lock` over the dispatch fence and the cancel CAS.

---

## Blast radius

| Artefact | Phase | Intentional or byte-identical |
|---|---|---|
| `OperatorActionBehavior` | 1 | **Intentional** — +2 ctor deps |
| `ActorBinder` | none | **Byte-identical** — precedence is already correct |
| `StaffRole`, the GD-1 matrix | none | **Byte-identical** |
| Customers/Payouts/Reporting/Deposits queries | 2 | **Intentional**, additive interface |
| `TenantContext` / `ActorContext` | 3 | **Intentional**; `ITenantContext` unchanged |
| `PostgresMessageTransport` | 3 | Must keep re-stamping successfully |
| `ReconcileOperationCommandHandler` | 4 | **Intentional**, +1 ctor dep |
| `VerificationCode`, challenge repo | 5, 9 | **Intentional** |
| `NotificationWebhooksController` | 12 | **Intentional**, wire-format change |
| `libs/api-contract/openapi/v1.json` | **8 only** | Byte-identical in every other phase |
| Pydantic contracts | none | **Untouched** |

## What we're NOT doing

- **#84** — blocked on an unowned spec-12 table and a single-node object store.
- **No ASP.NET auth stack, no `[Authorize]` rollout** — the dual-plane controllers make per-action
  gating the only correct shape, and it already exists.
- **No operator provisioning endpoint** — not needed, because this wave does not make login
  mandatory. Filed separately.
- **No real adapters for the five stub ports** (D1) — visibility, not replacement.
- **No secret deletion** (#73) — validation instead, because deletion breaks the host and the
  cross-language coupling.
- **No enum labels**, no changes to referenced contracts.

## Package & migration notes

**No new packages.** **Exactly two migrations**, in Phases 9 and 10, both additive, sequential on one
branch so no snapshot conflict. Spec 12 owns the DDL; neither adds an enum label (PostgreSQL cannot
drop one).

## Follow-ups this wave will file

1. Operator provisioning path — required before login can ever be made mandatory.
2. #84's two blockers: the artifact metadata table needs a spec-12 owner, and the object store needs
   a shared backend.
3. Real implementations for the five stub ports.
4. `MenuController`'s stale "open 08/14 decision" comment — the decision closed on 2026-07-11.

---

## Discoveries and notable information (filled during implementation)

### Phase 1
- The provenance gate had teeth immediately: `ManualRecoveryTests` dispatches operator commands
  straight through MediatR with no session and builds its own container, so it needed
  `Authorization:AllowAssertedStaffRole = true` explicitly. Six tests went red without it.

### Phase 2
- **The plan's mechanism was wrong.** `IOperatorActionRequest` mandates a non-empty `X-Reason-Code`,
  and the console's reads call `fetchApi` with no actor options — adopting it would 403 every
  customers/deposits/payouts/reporting screen. `OperatorGuard` (beside `TenantGuard`) checks only a
  proven `ActorId`, on the same `AllowAssertedStaffRole` switch.
- `GET /api/reporting/funnel` hand-rolled its own tenant check instead of `TenantGuard`; the sweep
  initially missed it for exactly that reason. Normalised.

### Phase 3
- `SetTenant` refuses a blank tenant as well as a different one. Three test fixtures
  (`PostgresFixture`, `AcceptanceApiFixture`, `TenantResolutionApiFixture`) legitimately build an
  *unscoped* context with tenant `""` to read tenant-not-resolved audits; they now use the unstamped
  constructor for that case.
- The DI registration became a factory (`_ => new TenantContext()`) so the new `(string)` constructor
  can never be considered by activator selection.
- `ActorContext` was left byte-identical: #80 concerns the tenant, and the actor is only bound by
  the verification middleware.
- The replay collision is covered by a unit test on `PostgresMessageTransport`, because
  `ReplayDeadLetterTests` substitutes the transport.

### Phase 4
- An open circuit is modelled as a synthetic `Transient` result, so it flows through the existing
  unresolved branches with no new path. That is better than the plan's "route to Wait": budget
  exhaustion still escalates, so a long outage reaches a human instead of parking forever.
- Lookups trip their own key, `(provider, "default:lookup")`. The ladder only continues past
  `NotFound`, so the final result decides health: `Created`/`AlreadyExists`/`NotFound` record a
  success, anything else a failure.

### Phase 5
- "Not literally Development ⇒ production" could not be done inside the static `VerificationCode`:
  it read process environment variables, but the integration host sets its environment through
  `UseEnvironment("Test")`, which never reaches them — and an unset variable means Production, so
  every unit and integration run would have thrown. The pepper became bound options
  (`Verification:CodePepper`, legacy `CORDIA_VERIFICATION_CODE_PEPPER` still honoured) with
  `Verification:AllowLocalPepper` defaulted in `Program.cs` exactly like `AllowAssertedStaffRole`,
  and validated **at startup** rather than on the first verification request.
- `tools/scripts/export_openapi.mjs` spawns the API as Development, so the contract hook is unaffected.

### Phase 6
- Implemented as a detector (`InsecureCommittedDefaults.FindIn`) called from `Program.cs` right after
  `Build()`, rather than options validation: the committed values live across four unrelated
  sections (connection string, signing keys, two webhook-secret keys, object-store key), and the
  check must run before migrations or any token is minted.
- The database password is **parsed** with `NpgsqlConnectionStringBuilder`, not substring-matched —
  mutation M3 proved a substring check would refuse a rotated password that merely contains the word.
- The test host opts in with `Security:AllowInsecureCommittedDefaults`; the integration suite's own
  connection string carries the committed password, so without it every host would refuse.
- `apps/langgraph/.env.example` and every committed value are byte-identical.

### Phase 8
- Notifications carry an operation id, never a booking id, so the booking read joins through
  `booking_operations.booking_id` (set on the create at Booked, and on every cancel/modify at
  submission). Two routes: `GET /api/Bookings/{id}/notifications` and
  `GET /api/booking-operations/{id}/notifications` — the drawer picks by `isBooking`.
- Both routes sit behind `TenantGuard` + `OperatorGuard` (added to `OperatorReadGuardTests`). The
  view omits `recipient_ref`, provider ref and idempotency key.
- The console's "operator cannot post a receipt" criterion is proven, not assumed: a request with
  full asserted operator headers and no webhook secret is 401 and leaves the row untouched.
- Mutation M1 (booking join ignoring the booking id) only went red after the booking test gained a
  same-tenant notification on an unrelated booking — the first draft could not see it.
- Local Bun is 1.3.14 and the web suite guards on ≥1.4.0 (CI uses latest), so web tests were run with
  `npx bun@latest test --parallel`: 725 pass.

### Phase 13 *(committed before 9 and 10 — no dependency on either)*
- The research note was right that renewing *between* rows cannot help the row inside the opaque
  transport call — but that row does not need it: `LeaseSeconds` is already sized for one slow row.
  The rows at risk are the ones **waiting**. So before each transport call the lane renews the
  current row and every row still queued behind it; the budget becomes per-row.
- The current row is renewed on its own so a zero is unambiguous: another publisher reclaimed it,
  and this worker skips it rather than publishing twice. `lease_owner = me` in the update is what
  stops renewal stealing a reclaimed row back (mutation M3).
- The first probe checked the lease at publish *start*, which cannot fail — the current-row renewal
  happens a line earlier. The probe now runs at the end of each simulated publish across every
  unpublished row, and only then did removing waiting-row renewal go red.

### Phase 9
- The fixed code had exactly three consumers: its definition, the request handler, and one
  integration test. Nothing in `apps/web`, `apps/langgraph`, docs or scripts depended on it; the
  agent's `verify_identity` node only polls for a grant and never submits a code.
- The "Development/Test-only retrieval path" is a `development_code` field on the hand-written
  `RequestVerificationResponse` wrapper (omitted when null), behind `Verification:ExposeCodeForDevelopment`
  defaulted like the other gates. The generated Pydantic `VerificationSentResult` is untouched, so
  no cross-language regen. `v1.json` gained four lines — a second legitimate contract change, forced
  by the absence of any delivery channel. The console's `VerificationGate` shows it when present.
- Lockout returns the existing `failed` status: no enum label added (spec 08 / Pydantic own it).
  `RecordFailedAttemptAsync` is bounded in SQL (`failed_attempts < max`) and `MarkVerifiedAsync`
  re-asserts the same bound, so parallel guesses cannot overshoot and a lock beats an in-flight
  right code. The digest comparison became `CryptographicOperations.FixedTimeEquals`.
- Migration `VerificationAttemptLockout`: one additive `failed_attempts integer not null default 0`.
  EF wrote the snapshot with CRLF on Windows; normalised to LF so the diff is the three real lines.
- The old test's "bad" code `111111` would collide with a random code one run in a million; the
  rewrite derives a code guaranteed to differ.

### Phase 10
- `Resolve` was synchronous, so a table lookup changed the port to `ResolveAsync`; one production
  caller (`CheckCandidateAvailabilityCommandHandler`) and one mock needed updating.
- The rung is stored as **text with a check constraint**, not a pg-enum — spec 12 forbids inventing
  enum labels, and a `CapacityPrimitive` enum has no spec-12 owner.
- **The only production caller resolves for the first-party provider**, and first-party inventory
  *is* the conditional `capacity_slots` update — it has no other rung. A row saying otherwise would
  make a first-party hold report `confirmed` (Inv-16). So the resolver pins `first_party` to the
  first-party rung without consulting the table, and a second check constraint refuses such a row.
  The table therefore changes behaviour only once an external provider is wired; that is the
  honest extent of #106 today, and the spec-07 §14 bullet now says so.
- No write API: choosing a rung is a statement about what a provider integration can do, made at
  onboarding, not an operator setting.

### Phase 11
- Confirmed the research count: six ports still stubbed. `IDepositPolicy` is real
  (`TenantDepositPolicyProvider`) and not listed; the extractor is listed under its existing
  `LangGraph:UseStubExtractor` flag, which defaults off because it has a real client.
- Per-stub flags `Stubs:<Port>` default on. A flag set **false** for a port with no real
  implementation refuses startup naming the key — it is a request this build cannot satisfy, and
  silently keeping the stub would leave the operator believing it was off. That is not the
  "refuse-to-boot" D1 ruled out: the default configuration always starts.
- One startup line names every active stub with its consequence (warning outside Development).
- Drift guard: `TheCatalogue_MatchesWhatTheContainerRegisters` inspects `AddInfrastructure`'s actual
  descriptors, so a real adapter replacing a stub fails the test until the catalogue is updated.

### Phase 12
- Wire format: `X-Webhook-Timestamp` (Unix seconds) + `X-Webhook-Signature: sha256=<hex>` over
  `"{timestamp}.{tenant_id}."` + raw body, ±5 min window. The **tenant is signed** because it arrives in
  a header — without it a captured receipt could be replayed against another tenant (mutation M3).
- The raw-body trap is solved with an `IAsyncResourceFilter` (`[VerifyWebhookSignature]`): it runs
  before model binding, buffers (64 KiB cap → 413), verifies, rewinds. `[FromBody]` and the documented
  request schema are unchanged; `v1.json` only gains the 413 response.
- The plain `X-Webhook-Secret` path is removed, not kept as a fallback; a test pins that the right
  secret in that header now authenticates nothing. T1–T7 rewritten to sign; T8/T9 keep their subject
  (which secret source keys the signature, and precedence).

### Phase 14
- The two decision points are the dispatch fence (`WorkflowAttemptRepository.TryMarkDispatchingAsync`)
  and Finalize's **cancel-before-create** transaction (FM#13) — not Compensate: the fence requires
  `Finalizing`, so it can never race a `CompensationRequired` operation.
- `pg_advisory_xact_lock(108051, hashtext(tenant:operation))` — the two-int form, so it cannot meet the
  test suite's single-bigint schema lock. `IOperationDecisionLock` throws outside a transaction,
  because an xact lock taken in an implicit statement transaction serialises nothing.
- The fence was a bare `ExecuteUpdateAsync`; it now opens its own transaction when none is ambient.
  The FM#13 lock is taken **before** the in-transaction attempt re-read — a unit test pins the order.
- The race test drives the exact interleaving against real Postgres: cancel worker locks and re-reads,
  the fence must still be waiting a second later, the CAS commits, and the fence then returns 0.
- Mutation M3 (lock keyed by tenant only) first **hung** the run instead of failing it: the paired
  negative asserted while still holding the lock, deadlocking teardown. The test now releases before
  asserting.
