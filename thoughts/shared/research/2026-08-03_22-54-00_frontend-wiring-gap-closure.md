---
date: 2026-08-03T22:54:00+10:00
researcher: Claude (spec-loop Stage 1)
git_commit: 343f220
branch: feat/ui-wire
repository: Cordia
topic: "Closing the frontend↔API wiring gaps (docs/frontend-api-wiring-gaps.md)"
tags: [research, baseline-audit, web, api, spec-04, spec-08, spec-14, spec-15]
status: complete
last_updated: 2026-08-03
---

# Research — Closing the frontend ↔ API wiring gaps

**Goal:** bring `apps/web` + the console-facing slice of `apps/api` into full conformance with
`docs/frontend-api-wiring.md`, closing the gaps catalogued in `docs/frontend-api-wiring-gaps.md`.

**Method note:** the gap catalogue was produced earlier this session by direct file reads and by
running every lane's test/lint/build target. No research sub-agents were re-spawned — the primary
evidence is firsthand and already in context. What follows adds the contract-level detail the plan
needs (identity model, spec ownership, blast radius).

## Summary

Fourteen gaps (G1–G14). Three are **not** frontend-wiring work and are excluded by the wiring doc's
own words or by spec ownership: G1 (modify/reschedule fulfilment — "spec 06/08 work, not frontend
wiring"), the G3 contract half (no ratified way to attach a guest identity to a staff-made booking),
and the deposits/campaigns screens (no spec owns them). Everything else is closable inside `apps/web`
plus two small, additive `apps/api` changes.

The single largest live defect is **G2**: the console fetches the real current week and labels it
with a hard-coded mock week (`9–15 June`). Live bookings therefore carry fabricated dates on the
grid, in the detail drawer, in the reschedule drawer and in the create drawer's day picker.

## Detailed findings

### Identity model (settles G3)

- `BookingRequest` (`apps/langgraph/cordia_agent/state.py:75-123`) has **no** customer identity or
  contact field, by design: `operation, service_id, location_id, starts_at, party_size,
  duration_minutes, special_requests, preorder, target_booking_id, target_booking_version`.
- `Confirmation.identity` (`apps/langgraph/cordia_agent/models/intent.py:56-58`) is documented as
  *"Verified identity, or 'anonymous_session' sentinel."* — it is a **verified identity reference**,
  not a free-text name.
- The console currently sends the guest's typed name there (`CreateBookingDrawer.tsx:145`
  `identityRef: name.trim()`), which the server surfaces as `customer_ref` in
  `/api/customers`. Both cancel and reschedule already send `getActor()` instead, so the create path
  is also **internally inconsistent**.
- ⇒ The web-side fix is unambiguous: send the operator identity, and stop presenting a name the
  platform does not transport. Attaching a *guest* to a staff-made booking needs a spec 04/08/12
  decision (new payload field or an identity-registration call) and is out of scope here.

### Spec-registry position

| Contract / mechanism | Owner spec | This work is | Constraint |
|---|---|---|---|
| `SubmitConfirmedIntentRequest`, canonical hash, idempotency | 04 | **referencer** — must match byte-for-byte | D3, Inv-2/3/4 |
| `Confirmation.identity`, `verification_level`, grants | 08 | **referencer** | D5, Inv-11 |
| `operation_status` values + normalized set | 06 | **referencer** | D1, D2 |
| Console read projections (`ConsoleProjections.cs`) | none (derived views added by this branch) | **owner** | no DDL (spec 12) |
| `X-Tenant-Id` shim / `TenantContext` | 15 | **referencer** | Inv-13 |
| Sensitive-read purposes, `customer_ref` minimisation | 14 | **referencer** | Inv-12, minimisation |
| Waitlist enrollment/offer lifecycle + auto-book consent | 11 | **referencer** | Inv-1 (§20: silent auto-book prohibited) |

No Pydantic model, JSON Schema, pg-enum, table or migration is touched ⇒ **no type regeneration**,
except the api-contract TS if a controller signature changes (it will not: the two API changes are a
middleware registration gate and no route change).

### Type-generation blast radius

None. `libs/shared-schemas/**` and `libs/api-contract/**` stay as generated today (44 paths /
46 operations, verified current). If the `IActionResult → ActionResult<T>` follow-up were taken it
*would* regenerate api-contract — it is deliberately **out of scope**.

## Existing Feature Behavior (Baseline Audit)

This is the regression baseline for Stage 6. Every item states what the code does **today**.

### B1 · Console read surface (`features/hooks.ts`, `pages/ConsolePage.tsx`)

Five `useQuery` hooks fire on mount regardless of active screen: schedule, operations
(`needsAttention: true, limit 100`), sessions (50), waitlist enrollments (`activeOnly, 100`),
customers (`"", 50`). Each feeds an adapter → the mock-shaped component types. `loadFailures`
(`ConsolePage.tsx:612-618`) covers exactly these five and renders one `ErrorState` naming them.
Demo fallback happens **only** when `query.data === undefined` (never on an empty array).
`truncated` renders a `PartialBadge` on Today and Customers.
**Upholds:** the doc's "empty is a real answer" rule; Inv-16 for these five.
**Must stay the same:** all of the above.

### B2 · Calendar dates (`data/console.ts:275`, `TodayScreen.tsx:922-944`)

`WEEK` is a frozen literal (`doms 9..15`, `month 'June'`, `today 3`). It supplies every date label
in TodayScreen, the ConsolePage header subtitle (`:596-602`), BookingDetailDrawer (`:62`),
RescheduleDrawer (`:131`), and `DAY_OPTS` used by CreateBookingDrawer (`console.ts:593`).
`weekWindow()` (`features/bookings/adapters.ts:32`) independently computes the **real** current
Monday→Monday window; `toDayAndStart` maps live `starts_at` to a 0-based weekday index.
**Currently violates** the "nothing asserted that the API does not record" rule.
**Intentionally changing.**

### B3 · Queue (`QueueScreen.tsx`, `features/operations/adapters.ts`)

Adapter maps `OperationSummary` → `QueueItem` with `retries: 0` and `channel: "web"` hard-coded, and
`name` falling back to `booking_id ?? intent_id ?? operation_id`. The screen renders a
`waitingTime(id, retries)` string that is `MOCK_WAIT[id] ?? retries*9+8` minutes, a `chMeta(channel)`
badge, `"{retries} retries"`, and a primary button labelled *Finish booking* / *Re-booking* wired to
`finishQueue`, which is a **local dismissal** keyed by `dismissalKey = id|status` so an escalation
resurfaces the row.
**Upholds:** the dismissal-key design (keep it). **Violates:** fabricated wait/retries/channel and a
button label promising a server action.
**Intentionally changing** (the fictions and the label); the dismissal semantics **must stay**.

### B4 · Conversations (`features/conversations/*`, `ConsolePage.tsx:653-705`)

Session list is live; `toThread` sets `status: "pending"` and `prov: "ai"` for every session because
`ConversationSessionSummary` carries neither. Transcript is fetched only for live session ids and
only after the operator picks a `SensitiveReadPurpose`; the query has `staleTime: Infinity`,
`refetchOnWindowFocus/Reconnect: false` (deliberate — one audit row per deliberate read).
`liveMessages` falls back to the mock `MESSAGES` map when `transcriptQuery.data` is undefined, and
`ConversationsScreen.tsx:212` renders `messages[active.id] || []` ⇒ a failed/403 transcript read
shows an **empty thread with no error**. `transcriptQuery` is absent from `loadFailures`.
`getTranscript` accepts a `grantId` nothing passes. `takeOver` is local state.
**Must stay the same:** the purpose gate, the no-refetch policy, the live-session-only rule.
**Intentionally changing:** the error path, the fabricated `status`/`prov`.

### B5 · Waitlist (`features/waitlist/*`, `WaitlistScreen.tsx`)

`listEnrollments` is live and includes `latest_offer_status` / `latest_offer_expires_at`.
`cancelEnrollment` is real, audited via `actor: true`, and optimistic with rollback
(`useRemoveFromWaitlist` — snapshot → rollback → `onSettled` refetch), covered by 2 vitest cases.
`acceptOffer` / `declineOffer` exist and are **called from nowhere**. There is no `enroll` function.
The master auto-book toggle and the per-guest Auto/Manual chip mutate local state only, although
`auto_book_authorized` is a **consent grant** (spec 11 §20 / Inv-1). "Book now" opens an empty create
drawer.
**Must stay the same:** removal semantics + its tests.
**Intentionally changing:** offer actions become real; the consent toggles stop lying.

### B6 · Booking writes (`features/bookings/*`)

- **Create:** `buildSubmission` builds the canonical payload, hashes once with `canon.v1`, sets both
  `revision.payload_hash` and `confirmation.intent_payload_hash` from it. Attempt identity is minted
  per drawer-open and reused across retries (`getOrCreateAttempt`). Submit → 202 → `useOperationStatus`
  polls; the drawer only reports success on a `booked` status, guarded by `announcedRef`.
  `identityRef` is the **typed guest name** (the defect); `verification_level` hard-coded
  `staff_confirmed`.
- **Cancel:** `useCancelBooking` runs read-version → submit → poll inside one `mutationFn`.
  `getBookingDetails(bookingId)` is called **without** the grant that is in scope. `reason_code`
  hard-coded `operator_request`. Reports `cancelled` only on a settled `booked` status.
- **Reschedule:** fully built (payload, hash, identity, version from a real read, polling, in-flight
  guard) but gated off by `RESCHEDULE_FULFILMENT_SUPPORTED = false`; `onRescheduleDone` refuses and
  does not move the row.
**Must stay the same:** hash binding, attempt-identity reuse, `retry: false` on mutations, the
terminal-status wording split (`normalizedStatusLabel(status, 'cancel')`), the reschedule gate,
all 221 vitest cases.
**Intentionally changing:** `identityRef` on create; the grant on the cancel pre-read.

### B7 · Verification (`features/verification/*`)

`VerificationGate` (request → code → grant, in-memory only) is mounted **only** for cancel
(`ConsolePage.tsx:843-853`), obtained *before* the first submit because `grant_id` is
`NotEmpty`-validated server-side. A 403 carrying `code: "verification_required"` re-opens the gate
(`isApiVerificationRequiredError`). `getBookingDetails` throws on the typed 403 and no caller catches
it.
**Must stay the same:** gate-before-submit ordering, in-memory-only grant, matching on `code` not
`title`.
**Intentionally changing:** the gate becomes reachable from gated reads (booking detail, transcript).

### B8 · Settings (`SettingsScreen.tsx`)

Consent policy (`/api/consent-policies?channel=web`) and catalog items (`/api/catalog/items`) are
live with their own pending/error/empty states. Services, hours and deposit policy are demo **and
badged**. Business setup, agent guardrails and the knowledge base are demo and **unbadged**, with
controls that appear to save.
**Must stay the same:** the two live panels.
**Intentionally changing:** badges on the three unbadged panels.

### B9 · Tenancy / actor (`api/config.ts`, `Program.cs:169-181`)

`fetchApi` sends `X-Tenant-Id` (from `window.APP_CONFIG.TENANT_ID` → `VITE_DEV_TENANT_ID` → `"demo"`)
and a minted `X-Correlation-Id` on every request; `X-Actor`/`X-Actor-Role` on `{actor: true}`;
`X-Grant-Id` on `{grantId}`; `Content-Type` only for string bodies. Server pipeline is
Correlation → **TenantContextVerification** → **TenantResolution (shim)**; the shim is gated to never
override a verified signed context (`TenantResolutionMiddleware.cs:28`) but is registered with **no
environment gate**, so where no signed context exists any client can assert a tenant.
**Must stay the same:** header set, shim subordination to the signed context, `TenantGuard` fail-closed.
**Intentionally changing:** the shim becomes non-production.

### B10 · Test coverage that must stay green

`bun nx test web` **221**; `bun nx test api` **1170**; `bun nx test langgraph` **1007**;
`bun nx test-integration api` **413 passed / 1 failed / 2 skipped** — the failure
(`RetentionSweepTests.Sweep_PrunesRecord_AndEmitsAuditEvent_WhenTerminalOpAndPastDeadline`,
line 88) **passes 6/6 in isolation**: shared-database/parallel-collection contamination, same family
as the documented preorder self-poisoning test. Treat as known-flaky, not a regression signal.

## Open questions for Stage 3

1. **G1** (modify/reschedule fulfilment) — in or out? The wiring doc explicitly calls it
   "spec 06/08 work, not frontend wiring".
2. **G3 contract half** — a guest identity on staff-made bookings needs a spec 04/08/12 decision.
   Recommended: web-side honesty now, contract decision deferred.
3. **G4** (commit/push/PR) — the user must confirm they want commits made.
