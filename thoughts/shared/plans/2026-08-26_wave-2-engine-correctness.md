---
date: 2026-08-26
planner: Claude (spec-loop Stage 2)
branch: fix/wave-2-engine-correctness
base: main @ aface13
repository: Cordia
topic: "Wave 2 — engine correctness: #51, #55, #59, #57, #56, #58"
tags: [plan, apps/api, booking-fulfillment, finalize, reconcile, compensate, inv-6, inv-7, inv-8, inv-16, spec-06, spec-07]
status: awaiting-approval
issues: [51, 55, 59, 57, 56, 58]
research:
  - thoughts/shared/research/2026-08-26_wave-2-engine-correctness.md
  - thoughts/shared/research/2026-08-26_wave-2-engine-correctness-baseline.md
---

# Plan — Wave 2: engine correctness

Seven phases across six issues, each independently shippable and independently revertable.
Ordering is **not** the roadmap's, and the reason is recorded in
[Why this order](#why-this-order-and-not-the-roadmaps).

---

## What research changed before a line was written

| Issue | The plan does NOT do what the issue asks, because |
|---|---|
| **#59 part 2** | **Its premise is false.** `POST /api/availability/slots` already shipped (`AvailabilityController.cs:70`, commit `970ca4c`) — owner-gated and idempotent. Closed as done, not built. |
| **#59 part 1** | **The filed remedy throws.** `Reconciling → CompensationRequired` is absent from `AllowedTransitions[Reconciling]` (`OperationStateMachine.cs:73-79`); `TryTransitionStatusAsync` calls `EnsureAllowed` first (`BookingOperationRepository.cs:71`), and the driver catches `InvalidStateTransitionException` as poison (`AdvanceBookingOperationCommandHandler.cs:94-113`). As filed it **dead-letters the operation instead of compensating it.** Routes to `ManualReview` instead (legal, verified in the table). |
| **#51** | **Both filed remedies are no-ops.** `IWorkflowAttemptRepository.UpdateAsync` returns `Task` (void), so a version bump the racing worker loses is unobservable to it — it calls `CreateBookingAsync` anyway. A `FOR UPDATE` releases at commit, and there is no row to lock in the `latest is null` shape. Replaced by a two-part fix (51a, 51b). |
| **#57** | **The stated trigger is mechanically impossible.** `SetCancellationRequestedAsync` (`BookingOperationRepository.cs:110-124`) never bumps the version; the only version writer always changes `Status`, which would then fail the shortcut's own `fresh.Status == Finalizing` gate (`:119`). The defect is real via a different route, and hides an **Inv-8 violation** the issue never mentions. |
| **#56** | Misdescribed in **both** directions: the branch self-exhausts (no prior exhaustion needed), and "forever" overstates it (one circuit probe per 30 s window escapes). |
| **#58** | The described harm is the opposite of the real one. **Nothing is ever compensated** — the ref is always null on that path, so it terminalises to `FailedFinal / failed_no_effect` plus a customer "booking failed" notification, in a single driver tick. |

Two defects nobody filed are folded in rather than filed separately, because both sit inside
lines these fixes already edit:

- **U1** — reconcile and manual-review reach `Booked` **without a `bookings` row**.
  `MaterializeBookedAsync` has exactly two call sites, both in Finalize (`:175`, `:540`).
  Seven consumers then fail, including `GetBookingDetailsQuery.cs:43`,
  `SubmitBookingCancellationCommandHandler.cs:41` and `SubmitBookingModificationCommandHandler.cs:41`.
  Those bookings are invisible to the console. → **Phase 3**
- **U2** — `Finalize:432` commits `attempt.ProviderRef = result.ProviderReference` *before* the
  `IsNullOrWhiteSpace` guard at `:530-531`, and `ProviderReference` is `string?`. An empty string
  therefore passes the shortcut's `is not null` gate at `:115` on the next drive. This is #57's
  deterministic, race-free reachability route, not a separate ticket. → **Phase 4**

---

## How this maps onto Cordia

Governing directive: *integrate with existing mechanisms (single front doors), never add parallel
implementations.* Every row below extends something that already exists.

| New concept | Existing Cordia artifact it extends | Consequence |
|---|---|---|
| "Is this attempt's effect unresolved?" | `AttemptOutcome` on `workflow_attempts`, already written by Finalize | `CompensateOperationCommandHandler` gains its **first** read of it — `grep AttemptOutcome` on that file returns nothing today |
| "No effect is *proven*" | `ProviderResultKind.NotFound` + `RetryClassifier.cs:34` (already says a `NotFound` cannot prove a create did not take effect) | Reconcile stops making its own kind decision at `:131-132` and adopts the classifier's meaning |
| Reconcile completing a `Booked` outcome | `FirstPartyCapacityReservation.FinalizeAgainstOperationAsync` + `BookingRepository.MaterializeBookedAsync` — both already idempotent, both already the single decision point | Reconcile **calls** them; neither changes |
| A shared definition of the `Booked` write-set | `FinalizeBookingCommandHandler.BuildBookingAggregate` (`internal static`, `:844-890`) | **Relocated, never duplicated** — a second copy would put two copies of the `CustomerId` precedence chain in the tree, the exact failure its own docstring warns about |
| Fencing a dispatch against a concurrent cancel | `VersionTokenUpdate` / `TryTransitionStatusAsync` conditional-write pattern (§22.1), ~20 existing call sites | A new conditional write on `booking_operations`, in the established shape |
| Escalating an unresolvable state | `ManualReviewEscalator.EscalateAsync` — the only sanctioned entry into `ManualReview` | Called, not reimplemented |

### Verified legal, not assumed

The single most expensive mistake available in this wave is proposing an illegal transition — it is
what #59's filed remedy does. Checked against `OperationStateMachine.cs:55-95` before writing:

| Transition | Legal? | Used by |
|---|---|---|
| `CompensationRequired → ManualReview` | ✅ yes (`:80-84`) | Phase 1 |
| `Reconciling → ManualReview` | ✅ yes (`:73-79`) | Phase 3 |
| `Reconciling → CompensationRequired` | ❌ **no** | **nothing — this is the trap** |

---

## Spec ownership & frozen decisions

| Contract / mechanism | Owner | This wave |
|---|---|---|
| `OperationStatus` + `AllowedTransitions` | **spec 06 §11** | **Referenced — must stay byte-identical.** Q1 was decided as "route to `ManualReview`" precisely so no edge is added. |
| §16.2 reconciliation ladder | spec 06 | Referenced; Phase 2 corrects the handler's reading of it, not the ladder |
| Capacity holds / `FinalizeAgainstOperationAsync`, lock ordering | **spec 07 §14** | Referenced. Phase 3's new call site must satisfy the normative ordering (holds before slots, ascending ids, whole set before the first write) |
| All DDL / pg-enums | **spec 12** | **Untouched — no migration in this wave** (see Package & migration notes) |
| Retry budgets / FM#10 / FM#11 | spec 06 §15.4 | Referenced; Phase 6 stops discarding the FM#11 signal |
| `NormalizedOperationStatus` (Pydantic → C#/TS) | generated | **Untouched.** `OperationStatus` is hand-written (`Cordia.Domain/Enums/OperationStatus.cs`); every status used here already collapses to `in_progress` or a terminal |

**Release-blocking invariants in play:** Inv-6 (capacity atomic & separate from idempotency),
Inv-7 (ambiguous → reconcile, never blind retry), Inv-8 (outcome outbox in the same txn as the
state change), Inv-16 (only authoritative outcomes reach the customer).

---

## Why this order, and not the roadmap's

The roadmap assumed `55/59 → 56/58 → 51/57`. That is close to **inverted on urgency**:

- **`55/59` cannot be a parallel pair.** They are two halves of one method
  (`ReconcileOperationCommandHandler.Handle`, `:131-132`/`:269-343` vs `:161-264`) plus a shared
  constructor change. They will conflict textually and semantically.
- **`51` last is wrong.** It contains the **only defect in the wave reachable with the shipped
  adapter**. #55/#56/#57/#58 all require a provider returning `Transient`/`Ambiguous`/`NotFound`-from-create/blank-ref,
  and `StubBookingProviderAdapter` produces none of those. #51 needs only concurrency plus a cancel
  request — and three hosted services dispatch to the same in-process driver with no per-operation lease.
- **`57` last is wrong.** It is the highest customer-visible severity in the set: it confirms a
  booking the guest asked to cancel, and notifies nobody.
- **`56/58` in the middle is wrong.** They are the two *lowest*-severity items, both latent.

**Corrected: `51a → 55 → 59p1+U1 → 57+U2 → 51b → 56 → 58`.**

---

## Phase 1 — #51a: Compensate must not declare "no effect" over an unresolved attempt

**Files:** `apps/api/src/Cordia.Application/Handlers/BookingFulfillment/Compensate/CompensateOperationCommandHandler.cs`

**The change.** Today the handler reads `latest?.ProviderRef` (`:64-65`) and nothing else. When it is
`null` it takes branch 4b: `CompensationRequired → FailedFinal` reason `failed_no_effect` (`:238-241`),
releases the first-party hold, reverts the pending booking lifecycle, and emits
`notification.booking_failed.v1` (`:258-273`). It never inspects `AttemptOutcome`, so an attempt in
`Planned` / `Dispatching` / `Ambiguous` — whose effect is **unknown by definition** — is
indistinguishable from no attempt at all.

Add that read. When the latest `finalize` attempt is unresolved, escalate via
`ManualReviewEscalator.EscalateAsync` (`CompensationRequired → ManualReview`, legal) instead of
terminalising. `FailedFinal / failed_no_effect` stays reserved for a genuinely absent or
`NotFound`-confirmed effect.

**Why first.** Independent of every other phase, ~15 lines, deterministically unit-testable, the only
stub-reachable defect in the wave, and it pre-hardens #58's landing zone (Phase 7) — both #51's orphan
and #58's false failure terminate at this same branch.

### Success criteria

**Automated**
- [x] `bun nx test api` green — **1839 passed** (baseline 1834, +5)
- [x] `bun nx lint api` green
- [x] Mutation: disable the guard → **exactly 3** red (the two in-flight theory cases and the notification test); the not-in-flight and successful-cancel paths stay green

**Manual**
- [x] A `Dispatching` attempt under a `CompensationRequired` op reaches `ManualReview`, and **no** `booking_failed` notification is emitted and **no** hold is released

### Discoveries and Notable Information

1. **The plan's unresolved set was wrong, and using it would have caused a regression.** This plan
   said `Planned` / `Dispatching` / `Ambiguous`. `Planned` must **not** be in it. A `Planned`
   attempt has provably not been dispatched — the provider was never called — and FM#13 is built
   on exactly that distinction: `FinalizeBookingCommandHandler.cs:218-223` routes a
   cancel-before-create to `CompensationRequired` **only** when the attempt is `null` or `Planned`,
   and sends `Dispatching`/`Ambiguous` to the Inv-7 reconcile path instead. Its comment states the
   contract outright: *"The Compensate handler will see providerRef == null and transition straight
   to FailedFinal."* Guarding `Planned` would send every legitimate cancel-before-create to a human.
   The shipped set is `{Dispatching, Ambiguous}`, matching Finalize's own Inv-7 guard at `:196-197`,
   and a theory case pins `Planned` and `Rejected` on the fast path so the mistake cannot be
   reintroduced.

2. **Reaching this handler with an in-flight attempt *is* the #51 race, by construction.** Because
   FM#13 only routes `null`/`Planned` here, a `Dispatching` attempt in `CompensationRequired` can
   only mean the window at `Finalize:239-240` was lost — which is useful: the guard is not
   defensive programming, it is the detector for the exact race Phase 5 closes at source.

3. **My first version of the notification test could not fail.** It mocked only the `ManualReview`
   transition, so the `FailedFinal` CAS returned `default(int)` = 0 and every branch-4b write sat
   behind `if (rows > 0)` and never ran — the assertions held with the guard removed. Caught by the
   mutation run showing 2 red instead of 3. Fixed by mocking the `FailedFinal` CAS to succeed as
   well. Worth remembering for Phases 4-7: **in this handler family, a `Times.Never` assertion is
   vacuous unless the CAS it depends on is mocked to return 1.**

4. **The escalation reason is new but the mechanism is not.** `compensation_effect_unresolved`
   joins `compensation_ambiguous` on the existing `ManualReviewEscalator` path;
   `CompensationRequired → ManualReview` was verified legal in `OperationStateMachine.cs:80-84`
   before the code was written.

---

## Phase 2 — #55: prove "no effect" positively

**Files:** `.../Reconcile/ReconcileOperationCommandHandler.cs`

**The change.** `effectProven` (`:131-132`) is a *positive* test only, and **`NotFound` is never tested
anywhere in the file** — so `Transient`, `Ambiguous`, `AuthError`, `PermanentError`, *and a `Created`
result carrying a null reference* all fall through to the re-create path (`:269-343`):
`Reconciling → Held`, which the driver immediately walks back to `Finalizing` and a second
`CreateBookingAsync` **in the same dispatch**. The class docstring at `:26` claims the branch means
"all lookups NotFound"; the code never checks.

Write it as the whitelist — `noEffectProven = r.Kind == ProviderResultKind.NotFound` — which also
closes the `Created`-with-null-reference hole for free. Anything neither proven-created nor
proven-absent is **unresolved**: stay in `Reconciling` and return `StepOutcome.Wait` (Q2), letting
`StepSweeperHostedService` re-drive until the 10-minute `MaxElapsed` self-terminates it into the
existing budget-exhausted escalation at `:282-309`.

Also fix the discarded signal: `nextEligible` is computed at `:276-280` and **thrown away**, so
nothing is ever scheduled and the 4-attempt budget burns as fast as the driver loops.

**Why here.** Two later phases route into the definition of "unresolved". Provably test-neutral: every
lookup stub in the repo returns only `NotFound`/`AlreadyExists`, so no existing test changes meaning.

### Success criteria

**Automated**
- [x] `bun nx test api` green — **1845 passed** (+6)
- [x] `bun nx lint api` green
- [x] Mutation: remove the gate → **exactly 5** red (four kind cases + `Created`-with-null-ref); the `NotFound` re-create, `AlreadyExists` book, and budget-exhausted paths stay green
- [ ] `bun nx test-integration api` — deferred to the end-of-wave run

**Manual**
- [x] A `Transient` lookup leaves the op in `Reconciling`, writes nothing, and issues no second `CreateBookingAsync`

### Discoveries and Notable Information

1. **The change is exactly test-neutral, as predicted.** All 1839 pre-existing tests passed before
   a single new test was added — every lookup stub in the repo returns only `NotFound` or
   `AlreadyExists`, so no existing case changes meaning. That is worth stating because it is also
   the reason the bug survived: **the shipped adapter cannot produce the input that triggers it.**

2. **The plan's claim that `nextEligible` is "computed and discarded" was too strong.** It *is*
   used — `if (nextEligible == null)` drives the FM#11 escalation. What is unused is the
   `DateTimeOffset` it returns when the budget remains, because Q2 chose sweeper re-drive over a
   reconcile-side scheduler. Nothing to fix; the code now says so, so the next reader does not
   "restore" a scheduler that was never wanted.

3. **Gate placement matters more than the gate.** It sits *after* the budget probe, deliberately.
   Placed before it, an unresolved operation would return `Wait` forever with nothing to terminate
   it — trading a double-submit for a permanent park. After it, exhaustion still escalates to a
   human on the unresolved path, which `Handle_Unresolved_WithBudgetExhausted_StillEscalatesToAHuman`
   pins; that test stays green under the gate's mutation precisely because it never reaches the gate.

4. **The unresolved path must not abandon the in-flight attempt.** Marking it `Abandoned` is what
   unblocks Finalize's Inv-7 guard for a re-create, so doing it on this path would re-open the door
   the phase closes. Asserted with `_attemptRepo.Verify(UpdateAsync, Times.Never)`.

5. **Both destinations are mocked to succeed in the new tests.** Carried over from Phase 1's
   discovery 3: a `Times.Never` assertion here is vacuous unless the CAS it depends on returns 1,
   otherwise a wrong route silently no-ops and the test passes for the wrong reason.

---

## Phase 3 — #59p1 + U1: complete the reconcile `Booked` write-set

> **The riskiest phase in the wave.** Two commits, not one.

**Files:** `.../Reconcile/ReconcileOperationCommandHandler.cs`; a relocation of
`BuildBookingAggregate`/`ReadFirstString` out of `FinalizeBookingCommandHandler.cs`

**The problem, stated once.** There are **four writers of the `Booked` outcome and no shared
definition of what that outcome must contain**:

| Writer | capacity finalize | CAS | `bookings` row | outbox | audit | metrics |
|---|---|---|---|---|---|---|
| Finalize TXN2 (`:487-581`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Finalize recovery shortcut (`:136-178`) | ✅ | ✅ | ✅ | ❌ **#57** | ❌ **#57** | ❌ |
| Reconcile (`:161-264`) | ❌ **#59p1** | ✅ | ❌ **U1** | ✅ | ✅ | ✅ |
| ResolveManualReview (`:95-104`) | ❌ | ✅ | ❌ | — | — | — |

**Commit 3a — capacity.** Inject `ICapacityReservation`; call `FinalizeAgainstOperationAsync` inside
the effect-proven transaction. Without it the Active hold is later swept, decrementing
`HeldCapacity` (`FirstPartyCapacityReservation.cs:523`) while `BookedCapacity` is never incremented —
**slot reads free under a terminal `Booked` operation** (Inv-6). When the hold has already expired and
cannot be reclaimed, escalate to `ManualReview` (Q1 — legal; `Reconciling → CompensationRequired` is not).

**Commit 3b — materialisation.** Inject `IBookingRepository` **and `IBookingIntentRepository`** (the
latter because `BuildBookingAggregate` needs `canonicalPayload` and reconcile loads no intent revision
today), relocate the helper to a shared factory **content-preserving**, and call
`MaterializeBookedAsync`.

**Why here.** Same method as Phase 2, so serial regardless; must precede Phases 6 and 7, which each add
a new live route *into* `Reconciling`. Fix the destination before widening the funnel.

### Success criteria

**Automated**
- [x] `bun nx test api` green — **1851 passed** (+6 over Phase 2)
- [x] `bun nx test-integration api` green — **696 passed / 0 failed / 1 pre-existing skip**, including the two new reconcile-against-hold tests
- [x] `FinalizeBookingProvenanceTests` and `HardeningRegressionTests` pass with assertions unchanged, re-pointed at `BookingAggregateFactory`; the moved body verified byte-identical by `diff`
- [x] Mutation (unit): drop the capacity call → exactly 2 red; change the step key to `"finalize"` → the same 2 red; drop the materialisation → exactly 2 red
- [x] Mutation (integration): drop the materialisation → exactly 1 red, the ledger test
- [x] `bun nx lint api` green

**Manual**
- [x] A reconciled booking now has a `bookings` row readable by the console and both mutation doors
- [x] Slot `held → booked` moves with the hold, so availability does not free under a `Booked` operation

### Discoveries and Notable Information

1. **The capacity step key is `"create"`, not the handler's `StepType` of `"finalize"`, and getting
   it wrong fails silently.** All five existing call sites pass the literal `"create"`
   (`FinalizeBookingCommandHandler:137,494`, `ResolveManualReviewCommandHandler:291`,
   `ModifyBookingCommand:446,455`). Passing `"finalize"` finds no hold, returns
   `FinalizeOutcome.NotTracked`, and reproduces the exact bug being fixed **while looking
   correct** — the handler would still book, still emit the confirmation, and still leave the hold
   Active. It is now a named constant carrying that warning, and a test asserts the key in both
   directions. A step-key mutation was run specifically to prove that assertion fires.

2. **`FinalizeOutcome.Finalized` is the enum's ZERO value.** An unmocked `FinalizeAgainstOperationAsync`
   therefore returns "capacity settled" on its own — a permissive default that would let a test
   pass without the call ever happening. The test harness now sets it explicitly, and the
   assertions verify the *call*, not just the outcome.

3. **`ResolveManualReviewCommandHandler` does settle capacity — the synthesis was wrong about
   that.** It calls `FinalizeAgainstOperationAsync` at `:291`. The research's "fourth writer =
   CAS only" claim holds for the **booking row** (it has no `MaterializeBookedAsync` call) but not
   for capacity. Left alone as the plan says, but the follow-up should be filed as
   "missing the booking row" rather than "missing both".

4. **The relocation was worth doing as its own commit.** `git diff --stat` on the move alone showed
   `150 deletions, 12 insertions` across three files with the moved body byte-identical, which is
   a reviewable claim. Folded into the behaviour change it would have been a 200-line diff where
   nobody could tell the move from the edit.

5. **The intent revision is loaded inside the effect-proven branch, not at the top of `Handle`.**
   Finalize loads it early because it needs `PayloadHash` for the provider request; reconcile needs
   only `CanonicalPayload`, and only on the one branch that materialises a booking. Loading it at
   the top would add a database read to every unresolved and no-effect pass.

6. **The `HoldNoLongerValid` branch deliberately leaves the attempt marked `Reconciled`.** Those
   writes commit even though the operation escalates rather than books — whoever picks up the work
   item needs the provider reference the lookup just proved, and losing it would make the manual
   resolution guesswork.

---

## Phase 4 — #57 + U2: the recovery shortcut

**Files:** `.../Finalize/FinalizeBookingCommandHandler.cs` (`:108-186`, `:432`, `:530-531`)

**The change.** Three defects in one region:

1. **No cancellation check.** The shortcut is the one `Finalizing → Booked` writer that never reads
   `CancellationRequestedAt` (grep: only `:221`, `:454`). Add the read.
2. **Inv-8 violation.** Its `bookedRows > 0` block (`:173-178`) writes **no outbox row, no audit event
   and no metric** — `outboxRepo` appears in this file only at the ctor and `:547`. A booking confirmed
   through this path notifies nobody, while `GetOperationStatusQuery.cs:109` cheerfully tells the guest
   "Your booking is confirmed." Emit the same outcome envelope the main TXN2 does, in the same transaction.
3. **U2 — blank ref.** Guard `:432` with the same `IsNullOrWhiteSpace` test already at `:530-531`, so an
   empty `ProviderReference` cannot be committed and then pass `:115`'s `is not null` gate on the next drive.

**Why here.** Highest customer-visible severity; independently landable; sits in a Finalize region
disjoint from Phases 5–7, but Finalize edits are serialised to keep the file's four PRs reviewable.

### Success criteria

**Automated**
- [ ] `bun nx test api` green; new unit tests for the cancellation gate, the outbox/audit emission, and the blank-ref guard
- [ ] `bun nx test-integration api` green
- [ ] Mutation: drop the outbox emission → only the Inv-8 test reds; drop the cancellation read → only that test reds

**Manual**
- [ ] A booking recovered via the shortcut produces exactly one `booking_confirmed` notification and one audit row
- [ ] An op with `CancellationRequestedAt` set does not take the shortcut

### Discoveries and Notable Information
_(implementer fills in)_

---

## Phase 5 — #51b: fence the `Planned → Dispatching` write

**Files:** `.../Finalize/FinalizeBookingCommandHandler.cs:404-405`; `IWorkflowAttemptRepository` +
`WorkflowAttemptRepository`

**The change.** The real window is `[Finalize:291 → :405]` — worker B's TXN1 op read to B's dispatch
write — **not** the `[:239, :272]` the issue names. Because TXN1 is a multi-round-trip transaction, the
exposure is milliseconds rather than two statements.

Replace the unconditional `UpdateAsync` at `:404-405` with a **conditional write that re-asserts
`status = 'finalizing' AND cancellation_requested_at IS NULL` on `booking_operations` and returns
rows-affected**; abandon the dispatch on 0. `cancellation_requested_at` is monotonic and set before the
cancel worker ever runs, so this closes the window at source.

`workflow_attempts` has **no version column** (`WorkflowAttemptConfiguration.cs:7`; spec 12:78), which
is why the guard must be on `booking_operations`. `UpdateAsync` itself must stay byte-identical —
every other caller depends on its unconditional semantics; this is a **new, additive** method.

**Why here.** After Phase 4 only to keep Finalize edits serial. Phase 1 already removed this defect's
*permanence*; this removes its *occurrence*.

### Success criteria

**Automated**
- [ ] `bun nx test api` + `bun nx test-integration api` green
- [ ] Mutation: revert to the unconditional write → only the fence test reds
- [ ] `UpdateAsync`'s existing call sites unchanged (`git diff` shows no edit to `:32-43`)

**Manual**
- [ ] A cancel arriving mid-TXN1 aborts the dispatch instead of orphaning a provider booking

### Discoveries and Notable Information
_(implementer fills in)_

---

## Phase 6 — #56: keep the `null`

**Files:** `.../Finalize/FinalizeBookingCommandHandler.cs:378-382`

**The change.** `?? now.AddSeconds(30)` — the only `??` over a `NextEligibleAt` call in `apps/api/src` —
discards the FM#11 exhaustion signal and converts it into a perpetual reschedule, inserting a fresh
`Abandoned` attempt each cycle. Honour the `null`: route to `Reconciling` with reason
`retry_budget_exhausted`, whose destination Phases 2 and 3 have by now made correct.

Note for the implementer: the issue *understates* reachability (the branch self-exhausts — no prior
exhaustion needed, `:345-346` inserts at `latestAttemptNumber + 1` and `Abandoned` matches none of the
earlier guards) and *overstates* duration ("forever" — `InMemoryProviderCircuitBreaker.CheckIsOpen`
releases one probe per open window).

### Success criteria

**Automated**
- [ ] `bun nx test api` green; a test asserting `NextEligibleAt == null` routes to `Reconciling`, not a reschedule
- [ ] Mutation: restore the `??` → only that test reds

**Manual**
- [ ] A budget-exhausted op stops accumulating `Abandoned` attempt rows

### Discoveries and Notable Information
_(implementer fills in)_

---

## Phase 7 — #58: make the switch exhaustive

**Files:** `.../Finalize/FinalizeBookingCommandHandler.cs:733`, `:764-793`

**The change.** `_ => AttemptOutcome.Abandoned` (`:772`) is a trap: it swallows any unhandled
`ProviderResultKind` into the most destructive terminal arm — `finalize_permanent_error` →
`FailedFinal / failed_no_effect` → a customer "booking failed" notification, in one driver tick.
Give `NotFound` its explicit arm routing to `Reconciling`, matching `RetryClassifier.cs:34`
("a NotFound cannot prove the create did not take effect"), and make the switch exhaustive so the
default stops being a landmine for future kinds.

**Why last.** Lowest severity, unreachable in-tree, and its harm is already backstopped by Phase 1.

### Success criteria

**Automated**
- [ ] `bun nx test api` green; a test per `ProviderResultKind` through this switch
- [ ] Mutation: restore the `_ =>` default → only the `NotFound` test reds

**Manual**
- [ ] n/a — unreachable with the shipped adapter; covered by unit tests only

### Discoveries and Notable Information
_(implementer fills in)_

---

## Blast radius

| Artefact | Phase | Intentional, or byte-identical |
|---|---|---|
| `ReconcileOperationCommandHandler.cs` | 2, 3 | **Intentional**, both halves of `Handle`; **+3 ctor deps** (`ICapacityReservation`, `IBookingRepository`, `IBookingIntentRepository`) — rewrites every `CreateHandler()` in its test file |
| `FinalizeBookingCommandHandler.cs` | 4, 5, 6, 7 | **Intentional**, four disjoint regions. One file, four PRs — **serialise** |
| `CompensateOperationCommandHandler.cs` | 1 | **Intentional** at `:64-65`/`:96`; adds the file's first `AttemptOutcome` read |
| `BuildBookingAggregate` / `ReadFirstString` | 3b | 🚩 **Relocated, never duplicated.** Pinned by `FinalizeBookingProvenanceTests` + `HardeningRegressionTests`; a second copy re-creates the "two copies of one rule" failure spec 06 §19.1 warns about, on the `CustomerId` field `AuthorizeMutationAsync` later matches a grant against |
| `IWorkflowAttemptRepository` / `WorkflowAttemptRepository` | 5 | **Additive.** New conditional-write method; `UpdateAsync:32-43` stays byte-identical |
| `OperationStateMachine.AllowedTransitions` | none | 🚩 **Byte-identical.** Q1 was answered "`ManualReview`" specifically to keep it so. Note `OperationStateMachineTests` derives from the dictionary, so an edge added here would fail **no test** — the guard is purely human |
| `RetryClassifier.cs` | none | **Byte-identical.** Already correct; the handlers are the divergent copies |
| `TryTransitionStatusAsync` / `VersionTokenUpdate` | none | **Byte-identical.** §22.1 CAS primitive, ~20 call sites |
| `ManualReviewEscalator.cs` | 1, 2, 3 as callers | **Byte-identical** |
| `FinalizeAgainstOperationAsync` / `MaterializeBookedAsync` | 3 as caller | **Byte-identical** — already idempotent, already the single decision point |
| `IBookingProviderAdapter` / `StubBookingProviderAdapter` | none | **Byte-identical.** Phases 4 and 7 are handler-side hardening against a contract the interface never stated |
| `UnitOfWork.cs:17` | none | **Byte-identical.** READ COMMITTED; `docs/dotnet-api-patterns.md:153-155` forbids raising isolation to fix a race |
| `MessagingOptions` | none | **Byte-identical.** Do not tune lease/staleness inside a bug fix |

### Type-generation boundary — crossed by zero phases

No controller signature, DTO or route changes ⇒ **`libs/api-contract/openapi/v1.json` must regenerate
byte-identical.** A `v1.json` diff on any Wave 2 PR is a red flag. Per `CLAUDE.md` it is tracked and
pre-commit-regenerated, and must never be hand-merged.

⚠️ One guardrail to re-check per PR, not once: `GetOperationStatusQuery.BuildCustomerMessage:119-122`
appends the raw `StatusReason` **verbatim** to the customer-facing message for terminal, non-`Booked`
states. Every reason introduced in this wave is non-terminal (`Reconciling`, `ManualReview`), so the
property holds today — but that is a property of the reasons chosen, not of the wave.

---

## Cross-cutting requirements

- **Inv-7** — no phase may re-submit to a provider on an unproven effect. Phase 2 is the direct fix; Phases 1 and 7 harden the terminal side.
- **Inv-8** — Phase 4 restores the outcome outbox to the shortcut's transaction. Every new `Booked` writer must carry the full write-set or explicitly justify what it omits.
- **Inv-6** — Phase 3a restores capacity finalisation on the reconcile path. Spec 07 §14 lock ordering is normative and has no reconcile-lane precedent; follow it explicitly.
- **Inv-16** — Phases 1 and 7 stop `failed_no_effect` being told to a customer when the effect is unknown.
- **Queue safety (Inv-5)** — all four handlers run inside `InboxDeduplicationBehavior`'s transaction. Do **not** open a new `IUnitOfWork`, and never throw for an expected terminal failure; settle to a clean terminal state instead.
- **Optimistic concurrency** — all state transitions stay on `VersionTokenUpdate.UpdateAsync` guarded by (expected status AND expected version); 0 rows = lost race → reload.
- **File encoding** — new/edited `.cs` files must be UTF-8 **with BOM** or `bun nx lint api` fails.
- **Mutation discipline** — every guard added must be proven to fail when the thing it guards is broken. A guard that stays green under its own mutation has not been tested.

---

## What we're NOT doing

- **Not building #59 part 2.** Already shipped. Closed as done; the genuine residue (a stale `docs/specs/07:365` line and the hardcoded `CapacityPrimitiveResolver`) is re-filed as two small tickets.
- **Not adding a state-machine edge.** Q1 chose `ManualReview` to keep spec 06 §11 frozen.
- **Not extending the circuit breaker over reconcile lookups** (Q2). `grep "IsOpen(" apps/api/src` returns exactly one call site, so the §16.2 ladder calls the provider unguarded, and Phases 2 and 6 increase that traffic **by design**. This is a knowing partial defeat of FM#10/§15.4, filed separately rather than silently absorbed.
- **Not wiring a reconcile retry scheduler.** Q2 chose sweeper re-drive; `:276-280`'s discarded `nextEligible` is deleted or used, not turned into new machinery.
- **Not making `RetryAction` load-bearing.** It has zero production readers (only `RetryClassifierTests`). The right structural fix, but a refactor, not a bug fix — Wave 3.
- **Not fixing `ResolveManualReviewCommandHandler`'s write-set** beyond noting it as the fourth writer, unless Phase 3 makes the shared factory cheap enough to adopt there in the same PR.
- **Not raising transaction isolation** to fix any race here.
- **Not tuning lease or staleness options.**

---

## Package & migration notes

- **No new packages** in any lane.
- **No EF migration in this wave.** Phase 3 writes `bookings` via the existing `MaterializeBookedAsync` upsert; Phase 5's fence uses the existing `status` and `cancellation_requested_at` columns. Spec 12 owns all DDL and is untouched. If any phase turns out to need a column, **stop and re-plan** — that is a spec-12 change, not an implementation detail.

---

## Follow-ups this wave will file

1. **Reconcile's §16.2 ladder has no circuit guard** — the Q2 gap, stated above.
2. **`docs/specs/07:365` is stale** — "Slot provisioning is out of scope" contradicts the shipped `POST /api/availability/slots`.
3. **`CapacityPrimitiveResolver` is hardcoded** — needs a per-`(tenant, provider)` table.
4. **`ResolveManualReviewCommandHandler` is the fourth `Booked` writer** with the same two holes, if not folded into Phase 3.
