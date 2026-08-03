# Frontend ↔ API Wiring — Gap Closure Implementation Plan

**Date:** 2026-08-03 · **Branch:** `feat/ui-wire` · **Baseline audit:**
`thoughts/shared/research/2026-08-03_22-54-00_frontend-wiring-gap-closure.md` ·
**Gap catalogue:** `docs/frontend-api-wiring-gaps.md` · **Requirement:** `docs/frontend-api-wiring.md`

## Overview

Bring the console into full conformance with the two rules `docs/frontend-api-wiring.md` §11 sets
for itself —

1. *A successful-but-empty response is a real answer* (demo data never papers over an error), and
2. *Nothing is asserted that the API does not record* —

and close the reachability gaps in flows §11 already claims are live (verification, waitlist).
Plus one security fix the spec-15 landing exposed.

This is **web-side work** with two small, additive API changes. No Pydantic model, JSON Schema,
pg-enum, table or migration is touched, so **no type regeneration is required**.

## Current State

`feat/ui-wire` (uncommitted: 50 modified + 42 untracked). All lanes green except one known-flaky
integration test. Six console read endpoints live; create and cancel live end-to-end; reschedule
built but gated off behind `RESCHEDULE_FULFILMENT_SUPPORTED = false`.

## Key discoveries (drive the phase order)

1. **The calendar is dated by a mock constant while showing live rows.** `WEEK` (`data/console.ts:275`)
   pins *9–15 June, today = Thursday*; `weekWindow()` fetches the **real** current week. Every date
   an operator reads on a live booking is fabricated. Highest severity, so it goes first.
2. **`Confirmation.identity` is a *verified identity reference*** ("Verified identity, or
   `anonymous_session` sentinel" — `models/intent.py:56-58`), and `BookingRequest` has **no**
   customer field at all. The create drawer putting the guest's typed name there is both a spec-14
   minimisation violation and inconsistent with cancel/reschedule, which already send `getActor()`.
   The web fix is unambiguous; attaching a real guest identity is a spec 04/08/12 decision and is
   **out of scope**.
3. **The transcript query is the one read outside `loadFailures`** — and it falls back to the mock
   `MESSAGES` map, so the most sensitive read in the product fails silently to an empty thread.
4. **`acceptOffer`/`declineOffer` already exist and are called from nowhere**; the enrollment
   summary already carries `latest_offer_status` / `latest_offer_expires_at`. Phase 5 is wiring, not
   building.
5. **`TenantResolutionMiddleware` is registered unconditionally** despite its own "DEV/TEST-ONLY"
   contract. Subordinate to a verified signed context, but authoritative when none is present.

## How this maps onto Cordia

| New concept | Existing Cordia artifact it reuses/extends | Consequence |
|---|---|---|
| Real calendar dates | `weekWindow()` / `toDayAndStart()` in `features/bookings/adapters.ts` — already the *only* correct date logic in the app | `WEEK` stops driving live surfaces; one date source, not two |
| Week navigation | the existing `scheduleWindow` `useMemo` becomes state; `queryKeys.bookings.schedule(from,to)` already keys per window | react-query caches each week for free; no new fetch machinery |
| Truthful queue rows | `OPERATION_STATUS_COPY` (D1's 16 statuses → operator language) already exists | remove fabrication; keep the ratified copy map and the `dismissalKey` state-aware dismissal |
| Transcript failure surface | the existing `ErrorState` + `loadFailures` pattern from `ConsolePage` | one pattern for all six reads, not five plus an exception |
| Verification reach | `VerificationGate` + `isApiVerificationRequiredError` (matches on `code`, never `title`) | the gate becomes reachable from gated reads without new components |
| Waitlist offers | `useRemoveFromWaitlist`'s optimistic-with-rollback shape | offer accept/decline mirror the proven mutation, incl. `actor: true` audit |
| Demo honesty | `DemoBadge` / `PartialBadge` | no new component; the three unbadged panels adopt the existing marker |
| Tenant shim gate | `app.Environment.IsDevelopment()` + existing `TenantContextVerificationMiddleware` ordering | shim keeps its dev role, loses its production reach |

**Governing directive:** integrate with existing mechanisms (single front doors); never add a
parallel implementation.

## Spec ownership & frozen decisions

| Contract / mechanism | Owner | This plan | Constraint in play |
|---|---|---|---|
| `SubmitConfirmedIntentRequest`, canonical hash, idempotency identity | spec 04 | **references** — byte-for-byte unchanged | D3, Inv-2/3/4 |
| `Confirmation.identity`, `verification_level`, verification grants | spec 08 | **references** — we change *what the console puts in* `identity`, not the contract | D5, Inv-11 |
| `operation_status` (16) / normalized set | spec 06 | **references** | D1, D2 |
| Waitlist enrollment + offer lifecycle, auto-book consent | spec 11 | **references** | Inv-1 (§20 silent auto-book prohibited) |
| `customer_ref` minimisation, sensitive-read purposes | spec 14 | **references** | Inv-12 + minimisation |
| Tenant resolution / `TenantContext` | spec 15 | **references** | **Inv-13** |
| Console read projections (`Cordia.Domain/Console/ConsoleProjections.cs`) | this branch | **owns** | no DDL — spec 12 owns all DDL |

**No DDL, no migration, no pg-enum, no Pydantic change ⇒ no `generate-schemas` / `generate-types` run.**

## Blast radius (from the baseline audit)

| Item (audit ref) | Verdict |
|---|---|
| B1 five list queries + `loadFailures` + demo-only-on-`undefined` rule | **intentionally extended** (transcript joins the pattern); the five existing behaviours must not change |
| B2 `WEEK`-driven date labels | **intentionally changed** |
| B3 queue `dismissalKey` state-aware dismissal | **must stay byte-for-byte** |
| B3 fabricated wait/retries/channel + button label | **intentionally changed** |
| B4 transcript purpose gate, `staleTime: Infinity`, no refetch-on-focus, live-session-only | **must stay byte-for-byte** |
| B4 transcript error path, thread `status`/`prov` | **intentionally changed** |
| B5 `useRemoveFromWaitlist` optimistic rollback + its 2 tests | **must stay byte-for-byte** |
| B6 canon.v1 hash binding, attempt-identity reuse, `retry: false`, terminal wording, reschedule gate | **must stay byte-for-byte** |
| B6 create `identityRef`; cancel pre-read grant | **intentionally changed** |
| B7 gate-before-submit, in-memory grant, match on `code` | **must stay byte-for-byte** |
| B8 consent-policy + catalog live panels | **must stay byte-for-byte** |
| B9 header set, shim subordination, `TenantGuard` fail-closed | **must stay**; shim *registration* intentionally gated |
| `data/console.ts` **types** (`Booking`, `QueueItem`, `Thread`, …) | may gain optional fields; **no field removed, no demo constant deleted** — demo screens still consume them |
| Generated `libs/*/generated/**`, applied migrations, `Program.cs` middleware **order** | **untouched** |
| langgraph lane | **untouched** |

Anything not in this table is out of bounds for Stage 4.

## What we're NOT doing

- **G1 — modify/reschedule fulfilment.** `docs/frontend-api-wiring.md` §11 says in terms: *"This is
  spec 06/08 work, not frontend wiring."* The console already refuses truthfully. Out of scope.
- **G3 contract half — a guest identity on staff-made bookings.** Needs a spec 04/08/12 decision
  (new payload field or an identity-registration call). We fix the *misuse*; we do not invent a field.
- **Operator operation actions** (`resume`/`resolve`/`alternatives`/`cancel` on an operation) and
  **conversation writes** (reply, consent, transcript-turns) — §11 records these as known
  follow-ups, not as claims. No new UI flows.
- **Deposits / Campaigns backends**, analytics KPI tiles, "who handled them" — no spec owns them;
  they stay badged demo.
- **Routing restructure / deep links / URL state**, pagination or cursors, a projected indexed
  `starts_at` column (that is spec-12 DDL), `IActionResult → ActionResult<T>` (would regenerate the
  contract), design-system changes, new dependencies.
- **No new packages.** Everything below uses what is already installed.

## Cross-cutting requirements

- **Inv-13 (tenant scope):** Phase 7 narrows, never widens, tenant resolution. The signed-context
  path and `TenantGuard` fail-closed behaviour must be unchanged; add a test proving the shim is
  inert outside Development and that a verified context still wins.
- **Inv-11 (authorization):** grants stay server-verified, in memory only, obtained before the first
  submit. Phase 4 only *forwards* a grant that already exists — it never fabricates or persists one.
- **Inv-2/3/4 (idempotency):** no change to attempt identity, canonical hashing, or key reuse. Phase 6
  changes one *value* inside `confirmation.identity`; it must not change the canonical payload, so the
  **hash must be unaffected** (`identity` lives on the confirmation, not the revision — verify).
- **Inv-16 (truthful reporting):** every phase here exists to serve it. No screen may state an
  outcome the API did not report.
- **Inv-12 / spec-14 minimisation:** Phase 6 stops the console writing a plain-text guest name into
  the minimised `customer_ref` path.
- **Encoding:** any new/edited `.cs` file is UTF-8 **with BOM** or `bun nx lint api` fails.
- **FluentValidation** remains the sole required-field enforcement (no `[Required]`).
- Run **one `nx` command at a time**; tee to `.tmp/`, never pipe to `tail`.

---

## Phase 1 — Real calendar dates + week navigation (G2) 🔴

**Files:** `features/bookings/adapters.ts`, `pages/ConsolePage.tsx`,
`components/platform/screens/TodayScreen.tsx`, `components/platform/drawers/{BookingDetailDrawer,
RescheduleDrawer,CreateBookingDrawer}.tsx`, new `features/bookings/week.ts` (+ test).

1. New `features/bookings/week.ts`: `buildWeek(weekStartIso)` → `{ dows, doms, month, rangeLabel,
   todayIndex | null, dates: Date[] }`, derived entirely from the window the schedule query actually
   used. `todayIndex` is **null** when the displayed week does not contain today (so nothing is
   highlighted as "today" in another week).
2. `scheduleWindow` becomes state (`useState(() => weekWindow())`) with `goToPrevWeek / goToNextWeek /
   goToThisWeek`. The query key already includes `from`/`to`, so each week caches independently.
3. Thread the derived week (not `WEEK`) into TodayScreen's headers/range label, the ConsolePage
   header subtitle, both drawers' date labels, and the create drawer's day options.
4. Demo screens keep importing `WEEK` — it is not deleted, only removed from every live surface.
5. Guard: `toIsoInstant` in the create/reschedule paths must use the **displayed** week start, not
   `startOfWeek(new Date())`, or a booking made while viewing next week lands in the wrong week.

**Automated Verification**
- [ ] `bun nx test web` — new `week.test.ts` covers: month-boundary week (labels span two months),
      a week not containing today (`todayIndex === null`), DST-crossing week, and
      `toIsoInstant(displayedWeekStart, …)` landing on the intended date
- [ ] `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual Verification**
- [ ] Today's grid shows the real current dates; "today" is highlighted on the real weekday
- [ ] Prev/next moves the grid **and** refetches that week; the badge/empty states behave per week
- [ ] A booking opened from a non-current week shows that week's date in the detail drawer
- [ ] *Regression:* the five list queries, their error banner and `truncated` badges unchanged

### Discoveries and Notable Information

---

## Phase 2 — Stop asserting what the API doesn't record: queue + conversations (G5, G6)

**Files:** `features/operations/adapters.ts`, `components/platform/screens/QueueScreen.tsx`,
`features/conversations/adapters.ts`, `components/platform/screens/ConversationsScreen.tsx`,
`pages/ConsolePage.tsx`.

1. **Queue:** delete `MOCK_WAIT` / `waitingTime`. Render only what `OperationSummary` carries —
   `updated_at` as a real relative age ("updated 12 min ago"), the ratified status label, and the
   operation type. Drop the `retries` chip and the channel badge for live rows (the API models
   neither); make `QueueItem.retries`/`channel` optional so demo rows keep theirs.
2. Rename the primary action to **"Clear from queue"** with a tooltip explaining it is a local
   acknowledgement (matching `finishQueue`'s actual behaviour). Keep `dismissalKey` semantics exactly.
3. Use a readable identifier for `name` (booking reference when present, else a short operation id),
   never a bare UUID presented as a guest name.
4. **Conversations:** make `Thread.status` and `Thread.prov` optional; live threads set neither.
   `ConversationsScreen` renders the chip/tag only when present.
5. Add `transcriptQuery` to the failure surface: a failed/forbidden transcript renders `ErrorState`
   with retry inside the thread pane — never an empty message list. Remove the mock-`MESSAGES`
   fallback for **live** sessions (demo threads keep theirs).

**Automated Verification**
- [ ] `bun nx test web` — adapter tests assert no fabricated `retries`/`channel`/`wait`; a
      conversations test asserts a failed transcript renders an error, not an empty thread
- [ ] `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual Verification**
- [ ] Stop the API, open a live conversation → error + retry, no blank thread
- [ ] Queue rows show no invented wait time, retry count or channel
- [ ] *Regression:* dismiss/undo still work; an escalated operation still resurfaces

### Discoveries and Notable Information

---

## Phase 3 — Badge every remaining demo surface (G7)

**Files:** `components/platform/screens/SettingsScreen.tsx`,
`components/platform/screens/WaitlistScreen.tsx`, `pages/ConsolePage.tsx`,
`components/ui/CommandPalette.tsx`.

1. `DemoBadge` on **Business setup**, **Agent guardrails**, **Agent knowledge base** with accurate
   reasons ("no settings endpoint — changes are not saved").
2. **Waitlist auto-book (Inv-1 / spec 11 §20):** `auto_book_authorized` is a recorded consent grant
   and there is no endpoint to change it. Render it **read-only** for live rows with an explanation
   ("recorded when the guest joined"), and badge the master toggle as demo. A control that appears
   to grant or revoke a guest's consent without writing anything is the worst offender on this list.
3. **Command palette:** search the live customers list; fall back to demo names only when the query
   has never loaded, and label those results "Demo". Selecting a live customer focuses a real
   `customer_ref`.

**Automated Verification**
- [ ] `bun nx test web` — a palette test asserts live customers are searched and demo rows are
      labelled; a settings test asserts the three panels render a badge
- [ ] `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual Verification**
- [ ] Every non-live surface carries a visible **Demo data** badge (walk all 10 screens)
- [ ] The per-guest auto-book chip can no longer be clicked into a state nothing records
- [ ] *Regression:* consent-policy and catalog panels still live, with their own error/empty states

### Discoveries and Notable Information

---

## Phase 4 — Verification reaches every gated call (G8) 🔒

Load `engineering-security-engineer.md` before implementing.

**Files:** `features/bookings/mutations.ts`, `features/bookings/api.ts`, `features/hooks.ts`,
`pages/ConsolePage.tsx`, `features/conversations/api.ts`.

1. **Cancel pre-read forwards the grant** — `getBookingDetails(bookingId, grantId)` in
   `useCancelBooking`. Today the operator verifies and the *read* still 403s. Same for
   `submitReschedule`'s read (latent, but wrong).
2. A `verification_required` error from a **booking-detail read** mounts `VerificationGate` and
   retries the read with the grant, rather than surfacing a dead error.
3. A `verification_required` error from a **transcript read** mounts the gate for
   `read_sensitive`, then retries with `X-Grant-Id` — `getTranscript` already accepts it.
4. Grants stay **in memory only**, still obtained before the first submit, still matched on
   `code` (never `title`). No grant is cached across bookings or sessions.

**Automated Verification**
- [ ] `bun nx test web` — cancel test asserts the pre-read carries `X-Grant-Id`; a 403
      `verification_required` on a transcript mounts the gate and the retry carries the grant;
      an `AuthorizationFailedException`-shaped 403 (no `code`) does **not** mount the gate
- [ ] `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual Verification**
- [ ] Cancel a booking that requires verification end-to-end
- [ ] *Regression:* gate-before-submit ordering unchanged; grant never written to storage

### Discoveries and Notable Information

---

## Phase 5 — Waitlist offers become real (G9)

**Files:** `features/waitlist/api.ts`, `features/bookings/mutations.ts` (or a new
`features/waitlist/mutations.ts`), `components/platform/screens/WaitlistScreen.tsx`,
`features/waitlist/adapters.ts`, `pages/ConsolePage.tsx`.

1. Surface `latest_offer_status` / `latest_offer_expires_at` on the row (offer sent / expires in N).
2. Wire **Accept** / **Decline** to the existing `acceptOffer`/`declineOffer` (both already send
   `actor: true`), modelled on `useRemoveFromWaitlist`: optimistic → rollback on failure →
   `onSettled` refetch. Actions render **only** when an actionable offer exists.
3. "Book now" prefills the create drawer with the enrollment's service, party size and window.
4. No `enroll` UI — enrollment is a guest-initiated flow; out of scope (record in §11).

**Automated Verification**
- [ ] `bun nx test web` — accept/decline mutation tests (success, rollback on failure, actor header);
      a row with no offer renders no action
- [ ] `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual Verification**
- [ ] Accepting an offer reports the server's outcome, not an assumed one
- [ ] *Regression:* removal (optimistic + rollback) unchanged, both existing tests green

### Discoveries and Notable Information

---

## Phase 6 — Create-drawer identity honesty (G3, web half) 🔒

Load `engineering-security-engineer.md` before implementing.

**Files:** `components/platform/drawers/CreateBookingDrawer.tsx`, `features/bookings/buildSubmission.ts`
(comment only), `docs/frontend-api-wiring.md`.

1. Send `identityRef: getActor()` — the operator **is** the confirming party for a
   `staff_confirmed` booking, matching cancel and reschedule. Stops a plain-text guest name being
   written into the minimised `customer_ref` path (spec 14).
2. The guest-name field stays in the drawer as a **local label only**, and the UI says so
   ("used on this screen only — the platform records the booking against you until guest identity
   is captured"). Do not silently keep collecting a field that goes nowhere.
3. Assert in a test that `confirmation.identity` no longer carries user-typed free text and that
   **`revision.payload_hash` is byte-identical** to before (identity lives on the confirmation, not
   the canonical payload — Inv-2/3 unaffected).
4. Record the contract gap in §11 as an explicit open item for spec 04/08/12.

**Automated Verification**
- [ ] `bun nx test web` — submission test pins the unchanged canonical hash **and** the new
      `confirmation.identity`
- [ ] `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual Verification**
- [ ] A console-created booking appears under the operator ref, and the drawer says so
- [ ] *Regression:* create still polls to `booked` and confirms only on a real terminal status

### Discoveries and Notable Information

---

## Phase 7 — Gate the `X-Tenant-Id` dev shim (API) 🔒 Inv-13

Load `engineering-security-engineer.md` + `engineering-backend-architect.md`.

**Files:** `apps/api/src/Cordia.Api/Program.cs`, `apps/api/src/Cordia.Api/Middleware/TenantResolutionMiddleware.cs`
(doc comment), `apps/api/test/Cordia.Tests/…` (integration test).

1. Register `TenantResolutionMiddleware` **only** when `app.Environment.IsDevelopment()` or an
   explicit `Tenancy:AllowHeaderShim` config flag is true (default **false** outside Development).
   Middleware **order is unchanged**; only registration is conditional.
2. The verified-signed-context path is untouched, and `TenantGuard.RequireTenant` still fails closed —
   with the shim off and no signed context, a data request is refused, not served cross-tenant.
3. Test-host fixtures must keep working (they rely on the header) — set the flag in the test
   configuration rather than weakening the default.

**Automated Verification**
- [ ] `bun nx build api` · `bun nx lint api` (new/edited `.cs` UTF-8 **with BOM**)
- [ ] `bun nx test api`
- [ ] `bun nx test-integration api` — new tests: shim honoured in Development; **ignored** when
      disabled (request fails closed); a verified `X-Tenant-Context` still wins in both modes

**Manual Verification**
- [ ] `bun nx serve api` + `bun nx serve web` still work unchanged in dev

### Discoveries and Notable Information

---

## Phase 8 — Make the integration suite deterministic (G14)

**Files:** the two offending test classes (+ fixture helper if it proves cleaner).

`RetentionSweepTests.Sweep_PrunesRecord_…` fails in a full run and passes 6/6 in isolation;
`PreorderSagaRepository.FindStuckAsync`'s 500-row cross-tenant sweep has the documented same
disease. Narrow fix: give each such test a **unique tenant** and clean up the rows it seeds, so a
bounded query over accumulated state can no longer starve it. No fixture re-architecture.

**Automated Verification**
- [ ] `bun nx test-integration api` — full suite green **twice consecutively**
- [ ] The two tests still pass in isolation

### Discoveries and Notable Information

---

## Phase 9 — Update `docs/frontend-api-wiring.md` (§11)

Corrections listed in §10 of the gap doc: the spec-15 tenancy landing, the two rules now genuinely
held, the waitlist/verification/create rows, and G2/G4 which appear nowhere. Mark closed gaps in
`docs/frontend-api-wiring-gaps.md` rather than deleting them, so the audit trail survives.

**Automated Verification**
- [ ] All lanes green: `bun nx test web` · `bun nx test api` · `bun nx test-integration api` ·
      `bun nx test langgraph` · lints · builds

**Manual Verification**
- [ ] Walk all 10 screens against §11's per-screen table; every row's claim is true

### Discoveries and Notable Information

---

## Package notes

**No new dependencies.** Everything uses `@tanstack/react-query`, the generated contract packages and
existing UI primitives already in the tree.
