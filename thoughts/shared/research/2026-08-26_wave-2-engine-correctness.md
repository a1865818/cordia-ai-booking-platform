---
date: 2026-08-26
researcher: Claude (spec-loop Stage 1 — 13-agent workflow: 6 locate → 6 adversarial verify → adjudicated synthesis)
git_commit: aface13
branch: fix/wave-2-engine-correctness
repository: Cordia
topic: "Wave 2 — engine correctness: reconcile lookup proof (#55), reconcile capacity + slot provisioning (#59), circuit-open retry budget (#56), NotFound-from-create routing (#58), cancel-before-create TOCTOU (#51), idempotent-recovery shortcut (#57)"
tags: [research, baseline-audit, apps/api, booking-fulfillment, finalize, reconcile, compensate, inv-6, inv-7, inv-8, inv-16, spec-06, spec-07, spec-12]
status: complete
issues: [55, 59, 56, 58, 51, 57]
document: synthesis
---

# Research — Wave 2: engine correctness

> Per-issue baseline detail — what each code path does today, file by file — lives in the companion
> [`2026-08-26_wave-2-engine-correctness-baseline.md`](2026-08-26_wave-2-engine-correctness-baseline.md).
> That document is the regression baseline Stage 6 diffs against. This one is the argument.

## Why this research was shaped this way

Wave 1 taught the lesson this was designed around: **four of its six issues did not describe the defect that
was actually present.** One had already been fixed, one rested on a false premise, one was a race rather than
an unconditional error, and one pointed a layer below its real cause. So Wave 2 was researched as *locate,
then adversarially verify the premise*, with every verifier told to assume nothing in the issue text is true
until confirmed against the code.

The synthesis was then produced **twice, independently** — once with the readers' output and once without it.
(The second was an accident: a post-processing bug in the workflow script handed the synthesis agent an empty
array. It said so, and re-derived everything first-hand rather than inventing a summary — which turned a bug
into a genuine second opinion.) The two passes disagreed on four issues. The adjudication below resolves each
against the code rather than averaging them.

## Headline: the premise verdicts

| Issue | Verdict | Severity | Reachable with the shipped adapter? |
|---|---|---|---|
| **#51** | AS_DESCRIBED | high | **Yes — the only one** |
| **#55** | AS_DESCRIBED | high | No |
| **#56** | REAL_BUT_MISDESCRIBED | medium | No |
| **#57** | REAL_BUT_MISDESCRIBED | high | No |
| **#58** | REAL_BUT_MISDESCRIBED | low | No |
| **#59** | REAL_BUT_MISDESCRIBED | high | **Yes** (part 1, via the Inv-7 race) |

> **The severity column above is the ADJUDICATED value, not the verifiers' first pass.** It carried
> the verifier severities for #56, #57 and #58 in an earlier revision, which contradicted this
> document's own conflict table three sections down: the adjudication settles #56 at **medium**
> (the escape is real and the branch is unreachable with the shipped adapter), #57 at **high** (it
> is the wave's highest customer-visible severity — a booking confirmed to a guest who cancelled,
> notifying nobody), and #58 at **low** (unreachable, and backstopped by the Compensate guard). A
> reader who stopped at the summary would have mis-ranked half the wave.

Unlike Wave 1, **nothing came back `ALREADY_FIXED` or `NOT_A_DEFECT` at the issue level** — all six describe
real defects. But two carry traps that matter more than the misdescriptions:

1. **#59 bundles a part 2 whose premise is factually false.** `POST /api/availability/slots` already shipped
   (`AvailabilityController.cs:70`, commit `970ca4c`, owner-gated and idempotent). The issue's claim that slots
   are "only inserted by tests/manual seeding" is wrong, not merely out of scope.
2. **Two filed remedies would make things worse.** #59p1's literal fix throws
   `InvalidStateTransitionException` and dead-letters the operation; both of #51's proposed fixes are no-ops.
   Detail in the adjudication.

---

# Wave 2 — engine correctness: adjudicated sequencing synthesis

Verified first-hand against `main` @ `aface13`, working tree clean. Read-only.

> **Note on the conflict list I was handed:** it disagrees with the verifier JSON it accompanies. The prompt lists #51 as `AS_DESCRIBED`, #58 as `medium`, #57 as `medium`; the payload says `REAL_BUT_MISDESCRIBED`, `low`, `high` respectively. I adjudicated against the code and the payload, not the summary.

---

## 0. Where the two passes disagreed, and who was right

| Conflict | Verifier | Prior pass | **Who is right** | Deciding evidence |
|---|---|---|---|---|
| **#56 premise** | misdescribed | "exactly as filed" | **Verifier**, decisively | The issue presumes the budget is *already* exhausted. It isn't: the branch self-exhausts. `FinalizeBookingCommandHandler.cs:345-346` inserts a fresh attempt at `latestAttemptNumber + 1` on every re-entry, and `Abandoned` matches none of the earlier guards (`:115` Succeeded/Reconciled, `:196-197` Dispatching/Ambiguous, `:221-222` null/Planned), so control always reaches TXN1. With `StepRetryBudget.cs:18-19` = `(4, 10 min, 1 s, 30 s)`, pass 4 hits `completedAttemptNumber >= MaxAttempts` (`FullJitterRetryPolicy.cs:25-27`) with **zero provider calls**. Separately, "forever" overstates: `InMemoryProviderCircuitBreaker.CheckIsOpen` releases exactly one probe per open window (`:126-132`), so an op does eventually escape. |
| **#56 severity** | high | medium | **Prior pass** | The escape is real for a single op (~30-60 s), and an operator *is* told at 600 s — `EvaluateOperationSlaCommand.cs:34` `InProgressEscalationSeconds = 600` opens an `sla_escalation` work item (it just never transitions status, `:17`). And the branch is **unreachable with the shipped adapter**: `StubBookingProviderAdapter.CreateBookingAsync` returns only `Created`/`AlreadyExists` (`:24-36`), which calls `RecordSuccess`, so the circuit never opens. The verifier's "high" rests on the probe-contention tail (one probe per global `(providerName,"default")` key), which is real but is the tail, not the mode. **Medium.** |
| **#58 premise** | misdescribed | "holds as filed" | **Verifier**, decisively | The issue says the path "attempts to compensate a booking whose effect is unprovable (and whose provider ref *may* be unknown)". **No compensation is ever attempted and the ref is never known.** The catch-all (`:766-790`) never assigns `attempt.ProviderRef` — contrast the success branch at `:432` — so `CompensateOperationCommandHandler.cs:65` reads `null`, `:96` `if (providerRef is not null)` is false, `CancelBookingAsync` is never called, and branch 4b runs: `CompensationRequired → FailedFinal` reason `"failed_no_effect"` (`:238-241`) plus `notification.booking_failed.v1` (`:258-273`). The real harm is the *opposite* of the filed one and worse: a definitive "nothing happened" told to the customer, terminally, in one driver tick (`Finalize:793` returns `Continue`; `AdvanceBookingOperationCommandHandler.cs:90-91` loops). |
| **#58 severity** | low | low | Both agree | Unreachable in-tree; the durable hazard is the `_ => AttemptOutcome.Abandoned` default at `:772`, which will swallow any future `ProviderResultKind` into the most destructive terminal arm. **Low.** |
| **#51 mechanism** | window is on the *dispatching* worker's timeline; both filed remedies fail | window as filed; consequence worse | **Verifier**, decisively | The window is **not** `[:239, :272]`. It spans B's TXN1 op read (`:291`) → B's dispatch write (`:405`). If A's in-txn read lands *before* B's TXN1 commits, A sees `latestInTxn is null` and proceeds too — TXN1 is a multi-round-trip transaction (op read, `GetOrAddAsync`, attempt read, attempt insert), so the exposure is milliseconds, not two statements. And both filed remedies provably fail: `IWorkflowAttemptRepository.UpdateAsync` returns `Task` (void), so a version bump B loses is **unobservable to B**, which calls `CreateBookingAsync` at `:412` anyway; and a `FOR UPDATE` in A's txn releases at A's commit (and there is no row to lock in the `latest is null` shape). |
| **#51 severity** | high | medium | **Verifier** | Not on likelihood — on the fact that **#51 is the only Wave-2 defect reachable with the shipped adapter.** #55/#56/#58/#57 all require an adapter that returns `Transient`/`Ambiguous`/`NotFound`-from-create/blank-ref, and `StubBookingProviderAdapter` produces none of those. #51 needs only concurrency and a cancel request, both of which exist today: three hosted services (`MessagingServiceCollectionExtensions.cs:37,41,45`) dispatch in-process to the same driver (`ApplicationServiceCollectionExtensions.cs:92-96`), with no per-operation lease. **High.** |
| **#57 premise** | misdescribed | "holds and understates" | **Verifier**, decisively | The issue's stated trigger is **mechanically impossible**. `SetCancellationRequestedAsync` (`BookingOperationRepository.cs:110-124`) sets only `CancellationRequestedAt` + `UpdatedAt` via `ExecuteUpdateAsync` — no version bump. The *only* version writer is `VersionTokenUpdate.UpdateAsync` (`:44-52`), invoked solely from `TryTransitionStatusAsync` (`:74`, `:84`), which always changes `Status`. From `Finalizing` the legal targets are `{Booked, Reconciling, CompensationRequired}` (`OperationStateMachine.cs:67-72`), so any lost version race also moves the op **off** `Finalizing` — and the shortcut's own gate `fresh.Status == OperationStatus.Finalizing` (`:119`) then fails. A cancellation request can never cause the state the issue describes. |
| **#57 severity** | high | high | Both agree | Both also independently found the Inv-8 gap (no outbox, no audit, no metrics in the shortcut's `bookedRows > 0` block at `:173-178`; `outboxRepo` appears in this file only at the ctor and `:547`). **High.** |
| **U1** (no `bookings` row) | not raised | raised | **Prior pass — survives, strengthened** | `MaterializeBookedAsync` has exactly two call sites, both in Finalize (`:175`, `:540`). `ReconcileOperationCommandHandler` books at `:216-222` with outbox + audit + metrics and **no** booking row; `ResolveManualReviewCommandHandler` transitions `ManualReview → Booked` at `:95-104` and uses `bookingRepo` only for `GetByIdAsync`/`TryTransitionLifecycleAsync`/`TryReleasePendingAsync`. Consumers that then fail: `GetBookingDetailsQuery.cs:43`, `SubmitBookingCancellationCommandHandler.cs:41`, `SubmitBookingModificationCommandHandler.cs:41`, `ValidateBookingChangeQueryHandler.cs:24`, `CancelBookingCommandHandler.cs:88`, `ModifyBookingCommand.cs:112`, `RequestVerificationCommandHandler.cs:34`. |
| **U2** (empty-string ref) | raised as #57's trigger | raised as separate finding | **Verifier's framing wins** | It is not a separate defect — it is **#57's deterministic, race-free reachability route.** `:432` commits `attempt.ProviderRef = result.ProviderReference` *before* the guard at `:530-531`, and `ProviderResult.ProviderReference` is `string?`, so `""` passes `:115`'s `is not null` on the next drive. Fold U2 into #57 rather than filing it. |
| **#59 part 2** | already shipped; premise stale | "premise holds but it's a feature — eject" | **Verifier on the facts; prior on the action** | `POST /api/availability/slots` exists at `AvailabilityController.cs:70`, added 2026-08-06 in `970ca4c` (`git log --diff-filter=A`), owner-gated and idempotent. The issue's claim "slots are only inserted by tests/manual seeding" is **false**, not merely out of scope. Eject — but close it as already-done, not defer it. |
| **"#51 is independent of everything"** | — | asserted | **Prior pass wrong** | #51 and #58 share a concrete symbol: `CompensateOperationCommandHandler.cs:64-65` + `:96` — both land in the null-`ProviderRef` branch. And `grep -n "AttemptOutcome"` on that file returns **nothing**: Compensate never inspects the attempt outcome, so a `Dispatching` attempt (effect unknown by definition) is indistinguishable from no attempt at all. That is one shared hole, not two coincidences. |

---

## 1. Premise verdicts

| Issue | Premise verdict | The real defect, one line | Severity | Reachable with shipped adapter? |
|---|---|---|---|---|
| **#55** Reconcile: non-NotFound lookup → re-create | ✅ **Holds, and understates** | `effectProven` (`Reconcile:131-132`) is a *positive* test only; `NotFound` is never tested anywhere in the file, so `Transient`/`Ambiguous`/`AuthError`/`PermanentError` **and `Created` with a null reference** all fall to `:269-343` → `Reconciling→Held` → re-create. Bonus: `nextEligible` (`:276-280`) is computed and **discarded** — nothing is scheduled, so the 4-attempt budget burns as fast as the driver loops. | **High** | No (needs a non-definitive lookup kind) |
| **#59 part 1** Reconcile capacity-blind | ⚠️ **Holds; issue MISCLASSIFIES it as "defense-in-depth". It is live today.** | The effect-proven branch (`:161-264`) books with no `FinalizeAgainstOperationAsync` and no `MaterializeBookedAsync`; ctor `:33-43` has no `ICapacityReservation`, `IBookingRepository` or `IBookingIntentRepository`. The Active hold is later swept, decrementing `HeldCapacity` (`FirstPartyCapacityReservation.cs:523`) with `BookedCapacity` never incremented → **slot reads free under a terminal `Booked` op.** | **High** | **Yes** (see note below) |
| 🟥 **#59 part 2** slot provisioning | 🟥 **PREMISE IS FALSE — ALREADY SHIPPED** | `POST /api/availability/slots` (`AvailabilityController.cs:70`) shipped in `970ca4c`. Residue is a stale spec line (`docs/specs/07:365` "Slot provisioning is out of scope") + the still-hardcoded `CapacityPrimitiveResolver` + no bulk/resize. | **Not a defect** | — |
| **#56** circuit-open swallows exhaustion | ⚠️ **Misdescribed in both directions** | `?? now.AddSeconds(30)` (`Finalize:378-382`) discards the FM#11 signal — the only `??` over a `NextEligibleAt` call in `apps/api/src`. Issue *understates* reachability (branch self-exhausts, no prior exhaustion needed) and *overstates* duration ("forever" — one probe per 30 s window escapes). | **Medium** | No (circuit never opens with the stub) |
| **#58** `NotFound` from create → CompensationRequired | ⚠️ **Real, but the described harm is wrong** | `_ => AttemptOutcome.Abandoned` (`:772`) + `finalize_permanent_error` (`:785-788`). **Nothing is compensated** — ref is always null, so it terminalises to `FailedFinal / "failed_no_effect"` + `booking_failed.v1` in one tick. Attempt row is stamped `Ambiguous` (`:774`) while the op takes the permanent edge. | **Low** | No |
| **#51** cancel-before-create TOCTOU | ⚠️ **Mechanics hold; window and both remedies misdescribed** | Window is `[Finalize:291 → :405]`, not `[:239 → :272]`. Both filed fixes fail (void return; lock released at commit). Compounded by `CompensateOperationCommandHandler` never reading `AttemptOutcome`, which turns a race into a permanent orphan + a false failure notification. | **High** | **Yes — the only one** |
| **#57** recovery shortcut ignores cancellation | ⚠️ **Real; stated trigger is impossible; also an Inv-8 violation** | Shortcut `:108-186` is the one `Finalizing→Booked` writer with no `CancellationRequestedAt` read (grep: only `:221`, `:454`) **and** no outbox/audit/metrics. Reached via the blank-ref route (U2, deterministic) or the `Reconciling→Held→Finalizing` walk-back. | **High** | No (blank-ref needs a misbehaving adapter) |

> **Why #59p1 is live today, with the stub:** worker B is inside `CreateBookingAsync`; the attempt is `Dispatching`. Worker A arrives, hits the Inv-7 guard (`Finalize:196-209`) and CASes `Finalizing→Reconciling`. B returns, TXN2 commits attempt=`Succeeded`, then `:451-452` sees the op is no longer `Finalizing` and returns — **so `FinalizeAgainstOperationAsync` at `:493` is never reached and the hold stays Active.** Reconcile's key lookup then answers `AlreadyExists` (`StubBookingProviderAdapter:43-45`) and books. Same concurrency door as #51.

---

## 2. Dependency graph

```
                    ┌─────────────────────────────────────────────────┐
   (spec decision)  │ Q1: §11 edge Reconciling → CompensationRequired  │
                    └──────────────────┬──────────────────────────────┘
                                       │ blocks the literal remedy only
      #55 ───────────────────────────► #59p1 + U1        [same method]
       │  defines "unresolved"           │
       ├─────────────────────────────► #56                (new route into Reconciling)
       └─────────────────────────────► #58                (new route into Reconciling)

      #51a (Compensate guard) ───────► #58                [shared symbol]
       │
      #51b (dispatcher fence) ~~~~~~~~ #59p1 + U1 + #57(b) [shared ROOT CAUSE, not an order edge]

      #57 + U2 ── co-located, not causal ── #51b          [Finalize, disjoint regions]
```

| Edge | Concrete shared symbol | Why it is real |
|---|---|---|
| **#55 → #59p1** | `ReconcileOperationCommandHandler.Handle`; locals `r`, `latest`, `step`, `providerKey` (`:105-132`) | One method's two halves (`:131-132`/`:269-343` vs `:161-264`), plus a ctor change (`:33-43`) that rewrites every `CreateHandler()` in `ReconcileOperationCommandHandlerTests`. **Cannot be parallel.** |
| **#55 → #56** | `Reconcile:276-308` — `nextEligible == null` → `ManualReviewEscalator.EscalateAsync("reconcile_no_effect_budget_exhausted")` | #56's fix routes `Finalizing→Reconciling` and *relies on reconcile escalating*. If #55 lands as "unresolved ⇒ stay in Reconciling" without an exhaustion escape, #56 creates a fresh non-terminating loop at a new address. |
| **#55 → #58** | same destination; `RetryClassifier.cs:34` | #58 sends `NotFound` into `Reconciling`. Safe today only because the reconcile lookup happens to also return `NotFound`. #55 makes that destination correct for every lookup outcome. |
| **#59p1 → #56, #59p1 → #58** | `OperationStatus.Reconciling` as routing target (`Finalize:668`, `:711`, `:754`, plus two new routes) | Both widen the funnel into a destination that is capacity-blind and booking-materialisation-blind. Fix the destination before widening the funnel. |
| **#51a → #58** | `CompensateOperationCommandHandler.cs:64-65` + `:96` + `:238-241` | Both #51's orphan and #58's false-failure terminate at the same null-`ProviderRef` branch. A guard that refuses to declare "no effect" while the latest finalize attempt is `Planned`/`Dispatching`/`Ambiguous` removes #51's permanence **and** hardens #58's landing zone. `grep AttemptOutcome` on that file returns nothing today. |
| **#57 ≡ #59p1 ≡ U1** (one defect, three angles) | `opRepo.TryTransitionStatusAsync(..., → Booked)` and the **write-set** around it | **Four writers of `Booked`, no shared definition of what that outcome must contain.** Main TXN2 (`Finalize:487-581`) = capacity + CAS + Materialize + outbox + audit + metrics. Recovery shortcut (`Finalize:136-178`) = capacity + CAS + Materialize, **missing outbox/audit** (#57). Reconcile (`Reconcile:161-262`) = CAS + outbox + audit + metrics, **missing capacity** (#59p1) **and Materialize** (U1). ResolveManualReview (`:95-104`) = CAS only, **missing capacity, Materialize, and — for creates — everything else**. |
| **#51b ~ #59p1 ~ #57(b)** (shared root, not an order edge) | no per-operation lease + the unfenced write at `Finalize:404-405` | The same concurrency door produces #51's orphan, #59p1's unfinalized hold (via the Inv-7 race), and #57's walk-back route. This is why #51 must not be treated as an isolated tail item. |

---

## 3. Recommended landing order

| # | Item | Why here |
|---|---|---|
| **1** | **#51a — Compensate "unresolved attempt" guard** | Separate file, ~15 lines, deterministically unit-testable (seed the attempt, assert no transition). It is the **only Wave-2 defect reachable with the shipped adapter**, it makes the permanent orphan impossible regardless of who wins the CAS, and it pre-hardens #58's landing zone. Nothing else depends on it, so it can go out immediately. |
| **2** | **#55 — positive `NotFound` proof** | Foundation: two later fixes route into the definition of "unresolved". Provably test-neutral — every lookup stub in the repo returns only `NotFound`/`AlreadyExists` (`ReconcileOperationCommandHandlerTests:150,188,190,227,229,231,267,303,432,477`; the grep for a non-definitive lookup stub across `apps/api/test` returns nothing). Write it as the **whitelist** (`noEffectProven = r.Kind == NotFound`), which also closes the `Created`-with-null-reference hole for free. |
| **3** | **#59p1 + U1 — complete the reconcile `Booked` write-set** | Same method as #55, so serial regardless. Must precede #56/#58. Gated on Q1 — see §6; do not let that gate hold #55. |
| **4** | **#57 + U2 — cancellation check + Inv-8 outbox/audit + blank-ref guard** | Highest customer-visible severity in the wave: it confirms a booking the guest asked to cancel (`GetOperationStatusQuery.cs:109` then reads "Your booking is confirmed."; the cancellation wording at `:126` is gated on `!terminal`) **and** notifies nobody. Independently landable; U2 is its trigger, not a separate ticket. |
| **5** | **#51b — fence the `Planned→Dispatching` write** | Make it a conditional write re-asserting `status = Finalizing AND cancellation_requested_at IS NULL`, returning rows-affected, abandoning the dispatch on 0. The flag is monotonic and set before the cancel worker ever runs, so this genuinely closes the window at source. Lands after #57 only to keep Finalize edits serial. |
| **6** | **#56 — keep the `null`** | Single-branch edit at `:378-382`. Needs #55/#59p1's destination correct, and needs the Q2 answer about lookups against an unhealthy provider. |
| **7** | **#58 — give `NotFound` an explicit arm** | Lowest severity, unreachable, and its harm is already backstopped by step 1. Land it as "make the switch exhaustive" so the `_ =>` default stops being a trap for future kinds. |

### Where the roadmap's assumed order (55/59 → 56/58 → 51/57) is wrong

- **"55/59" as a parallel pair is wrong twice.** They are one method's two halves plus a shared ctor — never parallel. And "#59" as filed bundles a part 2 whose premise is factually false, so scheduling the issue as written schedules already-completed work.
- **"51/57" last is wrong twice.** #51 contains the only stub-reachable defect in the wave; its cheap half belongs *first*, not last. #57 is the highest customer-visible severity and is trivially separable.
- **"56/58" in the middle is wrong.** They are the two **lowest**-severity items in the set (medium and low, both latent) and belong last.
- **Net:** the roadmap's slab order is close to inverted on urgency. Corrected: **51a → 55 → 59p1+U1 → 57+U2 → 51b → 56 → 58.**

---

## 4. Blast radius

| Artefact | Touched by | Intentional, or must stay byte-identical |
|---|---|---|
| `Reconcile/ReconcileOperationCommandHandler.cs` | #55, #59p1, U1 | **Intentional**, both halves of `Handle`. Adds **three** ctor deps: `ICapacityReservation`, `IBookingRepository`, **and `IBookingIntentRepository`** — the last because `BuildBookingAggregate` requires `canonicalPayload` and reconcile loads no intent revision today. |
| `Finalize/FinalizeBookingCommandHandler.cs` | #56, #57+U2, #58, #51b | **Intentional**, four disjoint regions: `:108-186` (#57+U2), `:372-399` (#56), `:404-405` (#51b), `:733`/`:764-793` (#58). Four PRs, one file — serialise. |
| `Compensate/CompensateOperationCommandHandler.cs` | #51a (and #58's harm) | **Intentional**, at `:64-65` / `:96`. Add an `AttemptOutcome` read — the file currently has none. |
| `IWorkflowAttemptRepository` / `WorkflowAttemptRepository.cs` | #51b | **Intentional, additive.** New conditional-write method returning rows-affected. `UpdateAsync` (`:32-43`) must stay byte-identical — every other caller depends on its unconditional semantics. `workflow_attempts` has no version column (`WorkflowAttemptConfiguration.cs:7`; spec 12:78), so the guard must be on `booking_operations`, not on the attempt row. |
| `OperationStateMachine.cs` `AllowedTransitions` | **#59p1 only, and only if Q1 answers "(b) new edge"** | 🚩 **Frozen authority.** `[Reconciling] = {Held, Booked, AlternativesRequired, ManualReview}` (`:73-79`) matches `docs/specs/06:205-208` verbatim. Adding an edge changes no derived set (`TerminalStates`, `SlaEligibleStates`, `IsDriverDispatchable`) and `OperationStateMachineTests` derives from the dictionary, so **nothing would fail** — which is exactly why it needs a human decision, not a test. |
| `RetryClassifier.cs` | none | **Byte-identical.** Already correct; the handlers are the divergent copies. `RetryAction` has **zero production readers** (only `RetryClassifierTests.cs:12-19`) — making it load-bearing is the right structural fix and a Wave-3 refactor, not a bug fix. |
| `BookingOperationRepository.TryTransitionStatusAsync` + `VersionTokenUpdate` | none | **Byte-identical.** The §22.1 CAS primitive; ~20 call sites read `X.Version` then transition. |
| `ManualReviewEscalator.cs` | #55, #59p1, #56 as **callers** | **Byte-identical.** Note it does not terminate the workflow step, so escalated ops keep being swept (`StepSweeperOptions.StalenessSeconds = 60`). Pre-existing; Wave 2 makes it more visible. |
| `ICapacityReservation` / `FirstPartyCapacityReservation.FinalizeAgainstOperationAsync` | #59p1 as **caller** | **Byte-identical.** Already the single decision point and already idempotent for an already-`Finalized` hold. The **new call site** must satisfy the normative lock ordering in `docs/specs/07` §14 (holds before slots, ascending ids, whole set before the first write). |
| `BookingRepository.MaterializeBookedAsync` | U1 as **caller** | **Byte-identical.** Already an upsert (`:19-41`). |
| `FinalizeBookingCommandHandler.BuildBookingAggregate` / `ReadFirstString` (`:844-924`, `internal static`) | U1 | 🚩 **Must be relocated, never duplicated.** Pinned by `FinalizeBookingProvenanceTests` and `HardeningRegressionTests`. A second copy would re-create the "two copies of one rule" failure spec 06 §19.1 warns about, on the `CustomerId` field that `AuthorizeMutationAsync` later matches a verification grant against. |
| `IBookingProviderAdapter` / `StubBookingProviderAdapter` | none | **Byte-identical.** #58 and U2 are handler-side hardening against a contract the interface never stated. If the contract is to be *stated*, that is a doc change to `IBookingProviderAdapter.cs`, not a signature change. |
| `MessagingOptions` (`ScheduledJobPollerOptions.LeaseSeconds = 30`, `StepSweeperOptions.StalenessSeconds = 60`) | context for #51 | **Byte-identical.** They are the mechanism that makes concurrent drivers on one operation possible; do not tune them inside a bug fix. |
| `UnitOfWork.cs:17` | none | **Byte-identical.** `BeginTransactionAsync` with no isolation level = READ COMMITTED. Every "re-read inside the txn" here is a READ COMMITTED read; `docs/dotnet-api-patterns.md:153-155` forbids raising isolation to fix a race. |

### Type-generation boundary — **crossed by zero items in this wave**

- `OperationStatus` is **hand-written** (`Cordia.Domain/Enums/OperationStatus.cs`, `[PgName]`/`[JsonStringEnumMemberName]`), not generated. The Pydantic-derived customer-facing enum is `libs/shared-schemas/schemas/enums/NormalizedOperationStatus.json` and is untouched — every status these fixes use already collapses to `in_progress` or a terminal.
- No controller signature, DTO, or route changes ⇒ **`libs/api-contract/openapi/v1.json` must regenerate byte-identical.** A v1.json diff on any Wave-2 PR is a red flag; per `CLAUDE.md` it is tracked, pre-commit-regenerated, and must never be hand-merged.
- ⚠️ **One guardrail the prior pass stated too broadly.** `GetOperationStatusQuery.BuildCustomerMessage:119-122` appends the raw `StatusReason` verbatim to the customer message **for terminal, non-`Booked` states**. Every new reason in this wave is non-terminal (`Reconciling`, `ManualReview`, `CompensationRequired`), so the claim holds *today* — but it is a property of the reasons chosen, not of the wave. Re-check it per PR.
- 🟥 The one item that *would* have crossed — #59 part 2 — already shipped in `970ca4c`. Nothing to schedule.

---

## 5. The riskiest single change, and what would make it safe

**#59p1 + U1 — completing the reconcile `Booked` write-set.** (The prior pass named #51's version bump; I am not recommending that remedy, and with it off the table this is clearly the riskiest.)

Why:

1. **Three new ctor dependencies** on a handler that has ten (`Reconcile:33-43`), breaking every `CreateHandler()` in `ReconcileOperationCommandHandlerTests:36-39` and any DI-shape assertion.
2. **It forces a helper relocation.** `BuildBookingAggregate` is `internal static` inside `FinalizeBookingCommandHandler` (`:844-890`) and needs `canonicalPayload`, which reconcile cannot produce without `IBookingIntentRepository`. Duplicating it puts two copies of the `CustomerId` precedence chain in the tree — the exact failure that helper's own docstring was written to prevent.
3. **New capacity call site inside a transaction that already writes the op row** ⇒ must satisfy `docs/specs/07` §14 lock ordering, which is normative and currently has no reconcile-lane precedent.
4. **The issue's literal remedy throws.** `Reconciling → CompensationRequired` is not in `AllowedTransitions[Reconciling]` (`OperationStateMachine.cs:73-79`); `TryTransitionStatusAsync` calls `EnsureAllowed` first (`BookingOperationRepository.cs:71`); `InvalidStateTransitionException` is caught by the driver as poison/DLQ (`AdvanceBookingOperationCommandHandler.cs:94-113`). Implemented as filed, it **dead-letters the operation instead of compensating it.**
5. **Zero existing coverage.** `ReconciliationTests` contains no `CapacitySlot`/`CapacityHold` reference at all (grep returns nothing across 468 lines); the unit suite has no capacity mock. Nothing in CI can tell you whether the fix works or whether it broke the ledger.

What makes it safe, in order of value:

1. **Pre-answer Q1 as (a) `ManualReview`** so no state-machine or spec-06 amendment enters a bug-fix wave. `ResolveManualReviewCommandHandler` already treats an unreclaimable hold as an operator's problem; §16.2's own ladder ends in "human".
2. **Write the failing test first.** Seed a tracked op (a `capacity_slots` row + an Active hold under step `"create"`) in `Reconciling` and assert three things: hold `Active→Finalized`, slot `held→booked`, and a `bookings` row readable by `GetBookingDetailsQuery`. `FinalizeAgainstHoldTests.SeedFinalizeWithHoldAsync` is the ready-made base.
3. **Split into two commits**: (i) capacity finalize — two deps, no payload; (ii) booking materialisation — the payload dep plus the helper move. The second is where the risk lives.
4. **Relocate `BuildBookingAggregate`/`ReadFirstString` into a shared factory in a separate, content-preserving commit**, with the existing provenance tests re-pointed and their assertions unchanged. Never duplicate.
5. **Reuse `FinalizeAgainstOperationAsync` unchanged** — it is already the single decision point and already idempotent for an already-`Finalized` hold.
6. Consider whether `ResolveManualReviewCommandHandler`'s create-resolved-as-`Booked` path (`:95-104`) should use the same shared write-set in the same PR. It is the fourth writer of the same outcome and has the same two holes.

---

## 6. Open questions a human must answer

**Q1 — BLOCKING #59p1. `Reconciling → CompensationRequired` is not a legal edge. What should effect-proven-but-hold-expired do?**
Options: **(a) `ManualReview`** — legal today, zero spec change, matches §16.2's ladder and existing precedent. *Recommended.* **(b)** Add the edge — a spec-06 §11 amendment (`docs/specs/06:205-208`); structurally safe, but note that `OperationStateMachineTests` derives from the dictionary, so **no test would fail** and the guard is purely human. **(c)** Best-effort finalize, ignore the outcome, record the discrepancy for the operator. Spec 06 owns this decision, not the implementer.

**Q2 — SHAPES #55 AND #56. When a lookup proves nothing and the budget remains, what happens — and what protects the provider?**
There is no reconcile-specific retry mechanism (no `RetryScheduler` injected; `Reconcile:276-280` computes a time and throws it away). The zero-new-machinery answer is *stay in `Reconciling`, return `Wait`* and let `StepSweeperHostedService` re-drive, self-terminating at the 10-minute `MaxElapsed`. **Confirm that is acceptable**, because `grep "IsOpen(" apps/api/src` returns exactly one call site — `Finalize:372` — so the reconcile ladder (`:116-129`) hits the provider with **no circuit guard at all**. #55 increases that traffic and #56's fix increases it again, by design. That is a partial defeat of FM#10/§15.4 and needs an explicit decision.

**Q3 — CONFIRM #51's REMEDY IS REPLACED.** Both fixes the issue proposes provably fail (void return; lock released at commit; no row to lock in the `latest is null` shape). Confirm the substitute pair: **51a** the Compensate unresolved-attempt guard, **51b** the conditional dispatch write. If only one lands, land 51a — it is cheap, deterministic, and covers #58's harm too.

**Q4 — CONFIRM SCOPE.** Fold **U1** into #59p1 and **U2** into #57 rather than filing them (both sit inside lines those fixes already edit). **Close #59 part 2 as already-implemented** and re-file the genuine residue as two small tickets: a `docs/specs/07:365` correction, and the `CapacityPrimitiveResolver` per-`(tenant, provider)` table.

No other blockers. #51a, #55, #57, #56 and #58 are all implementable against current code and spec without a decision, and the Wave-0 `FinalizeBookingCommandHandlerTests` harness (`CreateHandler` at `:58-64`, with the `FinalizeOutcome`-zero-value trap already neutralised) was built for exactly these.

---

## Decisions taken on the open questions

Put to the repository owner on 2026-08-26. Answers:

| Q | Decision |
|---|---|
| **Q1** — destination when the effect is proven but the hold expired | **Route to `ManualReview`.** Legal edge today, zero spec-06 amendment, matches §16.2's ladder ending in a human. `Reconciling → CompensationRequired` stays illegal. |
| **Q2** — reconcile retry when a lookup proves nothing but budget remains | **Stay in `Reconciling`, return `Wait`** and let `StepSweeperHostedService` re-drive, self-terminating at the 10-minute `MaxElapsed`. No new machinery. |
| **Q3** — #51's remedy | **Both filed remedies are replaced.** `51a` = the Compensate unresolved-attempt guard; `51b` = the conditional dispatch write. |
| **Q4** — scope | **U1 folds into #59p1, U2 folds into #57.** #59 part 2 closes as already-implemented; its genuine residue is re-filed separately. |

> **Q2 leaves a known gap on purpose.** `grep "IsOpen(" apps/api/src` returns exactly one call site —
> `FinalizeBookingCommandHandler.cs:372` — so the §16.2 reconcile ladder calls the provider with **no circuit
> guard at all**, and both #55 and #56 increase that traffic by design. This is a partial defeat of
> FM#10/§15.4, accepted knowingly and filed separately rather than silently absorbed.
