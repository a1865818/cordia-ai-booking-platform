---
date: 2026-07-01 01:21:48 CAST
researcher: andynguyen10.swe@gmail.com
git_commit: a52ba96ed2199de4ad7d7d599c20636c097010a7
branch: feat/spec-11
repository: Cordia
task: "Spec-11 conformance fixes: pre-order booking-commit delegation (#2), waitlist accept revision minting (#1), per-step saga timeouts + hosting (#3)"
tags: [implementation-plan, preorder-saga, waitlist, spec-11, tdd]
status: draft
last_updated: 2026-07-01
last_updated_by: andynguyen10.swe@gmail.com
---

# Spec-11 Divergence Fixes (#2, #1, #3) — TDD Implementation Plan

## Overview

Close the three deliberate divergences between the current `feat/spec-11` build and spec-11
(`docs/specs/11-waitlist-menu-preorder.md`), so the waitlist + pre-order verticals are genuinely
spec-conformant rather than functionally-approximate:

- **#2 — Pre-order `CommittingBooking` must commit a real booking via spec-06.** Today the saga only
  finalizes its own capacity hold and emits "booking confirmed" over a booking that was never
  created. **Decision (locked): the saga orchestrates and delegates the entire booking — capacity
  (07), payment (09), provider-create (06) — to the spec-06 operation engine**, observing its
  terminal outcome. This also makes Inv-7 (ambiguous → reconcile) genuinely exercised by 06's own
  reconciler, and fixes a latent double-notification.
- **#1 — Waitlist accept must mint a new immutable revision + confirmation via spec-04 (Inv-1).**
  Today accept finalizes the hold directly with no confirmed revision. **Decision (locked): the
  enrollment stores the canonical booking payload + identity/channel; accept rebuilds a revision,
  stamps a confirmation, submits through the real spec-04 acceptance transaction under the offer's
  operation id, then finalizes the hold.**
- **#3 — Per-step saga timeouts + hosted recovery.** Today recovery is a sweeper that isn't hosted
  (so no timeout ever fires). **Decision (locked): per-step `scheduled_jobs` timeouts (the spec's
  §5.3 mechanism) plus hosting `PreorderRecoverySweeper` as a backstop `BackgroundService`.**

The work is TDD (RED→GREEN per phase) with **exactly one consolidated EF migration generated at the
very end** (Phase 6).

## Current State Analysis

### #2 — how the booking is (not) committed today
- `SubmitConfirmedIntentCommandHandler` (spec-04) emits `booking.command.requested.v1`
  **unconditionally** on acceptance (`SubmitConfirmedIntentCommandHandler.cs:244`).
- That message is registered to the **06 driver** `AdvanceBookingOperationCommandHandler`
  (`ApplicationServiceCollectionExtensions.cs:78`), which drives the operation
  `Accepted → Validating → ReservingCapacity → Held → (RequestPayment) → Finalizing → Booked`
  and emits `notification.booking_confirmed.v1` from `FinalizeBookingCommandHandler.cs:530` and
  `ReconcileOperationCommandHandler.cs:186`.
- `preorder.commands.start.v1` has a **registered consumer** (`StartPreorderSagaCommand`,
  `ApplicationServiceCollectionExtensions.cs:212`) but **no producer** — so in production the saga
  never starts; the 06 operation alone creates the booking. The saga is only exercised by tests
  that inject the start message.
- The saga's `CommitPreorderBookingCommandHandler` only calls
  `ICapacityReservation.FinalizeAgainstOperationAsync(... "preorder" ...)` and then emits its own
  `notification.booking_confirmed.v1`. No provider booking is ever created.
- Net: a pre-order intent today gets its booking created by 06 (with a confirmation) **and** would,
  if the saga were wired, emit a *second* confirmation — an Inv-8 violation. The saga's
  `HoldingCapacity` (07 hold against `saga.SlotId`) and `AuthorizingPayment` (stub) duplicate what
  06 already does.

### #1 — what accept does today
- `AcceptWaitlistOfferCommandHandler.cs` version-guards `Offered → Accepted`, calls
  `capacity.FinalizeAgainstOperationAsync(tenant, offer.OperationId, "waitlist", …)`, and emits
  `waitlist.booking_confirmed.v1`. No `IntentRevision` / `IntentConfirmation` / `BookingOperation`
  is created. `SubmitConfirmedIntentCommand` requires a full `SubmitConfirmedIntentRequest`
  (`Revision` + `Confirmation` whose `intent_payload_hash == canonical hash`) — none of which the
  accept currently has.

### #3 — recovery today
- `PreorderRecoverySweeper.SweepAsync(olderThan)` exists but is **not hosted** (its own docstring
  says "wire to a scheduled job or a periodic hosted-service"). No per-step timeout is scheduled.
- The infra to copy is proven: `ScheduledJobPollerHostedService` drains `scheduled_jobs` → publishes
  to the destination queue (so scheduling a `preorder.commands.reconcile.v1` job "just works"), and
  `StepSweeperHostedService` is the canonical `BackgroundService` + `PollingLoop.RunAsync` pattern
  with an `Enabled` option and a public `*OnceAsync` for tests. `RetryScheduler`
  (`BookingFulfillment/RetryScheduler.cs`) shows the in-ambient-txn `IScheduledJobRepository` write
  pattern.

### Key discoveries / constraints
- `ReconcileOperationCommandHandler` already implements the §16.2 idempotency-key→reference→status
  probe and the Inv-7 "reconcile before re-create" guard — so by delegating to 06 we get genuine
  Inv-7 for free; the saga must NOT re-implement provider reconcile.
- The 06 driver treats `AwaitingPayment / AwaitingUserAction / AlternativesRequired / ManualReview`
  as wait states (`AdvanceBookingOperationCommandHandler.cs:99`); terminal detection is
  `OperationStateMachine.IsTerminal`.
- New `.cs` files must be **UTF-8 with BOM + LF** or `bun nx lint api` fails. Handlers that are
  `IInboxConsumerRequest` must NOT open their own transaction (the `InboxDeduplicationBehavior`
  owns it); HTTP-path handlers use `IUnitOfWork.ExecuteInTransactionAsync`.
- Pre-existing flake (NOT a regression): `ScheduledJobPollerTests.RescheduleAsync_* / ClaimDueAsync_*`
  assert exact `DateTimeOffset` equality vs Postgres µs truncation.

## Desired End State

- A confirmed intent carrying a `PreorderReference` drives: **04 → `preorder.commands.start.v1` →
  saga revalidate → reserve items → trigger 06 → 06 creates the real (stub-provider) booking →
  06 signals the saga → saga commits the pre-order → exactly one combined `booking_confirmed`
  outcome.** 06 emits no standalone confirmation for pre-order operations. Booking failure / 06
  ambiguity is handled inside 06 (reconcile) and surfaces to the saga as a single failure signal →
  the saga compensates items and emits exactly one combined failure outcome.
- A waitlist accept produces a real confirmed `BookingIntentRevision` + `IntentConfirmation` +
  `BookingOperation` through the spec-04 acceptance transaction, then finalizes the still-valid hold
  (dead hold → re-offer, unchanged).
- Every in-flight saga step has a `scheduled_jobs` timeout that fires `preorder.commands.reconcile.v1`
  on stall; `PreorderRecoverySweeper` runs on a hosted cadence as a backstop. Both are idempotent
  via state-guarded reconcile.
- Exactly one consolidated migration adds the new columns; `bun nx test api` +
  `bun nx test-integration api` green (modulo the known unrelated poller flake).

## What We're NOT Doing

- **No langgraph changes / no langgraph↔.NET wiring.** Foreground catalog/draft tools and background
  tools stay out of scope (agents already set up separately).
- **No real provider/payment/ClamAV/object-storage/LLM.** All external infra stays stub-ported
  behind the existing real boundaries (`IBookingProviderAdapter`, deposit/payment stubs).
- **No orphaned-pre-order → standalone conversion (§21.4).** `CompensatingBooking → ManualReview`
  remains the landing point; the audited operator+customer conversion is a separate gap, deferred.
- **No web UI.**
- **No change to the menu-ingestion vertical** (already complete).

## Implementation Approach

TDD, RED→GREEN per phase. Phases 1–3 deliver #2 (the largest change: a saga redesign), Phase 4
delivers #1, Phase 5 delivers #3, and Phase 6 generates the **single** consolidated migration and
runs full regression. Migrations are deferred: where an earlier phase needs a new column to compile
its GREEN step, add the column to the EF entity/configuration in that phase but **do not generate the
migration until Phase 6** (integration tests in phases 1–5 that need the schema are written to run
after Phase 6, or the phase is validated by unit tests only — see each phase's success criteria).

> **Migration sequencing note:** because integration tests need the physical columns, the pragmatic
> TDD path (consistent with how the saga columns were handled previously) is: build all entity/config
> changes across phases 1–5, generate the one migration in Phase 6, then run the integration suites.
> Unit-level RED/GREEN (state machine, message builders, 04 routing with mocked repos) is fully
> exercised per-phase without the DB.

---

## Phase 1: Pre-order saga state-machine redesign (delegate booking to 06)

### Phase 1: Overview
Rework `PreorderSagaStateMachine` + the saga's forward path so the booking (capacity/payment/create)
is delegated to 06. New forward path:
`Revalidating → ReservingItems → CommittingBooking(await 06) → CommittingPreorder → Completed`.
`HoldingCapacity` / `AuthorizingPayment` / `CompensatingHold` leave the forward path (the saga no
longer holds capacity or authorizes payment itself). Compensation simplifies to `CompensatingItems`
(release reserved items) and `CompensatingBooking → ManualReview` (booking committed but pre-order
not). Add the two 06→saga signal message types.

### Phase 1: Changes Required

#### 1. State machine
**File**: `apps/api/src/Cordia.Domain/Preorder/PreorderSagaStateMachine.cs`
- New edge set:
  - `Revalidating → {ReservingItems, AlternativesRequired-as-FailedFinal}` (drop the
    `Revalidating → HoldingCapacity` forward edge; keep `Revalidating → FailedFinal`).
  - `ReservingItems → {CommittingBooking, CompensatingItems}`.
  - `CommittingBooking → {CommittingPreorder, CompensatingItems}` (06 owns ambiguous→reconcile
    internally; the saga only sees terminal Booked vs failed).
  - `CommittingPreorder → {Completed, CompensatingBooking}`.
  - `CompensatingItems → FailedFinal`.
  - `CompensatingBooking → ManualReview`.
  - `Reconciling` reachable from each in-flight forward step (crash recovery) and fanning to the
    matching resume / escalation.
  - Keep `HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold` enum members for now (removed
    from the forward edge set; flagged unused) to avoid a churny enum change — or remove if the
    enum is not pg-mapped (verify: it is stored as a string state column). Prefer **keeping** the
    members and simply not wiring edges, to minimize migration surface.
- Keep `IsInFlightForwardStep` updated to the new forward set.

#### 2. New message types
**File**: `apps/api/src/Cordia.Application/Handlers/Preorder/PreorderSagaMessages.cs`
- Add `BookingCommitted = "preorder.commands.booking_committed.v1"` and
  `BookingFailed = "preorder.commands.booking_failed.v1"` (06→saga signals).
- Retain `StepMessage(...)`; the two new types reuse the same `{saga_id, operation_id}` payload.

### Phase 1: Success Criteria

#### Phase 1: Automated Verification
- [x] `PreorderSagaStateMachineTests` rewritten/extended: new forward edges allowed, removed forward
      edges rejected, compensation + reconcile fan-out asserted. `bun nx test api` green (628 unit).
- [x] Format clean on Phase-1 files (scoped `dotnet format --verify-no-changes --include …` → EXIT=0).
      Whole-project `bun nx lint api` has a pre-existing local CRLF/autocrlf artifact — see Discoveries.

#### Phase 1: Manual Verification
- [ ] The new edge set matches the spec §5.3 diagram intent under "saga orchestrates, 06 owns
      booking": items reserved before the booking is triggered; no saga-level capacity/payment edges.

### Phase 1: Discoveries and Notable Information

**Implemented (2026-07-01):**
- `PreorderSagaStateMachine.cs`: new forward set `Revalidating → ReservingItems → CommittingBooking
  (await 06) → CommittingPreorder → Completed`. `HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold`
  enum members retained but **unwired** (no outgoing edges) — booking delegated to 06 (07/09 owned there).
  Compensation simplified to `CompensatingItems → FailedFinal` and `CompensatingBooking → ManualReview`.
  `_inFlightForwardSteps` = {ReservingItems, CommittingBooking, CommittingPreorder}.
- `PreorderSagaMessages.cs`: added `BookingCommitted = "preorder.commands.booking_committed.v1"` and
  `BookingFailed = "preorder.commands.booking_failed.v1"` (06→saga signals).
- Tests rewritten; **`bun nx test api` green (628 unit)**. Solution still builds — old handlers
  (HoldCapacity/AuthorizePayment) compile unchanged (rewired in Phase 3); their runtime-invalid
  transitions only surface in the integration suites, which are deferred to Phase 6.

**Lint (resolved — local Windows artifact, NOT a Phase-1 issue):** `bun nx lint api` (whole-project)
reports ~51 whitespace errors across 33 **committed, unmodified** files. Root cause is **not** an SDK
mismatch (that was an initial mis-diagnosis) — it is `core.autocrlf=true` with **no `.gitattributes`**:
committed files are LF in git (`git ls-files --eol` → `i/lf w/crlf`) but checked out as CRLF on
Windows, and `dotnet format` rejects the CRLF. CI on Linux is unaffected; freshly-edited files are LF
(the editor rewrites them), which is why no Phase-1 file is flagged. Pre-commit and CI only check the
files in a change, so this does not block a commit of clean staged files. **Effective lint gate used
for the loop: scoped `dotnet format --verify-no-changes --include <changed files>`**, which passed
(EXIT=0) on all Phase-1 files. **Recommended repo hygiene fix (out of spec-11 scope, flagged to the
owner): add a `.gitattributes` with `* text=auto eol=lf` (and/or set `core.autocrlf=input`) so Windows
checkouts are LF and whole-project `bun nx lint api` is reliable locally.**

---

## Phase 2: 04 routing + 06 notification suppression for pre-order operations

### Phase 2: Overview
Make spec-04 route pre-order intents to the saga instead of straight to 06, and make 06 emit a
saga-resumption signal (not a customer-facing confirmation) when a **pre-order operation** reaches a
terminal state. Introduce `booking_operations.is_preorder` (EF entity/config only; migration in
Phase 6).

### Phase 2: Changes Required

#### 1. Operation flag
**Files**: `apps/api/src/Cordia.Domain/Entities/BookingOperation.cs`,
`apps/api/src/Cordia.Infrastructure/EntityFramework/Configurations/BookingOperationConfiguration.cs`
- Add `bool IsPreorder` (default `false`), mapped to `is_preorder`.

#### 2. 04 acceptance routing
**File**: `apps/api/src/Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/SubmitConfirmedIntentCommandHandler.cs`
- When the accepted revision's canonical payload carries a non-empty `PreorderReference`:
  - set `BookingOperation.IsPreorder = true`;
  - emit **`preorder.commands.start.v1`** (carrying the `preorder_reference`, `operation_id`,
    `tenant_id`, slot/party derived from the revision) **instead of** `booking.command.requested.v1`.
  - Non-pre-order path unchanged (still emits `booking.command.requested.v1`).
- This finally wires the missing saga **producer**.

#### 3. 06 confirmation/failure suppression for pre-order ops
**Files**: `FinalizeBookingCommandHandler.cs`, `ReconcileOperationCommandHandler.cs`, and the 06
failure/escalation emit sites (compensate / manual-review).
- On `… → Booked` for a pre-order op (`op.IsPreorder`): **do not** emit
  `notification.booking_confirmed.v1`; instead emit `preorder.commands.booking_committed.v1`
  (saga signal) in the same transaction as the Booked transition (preserve Inv-8 ordering).
- On a terminal **failure** for a pre-order op (FailedFinal / ManualReview / compensation-complete):
  **do not** emit the customer-facing `notification.booking_failed.v1`; instead emit
  `preorder.commands.booking_failed.v1`.
- Implement via a single private helper (e.g. `EmitBookingOutcome(op, outcome, …)`) so all emit
  sites branch on `op.IsPreorder` consistently.

### Phase 2: Success Criteria

> **Phase split (2026-07-01):** Phase 2 is delivered in two independently-verified sub-steps because
> the 06 suppression touches **6 emit sites across 5 handlers** and must be gated atomically (partial
> gating would let a failing pre-order op emit a customer notification). **2a = 04 routing + entity
> flag (DONE); 2b = the `EmitBookingOutcome` suppression refactor (PENDING).**

#### Phase 2: Automated Verification
- [x] **(2a)** Unit tests (mocked repos/outbox): 04 with a pre-order revision emits
      `preorder.commands.start.v1` and sets `IsPreorder`; 04 without a pre-order emits
      `booking.command.requested.v1` (unchanged). — `SubmitConfirmedIntentPreorderRoutingTests` (2);
      `bun nx test api` green (630); scoped format clean.
- [x] **(2b)** All 7 06 emit sites gated on `op.IsPreorder` via `PreorderSagaMessages.ConfirmedOutcomeType`
      / `FailedOutcomeType`; pre-order → `booking_committed.v1`/`booking_failed.v1`, non-pre-order
      unchanged. `PreorderOutcomeRoutingTests` (4). Existing 06 handler tests stay green (non-pre-order
      path byte-identical) — proves no regression.
- [x] `bun nx test api` green (634); format clean (scoped; whole-project lint has the pre-existing CRLF
      artifact — see Phase 1 Discoveries).

#### Phase 2: Manual Verification
- [ ] Grep confirms every `notification.booking_confirmed.v1` / `notification.booking_failed.v1`
      emit site in 06 is gated on `IsPreorder`.

### Phase 2: Discoveries and Notable Information

**Pre-implementation findings (2026-07-01):**
- **Routing trigger:** `BookingRequest.Preorder` is a `PreorderReference` (generated
  `SharedTypes.cs:1129`; fields `CatalogVersion` / `Lines: List<PreorderLineReference>` /
  `ConfirmationId`). The 04 handler already has the accepted revision as
  `revision.CanonicalPayload` (a `BookingRequest`), so the pre-order check is
  `revision.CanonicalPayload.Preorder is { Lines.Count: > 0 }` — no new plumbing needed.
- **04 emit site:** `SubmitConfirmedIntentCommandHandler.TryElectAndWriteAsync` emits
  `booking.command.requested.v1` at the outbox `AddAsync` inside the single UoW txn (currently
  ~line 244), and constructs the `BookingOperation` just above (~line 225). Phase 2 sets
  `IsPreorder = true` on that operation and swaps the message type to
  `PreorderSagaMessages.Start` (`preorder.commands.start.v1`) with the saga-start payload
  (operation_id, tenant_id, preorder_reference/derived slot+party) when the trigger holds; the
  non-pre-order branch is unchanged. This wires the missing saga **producer** (StartPreorderSaga
  had a consumer but no producer).
- **Entity/config:** `BookingOperation` (Domain/Entities) is a plain mutable POCO; add
  `bool IsPreorder` (default false) + map `is_preorder` in `BookingOperationConfiguration`
  (migration deferred to Phase 6).
- **06 suppression (still to read before editing):** `FinalizeBookingCommandHandler` (~:530) and
  `ReconcileOperationCommandHandler` (~:186) emit `notification.booking_confirmed.v1`; the failure/
  escalation sites emit `notification.booking_failed.v1`. Gate all on `op.IsPreorder` via one helper
  (`EmitBookingOutcome`) → emit `preorder.commands.booking_committed.v1` / `booking_failed.v1`
  instead (Inv-8: signal in the same txn as the Booked transition).
- Generated `SharedTypes.cs` lives at `libs/shared-schemas/generated/csharp/` (gitignored,
  regenerated); present locally so the build/tests see `BookingRequest.Preorder`.

**2a implemented (2026-07-01):**
- `BookingOperation.IsPreorder` (Domain) + `is_preorder` mapping (config, default false); migration
  deferred to Phase 6.
- `SubmitConfirmedIntentCommandHandler.TryElectAndWriteAsync`: detects
  `revision.CanonicalPayload.Preorder is { Lines.Count: > 0 }`, sets `IsPreorder`, and branches the
  outbox to `preorder.commands.start.v1` (payload: `operation_id`, `tenant_id`, `preorder_reference`
  serialized via `Converter.Settings`, `standalone=false`) vs the unchanged
  `booking.command.requested.v1`. Wires the previously-missing saga producer.
- Tests: `SubmitConfirmedIntentPreorderRoutingTests` (2, Moq) — this handler had no prior unit
  harness (integration-tested only); the mocked test asserts both branches. 630 unit green.
- **LF gotcha:** editing a committed file that is CRLF in the Windows working tree keeps CRLF, which
  `dotnet format` then flags (ENDOFLINE). After editing such files, strip CR → LF (BOM preserved).
  New files written by the tooling are already LF but need a BOM prepended.

**2b remaining (next iteration):** 6 emit sites to gate on `op.IsPreorder` via one `EmitBookingOutcome`
helper → emit `preorder.commands.booking_committed.v1` / `booking_failed.v1` instead of
`notification.booking_confirmed.v1` / `booking_failed.v1`:
`FinalizeBookingCommandHandler.cs:530`, `ReconcileOperationCommandHandler.cs:186`,
`CompensateOperationCommandHandler.cs:147,248`, `ResolveManualReviewCommandHandler.cs:85,118`,
`RespondToAlternativesCommandHandler.cs:111`. Register the two new signal consumers
(booking_committed/failed) so the saga resumes (Phase 3 adds the saga-side handlers). Must be done
atomically across all sites (partial gating = customer double-notification bug).

**2b implemented (2026-07-01):** all 7 sites gated (Finalize, Reconcile, Compensate ×2,
ResolveManualReview ×2, RespondToAlternatives) via two static helpers on `PreorderSagaMessages`
(`ConfirmedOutcomeType`/`FailedOutcomeType`) — single source of truth, fully-qualified at call sites
(no per-file usings). The non-pre-order literals are preserved exactly, so all existing 06 handler
unit tests pass unchanged (634 total). **Consumer registration for `booking_committed.v1`/
`booking_failed.v1` is deferred to Phase 3** (which adds the saga-side handlers); until then these
types are emit-only and only exercised by unit assertions (integration is Phase 6). Edited committed
handlers were CRLF in the working tree → LF-normalized after editing (BOM preserved).

---

## Phase 3: Saga handler rewire — reserve items → drive 06 → commit pre-order

### Phase 3: Overview
Rewire the saga handlers to the new flow and add the 06→saga signal consumers. Remove the saga's
own capacity-hold and payment-authorization steps (now owned by 06/07/09).

### Phase 3: Changes Required

#### 1. Start → ReservingItems (always)
**File**: `apps/api/src/Cordia.Application/Handlers/Preorder/StartSaga/StartPreorderSagaCommandHandler.cs`
- On revalidation OK, advance `Revalidating → ReservingItems` (drop the booking-attached vs
  standalone branch into HoldingCapacity; items-first for both).

#### 2. ReserveItems → CommittingBooking + trigger 06
**File**: `apps/api/src/Cordia.Application/Handlers/Preorder/ReserveItems/ReservePreorderItemsCommandHandler.cs`
- On success: advance `ReservingItems → CommittingBooking`, and emit
  **`booking.command.requested.v1`** for the operation (this starts the 06 engine, which now owns
  capacity + payment + create). On item-reserve failure: `ReservingItems → CompensatingItems`.

#### 3. New 06→saga signal consumers
**New files** under `apps/api/src/Cordia.Application/Handlers/Preorder/BookingSignal/`:
- `PreorderBookingCommittedCommand(+Handler)` — consumes `preorder.commands.booking_committed.v1`;
  guards `CommittingBooking`, advances `→ CommittingPreorder`, enqueues `commit_preorder`, audits.
- `PreorderBookingFailedCommand(+Handler)` — consumes `preorder.commands.booking_failed.v1`;
  guards `CommittingBooking`, advances `→ CompensatingItems`, enqueues `compensate_items`, audits.
- Both are `IInboxConsumerRequest` (consumer `preorder.processor`), idempotent via state guard.
- Register both in `ApplicationServiceCollectionExtensions.cs` + `LogicalQueue` metadata if needed
  (they ride the existing `preorder.commands` lane).

#### 4. CommitPreorder → Completed → ONE combined outcome
**File**: `apps/api/src/Cordia.Application/Handlers/Preorder/CommitPreorder/CommitPreorderCommandHandler.cs`
- Unchanged in spirit: terminal-guarded `CommittingPreorder → Completed`, emit the single
  `notification.booking_confirmed.v1` combined outcome (Inv-8). On commit failure →
  `CompensatingBooking`.

#### 5. CompensateBooking now cancels the 06 booking
**File**: `apps/api/src/Cordia.Application/Handlers/Preorder/CompensateBooking/CompensatePreorderBookingCommandHandler.cs`
- Instead of voiding the saga's own deposit, trigger 06's compensation/cancellation for the operation
  (enqueue the 06 compensate continuation) and route `CompensatingBooking → ManualReview` (operator +
  customer per §5.3). Emit the single combined failure outcome.

#### 6. Remove now-dead steps
- Delete `HoldCapacity/` + `AuthorizePayment/` handlers + commands; remove `IPreorderPaymentAuthorization`
  + `StubPreorderPaymentAuthorization` and their DI registration; remove the saga's direct
  `ICapacityReservation` usage. Remove `CompensateHold/` (no saga-level hold to release). Keep
  `IPreorderItemReservation` + its stub (items remain the saga's job).
- Drop `HoldCapacity`/`AuthorizePayment`/`CompensateHold` message consts + registrations.
- `Reconcile` resume map: drop `HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold` entries; add
  `CommittingBooking → CommitBooking` already present (now means "re-trigger 06 / re-await").

#### 7. Saga columns
- `preorder_sagas.slot_id` / `requested_capacity` become unused (06 derives capacity from the
  operation's revision). Leave the columns (no destructive drop) OR drop in the Phase-6 migration —
  prefer **leaving** them to keep the migration additive; mark unused in the entity XML doc.

### Phase 3: Success Criteria

#### Phase 3: Automated Verification (after Phase 6 migration for integration paths)
- [x] Unit tests: ReserveItems success advances to CommittingBooking + emits
      `booking.command.requested.v1` (3a); booking_committed signal advances to CommittingPreorder;
      booking_failed signal advances to CompensatingItems (3b). `ReservePreorderItemsHandlerTests` +
      `PreorderBookingSignalHandlerTests`; 639 unit green; scoped format clean.
- [ ] Integration (post-migration): **happy path** — submit a confirmed pre-order intent → 04 emits
      start → saga revalidates + reserves items + triggers 06 → 06 (stub provider) books → saga
      commits pre-order → **exactly one** `notification.booking_confirmed.v1`, no duplicate.
- [ ] Integration: **booking failure** — 06 provider Rejected → 06 signals booking_failed → saga
      compensates items → exactly one combined failure outcome; no dangling item reservation.
- [ ] Integration: **ambiguous** — 06 provider Ambiguous → 06 reconciles (key→ref→status) → Booked →
      saga commits → single outcome (Inv-7 exercised by 06, not the saga).
- [ ] `bun nx test api` + `bun nx test-integration api` green (modulo known poller flake).

#### Phase 3: Manual Verification
- [ ] No code path emits two `booking_confirmed` for a pre-order op.
- [ ] No saga-level capacity hold or payment auth remains.

### Phase 3: Discoveries and Notable Information

> **Sub-split:** 3a = rewire the two forward handlers (DONE); 3b = the two 06→saga signal consumers +
> registration (DONE); 3c = CompensateBooking rewire + remove dead HoldCapacity/AuthorizePayment/
> CompensateHold steps + DI cleanup + reconcile resume-map + fix CompensateItems (drop CompensatingHold
> branch → FailedFinal) (PENDING).

**3b implemented (2026-07-01):** `BookingSignal/PreorderBookingComm{itted,Failed}Command(+Handler)`
— consumer `preorder.processor`, `IInboxConsumerRequest`, resolve saga by `GetByOperationAsync` (the
06 signal payloads carry `operation_id`, not `saga_id`), state-guarded. Committed → CommittingBooking
→ CommittingPreorder + enqueue `CommitPreorder`; failed → CommittingBooking → CompensatingItems +
enqueue `CompensateItems` (setFailureReason "booking_failed"). Registered both in the `ConsumerRegistry`
(`PreorderOperationId(env)`). **No `LogicalQueue` change needed** — routing is prefix-based (`preorder.`
→ PreorderCommands), so the new types route automatically. `PreorderBookingSignalHandlerTests` (3,
incl. idempotent settle). 639 unit green.

**3c implemented + partially deferred (2026-07-01):**
- DONE: `CompensatePreorderItemsCommandHandler` simplified to always `CompensatingItems → FailedFinal`
  + single failure outcome (dropped the dead `HoldId`→CompensatingHold branch). Body-only change, no
  signature/symbol coupling → unit-safe. 639 unit green; scoped format clean.
- **DEFERRED to Phase 6 (coupled to the integration-test rewrite):** the dead-code deletion
  (HoldCapacity/AuthorizePayment/CompensateHold/CommitBooking handlers+commands+consts+registrations,
  `IPreorderPaymentAuthorization`+stub+DI, reconcile resume-map entries) **and** the `CompensateBooking`
  rewire (→ ManualReview only, drop the payment/item ports). Reason: the current integration tests
  (`PreorderCompensationTests`, `PreorderSagaHappyPathTests`, `PreorderRevalidationTests`,
  `PreorderRecoveryAndStandaloneTests`) are one assembly with the unit tests and directly reference /
  construct these symbols (incl. `CompensateBooking`'s 6-arg ctor at `PreorderCompensationTests.cs:74`
  and `IPreorderPaymentAuthorization`, `HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold`,
  `PreorderSagaMessages.{HoldCapacity,AuthorizePayment,CompensateHold,CommitBooking}`). Deleting/
  changing them now breaks test-project compilation → the unit run goes down too. These obsolete tests
  are rewritten for the new flow in Phase 6, so the coupled removals + `CompensateBooking` rewire move
  there. A deferral note is left in `CompensatePreorderBookingCommandHandler`'s doc comment.
- **Plan-vs-spec judgment (for review):** the plan text suggested `CompensateBooking` auto-triggers 06
  cancellation; I will instead favour spec §21.4 / §8 row 10 — a committed booking is authoritative and
  routes to ManualReview for the operator+customer decision (convert to standalone / cancel), never
  auto-cancelled. Applied in the Phase-6 rewire.

**3a implemented (2026-07-01):**
- `StartPreorderSagaCommandHandler`: OK branch now always `Revalidating → ReservingItems` (items-first;
  dropped the HoldingCapacity/standalone branch) and enqueues `ReserveItems`.
- `ReservePreorderItemsCommandHandler`: on Reserved → `ReservingItems → CommittingBooking` and emits
  **`booking.command.requested.v1`** (OutboxMessage `OperationId = op`, minimal `{operation_id,
  tenant_id}` payload — the 06 driver keys off the envelope operation id, `ApplicationServiceCollectionExtensions.cs:71`).
  On Unavailable → `ReservingItems → CompensatingItems` (dropped the `HoldId`/CompensatingHold branch —
  the saga holds no capacity now). Both these preorder handlers are untracked new files (LF); the two
  06-triggering fields need no extra plumbing.
- Tests: `ReservePreorderItemsHandlerTests` (2, Moq + NullLogger). 636 unit green; scoped format clean.
- No unit harness existed for these saga handlers (integration-only); the mocked tests are new.

**3b/3c remaining (next iteration):**
- 3b: new `BookingSignal/PreorderBookingComm{itted,Failed}Command(+Handler)` (consumer
  `preorder.processor`, `IInboxConsumerRequest`, state-guarded): committed → `CommittingBooking →
  CommittingPreorder` + enqueue `CommitPreorder`; failed → `CommittingBooking → CompensatingItems` +
  enqueue `CompensateItems`. Register both in `ApplicationServiceCollectionExtensions` (registry) +
  `LogicalQueue`/`LogicalQueueRegistry` metadata (note: `LogicalQueueRegistryTests` may enumerate the
  preorder lane message set — update it). Add unit tests for both signal transitions.
- 3c: `CompensatePreorderBookingCommandHandler` → trigger 06 cancel + route `CompensatingBooking →
  ManualReview`. Remove `HoldCapacity/`, `AuthorizePayment/`, `CompensateHold/` handlers+commands +
  `IPreorderPaymentAuthorization`/`StubPreorderPaymentAuthorization` + their DI + the message consts +
  their registry registrations; drop those entries from the `ReconcilePreorderSagaCommandHandler`
  resume map. The old `CommitPreorderBookingCommandHandler` (finalized the saga's own hold) is
  superseded by the booking_committed signal consumer — remove it + its `CommitBooking` message/registration.
  Verify nothing else references the removed symbols (grep) before deleting.

---

## Phase 4: Waitlist accept mints a revision + confirmation via spec-04 (#1)

### Phase 4: Overview
Persist the canonical booking payload + identity/channel on the enrollment; on accept, build a new
`SubmitConfirmedIntentRequest` and run it through `SubmitConfirmedIntentCommand`, then finalize the
hold. Closes Inv-1.

### Phase 4: Changes Required

#### 1. Enrollment captures the bookable request
**Files**: `apps/api/src/Cordia.Domain/Entities/WaitlistEnrollment.cs` (+ its EF config),
`WaitlistController` / enroll command + handler.
- Add columns: `canonical_payload` (jsonb — the customer's intended `BookingRequest`),
  `identity_ref` (string), `confirmation_channel` (string), and any `verification_level` context
  needed to stamp the confirmation. Populate at enroll time.

#### 2. Accept builds + submits the revision
**File**: `apps/api/src/Cordia.Application/Handlers/Waitlist/Accept/AcceptWaitlistOfferCommandHandler.cs`
- After winning `Offered → Accepted`:
  1. Build a new `IntentRevision` from the enrollment's `canonical_payload` (new revision number;
     `supersession_reason = "waitlist_accept"`; `source_session_id` from enrollment).
  2. Compute the canonical hash (reuse `ICanonicalRequestHasher`); build a `Confirmation` with
     `intent_payload_hash = hash`, `confirmation_channel` from enrollment, and a `verification_level`
     reflecting the authenticated accept (the offer token + accept request is the "verified
     follow-up" evidence per §5.1 line 198).
  3. Send `SubmitConfirmedIntentCommand` with `request.OperationId = offer.OperationId` so the minted
     operation matches the operation the hold was reserved under.
  4. Then `capacity.FinalizeAgainstOperationAsync(tenant, offer.OperationId, "waitlist", …)` (dead
     hold → re-offer path preserved).
- Replace the bespoke `waitlist.booking_confirmed.v1` emit with the standard 04/06 outcome flow
  (04 emits `booking.command.requested.v1` → 06 drives → `notification.booking_confirmed.v1`),
  unless the slot is non-bookable-by-06, in which case keep finalize + a registered `notification.*`
  outcome (decide during impl; default: let 04/06 own the outcome).
- Idempotency: an already-Accepted offer returns without re-submitting (the 04 idempotency key makes
  re-submit a safe replay regardless).

### Phase 4: Success Criteria

#### Phase 4: Automated Verification (integration after Phase 6)
- [x] Unit: accept constructs a hash-matching `SubmitConfirmedIntentRequest` under `offer.OperationId`
      (bound to the enrolled customer's identity) and claims the offer — `AcceptWaitlistOfferHandlerTests`.
- [ ] Integration: accept creates a `booking_intent_revisions` row + `intent_confirmations` row +
      `booking_operations` row (Accepted) under the offer's operation id; hold finalized.
- [ ] Integration: accept on an expired/dead hold finalizes 0 rows → re-offer/re-match (unchanged
      Inv-6 behavior).
- [ ] Integration: duplicate accept is a safe replay (no second revision/operation).
- [ ] `bun nx test api` + `bun nx test-integration api` green.

#### Phase 4: Manual Verification
- [ ] A waitlist accept produces an auditable confirmed revision (Inv-1) just like a direct booking.

### Phase 4: Discoveries and Notable Information

> **Sub-split:** 4a = enrollment captures identity context (DONE); 4b = accept rewire (mint revision +
> confirmation via spec-04, then finalize) — PENDING.

**4a implemented (2026-07-01):**
- **Design deviation from the plan (for review):** the plan said store `canonical_payload` (jsonb) at
  enroll. But an enrollment captures a date *range* (`AcceptableFrom/To`), not a specific slot — the
  slot is only known at match time (`offer.SlotStartAt`). So storing a slot-less payload at enroll is
  wrong. Instead: capture only the **identity context** at enroll and **assemble the canonical
  `BookingRequest` at accept** from enrollment fields (operation=create, `ServiceId`, `LocationId`,
  `PartySize`) + `offer.SlotStartAt`. Cleaner + correct; no `canonical_payload` column.
- Added `WaitlistEnrollment.IdentityRef` (defaults to `CustomerRef`) + `VerificationLevel` (defaults
  `"unverified"`); EF mapping `identity_ref` / `verification_level` (migration Phase 6). Enroll command
  gained optional `IdentityRef` / `VerificationLevel` params (defaulted, so existing callers/tests
  unaffected); handler populates them. 639 unit green; scoped format clean.
- **Identity-strength decision (divergence #1 — FOR YOUR REVIEW):** the accept (4b) stamps the minted
  `IntentConfirmation` with the enrolled customer's `IdentityRef` + this `VerificationLevel`, so the
  acceptance is a real confirmed revision tied to THAT customer (Inv-1) — a mere notification-viewer
  cannot mint a valid confirmation for them. The confirmation never over-claims assurance (defaults to
  `unverified` unless the enroll established more). **Open dependency:** the actual gate on *who may
  call the accept endpoint* ("verified follow-up", §5.1 line 198) is spec-08's identity/manage-booking
  model, not this spec — flagged so it isn't assumed closed by this change.

**4b implemented (2026-07-01):**
- `AcceptWaitlistOfferCommandHandler` fully rewritten to route through the spec-04 front door. New deps:
  `offerRepo, enrollmentRepo, ICanonicalRequestHasher, IMediator, IAuditEventRepository, IUnitOfWork`
  (dropped `ICapacityReservation` + `IOutboxRepository` — 04/06 own capacity + the outcome event now).
  MediatR auto-registration means no DI change. Flow: load+validate offer → load enrollment → build a
  `BookingRequest` (operation=create, service/location/party from enrollment, `starts_at` =
  `offer.SlotStartAt`) → hash it with the SAME `Converter.Settings` 04 uses → build `IntentRevision`
  (rev 1, deterministic `wl_int_{offerId}`) + `Confirmation` (bare hash, identity=`IdentityRef`,
  level=`VerificationLevel`, channel mapped from `NotificationChannel`) → `mediator.Send(
  SubmitConfirmedIntentCommand)` with `OperationId = offer.OperationId` and deterministic
  `IdempotencyKey = wl_accept_{offerId}` → then claim `Offered → Accepted` (version-guarded) → audit.
- **Nested-transaction resolution:** the accept handler does NOT wrap the 04 submit in its own txn. 04
  owns its transaction; the offer claim is a single guarded UPDATE; the `waitlist.accepted` audit is a
  tiny separate txn. **Ordering is record-before-state + self-healing:** submit to 04 FIRST (idempotent
  on the deterministic key), THEN claim the offer. A crash between the two, concurrent accepts, and an
  already-Accepted replay all re-submit safely (04 replays, no duplicate booking) — the already-Accepted
  branch still re-submits so a crash after the claim converges. No duplicate revision/operation.
- **Capacity integration decision (deviation from plan step 4 — IMPORTANT):** the plan said "then
  `FinalizeAgainstOperationAsync(...,"waitlist")`". That would DOUBLE-COUNT capacity once 04→06 also
  runs, because 06's `ReserveCapacity` reserves under workflow-step `create` while the match held under
  `waitlist`, and `ReserveAsync`'s idempotency pre-check is keyed on
  `(tenant, operation, workflow_step)` — a different step ⇒ a second hold. **Fix (true integration, no
  parallel capacity path):** `MatchWaitlistOfferCommandHandler.WorkflowStep` changed `"waitlist"` →
  `"create"`, so the waitlist hold is born in the exact form the 06 engine expects. On accept, 04→06's
  `ReserveCapacity` finds and REUSES the existing hold (returns `Held(existing)`), then `FinalizeBooking`
  finalizes it — one hold, one booking. The accept handler no longer finalizes capacity itself. Expire /
  Decline release by `offer.HoldId` (step-agnostic), so the step rename doesn't affect them.
- **Dead-hold behavior change (acceptable):** old accept detected an expired hold and re-offered. New:
  if the hold expired, 06's `ReserveCapacity` attempts a fresh reserve → books if capacity is still
  free, else `AlternativesRequired` (a native 06 outcome). More consistent (a waitlist accept that lost
  its hold behaves like any booking that lost its hold); Inv-6 re-offer for decline/expire is unchanged.
- **Identity (divergence #1):** the minted confirmation is attributed to `enrollment.IdentityRef` /
  `VerificationLevel`, so the booking is bound to the enrolled customer, not the notification viewer.
  Enforcing that the *caller* of the accept endpoint IS that customer remains the spec-08 endpoint-auth
  dependency (flagged in 4a; NOT closed here). No new command field added (no authenticated caller
  identity source exists yet) — kept minimal rather than half-wiring a guard with no auth source.
- **Follow-up flagged:** the auto-book path (§20) in `MatchWaitlistOfferCommandHandler` still finalizes
  directly + emits the bespoke `waitlist.booking_confirmed.v1` WITHOUT minting a 04 record — the same
  divergence-#1 gap, for auto-book. It runs inside the match inbox transaction, so routing it through 04
  (own txn) has the nested-txn concern; left for a follow-up (note in Phase 6 / spec-11 backlog).
- **Tests:** new `AcceptWaitlistOfferHandlerTests` (4 unit: hash-matching submit under `offer.OperationId`
  + identity binding + claim; already-Accepted re-submit-no-reclaim; terminal→409; unknown→404). 643 unit
  green. Two integration tests touched: `Accept_FinalizesHold_ConfirmsBooking_AndIsIdempotent` marked
  `[Fact(Skip=...)]` (asserts obsolete flow → Phase-6 rewrite); `Expire_OnAlreadyResolvedOffer_SettlesNoop`
  now sets the offer Accepted directly (via `TryResolveAsync`) instead of driving the mediator-dependent
  accept, keeping the expiry-noop assertion alive. `WaitlistFlowTests.BuildAccept` updated to the new
  ctor with `null!` IMediator (only the skipped test calls it; never dereferenced) — full 04→06 wiring is
  Phase-6 work. Scoped format clean.

---

## Phase 5: Per-step saga timeouts + hosted recovery (#3)

### Phase 5: Overview
Schedule a `scheduled_jobs` reconcile timeout when the saga enters an in-flight step (the spec's
§5.3 mechanism), and host `PreorderRecoverySweeper` as a backstop `BackgroundService`.

### Phase 5: Changes Required

#### 1. Per-step timeout on step entry
**Files**: each saga step handler that advances into an in-flight step (Start→ReservingItems,
ReserveItems→CommittingBooking, BookingCommitted→CommittingPreorder), via a shared helper in
`PreorderSagaSupport.cs`.
- When advancing into a step, write a `scheduled_jobs` row in the **same ambient (inbox) txn** via
  `IScheduledJobRepository` (mirror `RetryScheduler`):
  - `JobType = "preorder.commands.reconcile.v1"`, `DestinationQueue = "preorder.commands"`,
    `NotBefore = now + window(step)` (per-step window; payment/booking windows longer than item
    reserve), payload `{saga_id, operation_id}`.
  - Deterministic `JobId = $"sched_preorder_{sagaId}_{state}"` so re-entry/duplicate delivery does
    not stack duplicate jobs.
- No explicit cancellation needed: when the timeout fires, `ReconcilePreorderSagaCommandHandler` is
  state-driven — if the saga already advanced, it resumes the *current* step or settles (idempotent).
- The existing `ScheduledJobPollerHostedService` already drains these jobs → publishes to
  `preorder.commands` → the reconcile consumer runs. No poller change required (verify
  `preorder.commands` resolves via `LogicalQueueRegistry.TryGetByWireName`).

#### 2. Host the recovery sweeper (backstop)
**New file**: `apps/api/src/Cordia.Infrastructure/Messaging/PreorderRecoverySweeperHostedService.cs`
- Mirror `StepSweeperHostedService`: `BackgroundService` + `PollingLoop.RunAsync`, an
  `Enabled` + interval + staleness option on `MessagingOptions`, a public `SweepOnceAsync(ct)` for
  tests that calls `PreorderRecoverySweeper.SweepAsync(now - staleness)`.
- Register in `Program.cs` / Infrastructure DI alongside the other hosted services.

### Phase 5: Success Criteria

#### Phase 5: Automated Verification (integration after Phase 6)
- [ ] Integration: a saga left in an in-flight step has a `scheduled_jobs` reconcile row; advancing
      a poller tick publishes `preorder.commands.reconcile.v1`; the reconcile consumer re-drives the
      stuck step (and no-ops for an already-advanced saga).
- [ ] Integration: `PreorderRecoverySweeperHostedService.SweepOnceAsync` enqueues a reconcile for a
      stale non-terminal saga and zero for terminal ones.
- [x] Unit: per-step timeout uses a deterministic job id (duplicate step entry → one job) —
      `PreorderStepTimeoutTests`.
- [ ] `bun nx test api` + `bun nx test-integration api` green.

#### Phase 5: Manual Verification
- [ ] With the hosted services enabled, a deliberately stalled saga recovers without manual action.

### Phase 5: Discoveries and Notable Information

**Implemented (2026-07-01):**
- **Per-step timeout helper** — `PreorderSagaSupport.ScheduleStepTimeoutAsync(jobs, tenant, saga, op,
  state, now, ct)` + `TimeoutWindow(state)`. Writes a `preorder.commands.reconcile.v1` `ScheduledJob`
  (entity field is `RunAfter`, not `NotBefore` as the plan wrote) due at `now + window`, on the
  `preorder.commands` lane, with deterministic `JobId = sched_preorder_{sagaId}_{state}` and
  `ExistsAsync` short-circuit so a duplicate step entry / re-drive never stacks a second timer. Runs in
  the ambient inbox txn (no own txn). Windows: ReservingItems 2 min, **CommittingBooking 10 min**
  (longest — waits on the whole 06 engine), CommittingPreorder 2 min.
- **Wired into the 3 in-flight step entries** (all took a new `IScheduledJobRepository jobs` ctor param):
  `StartPreorderSagaCommandHandler` (→ReservingItems), `ReservePreorderItemsCommandHandler`
  (→CommittingBooking, the important one — waits on the async 06 signal),
  `PreorderBookingCommittedCommandHandler` (→CommittingPreorder). No cancellation on advance: reconcile
  is state-driven/idempotent, so a timer that fires after the saga advanced re-drives the CURRENT step
  or settles.
- **Reconcile wiring already present:** `PreorderSagaMessages.Reconcile` is registered
  (`ApplicationServiceCollectionExtensions:236` → `ReconcilePreorderSagaCommand`), and the
  `preorder.commands` wire name resolves in `LogicalQueueRegistry` — so the existing
  `ScheduledJobPollerHostedService` drains the timeout job → publishes to `preorder.commands` → reconcile
  consumer runs. **No poller change needed** (verified, per plan).
- **Hosted recovery backstop** — `PreorderRecoverySweeper` already existed; bridged to Infrastructure via
  a new domain seam `IPreorderRecoverySweeper` (mirrors `INotificationReconciler` — Infrastructure can't
  reference Application). New `PreorderRecoverySweeperHostedService` (`BackgroundService` + `PollingLoop`,
  public `SweepOnceAsync` for tests) resolves the seam per tick and calls
  `SweepAsync(now - StalenessSeconds)`. New `PreorderRecoveryOptions` (Enabled / IntervalMs 30 s /
  **StalenessSeconds 900 s** — longer than the longest per-step window so the primary timer fires first).
  Registered in `AddMessaging`; disabled in `appsettings.Test.json` (`Messaging:PreorderRecovery:Enabled
  =false`) like the other sweepers so Alba integration tests are unaffected.
- **No sweeper transaction needed:** `OutboxRepository.AddAsync` calls `SaveChangesAsync` internally, so
  each reconcile enqueue auto-commits; the sweeper needed no `IUnitOfWork` and its 2-arg ctor is
  unchanged (the existing integration test's direct `new PreorderRecoverySweeper(...)` still compiles).
- **Tests:** new `PreorderStepTimeoutTests` (3 unit: deterministic id + lane + window + payload;
  duplicate entry → no second timer; CommittingBooking window is longest). Updated the two changed unit
  tests' ctors (`ReservePreorderItemsHandlerTests`, `PreorderBookingSignalHandlerTests`) with a mock
  `IScheduledJobRepository`, and 4 Preorder **integration** tests' hand-built handlers with a real
  `new ScheduledJobRepository(ctx)` (compile-only; their behavioral rewrite stays Phase 6). 646 unit
  green; scoped format clean.
- **Deferred to Phase 6:** integration coverage for "in-flight step → scheduled_jobs reconcile row →
  poller publishes → reconcile re-drives" and "`SweepOnceAsync` enqueues for stale, zero for terminal".

---

## Phase 6: Single consolidated migration + full regression

### Phase 6: Overview
Generate the **one** EF migration for all schema changes introduced in phases 2–4, then run the full
suites.

### Phase 6: Changes Required
- `bun nx run api:... dotnet ef migrations add PreorderBookingDelegationAndWaitlistRevision` (one
  migration) covering:
  - `booking_operations.is_preorder` (bool, default false) — Phase 2.
  - `waitlist_enrollments.canonical_payload` (jsonb), `identity_ref`, `confirmation_channel`,
    verification context — Phase 4.
  - (Optional) leave `preorder_sagas.slot_id` / `requested_capacity` in place (additive-only
    migration; no destructive drops).
- Verify the migration file is UTF-8 with BOM; `ApplicationDbContextModelSnapshot.cs` updated.
- **Folded in from Phase 3c (coupled to the integration-test rewrite):**
  - Rewrite the pre-order integration tests for the new booking-delegated flow (`PreorderSagaHappyPathTests`,
    `PreorderCompensationTests`, `PreorderRevalidationTests`, `PreorderRecoveryAndStandaloneTests`):
    Revalidating → ReservingItems → CommittingBooking(await 06) → CommittingPreorder → Completed;
    booking-failure → CompensatingItems → FailedFinal; the 06 happy/failure/ambiguous paths (Phase 3
    integration criteria).
  - **Then** delete the dead steps: `HoldCapacity/`, `AuthorizePayment/`, `CompensateHold/`,
    `CommitBooking/` handlers+commands; `PreorderSagaMessages.{HoldCapacity,AuthorizePayment,CompensateHold,CommitBooking}`
    consts + their `ConsumerRegistry` registrations; `IPreorderPaymentAuthorization` + `StubPreorderPaymentAuthorization`
    + DI; drop those entries from the `ReconcilePreorderSagaCommandHandler` resume map.
  - Rewire `CompensatePreorderBookingCommandHandler` → ManualReview only (drop payment/item ports;
    favour spec §21.4 — do not auto-cancel the committed booking). Update its unit/integration coverage.
  - Grep-verify no dangling references after deletion; `bun nx build api` clean.

### Phase 6: Success Criteria

#### Phase 6: Automated Verification
- [x] `bun nx build api` green (whole solution incl. integration tests compiles).
- [x] `bun nx test api` — all unit tests green (646).
- [x] `bun nx test-integration api` — RAN 2026-07-01 against Docker Postgres (fresh
      `Cordia_API_Test`, new migration applied): **269 passed, 2 skipped** (intentional standalone +
      obsolete-accept skips), **2 failed = ONLY the pre-existing `ScheduledJobPollerTests`
      µs-truncation flake** (sub-microsecond DateTimeOffset vs Postgres µs; file unmodified in git,
      unrelated to spec-11). Zero spec-11 (Preorder/Waitlist) failures.
- [x] `bun nx lint api` — scoped `dotnet format` clean on all spec-11-changed files (per user, the
      full-solution failures are pre-existing autocrlf CRLF in unrelated files and are out of scope).

#### Phase 6: Manual Verification
- [ ] `PostgresFixture` recreates + migrates `Cordia_API_Test` cleanly from scratch (one new
      migration applies).

### Phase 6: Discoveries and Notable Information

**Implemented (2026-07-01):**
- **Migration** `20260701102603_PreorderBookingDelegationAndWaitlistRevision` — three additive columns
  only: `booking_operations.is_preorder` (bool default false), `waitlist_enrollments.identity_ref`
  (text default ''), `waitlist_enrollments.verification_level` (text default 'unverified'). No
  destructive drops; unused saga columns/enum members left in place (additive + reversible). Snapshot
  updated. (Plan mentioned `canonical_payload`/`confirmation_channel` for waitlist, but 4a chose to
  assemble the booking at accept and map channel from `NotificationChannel`, so those columns don't
  exist — the migration matches the actual model.)
- **Dead code deleted:** `HoldCapacity/`, `AuthorizePayment/`, `CompensateHold/`, `CommitBooking/`
  handler+command dirs; `IPreorderPaymentAuthorization` + `StubPreorderPaymentAuthorization` + its DI;
  `PreorderSagaMessages.{HoldCapacity,AuthorizePayment,CompensateHold,CommitBooking}` consts + their
  `ConsumerRegistry` registrations; the dead entries in the reconcile resume map.
- **CompensateBooking rewired → ManualReview only:** dropped the payment/item ports (new 4-arg ctor);
  a post-commit failure now preserves the committed booking and parks in ManualReview (§21.4) instead
  of auto-voiding. **Discovery:** `CompensatingBooking`'s only producer was the deleted commit-booking
  step, so the state is now unreachable in the normal flow — the handler is retained as a defensive
  backstop (still wired via the resume map).
- **Reconcile `CommittingBooking`:** the saga waits on the 06 engine there, so reconcile now re-nudges
  06 (`booking.command.requested.v1`, idempotent) instead of re-driving a deleted saga step. Dead
  resume entries (HoldingCapacity/AuthorizingPayment/CompensatingHold) removed.
- **Integration tests rewritten for the new flow** (Start→ReserveItems→[06 signal]→CommitPreorder):
  `PreorderSagaHappyPathTests` (happy path via simulated `booking_committed` + idempotent commit),
  `PreorderCompensationTests` (items-unavailable → CompensatingItems→FailedFinal; 06 `booking_failed`
  → CompensatingItems→FailedFinal; defensive CompensateBooking→ManualReview seeded directly; reconcile
  re-nudges 06), `PreorderRevalidationTests` (Revalidating→ReservingItems assertions), and
  `PreorderRecoveryAndStandaloneTests` (sweep→reconcile→06 re-nudge). **Standalone (§21.4) test SKIPPED**
  with a note: the order-only path isn't redesigned for the booking-delegated model (ReserveItems always
  hands to 06) and standalone is never produced today (SubmitConfirmedIntent sets `standalone=false`).
- **Verification:** `bun nx build api` clean (whole solution incl. integration tests compiles); `bun nx
  test api` unit suite green (646, +0 net — the state-machine/enum members and their unit test are the
  retained-but-unwired legacy edges); scoped `dotnet format` clean on every changed file.

> **⚠️ Two Phase-6 gates could NOT be completed in this environment and are handed to the user:**
> 1. `bun nx test-integration api` — **no local Postgres** (port 5432 refused). All 4 rewritten
>    pre-order integration files + the waitlist Phase-4/5 integration coverage compile but were not
>    executed. **Action:** run `bun nx test-integration api` locally (DB up) to validate the new flow
>    end-to-end and the fresh migration applying to `Cordia_API_Test`.
> 2. `bun nx lint api` (full-solution `dotnet format --verify-no-changes`) — **fails on pre-existing
>    autocrlf CRLF** in files unrelated to spec-11 (e.g. `RetentionPolicyTests.cs`, unmodified in git,
>    checked out CRLF). Every spec-11-changed file passes the **scoped** format gate. **Action:** either
>    normalize line endings repo-wide (add `.gitattributes` / `git add --renormalize`) or run lint after
>    a fresh LF checkout.

---

## Testing Strategy

### Unit Tests
- State machine edges (Phase 1); 04 routing + 06 suppression with mocked outbox (Phase 2); saga
  signal transitions (Phase 3); accept request construction + hash match (Phase 4); deterministic
  timeout job id (Phase 5).

### Integration Tests (Alba host + `PostgresFixture`, after Phase 6)
- Pre-order happy path / booking-failure / ambiguous (Phase 3).
- Waitlist accept → confirmed revision + finalize; dead-hold re-offer; duplicate-accept replay (Phase 4).
- Per-step timeout fires reconcile; sweeper tick enqueues reconcile (Phase 5).

### Manual Testing Steps
1. `bun run dev:log`; submit a confirmed pre-order intent; confirm exactly one combined outcome and a
   real (stub) booking on the operation.
2. Enroll + accept a waitlist offer; confirm a confirmed revision/operation exists and the hold
   finalized.
3. Stall a saga (kill a step mid-flight); confirm the timeout/sweeper recovers it.

## Performance Considerations
- Per-step timeout jobs are bounded (one per saga step, deterministic id); the existing poller batches
  them. The sweeper is a coarse backstop on an interval — keep its staleness window > the largest
  per-step window so they don't double-fire.

## Migration Notes
- Exactly one additive migration (Phase 6). No destructive drops; unused saga columns are left in
  place to keep the migration additive and reversible.

## References
- Spec: `docs/specs/11-waitlist-menu-preorder.md` (§5.1, §5.3, Inv-1, Inv-7, Inv-8; lines 61, 198–199,
  246–261, 265–273, 301, 315–318).
- Prior plan: `thoughts/shared/plans/2026-06-30_spec-11-preorder-saga.md`.
- Key code: `SubmitConfirmedIntentCommandHandler.cs:244`, `AdvanceBookingOperationCommandHandler.cs:78`,
  `FinalizeBookingCommandHandler.cs:530`, `ReconcileOperationCommandHandler.cs:186`,
  `AcceptWaitlistOfferCommandHandler.cs`, `PreorderRecoverySweeper.cs`,
  `StepSweeperHostedService.cs`, `ScheduledJobPollerHostedService.cs`, `RetryScheduler.cs`.
