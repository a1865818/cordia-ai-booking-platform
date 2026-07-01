---
date: 2026-07-01
researcher: minhnt10122003@gmail.com
git_commit: 1a83371
branch: feat/spec-11
repository: Cordia
task: "Spec 11 — Waitlist, Menu Ingestion & Pre-order (consolidated implementation plan)"
tags: [implementation-plan, spec-11, waitlist, menu-ingestion, preorder-saga, langgraph, dotnet, tdd]
status: complete
last_updated: 2026-07-01
last_updated_by: minhnt10122003@gmail.com
---

# Spec 11 — Waitlist, Menu Ingestion & Pre-order (Consolidated Implementation Plan)

> **This is the single source of truth for spec 11.** It consolidates the six
> per-vertical / per-round plans that drove the work (menu-ingestion, menu-finishing,
> waitlist, preorder-saga, divergence-fixes, remaining-gaps). Their full history lives in
> git. Spec: [`docs/specs/11-waitlist-menu-preorder.md`](../../../docs/specs/11-waitlist-menu-preorder.md).

## Overview

Spec 11 delivers three coordinated features downstream of core booking, plus two rounds of
conformance hardening once the sibling specs (04/05/06/07/08/10) merged:

- **(a) Menu ingestion** — `upload → malware scan → isolated extraction → draft catalog → owner
  review → approval → immutable published version`, plus the **published-only** read path that
  backs the foreground tools.
- **(b) Waitlist** — durable enrollment → match → temporary hold (07) → time-boxed offer →
  accept/decline/expire → release, with offer-expiry re-match and consented auto-book.
- **(c) Pre-order saga** — a confirmed intent carrying a `PreorderReference` drives revalidate →
  reserve items → **delegate booking to the 06 engine** → commit pre-order → one combined outcome,
  with compensation and human-resolution parking.

**Governing directive:** treat the repo as a single evolving architecture — integrate with existing
mechanisms (single front doors), never add parallel implementations.

## Status: COMPLETE ✅

All three verticals are implemented and integrated with siblings 04/05/06/07/08/10; every spec
**MUST-NOT** and **don't-own** boundary is upheld in code. Verified at `1a83371`: **664 unit tests
pass**, build clean, scoped `dotnet format` clean, integration suites green (waitlist flow, preorder
happy-path/compensation/recovery/revalidation/persistence).

---

## What was built, by vertical

### (a) Menu ingestion (§1b, §5.2) — `apps/api` + `apps/langgraph`

- **Contracts** (`apps/langgraph/cordia_agent/contracts/catalog.py`, source of truth → C#/TS/Zod):
  `Money`, `CatalogItem` (incl. `modifiers`/`CatalogModifier`, `availability_window`/
  `AvailabilityWindow`, `image_ref`, `allergens`, `dietary_labels`, extraction-only fields),
  `PublishedCatalog`.
- **Isolated extraction agent** (`apps/langgraph/cordia_agent/menu/extraction_agent.py`): sandboxed
  **by construction** — empty `ToolAllowlist` with no setter, treats menu text as `UNTRUSTED`, flags
  (never acts on) injection-like content, can only reach `Extracting → DraftReady`. §27.1/§27.6.
- **Catalog state machine + lifecycle** (`Cordia.Application/Handlers/Menu/*`): ingest consumer
  (`uploaded → scanning → {quarantined | extracting → draft_ready}`, fail-secure), review, approve,
  publish (immutable `catalog_version`, prior-version supersede). `MenuController` (`api/menu`).
- **Published-only read path**: `SearchVendorCatalogQuery` (`SearchPublishedItemsAsync`) +
  `GetCatalogItemQuery` (`GetPublishedItemAsync`), served by `CatalogController` at
  `GET /api/catalog/items[/{item_id}]`; foreground tools `search_vendor_catalog`/`get_catalog_item`
  call these.
- **Fail-surfacing** (Inv-14): `menu.catalog.quarantined.v1` / `menu.catalog.extraction_failed.v1`.
- **Stub ports** (deliberate): `StubArtifactScanner` (`IArtifactScanner`),
  `StubIsolatedMenuExtractor` (`IIsolatedMenuExtractor`) — real ClamAV/LLM swap in behind the ports.

### (b) Waitlist (§1a, §5.1, §20) — `apps/api`

- **Domain**: `WaitlistOfferStateMachine` (`Pending → Matched → Offered → Accepted/Declined/
  Expired/Released`), `AutoBookGrant`/`DepositTerms` value objects, enrollment constraints.
- **Handlers** (`Cordia.Application/Handlers/Waitlist/*`): `Match` (FIFO-first eligible → hold under
  workflow step `create` so 06 reuses it → Matched→Offered → schedule expiry → notify), `Accept`
  (mints a spec-04 confirmed revision), `Decline`/`Expire` (release hold via 07 + re-match next
  enrollee), `Enroll`/`Cancel`. `WaitlistController` (`api/waitlist`).
- **Auto-book (§20, Inv-1)**: a match satisfying a recorded `AutoBookGrant` enqueues an `AutoAccept`
  step (`waitlist.commands.auto_accept.v1`) which mints an auditable spec-04 revision through the
  **same front door** as a customer accept — never a bespoke booking path — re-verifying the grant
  fail-secure and stamping consent evidence into the confirmation + audit.

### (c) Pre-order saga (§1c, §5.3, §21) — `apps/api` + `apps/langgraph`

- **Contracts**: immutable `PreorderReference`/`PreorderLineReference` embedded in the intent-revision
  (part of the canonical hash, Inv-1); foreground mutable `PreorderDraft`/`PreorderDraftLine` in
  `BookingDraft` (§4.3), stripped from `to_canonical()` so the draft is never a side effect.
- **Saga** (`Cordia.Application/Handlers/Preorder/*`, `PreorderSagaStateMachine`): `Revalidating →
  ReservingItems → CommittingBooking (await 06) → CommittingPreorder → Completed`; `LiveItemRevalidation`
  gate first (Inv-10); combined outcome emitted only after Completed (Inv-8).
- **Compensation / recovery**: item-reserve or 06-failure → `CompensatingItems → FailedFinal`;
  post-commit pre-order failure → `CompensatingBooking → ManualReview`; `Reconciling` hub;
  per-step `scheduled_jobs` timeouts + hosted `PreorderRecoverySweeper` backstop.
- **§21.4 standalone conversion**: `ManualReview → {Completed(standalone) | FailedFinal}` — customer
  half enforced by a single-use **spec-08 verification grant** (`Modify`, scoped to the committed
  booking) via `IGrantAuthorizationService`; operator recorded/audited.

---

## Conformance hardening (post-sibling-merge)

Two rounds closed the gap between "functionally approximate" and "spec-conformant" once
04/05/06/07/08/10 landed:

### Divergence fixes
- **Booking delegated to spec-06 (#2).** The saga no longer finalizes its own capacity or emits a
  bespoke "confirmed" over a booking that was never created. It reserves items, emits
  `booking.command.requested.v1`, and the 06 engine owns capacity (07) / payment (09) / provider-
  create (06). Every 06 terminal emit site (`Finalize`, `Compensate`×2, `Reconcile`,
  `RespondToAlternatives`, `ResolveManualReview`) branches through
  `PreorderSagaMessages.ConfirmedOutcomeType`/`FailedOutcomeType(isPreorder)` to signal the saga
  (`preorder.commands.booking_committed.v1` / `booking_failed.v1`) instead of notifying the customer.
  This supersedes the self-owned `HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold` steps
  (enum members retained but unwired).
- **Waitlist accept mints a real revision (#1)** — through spec-04 (see auto-book above).
- **Per-step timeouts + hosted recovery (#3)** — `scheduled_jobs` step timeouts + hosted sweeper.

### Remaining conformance gaps (from the 2026-07-01 audit)
- **Auto-book through the spec-04 front door** (Inv-1/§20) — done (see waitlist).
- **Standalone-order conversion** (§21.4) — done (see saga); later upgraded to consume a real
  spec-08 grant for the customer-agreement half.
- **Foreground mutable pre-order draft** (§4.3) — done (see contracts).
- **Contract/doc hygiene** — added the `booking_committed.v1`/`booking_failed.v1` job-event schemas,
  fixed the stale `preorder.commands.step.v1` enum, refreshed the spec status/§9.

---

## Key architectural decisions & discoveries (verified)

- **Single booking front door.** Both offer-accept and auto-book mint through
  `SubmitConfirmedIntentCommand` (spec-04); the saga delegates the whole booking to spec-06. No
  parallel booking/capacity path exists.
- **`UnitOfWork.ExecuteInTransactionAsync` always `BeginTransactionAsync`** → nesting throws. A
  consumer that must call `SubmitConfirmedIntentCommand` (which opens its own txn) must be a plain
  `IRequest`, **not** an `IInboxConsumerRequest` (which the inbox behavior already wraps in one txn).
  This is why `AutoAcceptWaitlistOfferCommand` is a plain `IRequest`.
- **Capacity reuse, not double-count.** The waitlist hold is reserved under workflow step `create`
  so the 06 engine's idempotency pre-check finds and reuses it.
- **`IGrantAuthorizationService` is customer-scoped** (keyed by `customerRef`/`targetBookingId`) — it
  fits the *customer* half of §21.4 conversion but is **not** staff RBAC. Operator-role gating for
  approve/publish + the operator half of conversion remains the §12 open decision.
- **Windows/EF**: new `.cs` files need UTF-8 BOM + LF; scoped `dotnet format --include <files>` is the
  lint gate. (Repo-wide `dotnet format` currently trips on pre-existing CRLF — a `.gitattributes`
  normalizing `*.cs` to LF would fix the pre-commit hook.)

---

## Deferred by design (§12 open decisions / cross-spec seams — NOT gaps)

These are intentional non-goals, flagged as seams for future specs to wire in:

- **Payment (spec 09)** — the saga delegates booking+payment to the 06 engine; deposits flow through
  automatically once 09 lands. No charge path exists today (so auto-charge is structurally impossible).
- **Staff RBAC** — catalog approve/publish + the operator half of conversion are audited (`X-Actor`)
  but not role-gated (§12 owner/ops roles).
- **Auto-book consent UX + `ConsentEvidenceId` format** (§20 / spec 14) — `AutoBookGrant` is wired and
  enforced; only the consent-capture UX is pending.
- **Real menu extractor / ClamAV** behind `IIsolatedMenuExtractor` / `IArtifactScanner` (stub ports today).
- **Foreground waitlist enrollment inside a confirmed intent** (§2/§9) — optional; enrollment works via
  `WaitlistController` today.
- **Retention windows** (specs 12/14) and **initial SLO values** (spec 13).
- **`apps/web` owner-review UI** — out of scope for this vertical.

---

## Test strategy (from §27)

- **Unit** (`Category=Unit`): catalog search/lookup, extraction flagging + empty-tool-surface
  isolation, waitlist + preorder state machines, auto-book grant satisfaction, standalone
  convert/cancel + grant consumption, outcome routing, step timeouts.
- **Integration** (`Category=Integration`, Alba + real Postgres): publish → foreground visibility,
  offer-expiry, waitlist flow, preorder happy-path / compensation / recovery-resume / revalidation,
  persistence.

## References

- Spec: `docs/specs/11-waitlist-menu-preorder.md`
- Superseded plans (git history): `2026-06-30_spec-11-menu-ingestion.md`,
  `2026-06-30_spec-11-menu-finishing.md`, `2026-06-30_spec-11-waitlist.md`,
  `2026-06-30_spec-11-preorder-saga.md`, `2026-07-01_spec-11-divergence-fixes.md`,
  `2026-07-01_spec-11-remaining-gaps.md`
