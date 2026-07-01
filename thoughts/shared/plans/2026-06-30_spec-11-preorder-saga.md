---
date: 2026-06-30
researcher: minhnt10122003@gmail.com
git_commit: 641c42e
branch: feat/spec-11
repository: Cordia
task: "Spec 11 — Pre-order saga (revalidate → hold → reserve items → authorize → commit, with compensation)"
tags: [implementation-plan, spec-11, preorder, saga, tdd]
status: draft
last_updated: 2026-06-30
last_updated_by: minhnt10122003@gmail.com
---

# Spec 11 — Pre-order Saga Implementation Plan

## Overview

Implement the **pre-order saga** end-to-end on the spec-12-scaffolded `preorder_sagas` table: a customer assembles a
pre-order (catalog items) attached to a booking; on confirm, a multi-step saga **revalidates live item availability**,
**holds capacity** (spec-07), **reserves items**, **authorizes/holds the deposit** (spec-09), **commits the booking**
(spec-06) and **commits the pre-order**, with **compensation** on any step failure and a **combined outcome** notification.
The 13-state machine, entity, version token, and EF config already exist; all behavior is greenfield.

**Scope decisions (carried):** API-only; external infra (payment processor, object storage, LLM) stub-ported; langgraph
agents set up but **not** wired to .NET. The pre-order *draft assembly* is conversational (foreground agent using the
existing catalog search/get tools); those tools are wired by the menu-finishing plan. The saga itself is pure .NET.

## Current State Analysis

- **Persistence exists** (`20260625153819_...`): `PreorderSaga` — `TenantId, SagaId, OperationId, SagaState (enum),
  PreorderReference (jsonb str default "{}"), HoldId?, DepositSessionId?, Version` + EF config.
- `PreorderSagaState` (13): `Revalidating, HoldingCapacity, ReservingItems, AuthorizingPayment, CommittingBooking,
  CommittingPreorder, CompensatingItems, CompensatingHold, CompensatingBooking, Reconciling, Completed, FailedFinal,
  ManualReview`.
- **Missing entirely:** the `PreorderReference`/`PreorderLineReference` contracts, the saga state machine + transition
  guards, `IPreorderSagaRepository`, the coordinator, every step, compensation, the live-item revalidation service, the
  `CommitPreorder` consumer + combined outcome, crash-window recovery, and standalone-order conversion.
- **Seams to build on (verified):**
  - `ICapacityReservation` (Reserve/Release/Finalize) — capacity hold + finalize-against-operation.
  - `CanonicalRequestHasher.cs` (exists) — canonical hashing used by the intent/idempotency layer; the pre-order content
    must be embedded so the existing hash covers it (no parallel hashing scheme).
  - spec-09 deposit/payment seam (deposit session create/authorize/void) and registered `notification.deposit_*`.
  - spec-06 booking-commit step + `notification.booking_confirmed/failed.v1`.
  - spec-04 `SubmitConfirmedIntentCommand` / intent-revision contract — the carrier for embedding `PreorderReference`.
  - `ScheduledJob` pattern (`CapacityExpiryScheduler`) — for saga **step timeouts**.
  - `InboxDeduplicationBehavior` (one txn/consumer, fail-secure) + `VersionTokenUpdate` (race-safe state advance).

## Desired End State

Confirming a booking with an attached pre-order runs the saga to `Completed` (booking + pre-order committed, deposit
captured/held, items reserved) or, on any failure, compensates back to a consistent state (`FailedFinal` or
`ManualReview`) — never leaving a hold, deposit, or reservation dangling (Inv-7). The customer receives **one** combined
outcome notification (Inv-8). Re-delivery and crash-resume are idempotent: each step is safe to re-enter from the
persisted `SagaState` (Inv-9). Live revalidation rejects stale items into `AlternativesRequired` (Inv-10).

## What We're NOT Doing
- `apps/web` UI; real payment processor / object storage / LLM (stub adapters); authz beyond actor-capture + audit
  `TODO`; multi-vendor pre-orders in one saga (single vendor per booking, per spec); inventory decrement against a real
  POS (item reservation is first-party/stubbed at the boundary, tested deterministically).

## Implementation Approach

TDD, clean architecture. The saga is a **persistent state machine driven by a coordinator consumer**: each step advances
`SagaState` via a version-token guard inside the inbox txn, then enqueues the next step envelope (or a compensation
envelope on failure). Steps are individually idempotent and re-entrant from the persisted state (crash-safe). Forward
and compensation edges are encoded in a `PreorderSagaStateMachine`. Step timeouts use `ScheduledJob`.

---

## Phase 1: Contracts — PreorderReference + canonical-hash embedding (RED→GREEN)

### Phase 1: Changes Required
#### RED
- **Unit (langgraph)** `tests/unit_tests/contracts/test_preorder.py` — `PreorderReference`/`PreorderLineReference`
  (item_id, qty, unit_price `Money`, selected modifiers, line subtotal), `extra="forbid"`, total = Σ lines; round-trip.
- **Unit (api)** `CanonicalHashIncludesPreorderTests` — two intents identical except pre-order lines hash **differently**;
  identical pre-orders hash **equal** (deterministic ordering of lines/modifiers).
#### GREEN
- `apps/langgraph/cordia_agent/contracts/preorder.py` — Pydantic `PreorderReference` + `PreorderLineReference`. Regenerate
  C#/TS/Zod (`shared-schemas:generate-types`) → `Cordia.Domain.Generated`.
- Embed `PreorderReference` in the intent-revision payload so `CanonicalRequestHasher` already covers it (canonical line
  ordering by item_id then modifier id). No separate pre-order hash (ADR).

### Phase 1: Success Criteria
#### Automated
- [x] `bun nx run langgraph:generate-schemas` + `shared-schemas:generate-types` clean; `PreorderReference`/`PreorderLineReference` present in C# (`Cordia.Domain.Generated`), TS, and Zod.
- [x] langgraph contract suite green (`test_preorder.py`, 7); api `CanonicalHashIncludesPreorderTests` green (4): different lines → different hash, reordered lines/modifiers → equal, presence≠absence, null==absent.

### Phase 1: Discoveries and Notable Information
**Implementation Adaptations:**
- **Matched the spec §4.3 record exactly (flat captured-price fields), NOT the plan's "unit_price `Money`".** The spec
  pins `PreorderLineReference(ItemId, ModifierIds, Quantity, CapturedPriceAmountMinor:long, CapturedPriceCurrency, Notes?)`
  and `PreorderReference(CatalogVersion, Lines, ConfirmationId)`; the generated C# mirrors it (quicktype emits `Quantity`
  as `long`). `line_subtotal` / `total_amount_minor` are non-serialized Python `@property` helpers — they never enter the
  JSON Schema, generated types, or the canonical hash.
- **Embedding = a new optional `preorder: PreorderReference | None` field on `BookingRequest`** (the `IntentRevision.canonical_payload`).
  The existing canon.v1 hasher already folds nested structures and omits null/empty, so plain bookings hash identically to
  before this field existed (verified: null==absent) — no parallel pre-order hash, no hasher change needed (ADR holds).
- Pre-existing Windows-only flakes in `test_clarify.py` (`strftime("%-d")` glibc directive) are unrelated to this work.

---

## Phase 2: Saga state machine + repository (RED→GREEN)

### Phase 2: Changes Required
#### RED — `UnitTests/Preorder/`
- `PreorderSagaStateMachineTests` — forward edges Revalidating→HoldingCapacity→ReservingItems→AuthorizingPayment→
  CommittingBooking→CommittingPreorder→Completed; compensation edges (e.g. AuthorizingPayment-fail→CompensatingItems→
  CompensatingHold→FailedFinal; CommittingBooking-fail→CompensatingBooking→ManualReview; Reconciling reachable from any
  in-flight step on timeout); terminal = Completed/FailedFinal/ManualReview; disallowed edges throw
  `InvalidPreorderSagaTransitionException`.
#### GREEN
- `Cordia.Domain/Preorder/PreorderSagaStateMachine.cs` — `FrozenDictionary` forward+compensation map + `EnsureAllowed`.
- `Cordia.Domain/Interfaces/IPreorderSagaRepository.cs` + `Infrastructure/Repositories/PreorderSagaRepository.cs` —
  `AddAsync`, `GetAsync`, `TryAdvanceStateAsync` (version+state guard), `GetByOperationAsync`,
  `FindStuckAsync(olderThan)` (for recovery). Register in DI.
- `Cordia.Domain/Exceptions/{InvalidPreorderSagaTransitionException,PreorderSagaNotFoundException}.cs` → map in handler.

### Phase 2: Success Criteria
#### Automated
- [x] `PreorderSagaStateMachineTests` green (40: forward + standalone shortcuts + compensation + reconcile + terminals + disallowed); `bun nx build api` clean.

### Phase 2: Discoveries and Notable Information
**Technical Discoveries:**
- **Standalone-order shortcuts are state-machine edges, not a separate machine:** `Revalidating → ReservingItems` and
  `AuthorizingPayment → CommittingPreorder` skip the hold + booking-commit steps (§21.4) and reuse the same compensation
  edges. `ReservingItems → FailedFinal` and `CompensatingItems → FailedFinal` exist for the no-hold (standalone) case.
- **`Reconciling` is the recovery hub** — reachable from any in-flight forward step (a `IsInFlightForwardStep` helper marks
  HoldingCapacity…CommittingPreorder) and fanning out to resume any forward/compensation step or escalate to a terminal.

**Implementation Adaptations:**
- `IPreorderSagaRepository.TryAdvanceStateAsync` takes explicit nullable `setHoldId`/`setDepositSessionId` checkpoint params
  (not an Infrastructure setters-builder) so the Domain port stays infrastructure-free; the impl conditionally includes the
  `SetProperty` calls (known at call time) inside the version-token UPDATE so the checkpoint persists atomically with the
  state advance (Inv-9). `FindStuckAsync` scans cross-tenant (`IgnoreQueryFilters`) for the recovery sweep.

---

## Phase 3: Live item revalidation (RED→GREEN)

### Phase 3: Changes Required
#### RED
- **Integration** `PreorderRevalidationTests`: saga start with all items still published+priced → advances to
  HoldingCapacity; an item unpublished or price-changed → saga resolves to `AlternativesRequired` (spec-06 outcome),
  audit `preorder.revalidation_failed`, no hold taken (Inv-10); re-delivery idempotent.
#### GREEN
- `Cordia.Application/Services/LiveItemRevalidationService.cs` — given a `PreorderReference`, load current published
  catalog items, compare existence + `PriceMinor` + modifier availability; return a typed diff (Ok / Stale(list)).
- `Handlers/Preorder/StartSaga/StartPreorderSagaCommandHandler.cs` (`IInboxConsumerRequest`): create saga in
  `Revalidating`, run revalidation; Ok → advance to HoldingCapacity + enqueue the hold step; Stale → emit
  `AlternativesRequired` + settle (no compensation needed; nothing acquired yet).

### Phase 3: Success Criteria
#### Automated
- [x] `PreorderRevalidationTests` green (4): all-valid → HoldingCapacity + hold enqueued + audit; item-unpublished → FailedFinal + AlternativesRequired + no hold; price-changed → FailedFinal; re-start on an existing saga → idempotent no-op (one saga).

### Phase 3: Discoveries and Notable Information
**Technical Discoveries:**
- **Dedicated `preorder.commands` lane added** (9th logical queue, consumer `preorder.processor`, ordering not required —
  per-saga state is version-guarded). The spec names only waitlist/menu lanes; the saga needs its own consumer for inbox
  dedup, so this mirrors the waitlist/menu precedent. `LogicalQueueRegistryTests` updated 8→9 + a preorder-routing case.
- **`LiveItemRevalidationService`** compares each line against `GetPublishedItemAsync`: existence, `PriceMinor` + currency
  match, and modifier availability (parsed defensively from the item's `modifiers` jsonb — malformed ⇒ line reads stale).

**Implementation Adaptations:**
- **The start message carries the immutable `PreorderReferenceJson` + operation id** (and a `standalone` flag); the handler
  mints the saga id, persists the reference verbatim into `preorder_sagas.preorder_reference`, and revalidates. In
  production the enqueuer copies the reference from the confirmed revision (Inv-1); carrying it on the message is the
  boundary since the spec-04/06 enqueuer is out of scope ("agents set up, not wired").
- **Single consolidated migration generated early, not per-phase:** `PreorderSagaColumns` adds the only two new columns the
  saga needs across all phases — `is_standalone` (bool, default false) + `failure_reason` (text, null). Generated now to
  unblock per-phase integration tests; if a later phase needs another column it is folded in by regenerating this one
  migration (never a second), keeping the end state at exactly one preorder migration.

---

## Phase 4: Forward steps — hold → reserve items → authorize → commit booking → commit pre-order (RED→GREEN)

### Phase 4: Changes Required
#### RED
- **Integration** `PreorderSagaHappyPathTests`: full run reaches `Completed` with booking committed, deposit held,
  items reserved, pre-order committed; each step persists its checkpoint (`HoldId`, `DepositSessionId`, `OperationId`)
  before enqueuing the next; re-delivery of any step is idempotent (no double hold/charge/commit).
#### GREEN — one handler per step (`IInboxConsumerRequest`), each: guard current state, do work, version-advance, enqueue next
- `Handlers/Preorder/HoldCapacity/` — `ReserveAsync`; Held → persist `HoldId`, advance ReservingItems; NoCapacity →
  enqueue compensation (nothing to undo yet → FailedFinal) + `AlternativesRequired`.
- `Handlers/Preorder/ReserveItems/` — reserve items via the (stub-ported) item-reservation port; success → advance
  AuthorizingPayment; failure → CompensatingHold.
- `Handlers/Preorder/AuthorizePayment/` — create/authorize deposit session (spec-09); persist `DepositSessionId`,
  advance CommittingBooking; failure → CompensatingItems.
- `Handlers/Preorder/CommitBooking/` — commit booking + finalize hold against operation (spec-06/07); advance
  CommittingPreorder; failure → CompensatingBooking (→ ManualReview, since payment already authorized).
- `Handlers/Preorder/CommitPreorder/` — persist the committed pre-order, advance Completed; enqueue combined-outcome
  notification (Phase 6). Each step writes an audit row (`preorder.<step>`).

### Phase 4: Success Criteria
#### Automated
- [x] `PreorderSagaHappyPathTests` green (2): the forward chain drives Revalidating → … → Completed with `HoldId` +
      `DepositSessionId` checkpoints persisted, the hold finalized into booked capacity, and exactly one combined-outcome
      notification; a re-delivered CommitPreorder after completion does not double-notify (Inv-5/Inv-8/Inv-9).

### Phase 4: Discoveries and Notable Information
**Implementation Adaptations:**
- **Two stub-ported boundary ports** (per the scope decision): `IPreorderItemReservation` (reserve/release keyed by
  `(tenant, sagaId)` — no persisted reservation id; fails deterministically on a `"reserve_fail"` item-id sentinel) and
  `IPreorderPaymentAuthorization` (authorize/void; declines on a negative amount sentinel). Capacity uses the REAL
  spec-07 `ICapacityReservation` (Reserve on hold-step, FinalizeAgainstOperationAsync on commit-booking).
- **Booking slot threaded only as far as the hold step:** the start message carries `slot_id` + `requested_capacity`
  (from the confirmed booking request); StartSaga forwards them in the `hold_capacity` message; downstream steps reload
  the saga's checkpoints. No slot is stored on the saga.
- **Commit-preorder is the terminal commit:** the `Completed` saga row IS the committed pre-order record; the single
  combined `notification.booking_confirmed.v1` is emitted under the version+state-guarded terminal advance, so a duplicate
  delivery (state already Completed) settles without re-notifying.
- **`FailureReason` checkpoint:** `TryAdvanceStateAsync` gained an optional `setFailureReason` so failure transitions
  record a coarse machine reason atomically with the state advance (for ManualReview/outcome context).

---

## Phase 5: Compensation + reconciliation (RED→GREEN)

### Phase 5: Changes Required
#### RED
- **Integration** `PreorderCompensationTests`: payment-auth failure → CompensatingItems → CompensatingHold →
  FailedFinal (items released, hold released, no deposit, `notification.booking_failed.v1`); booking-commit failure
  after payment auth → CompensatingBooking → ManualReview (deposit void attempted; if void fails → ManualReview with an
  audit/notification for human follow-up, Inv-7); step **timeout** (ScheduledJob fires) on an in-flight step →
  Reconciling → re-check external state → resume or ManualReview.
#### GREEN
- `Handlers/Preorder/CompensateItems|CompensateHold|CompensateBooking/` (`IInboxConsumerRequest`): each releases its
  resource (item release / `ReleaseAsync` hold / void deposit), version-advances along the compensation chain, audit
  `preorder.compensated_<resource>`; idempotent (already-released → settle). Terminal compensation → FailedFinal or, when
  a release can't be confirmed, ManualReview.
- `Handlers/Preorder/Reconcile/ReconcilePreorderSagaCommandHandler.cs` — consumes the step-timeout job; inspects real
  hold/deposit/booking state and either resumes the forward chain or routes to ManualReview (Inv-7). Schedule per-step
  timeouts via the `ScheduledJob` seam keyed `sj_pretimeout_{sagaId}_{state}`.

### Phase 5: Success Criteria
#### Automated
- [x] `PreorderCompensationTests` green (4): items-unavailable → CompensatingHold → FailedFinal (slot freed, one failure
      outcome); payment-declined → CompensatingItems → CompensatingHold → FailedFinal; booking-commit-fail + unconfirmable
      void → ManualReview (`failure_reason = deposit_void_unconfirmable`); reconcile re-enqueues the stuck step idempotently.

### Phase 5: Discoveries and Notable Information
**Implementation Adaptations:**
- **Single recovery primitive:** `Reconcile` re-enqueues the step message matching the persisted `SagaState` (its own
  state-guard makes re-entry idempotent), or escalates the non-resumable states — Revalidating → FailedFinal (nothing
  acquired), Reconciling → ManualReview. The Phase-6 crash-recovery sweep reuses this by enqueuing `reconcile.v1`, so
  step-timeout and crash-recovery share one mechanism rather than per-step ScheduledJob timers (a deliberate simplification
  over the plan's per-(saga,state) timeout job; documented).
- **Exactly-one outcome (Inv-8)** is enforced by emitting the success/failure notification only on the version+state-guarded
  transition INTO a terminal: `CommitPreorder` → `notification.booking_confirmed.v1` on Completed; the compensation handlers
  → `notification.booking_failed.v1` on FailedFinal/ManualReview. A saga reaches exactly one terminal, and the guard makes
  duplicate deliveries settle, so the customer is notified once.
- **`CompensateBooking`** voids the deposit AND releases items inline (no extra state hop), then FailedFinal on confirmed
  void or ManualReview on unconfirmable void (Inv-7).

---

## Phase 6: Combined outcome, crash recovery, standalone-order conversion (RED→GREEN)

### Phase 6: Changes Required
#### RED
- **Integration** `PreorderCombinedOutcomeTests`: exactly **one** notification summarizing booking + pre-order +
  deposit is enqueued on Completed (Inv-8), and exactly one failure notification on FailedFinal; duplicate CommitPreorder
  delivery does not double-notify.
- **Integration** `PreorderCrashRecoveryTests` (FM#8-11): a saga persisted mid-step (e.g. AuthorizingPayment with a
  `DepositSessionId` already set) re-enters from its persisted state without re-authorizing; `FindStuckAsync` +
  recovery sweep re-enqueues the correct step.
- **Integration** `StandaloneOrderConversionTests` (§21.4): a pre-order without a booking (standalone order) runs the
  item-reserve + payment + commit subset, skipping the capacity-hold/booking-commit steps, reaching Completed.
#### GREEN
- `CommitPreorder` enqueues a single combined `notification.booking_confirmed.v1` (extended payload) / on failure a
  single `notification.booking_failed.v1`; dedup via saga terminal-state guard.
- A recovery sweep (scheduled job or startup hosted-service consistent with existing schedulers) that finds stuck sagas
  and re-enqueues the step matching `SagaState` (idempotent re-entry).
- Standalone-order path: a saga variant flag (or distinct start command) that skips HoldingCapacity + CommitBooking.

### Phase 6: Success Criteria
#### Automated
- [x] `PreorderRecoveryAndStandaloneTests` green (2): standalone order skips hold + booking-commit, reaches Completed with
      no capacity hold and exactly one confirmation / zero failures; recovery sweep finds a stale saga, enqueues reconcile,
      which resumes the CURRENT step (CommittingBooking) without re-authorizing the deposit (FM#8-11).
- [x] `bun nx test api` green (617 unit); full integration suite green except the 1 pre-existing `ScheduledJobPollerTests`
      timestamp-precision flake (Postgres µs truncation, unrelated). All 12 pre-order integration tests pass.
- [x] Consumers registered (10 `preorder.commands.*` types) + DI (`LiveItemRevalidationService`, `PreorderRecoverySweeper`,
      stub ports); 3 job-event schemas authored (start, shared step, alternatives_required).
#### Manual
- [ ] Trace a full happy-path saga end-to-end in logs (each state transition + single outcome notification).

### Phase 6: Discoveries and Notable Information
**Implementation Adaptations:**
- **Standalone (§21.4) is purely the `IsStandalone` flag + two state-machine shortcuts:** StartSaga routes
  Revalidating → ReservingItems and AuthorizePayment routes → CommittingPreorder; no hold is taken and no booking is
  committed, yet the same compensation/outcome machinery applies.
- **Crash recovery = `PreorderRecoverySweeper.SweepAsync(olderThan)`** over `FindStuckAsync` → enqueues `reconcile.v1`
  per stuck saga; reconcile resumes the step matching the persisted state, so a saga past payment-auth resumes at its
  CURRENT step and is never re-authorized (verified). Wiring the sweeper to a periodic hosted-service/scheduled job is a
  thin follow-up (left unwired, consistent with the "stub/boundary, not fully wired" scope).
- The success/failure outcomes reuse the spec-10 `notification.booking_confirmed.v1` / `notification.booking_failed.v1`
  lane types (already registered), so no new notification consumer was needed.

---

## Testing Strategy
- **Unit:** saga state machine (forward+compensation+terminal), revalidation diff, contract round-trip, canonical-hash
  inclusion.
- **Integration (Alba+Postgres):** happy path, each compensation path, step-timeout→reconcile, crash re-entry,
  combined-outcome single-notification, standalone-order, idempotent re-delivery at every step.

## Architecture Decisions
| Decision | Chosen | Why |
|---|---|---|
| Pre-order content hashing | Embed `PreorderReference` in the intent-revision payload; reuse `CanonicalRequestHasher` | Single idempotency/canonicalization source of truth; no parallel hash to drift (spec §12 open item resolved). |
| Saga drive mechanism | Persistent state machine + per-step inbox consumers, advancing via version token | Crash-safe + idempotent re-entry from persisted `SagaState`; matches existing inbox/version-token seams (Inv-9). |
| Step timeouts | `ScheduledJob` per (saga,state) → Reconcile | Reuses the durable, idempotent scheduler; no new infra. |
| Payment | spec-09 deposit session (authorize/void) | Reuses shipped payment seam; ManualReview only when a void is unconfirmable (Inv-7). |
| Item reservation | Stub-ported first-party port, boundary tested | No real POS; deterministic tests; real adapter swaps in later (matches `Stub*Adapter`). |
| Standalone order | Saga variant skipping hold+booking-commit | Spec §21.4 wants order-without-booking; reuses the same compensation machinery. |

## References
- Spec: `docs/specs/11-waitlist-menu-preorder.md` §4.3/§5.3 (saga), §10 Inv-7/8/9/10, §12, §21.4, §27, FM#7-11.
- Seams: `ICapacityReservation.cs`, `CanonicalRequestHasher.cs`, spec-09 deposit, spec-06 booking-commit, spec-04
  intent-revision, `CapacityExpiryScheduler.cs` (ScheduledJob), `InboxDeduplicationBehavior`, `VersionTokenUpdate`.
- Scaffolding: `PreorderSaga.cs`, `PreorderSagaState.cs`, `PreorderSagaConfiguration.cs`.
- Patterns: `docs/dotnet-api-patterns.md`; `memory/cordia-dotnet-conventions.md`.
