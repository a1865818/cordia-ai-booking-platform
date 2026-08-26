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
- [ ] `bun nx test api` and `bun nx test langgraph` both green with the flipped vector
- [ ] new test: an `inline:` ref containing a Luhn-valid PAN is masked in the persisted payload
- [ ] **new test: a realistic menu with prices still extracts** — masking must not break ingestion
- [ ] `customer_ref` and the `_ref` suffix policy unchanged

**Manual Verification**
- [ ] Every new vector has ≥3 ASCII digits, or it passes via the `CountAsciiDigits < 3` fast path for the wrong reason.

**Discoveries** _(implementer fills in)_

---

## Phase 3 — Honest cancellation outcome (#53)

`fix(api): report the real outcome of a cancellation request`

Capture the handler's `bool` at `BookingOperationsController.cs:189`. If `false`, re-read the
operation: terminal → **409** (same body as the up-front guard); already-pending → **202** with
`cancellation_pending: true` (idempotent, correct). Keep `IActionResult` — formalising a DTO would
add generated-type surface for no benefit.

**Automated Verification**
- [ ] new integration test races a terminal transition into the window and asserts 409, not a false 202
- [ ] `Cancel_TerminalOp_Returns409` and `Cancel_NonTerminalOp_Returns202` pass unchanged
- [ ] `v1.json` regenerates identically

**Discoveries** _(implementer fills in)_

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
