---
date: 2026-06-30
researcher: minhnt10122003@gmail.com
git_commit: 641c42e
branch: feat/spec-11
repository: Cordia
task: "Spec 11 — Waitlist enrollment, matching, offer lifecycle, auto-book"
tags: [implementation-plan, spec-11, waitlist, saga, tdd]
status: draft
last_updated: 2026-06-30
last_updated_by: minhnt10122003@gmail.com
---

# Spec 11 — Waitlist Implementation Plan

## Overview

Implement the **waitlist** feature end-to-end on the persistence that spec-12 already scaffolded: a customer enrolls
with constraints (party size, acceptable window, location/service); when matching capacity frees up, the system places
a **temporary hold**, makes a **time-boxed offer**, and on accept mints a confirmed booking intent (spec-04), on
decline/expiry **releases the hold and re-matches** the next eligible enrollee. `auto_book` short-circuits the offer and
books directly. All behavior is greenfield — only the tables/entities/version-tokens exist today.

**Scope decisions (carried):** API-only (no `apps/web`); external infra stub-ported; agents (if any) set up in langgraph
but **not** wired to .NET. Waitlist matching is deterministic backend logic — no LLM is required by spec §3, so no new
langgraph agent is needed here; the only AI seam is the existing foreground assistant offering "join the waitlist?"
(spec-06 `AlternativesRequired` → waitlisted), which is a .NET capture path, not an agent.

## Current State Analysis

- **Persistence exists** (`20260625153819_CatalogsPaymentsWaitlistChannels`):
  - `WaitlistEnrollment` — `TenantId, EnrollmentId, CustomerRef, AcceptableFrom/To, PartySize, LocationId, ServiceId,
    OfferLifetimeSeconds, DepositTerms (jsonb str), NotificationChannel, AutoBook (jsonb str), Version` + EF config.
  - `WaitlistOffer` — `TenantId, OfferId, EnrollmentId, HoldId?, OperationId?, Status, OfferExpiresAt?, MatchedAt?,
    OfferedAt?, ResolvedAt?, Version` + EF config + `WaitlistOfferRepository` (AddAsync + version-token TryTransition).
  - `WaitlistOfferStatus` — `Pending Matched Offered Accepted Declined Expired Released` (7).
- **Missing entirely:** state machine, all handlers, enrollment repository, controller, command envelopes,
  matching trigger, offer-expiry scheduled-job consumer, notifications, the `AlternativesRequired→waitlist` capture.
- `LogicalQueueRegistry` already registers `WaitlistCommands` (logical queue exists for the envelopes).
- **Seams to build on (verified):**
  - `ICapacityReservation.ReserveAsync(ReserveRequest)→ReservationOutcome` (Held/NoCapacity/Ambiguous),
    `ReleaseAsync(tenantId, holdId, ReleaseReason)`, `FinalizeAgainstOperationAsync(...)`. Runs in caller's ambient txn.
  - `CapacityExpiryScheduler.ScheduleAsync(...)` → inserts a `ScheduledJob` (deterministic JobId, idempotent enqueue) —
    the pattern to reuse for **offer-expiry**.
  - `SubmitConfirmedIntentCommand` (spec-04) — mints a confirmed booking operation; the **accept** path delegates here.
  - Notification outbox + registered `notification.waitlisted.v1` / `notification.*.v1` consumers.
  - `InboxDeduplicationBehavior` (one txn per consumer; never throw on expected failure — settle/fail-secure).
  - `VersionTokenUpdate.UpdateAsync` guard (status+version) for race-safe offer transitions.

## Desired End State

A customer can enroll; a `waitlist.match` trigger finds eligible enrollees FIFO-by-`MatchedAt` honoring constraints,
holds capacity, and offers; the offer notifies the customer and auto-expires; accept → confirmed booking (04) +
hold finalized; decline/expiry → hold released + next enrollee matched; `auto_book` books without an offer; all
transitions are race-safe (version-token) and idempotent (inbox dedup); enrollment cancel stops further offers.

### Invariants honored (spec §10)
- Inv-1 one live offer per enrollment at a time; Inv-2 a held slot is never double-promised; Inv-3 expiry always
  releases the hold; Inv-5 accept is idempotent (re-delivery doesn't double-book); Inv-6 cancel is terminal.

## What We're NOT Doing
- `apps/web` UI; real notification transport (stub adapter stays); authz beyond actor-capture + audit `TODO`
  (blocked on specs 08/14); cross-tenant/global waitlist; priority tiers beyond FIFO-by-match-time (spec leaves
  prioritization open — §12; default FIFO, documented as ADR).

## Implementation Approach

TDD, clean architecture, one request+handler+validator per file under `Handlers/Waitlist/<Action>/`. HTTP commands
open their own `IUnitOfWork.ExecuteInTransactionAsync`; queue consumers (`IInboxConsumerRequest`) rely on the inbox
behavior's single txn and never throw on expected outcomes. Every state change goes through the offer state machine +
version-token guard, and writes an audit row.

---

## Phase 1: Domain core — value objects, state machine, repositories (RED→GREEN)

### Phase 1: Changes Required
#### RED — `UnitTests/Waitlist/`
- `WaitlistOfferStateMachineTests` — allowed: Pending→Matched→Offered→{Accepted,Declined,Expired}; Matched→Released;
  Offered→Released; terminal = Accepted/Declined/Expired/Released; every disallowed edge throws
  `InvalidWaitlistOfferTransitionException`.
- `AutoBookGrantTests` / `DepositTermsTests` — parse/validate the `auto_book` and `deposit_terms` jsonb payloads
  (typed records, not raw strings).
#### GREEN
- `Cordia.Domain/Waitlist/WaitlistOfferStateMachine.cs` — `FrozenDictionary<WaitlistOfferStatus,FrozenSet<...>>` +
  `IsAllowed`/`EnsureAllowed` (mirror existing state machines).
- `Cordia.Domain/Waitlist/{AutoBookGrant,DepositTerms}.cs` — typed projections of the jsonb columns; a
  `WaitlistEnrollmentExtensions` to deserialize on read.
- `Cordia.Domain/Exceptions/{InvalidWaitlistOfferTransitionException,WaitlistEnrollmentNotFoundException}.cs` → map in
  `GlobalExceptionHandler` (409/404).
- `Cordia.Domain/Interfaces/IWaitlistEnrollmentRepository.cs` + `Infrastructure/Repositories/WaitlistEnrollmentRepository.cs`
  — `AddAsync`, `GetAsync`, `CancelAsync` (version-guarded), `FindEligibleAsync(matchCriteria)` (tenant-scoped, ordered
  by `MatchedAt`/`CreatedAt`, filters party-size ≤ capacity, window overlap, location/service match, excludes enrollments
  with a live offer). Register in `InfrastructureServiceCollectionExtensions`.

### Phase 1: Success Criteria
#### Automated
- [ ] `bun nx test api` — new state-machine + value-object unit tests green.
- [ ] `bun nx build api` clean; repo registered.

### Phase 1: Discoveries and Notable Information
[filled on execution]

---

## Phase 2: Enrollment API + command envelopes (RED→GREEN)

### Phase 2: Changes Required
#### RED
- **Integration** `WaitlistEnrollmentTests` (Alba): `POST /api/waitlist/enrollments` → 201 + persisted (tenant-scoped),
  validation 400 (party-size>0, From<To, channel known); `DELETE .../{id}` → 200 cancel (terminal) / 404 / idempotent
  re-cancel; tenant isolation.
- **Unit** validators for `EnrollWaitlistCommand` / `CancelWaitlistEnrollmentCommand`.
#### GREEN
- `Handlers/Waitlist/Enroll/` + `Handlers/Waitlist/Cancel/` (command+handler+validator). Enroll persists enrollment,
  writes audit `waitlist.enrolled`, and **enqueues a `waitlist.commands.match.v1`** envelope (so a fresh enrollment is
  considered immediately if capacity already exists). Cancel guards Version, sets a terminal flag / cancels any live
  offer + releases its hold, audit `waitlist.cancelled`.
- `Cordia.Api/Controllers/WaitlistController.cs` — `[Route("api/waitlist")]`, thin, tenant-guarded.
- Hand-author envelopes under `schemas/job-events/`: `waitlist.commands.match.v1`, `waitlist.commands.offer_expiry.v1`
  (no secrets — Inv-12); register consumer routes in the messaging DI alongside existing `WaitlistCommands` queue.

### Phase 2: Success Criteria
#### Automated
- [ ] `bun nx test-integration api` — enrollment lifecycle green; tenant isolation + validation.
- [ ] envelope schemas validate; consumers resolve via the registry.

### Phase 2: Discoveries and Notable Information
[filled on execution]

---

## Phase 3: Match → hold → offer (core consumer) (RED→GREEN)

### Phase 3: Changes Required
#### RED
- **Integration** `WaitlistMatchTests`: capacity available + eligible enrollee → `ReserveAsync` Held, offer
  Pending→Matched→Offered, `OfferExpiresAt` set, offer-expiry `ScheduledJob` enqueued, `notification.waitlisted.v1`
  (offer) outbox row — all in ONE inbox txn; **no eligible enrollee** → settle no-op; **NoCapacity** → settle no-op
  (no offer); **two concurrent matches for one slot** → exactly one offer wins (Inv-2), the loser settles.
#### GREEN
- `Handlers/Waitlist/Match/MatchWaitlistOfferCommandHandler.cs` (`IInboxConsumerRequest`): pick the next eligible
  enrollment (`FindEligibleAsync`), `ReserveAsync` a temp hold; on Held → create/advance offer to `Offered`
  (version-guarded), set `OfferExpiresAt = now + OfferLifetimeSeconds`, `CapacityExpiryScheduler`-style schedule of a
  `waitlist.commands.offer_expiry.v1` job keyed `sj_offerexp_{offerId}`, enqueue offer notification, audit
  `waitlist.offered`. On NoCapacity/Ambiguous or no enrollee → settle (no throw). If `auto_book` → skip offer, go to
  Phase 5 path. Never opens its own txn.

### Phase 3: Success Criteria
#### Automated
- [ ] `WaitlistMatchTests` green incl. concurrency (single-winner) + idempotent re-delivery (dedup) + fail-secure no-op.

### Phase 3: Discoveries and Notable Information
[filled on execution]

---

## Phase 4: Accept / Decline / Expire (RED→GREEN)

### Phase 4: Changes Required
#### RED
- **Integration** `WaitlistOfferResolutionTests`: accept (HTTP) → offer Offered→Accepted, hold finalized via
  `FinalizeAgainstOperationAsync`, confirmed intent minted (`SubmitConfirmedIntentCommand`), audit `waitlist.accepted`;
  **re-accept / re-deliver → idempotent** (Inv-5, no double-book); decline → Offered→Declined, hold released, a fresh
  `waitlist.commands.match.v1` enqueued (re-match); **offer-expiry job** fires → Offered→Expired, hold released,
  re-match enqueued (Inv-3); accept **after** expiry → 409 (offer no longer Offered).
#### GREEN
- `Handlers/Waitlist/Accept/` (HTTP, own txn): version-guard Offered→Accepted, finalize hold, delegate to
  `SubmitConfirmedIntentCommand`, persist `OperationId`, audit. Idempotency via the offer status guard (already-Accepted
  → return the same result).
- `Handlers/Waitlist/Decline/` (HTTP, own txn): Offered→Declined, `ReleaseAsync(hold, ReleaseReason.WaitlistDeclined)`,
  enqueue re-match, audit.
- `Handlers/Waitlist/ExpireOffer/ExpireWaitlistOfferCommandHandler.cs` (`IInboxConsumerRequest`, consumes
  `waitlist.commands.offer_expiry.v1`): if still Offered → Offered→Expired, release hold, enqueue re-match, audit
  `waitlist.offer_expired`; if already resolved → settle no-op. Add `ReleaseReason` values as needed.

### Phase 4: Success Criteria
#### Automated
- [ ] `WaitlistOfferResolutionTests` green: accept idempotent, decline/expire release+re-match, post-expiry accept 409.

### Phase 4: Discoveries and Notable Information
[filled on execution]

---

## Phase 5: Auto-book + notifications + AlternativesRequired capture (RED→GREEN)

### Phase 5: Changes Required
#### RED
- **Integration** `WaitlistAutoBookTests`: enrollment with `auto_book` granted + match → no offer, hold finalized,
  confirmed intent minted directly, `notification.booking_confirmed.v1` enqueued, audit `waitlist.auto_booked`;
  auto-book failure (capacity lost in race) → compensating release + `notification.*` failure + settle.
- **Integration** `WaitlistFromAlternativesTests`: a spec-06 `AlternativesRequired` outcome with "join waitlist"
  selection creates an enrollment (capture path) + audit.
#### GREEN
- In the Match handler, the `auto_book` branch: finalize hold + `SubmitConfirmedIntentCommand` immediately (respecting
  `AutoBookGrant` constraints — max price/deposit ceiling from `deposit_terms`), enqueue booking-confirmed notification,
  audit. On failure, release + notify + settle.
- A capture handler/path that turns a waitlist selection from the booking flow into an `EnrollWaitlistCommand`
  (reuse Phase 2 handler). Wire the `notification.waitlisted.v1` template for the "you're on the waitlist" message.

### Phase 5: Success Criteria
#### Automated
- [ ] `WaitlistAutoBookTests` + `WaitlistFromAlternativesTests` green; auto-book honors deposit ceiling; failure compensates.

### Phase 5: Discoveries and Notable Information
[filled on execution]

---

## Phase 6: Hardening — races, re-match fairness, docs

### Phase 6: Changes Required
- Race/idempotency tests (spec §27.3 / FM#1-3): simultaneous accept+expiry (one wins), decline triggers exactly one
  re-match, cancel during a live offer releases the hold and stops re-match, duplicate match envelopes produce one offer.
- `docs/specs/11-waitlist-menu-preorder.md` status callout updated for the waitlist vertical; ADRs recorded.

### Phase 6: Success Criteria
#### Automated
- [ ] `bun nx test api` + `bun nx test-integration api` green; `bun nx lint api` clean (UTF-8 BOM + LF on new files).

### Phase 6: Discoveries and Notable Information
[filled on execution]

---

## Testing Strategy
- **Unit:** state machine (all edges), value-object parsing, validators, eligibility predicate.
- **Integration (Alba+Postgres):** enrollment lifecycle, match/hold/offer (incl. single-winner concurrency), accept
  idempotency, decline/expire release+re-match, auto-book, alternatives capture, cancel-during-offer.

## Architecture Decisions
| Decision | Chosen | Why |
|---|---|---|
| Match prioritization | FIFO by `MatchedAt`/`CreatedAt` | Spec §12 leaves it open; FIFO is the fair, explainable default. ADR-documented so a priority tier can replace it later. |
| Offer expiry mechanism | `ScheduledJob` (reuse `CapacityExpiryScheduler` pattern) | Same durable, idempotent, deterministic-id seam already used for hold expiry; no new infra. |
| Accept booking path | Delegate to spec-04 `SubmitConfirmedIntentCommand` | Reuses the confirmed-intent + audit machinery instead of a parallel booking path; keeps one source of truth. |
| Offer race safety | `VersionTokenUpdate` status+version guard | Single-winner on concurrent accept/expire without locks (Inv-2/5). |
| New langgraph agent | None for waitlist | Spec §3 matching is deterministic; the only AI seam (foreground "join waitlist?") is a .NET capture path. |

## References
- Spec: `docs/specs/11-waitlist-menu-preorder.md` §3, §4.1, §5.1, §10 Inv-1/2/3/5/6, §12, §27.3, FM#1-3.
- Seams: `ICapacityReservation.cs`, `CapacityExpiryScheduler.cs`, `SubmitConfirmedIntentCommand` (spec-04),
  `WaitlistOfferRepository.cs`, `LogicalQueueRegistry` (WaitlistCommands), notification outbox.
- Patterns: `docs/dotnet-api-patterns.md`; existing state machines; `memory/cordia-dotnet-conventions.md`.
