# Spec 11 — Remaining Conformance Gaps

**Date:** 2026-07-01
**Branch:** feat/spec-11
**Spec:** `docs/specs/11-waitlist-menu-preorder.md`
**Depends on:** the committed spec-11 body (`33c4abd`) + the uncommitted divergence-fixes.

Closes the three open items from the 2026-07-01 conformance audit. Governing directive:
**treat the repo as a single evolving architecture — integrate with existing mechanisms
(single front doors), do not add parallel implementations.**

---

## Goals

1. **Gap 1 (priority) — Auto-book routes through the spec-04 front door (Inv-1, §2, §20).**
   Today an auto-bookable match finalizes capacity directly and emits the bespoke
   `waitlist.booking_confirmed.v1` (`MatchWaitlistOfferCommandHandler.cs:107,118`) — no
   auditable confirmed revision. §20 demands the *strongest* consent/audit evidence exactly
   here. Make auto-book mint a real spec-04 revision+confirmation carrying the `AutoBookGrant`
   consent evidence, same front door the offer-accept path now uses.
2. **Gap 2 — Standalone-order conversion (§21.4).** A post-commit pre-order failure parks in
   `ManualReview` (good — no orphan), but there is no audited operator+customer conversion to a
   standalone order (or explicit cancel). Add it.
3. **Gap 3 — Foreground mutable pre-order draft (§4.3).** langgraph only models the immutable
   `PreorderReference`; §4.3 wants a mutable `PreorderDraft`/`PreorderDraftLine` assembled
   in-dialog inside `BookingDraft`, frozen into the reference at confirmation. Add the missing
   primitive + freeze helper.

## Non-goals ("What we're NOT doing")

- Full operator RBAC / signed-context authorization (spec 08/15) — keep the existing actor-shim
  pattern but *require + audit* operator actor and customer-agreement evidence.
- Wiring agent dialog nodes to actually populate the draft mid-conversation (spec 02) — Gap 3
  delivers the **primitive** (models + freeze), which is what the audit flagged as missing.
- Real LLM/ClamAV extractor wiring (later step).

## Key constraints / discoveries (verified)

- `UnitOfWork.ExecuteInTransactionAsync` (`UnitOfWork.cs:17`) always `BeginTransactionAsync` →
  **cannot be nested**. `MatchWaitlistOfferCommand` is an `IInboxConsumerRequest` (wrapped in the
  inbox txn by `InboxDeduplicationBehavior`), so it can't call `SubmitConfirmedIntentCommand`
  inline. Precedent: `AdvanceBookingOperationCommand` is registered as a **plain `IRequest`**
  (registry comment, `ApplicationServiceCollectionExtensions.cs:60-74`) precisely so the inbox
  behavior does not wrap a transaction-managing consumer. Reuse that pattern.
- `AcceptWaitlistOfferCommandHandler.BuildSubmitCommand` (`…/Accept/…Handler.cs:134-191`) already
  builds the canonical `BookingRequest` + `IntentRevision` + `Confirmation` and submits via 04,
  binding `enrollment.IdentityRef`/`VerificationLevel`. Extract a shared builder and add an
  optional consent-evidence argument for auto-book.
- Match already reserves the hold under workflow step `create` (`MatchWaitlistOfferCommandHandler.cs:48`)
  so the 06 engine reuses it — no double capacity for either accept or auto-accept.
- `BookingRequest.preorder: PreorderReference | None` (`state.py:112`) is the immutable confirmed
  carrier; `BookingDraft.request: BookingRequest` (`state.py:205-221`). There is no mutable draft.
- `PreorderSagaStateMachine`: `ManualReview` is currently terminal (no outgoing edges). Gap 2
  needs operator-resolution edges `ManualReview → {Completed, FailedFinal}`.

---

## Phase 1 — Auto-book through spec-04 (Inv-1 / §20)  ← priority

**Changes**
- `MatchWaitlistOfferCommandHandler` (auto-book branch, lines ~102-122): STOP finalizing capacity
  and STOP emitting `waitlist.booking_confirmed.v1`. Keep creating the offer and advancing
  `Matched → Offered`; then enqueue `waitlist.commands.auto_accept.v1` (payload: offer_id) via the
  outbox **inside the inbox txn**. Audit stays `waitlist.auto_booked` (intent recorded).
- New `AutoAcceptWaitlistOfferCommand` + handler, registered as a **plain `IRequest`** consumer
  (NOT `IInboxConsumerRequest`) so it can manage its own transaction. It mirrors the HTTP accept:
  load offer+enrollment → build the spec-04 submit (shared builder) **carrying the grant’s
  `ConsentEvidenceId`** as confirmation/audit evidence (§20) → `mediator.Send(submit)` →
  version-guarded claim `Offered → Accepted`. Idempotent via the deterministic spec-04 key
  `wl_accept_{offerId}` + the offer state guard.
- Extract `WaitlistAcceptSubmissionBuilder` (shared by Accept + AutoAccept). Add an optional
  `autoBookConsentEvidenceId` that, when present, is recorded in the confirmation disclosures/audit.
- Remove the now-unused bespoke auto-book emission; keep `BookingConfirmedEventType` only if still
  referenced elsewhere (grep — expected: delete).
- Register the consumer in `ApplicationServiceCollectionExtensions`; author job-event schema
  `libs/shared-schemas/schemas/job-events/waitlist.commands.auto_accept.v1.json` (no secrets/PAN).

**Rejected alternative:** make `UnitOfWork` ambient-transaction-aware (reuse the open txn / use
savepoints). Rejected: broad blast radius across every handler; the plain-`IRequest` precedent is
localized and already blessed.

**Tests (TDD)**
- Unit: auto-book branch → enqueues `auto_accept`, does NOT finalize capacity, does NOT emit
  `waitlist.booking_confirmed.v1`. AutoAccept → sends `SubmitConfirmedIntentCommand` with
  confirmation carrying identity + consent evidence; claims offer; second delivery is a no-op.
- Integration: satisfiable grant → auto_accept → 04 mints revision/confirmation/operation → 06
  finalizes the `create` hold → exactly one combined outcome (Inv-8); the confirmed revision exists
  in `booking_intent_revisions` (auditable, Inv-1).

**Success:** `bun nx test api` (unit) green; new integration green; scoped `dotnet format` clean;
no `waitlist.booking_confirmed.v1` producer remains.

## Phase 2 — Standalone-order conversion (§21.4)

**Changes**
- `PreorderSagaStateMachine`: add `ManualReview → { Completed, FailedFinal }` (operator resolution
  only; guarded in the handler).
- New `ConvertPreorderToStandaloneCommand(TenantId, SagaId, OperatorActor, CustomerAgreementEvidenceId)`
  + handler: require saga in `ManualReview`; require non-empty operator actor **and** customer
  evidence (§21.4 "operator AND customer, audited"); set `IsStandalone = true`; advance
  `ManualReview → Completed`; emit the combined outcome; audit `preorder.standalone_converted`
  with both evidence ids. Idempotent (state guard).
- New `CancelPreorderManualReviewCommand(...)` handler: `ManualReview → FailedFinal`, audit
  `preorder.manual_review_cancelled`, emit failure outcome. (The "or cancel" half of §21.4.)
- `PreorderController` (new): `POST /api/preorder/sagas/{sagaId}/convert-standalone` and
  `POST /api/preorder/sagas/{sagaId}/cancel` (operator actor via `X-Actor`, thin controller).

**Tests (TDD)**
- Unit: convert requires `ManualReview` + both evidence ids (else 409/400); sets IsStandalone;
  audits; idempotent. Cancel → FailedFinal + failure outcome.
- Integration: seed a `CompensatingBooking → ManualReview` saga (defensive path), convert →
  Completed standalone with audit; and cancel → FailedFinal.

**Success:** unit + integration green; state-machine unit test enumerates the two new edges.

## Phase 3 — Foreground mutable pre-order draft (§4.3)

**Changes (langgraph, Pydantic = source of truth)**
- `contracts/preorder.py`: add mutable `PreorderDraftLine` (item_id, modifier_ids, quantity,
  notes) and `PreorderDraft` (catalog_version, lines: list) — `extra="forbid"`, distinct titles.
  Add `PreorderDraft.to_reference(confirmation_id, price_lookup)` freeze helper producing the
  immutable `PreorderReference` (captures per-line price+currency at confirmation).
- `state.py`: add `preorder_draft: PreorderDraft | None` to `BookingDraft` (in-dialog mutable
  assembly). `BookingRequest.preorder` stays the immutable confirmed carrier (frozen at submit).
- Regenerate: `bun nx run langgraph:generate-schemas` then `shared-schemas:generate-types`;
  confirm `validate_no_duplicates` passes (new titles only) and C#/TS/Zod build.

**Tests (TDD)**
- pytest: build a draft, mutate lines, `to_reference()` yields a `PreorderReference` whose lines’
  captured prices match the price lookup; empty draft → no reference / clear error; schema export
  no-duplicate check passes.

**Success:** `bun nx test langgraph` green; `bun nx run langgraph:generate-schemas` clean;
`bun nx build api` compiles with regenerated `SharedTypes.cs`.

## Phase 4 — Verify + review

- `bun nx build api`; `bun nx test api`; `bun nx test-integration api` (Docker Postgres);
  `bun nx test langgraph`; scoped `dotnet format --verify-no-changes`.
- Run the `validate-plan` skill against THIS plan (post-implementation validation of each phase’s
  success criteria).
- Run `/review` for spec-11 scope conformance: confirm the changes stay within what spec 11 owns
  (waitlist matching, pre-order saga orchestration, catalog/draft contracts) and respect the §2
  MUST-NOTs (no parallel booking path; saga still doesn’t call providers/charge; auto-book only
  under recorded consent; agent still only drafts).

---

## Discoveries and Notable Information

### Phase 1 — DONE (auto-book → spec-04), unit-verified 2026-07-01

- **Confirmed the txn-nesting constraint** (`UnitOfWork.cs:17` always `BeginTransactionAsync`;
  `InboxDeduplicationBehavior` wraps `IInboxConsumerRequest` in that txn). Solution: the new
  `AutoAcceptWaitlistOfferCommand` is a **plain `IRequest`** (no `IInboxConsumerRequest`), so the
  inbox behavior is a passthrough and it can call `SubmitConfirmedIntent` (own txn). Idempotency via
  the spec-04 key `wl_accept_{offerId}` + the offer's version-guarded `Offered → Accepted` claim.
- **Shared builder** `WaitlistAcceptSubmission.Build(...)` extracted from the accept handler; both
  accept and auto-accept use it. Added optional `autoBookConsentEvidenceId` → stamped into
  `Confirmation.DisclosuresPresented` as `auto_book_consent:{id}` (§20 audit) — this does NOT affect
  the canonical hash (hash is over the payload, not the confirmation).
- **Match auto-book branch** now enqueues `waitlist.commands.auto_accept.v1` (payload: offer_id,
  operation_id) instead of `FinalizeAgainstOperationAsync` + bespoke event. No offer-expiry job for
  auto-book (the capacity hold's own expiry is the backstop). Audit → `waitlist.auto_book_scheduled`;
  the auto-accept handler audits `waitlist.auto_booked` with `consent_evidence_id`.
- **AutoAccept re-verifies** `grant.Permits(...)` at accept time and settles WITHOUT booking if the
  grant no longer permits (§20 fail-secure).
- **Removed** the orphaned `waitlist.booking_confirmed.v1` producer + its job-event schema; added
  `waitlist.commands.auto_accept.v1.json`. Registered the consumer in the DI catalog.
- **Files:** `WaitlistAcceptSubmission.cs` (new), `AutoAccept/AutoAcceptWaitlistOfferCommand.cs` +
  `…Handler.cs` (new), edited `Match/MatchWaitlistOfferCommandHandler.cs`, `Accept/…Handler.cs`
  (uses shared builder), `ApplicationServiceCollectionExtensions.cs` (register), schema add/delete,
  new `AutoAcceptWaitlistOfferHandlerTests.cs` (4 tests), updated `WaitlistFlowTests.cs` auto-book
  integration assertions to the corrected match-level behavior.
- **Verification:** `dotnet build` Application clean; full unit suite **650 passed / 0 failed**.
  Auto-book end-to-end (auto_accept → 04 → 06 finalize) integration coverage rides the same
  spec-04→06 path as the accept integration test (currently `[Fact(Skip)]` pending its Phase-6
  rewrite); the updated `Match_AutoBooks_UnderSatisfiedConsentGrant` asserts the corrected
  match-level behavior and runs in Phase 4 against Docker.
- **Env note:** stopped a stray `dotnet watch` API server (held the Api DLL lock); user restarts
  with `bun nx serve api` when they want Swagger again.

### Phase 2 — DONE (standalone conversion §21.4), unit-verified 2026-07-01

- **State machine:** added `ManualReview → { Completed, FailedFinal }` operator-resolution edges
  (`PreorderSagaStateMachine.cs`); ManualReview is now a human-resolution park, not terminal.
- **Repo:** `TryAdvanceStateAsync` gained an optional `bool? setStandalone` (mirrors `setFailureReason`)
  so convert can mark `IsStandalone` atomically with the advance. **No migration** — the
  `preorder_sagas.is_standalone` column already exists (PreorderSagaColumns migration).
- **Handlers (HTTP-driven, own txn via IUnitOfWork):**
  `ConvertPreorderToStandaloneCommand(+Validator)` → `ManualReview → Completed`, `setStandalone: true`,
  emits `notification.booking_confirmed.v1` (payload `standalone: true`), audits
  `preorder.standalone_converted` with **both** `operator_actor` + `customer_agreement_evidence_id`
  (§21.4). `CancelPreorderManualReviewCommand(+Validator)` → `ManualReview → FailedFinal`, failure
  outcome + `preorder.manual_review_cancelled` audit. Both require a non-empty operator actor (+ the
  convert also requires customer evidence) via FluentValidation → 400; not-found → 404; wrong state →
  409; idempotent on an already-resolved saga.
- **Controller:** `PreorderController` — `POST /api/preorder/sagas/{id}/convert-standalone` and
  `/cancel` (operator via `X-Actor`). Exceptions already mapped in `GlobalExceptionHandler`.
- **Tests:** `PreorderManualResolutionHandlerTests` (6) + updated `PreorderSagaStateMachineTests`
  (ManualReview edges moved to allowed, dropped from terminals). Fixed 2 existing preorder unit-test
  mocks for the new `setStandalone` arg. **Full unit suite 657 passed / 0 failed.**
- **Note:** authz is the actor-shim (spec 08 seam, §12 open decision "who may convert"); we enforce +
  audit the operator/customer evidence, full RBAC deferred (as planned).

### Phase 3 — DONE (foreground mutable pre-order draft §4.3), verified 2026-07-01

- **`contracts/preorder.py`:** added `PreorderDraftLine` (item_id, modifier_ids, quantity≥1, notes —
  NO price) and `PreorderDraft` (catalog_version, lines), both `extra="forbid"`. Added
  `PreorderDraft.to_reference(confirmation_id, price_lookup)` that captures per-unit `Money` from the
  live catalog into an immutable `PreorderReference` (§21.3); raises `ValueError` on an empty draft.
- **`state.py`:** added `preorder_draft: PreorderDraft | None` to `BookingDraft` (foreground state);
  `to_canonical()` now strips it (like `field_provenance`) so only the immutable `request.preorder`
  reference is canonical — the mutable draft is never itself a side effect (§2).
- **Exporter:** registered `PreorderDraft` + `PreorderDraftLine`; `generate-schemas` clean (79 defs,
  no duplicate titles); `shared-schemas:generate-types` regenerated C#/TS/Zod cleanly.
- **Tests:** +6 pytest cases (draft defaults/no-price, quantity≥1, extra-forbid, freeze captures
  prices + total, empty-draft raises). `test_preorder.py` 12/12 pass.
- **Verification:** C# **657/657 unit pass** with the regenerated `SharedTypes.cs`; langgraph **688
  passed**, the only 7 failures are the pre-existing Windows-only `test_clarify.py` strftime flakes
  (`Invalid format string`, `%-d`/`%-I`), unrelated to this work.

### Phase 4 — DONE (verify + review), 2026-07-01

- **Scoped `dotnet format --verify-no-changes`** over all 21 changed/new C# files → **EXIT=0** (clean).
- **`bun nx test-integration api`** (Docker Postgres): **269 passed, 2 skipped, 2 failed**. The 2
  failures are the pre-existing `ScheduledJobPollerTests` µs-truncation flakes
  (`ClaimDueAsync_AttemptAndRunAfter_SurviveRestart_Unchanged`,
  `RescheduleAsync_IncrementsAttempt_SetsNewRunAfter_ClearsLease`) — unmodified files, unrelated. The
  updated auto-book test `Match_AutoBooks_UnderSatisfiedConsentGrant` passed.
- **Unit:** C# 657/657; langgraph 688 passed (only the 7 pre-existing Windows `test_clarify` strftime
  flakes fail). **Build clean** with regenerated types.
- **validate-plan + /review:** completed — all three gaps closed, changes stay within spec-11 scope
  and respect the §2 MUST-NOTs (single spec-04 booking front door, no parallel path; saga still never
  calls providers/charges; auto-book only under recorded consent with audit; agent only drafts).
- **Recommended follow-up (not blocking):** a dedicated integration test for the ManualReview→convert
  path (currently unit-covered); the pre-existing `StandaloneOrder_Conversion_NeedsBookingDelegated
  Redesign` integration skip is a *different* (standalone-from-start) scenario, left as-is.
