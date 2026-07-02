# Spec-11 Progress Review — Conformance & Gap Assessment

- **Date:** 2026-07-03
- **Branch:** `feat/spec-11` @ `5fbdf43` (29 commits over `origin/main`; last 10 remediated the [2026-07-02 review](2026-07-02_pr65-spec-11-review.md))
- **Spec:** [`docs/specs/11-waitlist-menu-preorder.md`](../../../docs/specs/11-waitlist-menu-preorder.md)
- **Method:** `/code-review` high effort — 6 Sonnet finder/analysis agents (3 vertical
  conformance passes, contracts+conventions, line-by-line over the fix commits,
  cross-file + removed-behavior) → 17 deduped candidates → 3 Sonnet verifiers
  (16 CONFIRMED, 1 REFUTED-by-design) → Opus final synthesis.

## 1. Alignment verdict

The branch implements the full §1–§11 surface of spec 11 in structure, and the
load-bearing invariants are mostly present in the code path: contracts (§4.2/§4.3)
are aligned field-for-field, the menu 9-state pg-enum lifecycle is exact and
fail-secure, the waitlist enroll→match→offer→accept flow rides the spec-04 front
door (Inv-1) with one-transaction offer creation (Inv-4), and the pre-order saga
genuinely delegates capacity/payment/booking to the spec-06 engine and awaits its
terminal signal before the combined outcome (Inv-8). At the level of "is the
architecture the spec describes actually built," conformance is high — roughly the
whole happy-path surface across all three verticals is real and integrated. What is
*not* trustworthy is the set of **failure and terminal paths**: nearly every
confirmed finding lives on an error branch, a race, or a terminalization step, not
on the happy path.

The systemic weak seam is the **06-engine / saga boundary and operation-row
terminalization**. The confirmed findings cluster tightly there: when a pre-order
fails (no capacity, item unavailable, deposit expiry, tenant rejection, or a
post-commit pre-order bind failure), the code emits a customer-facing terminal
notification but leaves the `booking_operations` row non-terminal (C3+C15), or
emits the *wrong* terminal outcome (C14), or emits it *twice* because two 06 emit
sites missed the `IsPreorder` gate every sibling has (C12/C13), or re-nudges
forever because reconcile's terminal set omits `ManualReview` (C16). This is one
defect family — *the saga knows a pre-order ended, but the operation row and the
notification layer do not agree on how* — expressed at six or seven distinct
sites. C1 shares the theme from the notification side: a waitlist event writes a
notification against an `operation_id` that has no `booking_operations` row yet,
and the DB FK rejects it.

Confidence from the test suite is **materially lower than the green checkmarks
suggest**. Unit tests mock the repositories, so the C1 FK violation (a real
Postgres `23503` on every waitlist offer/expiry insert) is invisible;
`WaitlistFlowTests` asserts only that the outbox row was written, never that the
notification survives transport to a sender. The menu review e2e test uses *empty*
edits, so C6 — where every review with non-empty edits 404s and rolls back —
passes CI while the actual owner workflow is broken. There is no integration
coverage driving preorder + NoCapacity / deposit-expiry / validation-rejection to
observe the operation row's terminal state or count the outcome notifications. The
suite proves the happy paths and the isolation/immutability properties well; it
proves almost nothing about the failure-path correctness this review found broken.

## 2. Findings (ranked by customer impact)

### 1. C1 — Waitlist offer/expiry notifications are never delivered (FK violation → DLQ)
`apps/api/src/Cordia.Infrastructure/EntityFramework/Configurations/NotificationConfiguration.cs:25-30`
(with `SendNotificationCommandHandler.cs`)
The `notifications(tenant_id, operation_id)` FK to `booking_operations` (RESTRICT)
rejects every waitlist notification: match/expire emit with `operation_id = op_wl_*`
and no `booking_operations` row exists until *accept*. **Scenario:** a table opens,
the offer notification insert hits PG `23503`, the publisher retries and DLQs — the
customer never learns a table opened, silently re-breaking the exact flow the prior
remediation (`5b80b6e`) claimed to fix. **Fix:** make `notifications.operation_id`
nullable with FK-on-non-null (see §4 WP-A).

### 2. C14 — Customer with a confirmed reservation is told their booking failed
`apps/api/src/Cordia.Application/Handlers/Preorder/ManualResolution/CancelPreorderManualReviewCommandHandler.cs:66-67`
Cancelling a pre-order in `ManualReview` emits `notification.booking_failed.v1`
("We were unable to complete your booking…"), but `ManualReview` is reached only
*after* 06 signalled `booking_committed` — the table booking stays `Booked`; cancel
only unwinds the food order. **Fix:** emit a pre-order-specific "food order
cancelled, your table booking stands" outcome, not the FinalFailure booking template.

### 3. C12/C13 — Two 06 terminal emit sites lack the `IsPreorder` gate → double outcome + reservation leak
`apps/api/src/Cordia.Application/Handlers/BookingFulfillment/DepositEvent/ProcessDepositEventCommandHandler.cs:126-140`
and `Validate/ValidateOperationCommandHandler.cs:160-208`
Both emit `deposit_expired.v1` / `rejected.v1` directly to the customer with no
`IsPreorder` branch (every sibling 06 emit site routes via
`ConfirmedOutcomeType`/`FailedOutcomeType(IsPreorder)`). **Scenario:** a pre-order's
deposit expires (or tenant validation rejects it); the customer gets a raw failure
notice, the saga sits in `CommittingBooking` with items reserved ~10 min until the
reconcile timer re-synthesizes `booking_failed` — a *second* outcome (Inv-8 breach)
plus a reservation leak. **Fix:** gate both sites on `IsPreorder`.

### 4. C6 — Menu review-with-edits always 404s and rolls back
`apps/api/src/Cordia.Application/Handlers/Menu/ReviewCatalog/ReviewCatalogCommandHandler.cs:33-38`
(guard in `VendorCatalogRepository.ApplyItemEditsAsync:~146`)
Edits are applied *before* the `DraftReady → InReview` transition, but the
repository guard updates rows only when already `InReview` and throws
`CatalogItemNotFoundException` (404) on 0 rows. Every review with non-empty edits
— the core §5.2 owner action — 404s and rolls back; only the empty-edits CI path
works. **Fix:** transition to `InReview` before applying edits (or relax the guard
to accept `DraftReady` as the review entry state).

### 5. C3/C15 — Saga failure paths never terminalize the `booking_operations` row (two sites)
`apps/api/src/Cordia.Application/Handlers/BookingFulfillment/ReserveCapacity/ReserveCapacityCommandHandler.cs:132-183`
(NoCapacity) and `Preorder/ReserveItems/ReservePreorderItemsCommandHandler.cs` /
`CompensatePreorderItemsCommandHandler` (items-unavailable pre-06)
On NoCapacity the op parks in `AlternativesRequired` with no prompt and no exit
(SLA evaluator skips user-wait states); on items-unavailable-before-06 the op stays
`Accepted` forever (no `Accepted → FailedFinal` edge). Customer receives a terminal
`booking_failed` notification while `GetOperationStatusQuery` reports the operation
pending indefinitely. **Fix:** terminalize the operation in lockstep with the
failure notification (add the missing edge; drive `AlternativesRequired` to a real
prompt or terminal fail).

### 6. C16 — Reconcile omits `ManualReview` → infinite re-nudge, reservation leak
`apps/api/src/Cordia.Application/Handlers/Preorder/Reconcile/ReconcilePreorderSagaCommandHandler.cs:44-50`
`_terminalFailureStates` omits `OperationStatus.ManualReview`, so a saga in
`CommittingBooking` whose 06 op parked in `ManualReview` is treated as in-flight
and re-nudged forever (`AdvanceBookingOperationCommandHandler` no-ops for
`ManualReview`); items stay reserved; no escalation window exists for
`CommittingBooking`. **Fix:** add `ManualReview` to the terminal-failure set and
give `CommittingBooking` an escalation window that releases reservations.

### 7. C2 — Poller and sweeper never DLQ `PoisonMessageException` → infinite retry
`apps/api/src/Cordia.Infrastructure/Messaging/ScheduledJobPollerHostedService.cs:~101-114`
and `StepSweeperHostedService.cs:~90-104`
Both catch only generic `Exception` around `transport.PublishAsync`; the transport
now throws `PoisonMessageException` for routable-but-unregistered types
(`7217013`), and unlike `OutboxPublisher` these two reclaim and retry forever —
asymmetric even with the poller's own unknown-queue → `MarkDead` path. **Fix:**
add a `PoisonMessageException` catch that marks the job/step Dead.

### 8. C10 — Cancelling an enrollment drops the freed slot from the waitlist queue
`apps/api/src/Cordia.Application/Handlers/Waitlist/Cancel/CancelWaitlistEnrollmentCommandHandler.cs:41-54`
Cancel releases the live offer's hold but never enqueues `WaitlistRematchMessage`
(both Decline and Expire do) — the next eligible enrollee is never offered the
opening. **Fix:** enqueue the re-match on cancel.

### 9. C8 — Negative prices can be edited in, published, and served
`apps/api/src/Cordia.Application/Handlers/Menu/EditCatalogItems/EditCatalogItemsValidator.cs`
No range validation on `CatalogItemEdit.PriceMinor`. **Fix:**
`GreaterThanOrEqualTo(0)` (plus a sane upper bound) in the edit/review validators.

### 10. C11 — Empty consent evidence still authorizes silent auto-booking
`apps/api/src/Cordia.Application/Handlers/Waitlist/Enroll/EnrollWaitlistValidator.cs`
(and `AutoBookGrant.Permits`)
`AutoBookGrant` sub-fields are never validated and `Permits` does not require a
non-empty `ConsentEvidenceId` — an enrollment with `consent_evidence_id: ""` still
authorizes platform auto-book/charge with an empty audit-disclosures list,
violating the §20 recorded-consent MUST-NOT. **Fix:** validate `ConsentEvidenceId`
non-empty and the constraint window/deposit bounds.

**Cut to fit 10 (all below the correctness line, still worth fixing):**
C4 — `StartPreorderSagaCommandHandler` ignores the version-guarded transition's
rows-affected and emits notification/audit unconditionally; C7 — jsonb
`MenuMetadata`/`AllergenInfo` returned double-encoded on the wire (allergen data as
an opaque string); C9 — the public enrollment DTO lacks
`identity_ref`/`verification_level`, so every API enrollment is stamped
`Unverified` (§5.1 verified follow-up unreachable via the documented surface);
C17 — controller manual validation duplicating validators
(`docs/dotnet-api-patterns.md`). C5 (DLQ replay without a poison catch) was
REFUTED — documented publish-before-mark design.

## 3. Spec deviations

**(a) Already documented by the spec's implementation-status note — acceptable:**
- Saga delegation redesign (`HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold`/`Reconciling` retained but unwired).
- `ExtractFailed` modelled as stuck-in-`Extracting` + owner alert (D4).
- LangGraph extraction agent not wired to the .NET consumer; scanner/extractor are stub ports.
- All §12 open decisions deferred by design.

**(b) Undocumented deviations found by this review:**
- Search uses `ILIKE` with no trigram/GIN index — §7 specifies an *indexed* search for the <120 ms p95 target.
- Per-step saga handlers instead of the §10 `PreorderSagaCoordinator.cs` (benign, structural).
- Extra `CatalogController`/`PreorderController` beyond the §10 file list (benign).
- jsonb columns returned double-encoded (C7); enrollment surface cannot carry identity assurance (C9).

## 4. What remains to be completed (prioritized roadmap)

**P0 — confirmed correctness fixes, grouped:**
- **WP-A Waitlist notification delivery (C1).** Make `notifications.operation_id`
  nullable with FK-on-non-null (migration); waitlist sends resolve recipient from
  the enrollment. Preferred over minting a placeholder `booking_operations` row at
  match time (pollutes operation semantics and the very terminalization WP-C
  tightens) and over a recipient-without-operations redesign (larger blast radius).
- **WP-B Pre-order terminal-outcome correctness (C12/C13, C14).** Gate the two
  missed emit sites on `IsPreorder`; give manual-review-cancel a "food order
  cancelled, table stands" outcome (Inv-8).
- **WP-C Operation-row terminalization on saga failure (C3/C15, C16).** Missing
  `Accepted → FailedFinal` edge; drive `AlternativesRequired`/`ManualReview` to
  real terminal-or-prompt states in lockstep with the notification; `ManualReview`
  in reconcile's terminal set + `CommittingBooking` escalation window (§8 FM#8–10).
- **WP-D Menu review + validation (C6, C8).** Transition before edits; price range rule (§5.2).
- **WP-E Reliability + queue integrity (C2, C10, C11).** Poison-DLQ in
  poller/sweeper; re-match on cancel; consent-evidence validation (Inv-14/§20).

**P1 — test-gap closures:**
- End-to-end outbox → transport → sender test for waitlist notifications against real Postgres (catches C1).
- Menu review-with-non-empty-edits e2e (catches C6).
- Integration cases for preorder + NoCapacity / deposit-expiry / validation-rejection asserting operation terminal state **and** exactly-one outcome notification (catches C3/C12/C13/C15).

**P2 — known deferred work:**
- Wire the LangGraph extraction agent to the .NET `menu.commands` consumer (§5.2).
- Real ClamAV/LLM adapters behind the stub ports (§3).
- Spec-08 caller-identity check on accept endpoints + identity fields in the enrollment DTO (C9, §9).
- Staff RBAC over approve/publish/convert/auto-book configuration (§12).
- `apps/web` owner menu-review UI (§1/§10).
- Trigram/GIN index behind `search_vendor_catalog` (§7).
- Ratify the remaining §12 open decisions.
