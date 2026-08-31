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
