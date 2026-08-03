# Cancel Fulfilment + `useMutation` Refactor — Implementation Plan

**Fixes the two caveats left open by** `thoughts/shared/plans/2026-07-27_frontend-api-wiring.md`:
1. A `cancel` operation is accepted but fulfilled by the **create** path — it calls
   `CreateBookingAsync` and never touches the booking's lifecycle.
2. The web write flows are hand-rolled `fetchApi` calls rather than `useMutation`
   (`docs/frontend-api-wiring.md` §6).

**Branch:** `feat/ui-wire`

## Current state (verified, not assumed)

`SubmitBookingCancellationCommandHandler` creates a **separate** operation
(`OperationType = Cancel`, `Status = Accepted`, `BookingId` = target) and flips the booking
row to `CancellationPending`, then emits the generic `booking.command.requested.v1`.

From there the operation is driven by `AdvanceBookingOperationCommandHandler`, which
dispatches **purely on `OperationStatus`** — it never reads `OperationType`. So a cancel runs
`Accepted → Validating → ReservingCapacity → Held → Finalizing → Booked`, reserving capacity
it does not need and calling `adapter.CreateBookingAsync` at the end. Net effect: a cancel
**creates a second booking** and leaves the original `CancellationPending` forever.

Verified by: grep for `OperationType` across `Handlers/BookingFulfillment/**` (one hit — a
comment in the SLA handler declining to branch); grep for `TargetBookingId` there (zero);
`CancellationPending → Cancelled` is written nowhere in `Cordia.Application`/`Infrastructure`.

## Key discovery: no new enum value, no migration

The earlier assumption that this needs an `OperationStatus.Cancelled` + pg-enum migration is
**wrong**. A cancel is its own operation, so:

- the **operation** terminates at `Booked` — the generic "this operation succeeded" terminal
  (D1 has no `cancelled` operation status, and D2's `NormalizedOperationStatus` has none either);
- the **booking row** carries the cancellation: `CancellationPending → Cancelled`
  (`BookingLifecycleStatus` already has all four values);
- `NotificationEvent.CancelOrModifyConfirmed` **already exists** as a ratified pg-enum value,
  with a `NotificationIdentity` entry and a template-resolver entry — only the emission is missing.

So **no DDL, no pg-enum change, no migration.** That keeps spec 12 untouched.

## How this maps onto Cordia

| Need | Existing artifact it reuses | Consequence |
|---|---|---|
| Cancel with the provider | `IBookingProviderAdapter.CancelBookingAsync(account, providerRef)` — already on the interface, already implemented by the stub and every test fake | no adapter contract change |
| Staged provider call | `CompensateOperationCommandHandler`'s shape: load → create/load `WorkflowStep` → **call provider outside the txn** → persist in one txn (design §12.6) | new handler mirrors a proven pattern |
| Success terminal | `Finalizing → Booked` — the only edge to `Booked` (spec 06 state machine) | cancel runs its provider call at `Finalizing`, like create |
| Ambiguous outcome | `Finalizing → Reconciling` (Inv-7) | ambiguity reconciles, never blind-retries |
| Booking lifecycle write | `IBookingRepository.TryTransitionLifecycleAsync` (version-guarded) | same optimistic-concurrency primitive as acceptance |
| Notification | `NotificationEvent.CancelOrModifyConfirmed` + the `registry.Register("notification.*.v1", …)` pattern | one new message type, no new enum |
| Idempotent replay | booking already `Cancelled` ⇒ succeed **without re-invoking the provider** (spec 08 §9.4.4) | matches the spec verbatim |
| Web writes | `@tanstack/react-query` `useMutation` (already a dependency) | no new package |

## Spec ownership & frozen decisions

| Contract touched | Owner | Status | Constraint |
|---|---|---|---|
| `operation_status` (16 values) | 06 | **referenced** | D1 — no new value; cancel terminates at `Booked` |
| `OperationStateMachine` edge set | 06 | **EXTENDED — one edge** | `Validating → Finalizing`, so a cancel skips capacity it does not need |
| Cancellation lifecycle `pending → cancelled` | 08 §5.3 | **implemented as specified** | incl. §9.4.4 "already cancelled ⇒ no provider re-invoke" |
| `BookingLifecycleStatus` | 12 (DDL) / 08 (lifecycle) | referenced | all four values already exist |
| `NotificationEvent` | 10 | referenced | `cancel_or_modify_confirmed` already ratified |
| Worker staged txn + §16.2 reconciliation ladder | 06 | referenced | provider call outside the txn; ambiguous ⇒ `Reconciling` |
| All DDL / pg-enums | 12 | **untouched** | **no migration** |
| `ICapacityReservation` | 07 | **see the open decision below** | |

Invariants in play: **Inv-5** (duplicate-delivery safe), **Inv-7** (ambiguous ⇒ reconcile),
**Inv-8** (booking state committed before notification), **Inv-13** (tenant scope),
**Inv-16** (only authoritative outcomes), **MB-Inv-6** (cancellation is a transition, never a delete).

## Open decision for the gate: capacity release on cancel

After a create finalizes, `FinalizeAgainstOperationAsync` flips the hold `Active → Finalized`
and moves the slot's `HeldCapacity → BookedCapacity`. `ReleaseByOperationAsync` only releases
**active** holds, so it is a no-op for a booked operation.

A cancel therefore frees **no capacity** unless new spec-07 surface is added: a way to release
a *finalized* hold and decrement `BookedCapacity`, plus a way to find the original operation
from the booking id. That is a real capacity-model addition owned by spec 07.

Both options are honest; they differ in scope. Presented at the gate.

## Blast radius

| Item | Changing? | Note |
|---|---|---|
| `AdvanceBookingOperationCommandHandler` | **YES** | one branch at `Finalizing` on operation type |
| `ValidateOperationCommandHandler` | **YES** | cancel ops route to `Finalizing`, not `ReservingCapacity` |
| `OperationStateMachine` | **YES** | one added edge `Validating → Finalizing` |
| `Handlers/BookingFulfillment/CancelBooking/**` | **NEW** | the cancel step |
| `ApplicationServiceCollectionExtensions` | **YES** | register `notification.cancel_confirmed.v1` |
| `FinalizeBookingCommandHandler` | **NO** | untouched — cancel gets its own handler |
| `ReserveCapacityCommandHandler`, `RequestPayment`, `Compensate`, `Reconcile` | **NO** | |
| `SubmitBookingCancellationCommandHandler` | **NO** | acceptance already correct |
| Any migration / pg-enum / DDL | **NO** | |
| `apps/web` write flows (`ConsolePage`, `CreateBookingDrawer`) | **YES** | moved onto `useMutation` |
| `apps/web` read hooks, adapters, screens | **NO** | untouched |
| `apps/langgraph` | **NO** | |

## What we're NOT doing

- **No modify/reschedule fulfilment.** That needs "preserve the original reservation until the
  replacement is secured" (MB-Inv-5) — a separate vertical. `RESCHEDULE_FULFILMENT_SUPPORTED`
  stays `false`.
- **No new `OperationStatus`, no pg-enum, no migration.**
- **No refund/payment compensation on cancel** (spec 09, deferred).
- **No booking-lifecycle state machine.** None exists; adding one is spec-08 work and not
  required to make cancel correct.
- **No change to the acceptance path** — it is already correct.

---

## Phase 1 — Cancel fulfilment (API)

1. **`OperationStateMachine`**: add the `Validating → Finalizing` edge, with a comment that it
   exists for operations that reserve no capacity (cancel).
2. **`ValidateOperationCommandHandler`**: after the policy gate, route
   `OperationTypeEnum.Cancel` to `Finalizing` (reason `"validated_cancel"`) instead of
   `ReservingCapacity`. Create/modify paths unchanged.
3. **`AdvanceBookingOperationCommandHandler`**: at `OperationStatus.Finalizing`, dispatch
   `CancelBookingCommand` when `op.OperationType == Cancel`, else `FinalizeBookingCommand`.
4. **`CancelBookingCommandHandler`** (new), mirroring `CompensateOperationCommandHandler`:
   - guards: tenant match, `Status == Finalizing`, `OperationType == Cancel`;
   - idempotent `WorkflowStep` of type `"cancel"`;
   - resolve the target booking via `op.BookingId`; **already `Cancelled` ⇒ succeed without
     calling the provider** (spec 08 §9.4.4);
   - `adapter.CancelBookingAsync(ProviderAccount, booking.ProviderReference)` **outside** the txn;
   - success (`Created`/`AlreadyExists`/`NotFound`) ⇒ one txn: step → `Succeeded`; booking
     `→ Cancelled` version-guarded; operation `Finalizing → Booked` (reason `"cancelled"`);
     outbox `notification.cancel_confirmed.v1`; audit `operation.cancelled`;
   - no provider reference ⇒ same success path, reason `"cancelled_no_provider_effect"`;
   - `Ambiguous`/`Transient`/`PermanentError`/`AuthError` ⇒ `Finalizing → Reconciling`
     (Inv-7), return `Wait`;
   - booking row missing ⇒ `PoisonMessageException` (a cancel with no target is a defect).
5. **Register** `notification.cancel_confirmed.v1` → `NotificationEvent.CancelOrModifyConfirmed`.

**Automated verification**
- [x] `bun nx test api` (1141 passed) · `bun nx lint api` · `bun nx build api`
- [x] `bun nx test-integration api` (402 passed, 0 failed — up from 397)

**Manual verification**
- [x] A cancel submitted from the console drives to `booked` **and** leaves the booking
      `cancelled`, with no second booking created — asserted in `CancelFulfilmentTests`,
      which counts the tenant's booking rows after the cancel settles
- [x] Regression: a create still finalizes through `ReservingCapacity → Held → Finalizing`
      (`create still runs the capacity path`, plus the 402-test integration suite)

### Discoveries and Notable Information

- **`JobEnvelope` is ambiguous** between `Cordia.Domain.Generated` and
  `Cordia.Domain.Messaging`. The handler cannot carry a bare `using Cordia.Domain.Generated;`;
  `OperationTypeEnum` is fully qualified as `Domain.Generated.OperationTypeEnum.Cancel`
  instead. Existing fulfilment handlers do the same thing.
- **A "lost race" must write nothing.** When the version-guarded `Finalizing → Booked`
  transition returns `rows == 0`, another worker already committed the cancellation; the
  handler returns immediately rather than continuing to the booking update, capacity release,
  outbox and audit writes. Doing them anyway would double-release capacity and emit a second
  notification. `CancelBookingTests` pins this.
- **Two test doubles implement `ICapacityReservation` by hand** — `NoCapacityReservation`
  (`RetryAndFailureTests.cs`) and `CapstoneNoCapacityReservation`
  (`StateMachineCapstoneTests.cs`). Adding a member to the interface breaks them at compile
  time; both were updated.
- Guard order matters: the tenant / `Finalizing` / `Cancel` checks return `Wait`, but a
  missing `BookingId` or a missing booking row is a **defect**, not a race, so it throws
  `PoisonMessageException` rather than looping the queue forever.

---

## Phase 2 — Capacity release on cancel (selected at the gate)

Selected. Adds the spec-07 surface to release a finalized hold and decrement
`BookedCapacity`, plus the lookup from booking → original operation.

**Automated verification**
- [x] `bun nx test api` · `bun nx test-integration api` — the integration test asserts the
      slot's `booked_capacity` goes 2 → 0 and the hold flips to `Released`

### Discoveries and Notable Information

- **The cancel operation holds no capacity of its own** — it never passes through
  `ReservingCapacity`. The hold to release belongs to the *originating* booked operation, so
  `IBookingOperationRepository.GetBookedOperationForBookingAsync` finds it: status `Booked`,
  type in {Create, Reschedule, Modify}, most recently updated first.
- **Release decrements `BookedCapacity`, not `HeldCapacity`.** A finalized hold has already
  moved from held to booked; decrementing the wrong counter would leave the slot permanently
  oversubscribed. `FirstPartyCapacityReservation.ReleaseFinalizedByOperationAsync` uses a
  guarded `ExecuteUpdateAsync` (`HoldStatus.Finalized → Released`) so a redelivery releases
  at most once — verified by the "redelivery doesn't double-release" integration test.
- A booking with no reserved capacity (e.g. one created before the capacity path existed)
  releases nothing and still cancels cleanly.

---

## Phase 3 — Truthful cancel messaging (web)

`normalizedStatusLabel` renders `booked` as "Confirmed" — correct for a create, wrong for a
cancel, where the same terminal means "cancelled". Make the label take the operation type so
a cancel reports "Cancelled", and mark the booking row cancelled from the server's response
rather than from a local guess.

**Automated verification**
- [x] `bun nx test web` (191 passed) · `bun nx lint web` · `bun nx typecheck web`

### Discoveries and Notable Information

- **D1 defines no `cancelled` operation status.** A successful cancel terminates at `booked`
  — the generic "this operation succeeded" terminal — with the *booking* carrying the
  cancellation. Reusing the create copy would have told an operator their cancellation was
  "Confirmed", the opposite of what happened. `CANCEL_STATUS_COPY` overrides only the
  statuses that differ and falls back to the create wording for the rest, so a status neither
  map covers still renders as words rather than a raw enum.

---

## Phase 4 — `useMutation` for the write flows (web)

Move the three hand-rolled write paths onto `useMutation` so the configured mutation defaults
apply and loading/error/reset state stops being reimplemented per drawer:

1. **`useCreateBooking`** — `mutationFn` builds + submits; the caller keeps polling via
   `useOperationStatus`.
2. **`useCancelBooking`** — `mutationFn` reads the authoritative version, submits, polls to
   terminal; `onSettled` invalidates the booking + operation query keys in one place.
3. **`useRemoveFromWaitlist`** — the textbook optimistic case: `onMutate` cancels in-flight
   refetches and snapshots, `onError` rolls back from the snapshot, `onSettled` invalidates.
   This replaces the hand-rolled optimistic-remove-then-restore currently in `ConsolePage`.

Mutations stay `retry: false` (a write must not be silently re-sent).

**Automated verification**
- [x] `bun nx test web` (191 passed) · `bun nx lint web` (0 warnings, 0 errors) ·
      `bun nx typecheck web` · `bun nx build web`

**Manual verification**
- [x] Cancel reports the server's terminal outcome, phrased for a cancellation; waitlist
      removal restores the snapshot on failure (`onError` → `applyRows(context.snapshot)`)

### Discoveries and Notable Information

- **The submit→poll sequence belongs *inside* `mutationFn`, not around it.** That was the
  original objection to the hook, and it turned out to be the wrong shape: putting the whole
  sequence in `mutationFn` makes `isPending` cover the operation's real duration and moves
  invalidation into a single `onSettled`. The hook models it fine — it just should not be
  scoped to the POST.
- **Create is the deliberate exception**: it returns at the 202 so the drawer can watch
  intermediate states with `useOperationStatus`. Folding the poll in would hide exactly the
  states the operator is meant to see.
- **Hook ordering bites.** `useRemoveFromWaitlist(setWaitlist)` must be declared *after* the
  `waitlist` useState it closes over; placing it where the old function lived referenced the
  setter before initialisation.
- **Slice-based Python edits over a large TSX file are fragile.** A multi-edit
  `src.index()`-anchored script silently duplicated ~230 lines of `ConsolePage.tsx`. Asserting
  line boundaries before splicing — or editing via the Edit tool — is the reliable route.

---

## Review pass (two independent reviewers over the diff)

Both reviewers confirmed the structural claims — provider call outside the transaction, no
`CreateBookingAsync` on the cancel path, tenant scoping intact, capacity release idempotent on
the right counter, `ConsolePage.tsx` free of the earlier duplication. Eight real defects were
found and fixed:

**API**

1. **(HIGH) The booking transition's version guard was discarded.**
   `TryTransitionLifecycleAsync` returns rows-affected, and `0` means a concurrent writer moved
   the row. The result was ignored, so a version race would release the seat and emit
   `notification.cancel_confirmed.v1` for a booking that never became `cancelled` — with the
   operation already terminal, nothing would re-drive it. Now a bounded re-read-and-retry, and
   a hard `StaleBookingVersionException` that rolls the whole transaction back (including the
   operation's move to `Booked`) so the message redelivers. Provider cancellation is idempotent,
   so re-running is safe.
2. **The unit suite could not have caught it.** `ArrangeHappyPath` never stubbed
   `TryTransitionLifecycleAsync`, so Moq returned `0` — every happy-path test was asserting
   against a booking that never transitioned. Stubbed explicitly, plus two new tests: one
   pinning the roll-back, one pinning that losing the race to a writer that reached the *same*
   end state settles normally rather than retrying.

**Web**

3. **(HIGH) Optimistic rollback reached beyond its own row.** `onError` restored the entire
   pre-removal snapshot, so an overlapping removal that had already succeeded was resurrected —
   showing a guest as still waiting after the server confirmed they were gone. Now re-inserts
   only its own row, at its original index, and only if it is not already back. The regression
   test was verified to fail against the old implementation before the fix was restored.
4. **(HIGH) The reschedule idempotency key was not payload-scoped.** `modify:${id}` plus a
   fire-and-forget call with no in-flight guard meant two drags of the same booking could bind
   one key to two different canonical payloads (Inv-3). Key is now
   `modify:${id}|${startsAt}|${dur}`, and a `rescheduleInFlight` ref rejects the overlap.
5. Create's conflict handling was narrower than cancel's — only the typed idempotency body
   freed the attempt key, so a bare `409` left it bound forever. Both now use
   `isRetryableWithNewAttempt`.
6. A cancel accepted with no `operation_id` returned `settled: true` with a `null` status,
   which rendered as "Cancelling" on a finished mutation. It now claims nothing.
7. `closeDrawer()` fired on every success, including a settled *refusal* (`rejected`/`failed`)
   and an unfinished poll. A new `cancelled` flag on `CancelBookingResult` distinguishes
   "terminal" from "terminal **and** successful"; the drawer only closes on the latter.
8. `CANCEL_STATUS_COPY` was missing `alternatives_required`/`waitlisted`, and the fallback
   silently borrowed the create copy — an operator cancelling a booking could have been shown
   "That time isn't available". Both now have explicit cancel wording.
9. `pollToTerminal`'s `signal` was dead at both call sites: navigating away left the loop
   firing for the rest of the 60s budget. Both callers now pass an `AbortController` aborted on
   unmount, and the abort clears the pending timer instead of merely skipping the next fetch.

Not fixed, noted only: `ReleaseFinalizedByOperationAsync` has no defence-in-depth catch for the
`ck_capacity_slots_nonneg` check constraint, unlike `ReserveAsync`'s overcommit handler. It is
unreachable unless the idempotency guards above are already violated.

---

## Conformance pass against `docs/frontend-api-wiring.md`

A third review audited the whole implementation against the document's stated rules. Fifteen
defects were confirmed and fixed. Grouped by what they had in common:

**The `Booked`-means-two-things trap (four defects).** D1 gives a cancel no terminal of its
own, so it settles at `OperationStatus.Booked`. Four places read `Booked` as "a booking was
made": both read models' `ToLifecycleStatus` (a cancelled booking read back as *confirmed*),
the funnel's confirmed count (cancelling *raised* it), and — inverted — the web adapter, which
tested lifecycle `cancelled` before the failure statuses, so a hard-failed booking rendered as
a calm "Cancelled". Each now tests the operation type first.

**Cancel-shaped data in create-shaped code (three).** A cancellation's canonical payload has
no `starts_at`, so its row claimed the booking's dedupe slot in `ListScheduleAsync` and was
then dropped by the window filter — the booking vanished from the grid the moment a cancel was
*submitted*, while the guest still held the table. `ReconcileOperationCommandHandler` reads the
`finalize` step a cancel never writes, and its lookups prove a booking *exists*, which says
nothing about whether a cancellation landed; an ambiguous cancel could never resolve, never
exhaust its budget, and its not-proven path would have *recreated* the booking. Cancels now
escalate to manual review. And the already-cancelled short-circuit still queued
`notification.cancel_confirmed.v1`, so a duplicate cancel re-notified the guest.

**The client-only guard (one).** Modify/reschedule fell through to `FinalizeBookingCommand`,
which creates unconditionally. The only protection was `RESCHEDULE_FULFILMENT_SUPPORTED` in the
web app — the HTTP endpoints and the LangGraph lane were unguarded. Now a
`PoisonMessageException` at the driver, which dead-letters into the operator work-item queue.

**Silent dishonesty in the UI (four).** None of the console's five queries read `isError`, so
an API outage rendered as a confident empty week; `CustomersScreen` substituted six fabricated
customers — names, phone numbers, lifetime spend — with no badge; `AnalyticsScreen` badged a
*failed* load as "no activity in this window", stating as fact something it could not know;
and the booking drawer asserted a channel (`b.channel || 'phone'` turned a deliberate `null`
into "Booked via Phone") and a deposit the API never records.

**canon.v1 parity (two).** Verified by running the real .NET encoder rather than reasoning
about it — the same discipline that caught the uppercase-escape trap originally.
`System.Text.Json` writes `\b \t \n \f \r` as short escapes, and `DateTimeOffset.TryParse`
both tolerates surrounding whitespace and rejects offsets past +/-14:00. Eight new corpus
vectors (82 total), all three implementations agreeing.

**Housekeeping (one each).** `ConsentState.NotRequired` shipped as `"notrequired"`, not the
declared `"not_required"`; a shared `EnumWireNames.ToWireName` now reads the generated
attribute. Truncation reported a limit-capped page as complete, and `Skip`/`Take` paged over a
non-unique `ORDER BY UpdatedAt`. `vite-env.d.ts` ended with `export {}`, making its
`ImportMetaEnv` declarations module-local and therefore inert.

### The trap worth remembering

**`OperationTypeEnum.Cancel` is the ZERO value** of the generated enum (quicktype orders
members alphabetically), so an unset `OperationType` reads as a cancellation. Six existing
reconciler tests built operations without setting it and began failing the moment the handler
branched on type. Production sets it explicitly at all three creation sites, so this was a test
artifact — but every future `== Cancel` check inherits the same hazard.

---

## Cross-cutting requirements

- **Queue safety (Inv-5):** the cancel step is duplicate-safe — the `WorkflowStep` is
  create-or-load, every transition is version-guarded, and an already-`Cancelled` booking
  short-circuits without a second provider call.
- **Inv-7:** ambiguous provider outcomes go to `Reconciling`, never a blind retry.
- **Inv-8:** the booking transition and the notification outbox row commit in the **same**
  transaction, so the notification can never precede the state.
- **Inv-13:** every repository call is tenant-scoped; the handler compares `op.TenantId`.
- **Inv-16:** the console reports the server's terminal status, phrased by operation type.
- **No migration**; new `.cs` files are UTF-8 **with BOM**; one `bun nx` at a time.
