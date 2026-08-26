# Wave 1 — Cheap correctness and hygiene

Status: Plan · Branch: `fix/wave-1-correctness` · Base: `chore/wave-0-foundations` @958a168
Research: [`../research/2026-08-26_wave-1-correctness.md`](../research/2026-08-26_wave-1-correctness.md)
Closes: #96, #95, #53, #81, #77, #85, #79 · Partially: #94 (D9)

## How this maps onto Cordia

| New concept | Existing artifact it extends | Consequence |
|---|---|---|
| Concurrent outbox lanes | `LogicalQueueRegistry.All`'s **existing** `OrderingRequired` flag | No new concept. Group the claimed batch by `LogicalQueue`, run groups concurrently, stay sequential inside a group. The metadata to do this correctly already exists and was unused for it. |
| Longer publisher lease | `OutboxPublisherOptions.LeaseSeconds` | One value. Resolves spec 05 OD-3 at 300 s (2.5× the 120 s extraction). |
| Artifact-ref masking | `SensitiveDataMask._controlPlaneKeys` + `redaction.py._CONTROL_PLANE_KEYS` | Remove one entry from a hand-curated allowlist, in both lanes, pinned by the shared corpus. |
| Honest cancel response | `RequestCancellationCommandHandler`'s existing `bool` | Stop discarding a return value that is already correct and already tested. |
| Correlation provenance | `ICorrelationContext` / `CorrelationMiddleware` | Delete a second, unvalidated path; use the one that already validates. |
| Swagger gate | `OpenApiExport:Enabled`, kept in Wave 0 for exactly this | The exporter already sets it. |
| §13-C enforcement | a new web test | First automated guard; prose-only today. |

**Governing directive:** integrate with existing mechanisms; never add a parallel implementation.

## Spec ownership & frozen decisions

| Contract | Owner | This wave is | Action |
|---|---|---|---|
| Outbox/inbox protocol, lease semantics | spec 05 | **referencer** | resolves **OD-3** (user decision, 300 s) |
| `LogicalQueue` ordering metadata | spec 05 | **referencer** | read only |
| Redaction / Inv-12 | spec 14 | **referencer** | removes an exemption *toward* the spec |
| Cancellation + terminal set | spec 06 | **referencer** | fixes to match failure-mode row 13 |
| Correlation-id origin | spec 15 | **referencer** | removes an unsanctioned third mint point |
| All DDL | spec 12 | **referencer** | **no migration this wave** |

**Invariants:** Inv-12 (#95), Inv-16 (#53), Inv-5 (#96 must not weaken), Inv-13 (untouched).

---

## Phase 1 — Outbox lanes and lease (#96)

`fix(api): publish outbox lanes concurrently and size the lease to the slowest consumer`

1. `PublishPendingBatchAsync`: after the claim scope closes, group `claimed` by resolved
   `LogicalQueue` **in memory**. Dispatch groups with `Task.WhenAll`; **within** a group keep the
   existing sequential `foreach` verbatim. Unroutable rows keep dead-lettering before any grouping.
2. `MessagingOptions.cs:208` `OutboxPublisherOptions.LeaseSeconds` 30 → **300**, with a comment
   citing OD-3 and the 120 s `ExtractTimeoutSeconds`.
3. **Do not touch `:87`** (`ScheduledJobPollerOptions`) — different lane, governs Wave 2 redelivery.

**Automated Verification**
- [x] `SlowLane_DoesNotBlock_ADifferentLane` — slow `menu.commands` row stamped OLDER so the claim puts it first
- [x] `OrderedLane_PublishesInOccurredAtOrder` — 3 rows seeded out of order, published in `occurred_at` order
- [x] `MessagingOptions.cs` diff shows **exactly one** changed value line; `:87` still 30
- [x] `bun nx test api` **1812 passed** · `bun nx lint api` clean · `bun nx test-integration api` **672 / 671 passed / 0 failed**, 45 s (baseline 670/669)

**Manual Verification**
- [x] All 7 existing `OutboxPublisherTests` pass unchanged — the file was not touched at all.
- [x] Mutation verified, see discovery 2.

**Discoveries**

1. **The registry already had the metadata, so the fix is smaller and safer than planned.**
   `LogicalQueueMetadata.OrderingRequired` (`LogicalQueue.cs:55-64`) is already `true` for
   `booking.commands` and `booking.reconcile` and `false` for the rest — including `menu.commands`,
   which carries the slow call. The starved lanes were never the same queue as the slow one, so
   grouping by queue removes head-of-line blocking **without touching any ordering guarantee**. No
   SQL predicate, no mirrored edit to the Wave 0 tenant-scoped claim, no new concept.

2. **Mutation-verified.** Restoring the original sequential dispatch turns
   `SlowLane_DoesNotBlock_ADifferentLane` **red** — it waits out the full 10 s cross-lane budget
   (10 s run vs 827 ms) and reports the diagnostic it was written to emit. Restoring the lane
   version returns both to green. `OrderedLane_PublishesInOccurredAtOrder` passed under **both**,
   which is correct: it pins ordering, not concurrency.

3. **Three properties deliberately preserved.**
   - The per-row body was lifted **verbatim** into `PublishLaneAsync`; only scheduling moved.
   - Unroutable rows dead-letter **before** any lane starts — they touch no transport and must not
     queue behind a slow lane.
   - Concurrency is bounded by *distinct queues in the batch* (≤ 9), never by `BatchSize`, so this
     cannot open an unbounded number of scopes or connections.

4. **New tests live in a new file.** `OutboxPublisherTests` is the one class Wave 0 deliberately
   left holding the neutralize helper, because its first test is the only direct pin on production's
   cross-tenant claim. Adding to it risked that; `OutboxLaneTests` is separate and scopes its own
   claim per tenant — the Wave 0 lesson applied rather than re-learned.

5. **OD-3 is resolved by value, not by machinery.** The XML doc on `LeaseSeconds` now records why
   300 s (2.5× the 120 s `ExtractTimeoutSeconds`), states that heartbeat renewal is deliberately
   *not* implemented, and warns that the other `LeaseSeconds = 30` at `:87` is a different lane and
   must not be "aligned".

---

## Phase 2 — Artifact-ref masking (#95)

`fix(security): mask content-bearing artifact refs in both redaction lanes`

Remove `source_artifact_ref` from `SensitiveDataMask.cs:97` **and** `redaction.py:191`; flip the
`system-ref-artifact` vector to `expect_exempt: false`; add vectors for an `inline:` menu carrying a
Luhn-valid PAN and for the `_UNCONDITIONAL_MASK_DIGITS = 50` path. Update the `UploadMenuCommandHandler`
comment, which currently documents the hole.

**One commit across both lanes** — the corpus is shared; splitting reds one suite.

**Automated Verification**
- [x] `bun nx test api` **1815 passed** (was 1812) · `bun nx test langgraph` **1431 passed** (was 1428) · both lints clean
- [x] `InlineArtifactRefCarryingPan_IsMaskedInThePersistedPayload` + the `data:` twin — asserted against the row as persisted
- [x] `ArtifactRefWithOrdinaryMenuPrices_SurvivesVerbatim` — masking does not break ingestion
- [x] `customer_ref` still `expect_exempt: false`; `_ref` suffix policy untouched
- [x] `bun nx test-integration api` **676 / 675 passed / 0 failed**, 32 s (baseline 672/671)

**Manual Verification**
- [x] New vectors carry 16, 15 and 21 ASCII digits — all clear the `CountAsciiDigits < 3` fast path.

**Discoveries**

1. **The choke point was already running and doing nothing.** `OutboxRepository.AddAsync:12-19`
   calls `SanitizeJson` on every write as the documented Inv-12 choke point. It executed, saw
   `source_artifact_ref` on the exemption list, and returned the value verbatim. This is the same
   failure shape Wave 0 kept finding in tests — a guard that runs and cannot fire — but in
   production code.

2. **Mutation was precise.** Restoring the exemption reds exactly the two PAN tests
   (`Inline…`, `DataUrl…`) and leaves the two that must not move green. That asymmetry is the point:
   it proves one entry left the list, not the mechanism.

3. **The cost of the fix is bounded and now asserted.** `IngestVendorMenuCommandHandler` reads the
   ref straight back out and hands it to the scanner and extractor, so a fix that mangled ordinary
   content would break ingestion rather than secure it. Masking replaces only *detected* spans and a
   price list contains no Luhn-valid 13–19 digit run — asserted by
   `ArtifactRefWithOrdinaryMenuPrices_SurvivesVerbatim` rather than assumed.

4. **`provider_ref` deliberately stays exempt**, with its own regression test. Masking a
   Luhn-coincidental provider reference would break the §16.2 reconciliation joins that look up by
   it. `ProviderRef_RemainsExempt_AndRoundTripsVerbatim` fails if anyone "tidies" the list by
   deleting the mechanism.

5. **An obsolete instruction was removed, not just a value.** `UploadMenuCommandHandler`'s comment
   said *"The exemption is DELIBERATE and pinned by cross-language vectors … do not flip it here."*
   Left in place it would have told the next reader to undo this change.

6. **#84 is still the structural fix.** Once artifacts live in object storage the ref is a genuinely
   opaque key and there is no document to mask. This closes the leak; it does not remove the reason
   the leak was possible.

---

## Phase 3 — Honest cancellation outcome (#53)

`fix(api): report the real outcome of a cancellation request`

Capture the handler's `bool` at `BookingOperationsController.cs:189`. If `false`, re-read the
operation: terminal → **409** (same body as the up-front guard); already-pending → **202** with
`cancellation_pending: true` (idempotent, correct). Keep `IActionResult` — formalising a DTO would
add generated-type surface for no benefit.

**Automated Verification**
- [x] the window is exercised deterministically — **as unit tests, not an integration race**; see discovery 2
- [x] `Cancel_TerminalOp_Returns409` and `Cancel_NonTerminalOp_Returns202` pass unchanged
- [x] `v1.json` regenerates identically — no drift from touching the controller
- [x] `bun nx test api` **1819 passed** (was 1815) · lint clean · integration **676 / 675 passed / 0 failed**, 38 s

**Discoveries**

1. **The handler was already right; the whole defect was a discarded return value.** Its repository
   filter is `CancellationRequestedAt == null AND NOT terminal`, and
   `CompensationAndCancellationTests.cs:758-802` already asserted it returns `false` for a terminal
   op. The fix captures that value.

2. **Deviation from the plan, recorded rather than substituted quietly.** The plan asked for an
   *integration* test that races a terminal transition into the window. It is not written that way.
   Reproducing the window through the HTTP stack means winning a timing race on every CI run, and
   this repo has already declined that kind of test once as inherently flaky
   (`2026-08-04_frontend-api-wiring` review). Mocking `IMediator` makes the window deterministic —
   the second `GetOperationStatusQuery` answers differently from the first, which is exactly what a
   lost race looks like from the controller's side.

3. **`false` is ambiguous by design, so the fix re-reads rather than guesses.** It means "already
   pending" *or* "raced into terminal", and those need different answers (202 vs 409). A third case
   fell out of writing it: the row can also vanish inside the window, which is a 404, not a
   fabricated 202.

4. **The 409 body is shared between both checks**, so a caller cannot tell whether the up-front
   guard or the post-write re-read fired.

5. **Mutation-verified.** Discarding the result again reds exactly
   `RacedIntoTerminal_Returns409_NotAFalsePending` and `DisappearsInsideTheWindow_Returns404`; both
   202 paths stay green, which is correct — they return 202 either way.

6. **Left alone deliberately:** `BookingOperationsController.cs:161`'s `T11 TODO: surface
   cancellation_pending in the status projection`. `GetOperationStatusQuery` surfaces pending-ness
   through `CustomerMessage` because a dedicated field needs a Pydantic change and a cross-language
   regen. Adjacent, but genuinely separate work — Phase 6 retags it.

---

## Phase 4 — CORS, correlation, Swagger (#81, #77, #94 D9)

`fix(api): tighten CORS, correlation provenance and Swagger exposure`

- **#81:** replace `AllowAnyHeader().AllowAnyMethod()` with the explicit header/method sets the
  console uses. Add the missing `CorsPolicyTests`.
- **#77:** `VerificationsController` reads `ICorrelationContext` instead of `[FromHeader]`; drop the
  `TraceIdentifier` fallback.
- **#94 D9:** gate `UseSwagger`/`UseSwaggerUI` on `IsDevelopment() || OpenApiExport:Enabled`. They sit
  *before* `UseCors`, so the gate goes on the calls.

**Automated Verification**
- [ ] `bun nx run api-contract:generate-types` then `git diff --exit-code libs/api-contract/openapi/v1.json` — the exporter must still scrape `/swagger`
- [ ] new CORS test; `CorrelationChainContractTests` passes unchanged

**Discoveries** _(implementer fills in)_

---

## Phase 5 — Docs to current truth + §13-C guard (#85)

`chore(docs): correct the stale gap inventory and enforce the §13-C console guardrail`

Rewrite `docs/backend-gaps.md`: mark **G1–G16 closed** with evidence, keep **G17/G18** open, correct
the TL;DR. Fix `frontend-api-wiring.md` §12 gaps 1, 2, 9. Add a web test asserting none of the six
§13-C paths is called, allowlisting the four legitimate mentions by exact path.

**Trap:** `/api/notification` is a prefix of both the forbidden C3 path and #83's future
`/api/notifications`. Anchor on full paths. `GET /api/consent-policies` is a different resource from
`POST …/consent`.

**Automated Verification**
- [ ] the guard fails when a forbidden path is added to a component, and passes on `main`
- [ ] every closed-gap claim carries a `file:line`
- [ ] **cannot run `bun nx test web` locally (Bun 1.3.14 < 1.4.0)** — CI verifies

**Discoveries** _(implementer fills in)_

---

## Phase 6 — Retag deferred TODOs (#79)

`chore(api): retag deferred TODOs to issues`

Only **4** real TODOs exist. Retag each to an issue; note `NotificationWebhooksController.cs:59` is a
duplicate of **#74** and `BookingOperationsController.cs:161` is adjacent to **#53** (Phase 3 may
close it). The 25 `TODO(cordia)` markers are in `.github/workflows` and belong to **#94** — out of
scope. Re-scope the issue body to match.

**Discoveries** _(implementer fills in)_

---

## What we're NOT doing

- No migration; no DDL. No Pydantic contract change.
- Not touching `ScheduledJobPollerOptions.LeaseSeconds` (`:87`).
- Not adding lease **renewal** (OD-3's other half) — user chose the value, not the machinery.
- Not changing `apps/web` behaviour; Phase 5 adds a test only.
- Not formalising #53's response DTO.
- Not fixing #74 (HMAC) or #84 (object storage) — later waves.
- Not weakening `customer_ref` masking or the `_ref` suffix policy.

## Cross-cutting

- **Inv-12:** Phase 2 moves *toward* the spec. Both lanes change in one commit.
- **Inv-5:** Phase 1 must not weaken duplicate-safety; the inbox `ON CONFLICT` is untouched.
- **Inv-16:** Phase 3 is the point.
- One nx command at a time; zero `testhost` before trusting counts; tee to `.tmp/`.
- New `.cs` files UTF-8 **with BOM**, LF endings.
- `SKIP=api-contract-generate` is safe on intermediate test-only commits.

## Package notes

**No new packages** in any lane.
