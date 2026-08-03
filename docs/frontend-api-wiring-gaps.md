# Frontend ↔ API Wiring — Gap Audit

**Status:** Audit · **Audited against:** [`docs/frontend-api-wiring.md`](./frontend-api-wiring.md)
(§11, "Implementation status 2026-07-27") · **Date:** 2026-08-03 · **Branch:** `feat/ui-wire`

This is a re-read of the code against every claim the wiring guide makes, plus a sweep for
things the guide does not mention. It is deliberately blunt: §11 is largely accurate, and the
items below are what is left, what is wrong, and what nobody has written down yet.

---

## 0. Health of the tree right now (measured, not assumed)

| Check | Result |
|---|---|
| `bun nx typecheck web` | ✅ clean |
| `bun nx lint web` | ✅ clean |
| `bun nx test web` | ✅ **221 passed** / 10 files |
| `bun nx build api` | ✅ builds (warning `NU1903`: `Microsoft.OpenApi` 2.4.1 has a known **high-severity** advisory) |
| `bun nx lint api` | ✅ clean |
| `bun nx test api` (unit) | ✅ **1170 passed** |
| `bun nx test-integration api` | ⚠️ **413 passed, 1 failed, 2 skipped** — see G14 |
| `bun nx test langgraph` | ✅ **1007 passed** |
| OpenAPI contract | 44 paths / 46 operations, includes the spec-15 `/api/channels/*` webhooks → **up to date** |

So the code is green apart from one non-deterministic integration test. The gaps below are
almost all *unbuilt* or *untrue-on-screen*, not *broken build*.

---

## 1. Verdict per §11 claim

| §11 claim | Holds? | Note |
|---|---|---|
| Today / Queue / Conversations / Customers / Waitlist / Analytics read live data | ✅ | wired via `features/hooks.ts` |
| Create is live end-to-end (202 + poll) | ⚠️ | live, but **the guest's name and the chosen staff member are never sent** — G3 |
| Cancel is live end-to-end | ⚠️ | live, but the pre-read that fetches `version` **drops the grant** — G8 |
| Reschedule disabled behind `RESCHEDULE_FULFILMENT_SUPPORTED` | ✅ | `features/bookings/submit.ts:134` |
| Verification live for cancel | ⚠️ | *only* for cancel; not for gated booking reads or transcripts — G8 |
| "Everything still on demo data carries a visible **Demo data** badge" | ❌ | guardrails, knowledge base, business setup, waitlist auto-book, command-palette customers, queue wait-times are all demo/local and **unbadged** — G5–G7 |
| "Nothing is asserted that the API does not record" | ❌ | the calendar's dates, queue wait time / retries / channel, and every ended thread's "pending" chip are asserted and unrecorded — G2, G5 |
| "A failed fetch never renders as a confident empty list" | ❌ | holds for the five list queries; **not** for the transcript read — G6 |
| Waitlist "live list + real removal" | ✅ | removal is real and audited; the rest of the screen is not — G9 |
| Partial-results badge on schedule + customers | ✅ | `ConsolePage.tsx:633, 721` |

---

## 2. Blocking gaps (P0 — a venue could be harmed)

### G1 · Fulfilment engine still cannot modify or reschedule *(already documented — restating because it bounds everything else)*

`AdvanceBookingOperationCommandHandler.cs:104-108` throws `PoisonMessageException` for
`Modify`/`Reschedule` at `Finalizing`; `revision.canonical_payload.target_booking_id` is read
by no handler. The console builds, hashes and could submit a valid reschedule, but
`RESCHEDULE_FULFILMENT_SUPPORTED = false` keeps it off.

**Work required:** branch `FinalizeBookingCommandHandler` on `OperationTypeEnum`, add a
`ModifyBookingCommandHandler` mirroring `CancelBooking/` (provider call outside the
transaction, staged transaction, capacity move rather than release, version guard,
`notification.modify_confirmed.v1`, reconcile path). Spec 06/08 work, ~the size of the cancel
handler plus capacity re-reservation. Then flip one constant.

### G2 · The calendar is dated by a hard-coded mock week — while showing live bookings 🔴 **NEW**

`data/console.ts:275` pins `WEEK = { doms: [9..15], month: 'June', today: 3 }`. That constant
still drives **every date the operator sees**:

- `TodayScreen.tsx:922-944` — column headers and the "June 9–15" range label
- `ConsolePage.tsx:596-602` — the header subtitle ("Thu, 12 June")
- `BookingDetailDrawer.tsx:62`, `RescheduleDrawer.tsx:131` — the booking's date
- `CreateBookingDrawer.tsx:61,84` + `DAY_OPTS` (`console.ts:593`) — the day picker
- `AnalyticsScreen.tsx:149` — which bar is highlighted as "today"

Meanwhile `weekWindow()` (`features/bookings/adapters.ts:32`) fetches the **real** current
week, and `toDayAndStart` places live rows by real weekday index. Today is 2026-08-03: the
grid fetches 3–9 August and labels those columns *9–15 June*. An operator reading a booking's
date, or picking a day in the create drawer, is reading a fabricated date attached to a real
booking. This is the exact failure mode §11's second rule exists to prevent, and it is
unbadged.

**Work required:** derive `dows/doms/month/today/rangeLabel` from `scheduleWindow.from`; make
`selDay` and `DAY_OPTS` date-based; delete `WEEK` from the live path. While you are there, add
week navigation (see G12) — the window is currently `useMemo(..., [])`, so the console can
*only* ever show the current week.

### G3 · A console-created booking carries no guest identity 🔴 **NEW**

`toCanonicalPayload` (`buildSubmission.ts:58-75`) emits only
`operation, service_id, starts_at, party_size, duration_minutes, location_id,
special_requests, target_*`. So:

- The **guest's name**, typed into the create drawer, is not in the payload. It is passed as
  `confirmation.identity` (`CreateBookingDrawer.tsx:145` → `identityRef: name.trim()`), which
  the server treats as `identity_ref` and the customers projection surfaces as `customer_ref`
  (`ConsoleReadModel.BuildCustomerResolverAsync`). Two consequences: (a) the guide's claim
  that `/api/customers` exposes "no name, phone or email" is **defeated by the console
  itself** — it writes a plain-text name into the opaque ref; (b) `customer_ref` is a spec-14
  minimised field, so a name there is a data-classification violation, not just untidy.
- **No phone or e-mail is collected at all.** A booking made in the console therefore has no
  notification destination, and `GET /api/Bookings?phone_e164=…` (the guest-facing identity
  lookup) can never find it.
- The **staff member / resource** the operator picks is silently discarded — `STAFF` is
  entirely mock (`console.ts:294`) and there is no field for it in `BookingRequest`.

**Work required:** decide, at spec level, how a staff-originated booking carries guest
identity (contact fields on `BookingRequest`, or an identity-registration call producing a
real `customer_ref`) and whether resources are modelled at all. Until then the create drawer
should stop collecting a name it does not send, or say plainly that it is only a label.

### G4 · None of this work is committed 🔴 **NEW**

`git status`: **50 files modified, 42 untracked**, ~3 200 insertions, and no
`origin/feat/ui-wire` exists. The entire wiring feature — including the §11 rewrite of
`docs/frontend-api-wiring.md`, `apps/web/src/features/**`, `apps/web/src/api/**`, the cancel
fulfilment handler, the console read models and their tests, and the `canon.v1` corpus — lives
only in one working tree. One `git checkout` loses all of it.

**Work required:** commit in reviewable slices (Conventional Commits — pre-commit will run
format/tests per file type), push, open the PR. Do this before anything else on this list.

---

## 3. Truthfulness gaps — the console asserts things the API does not record

These all violate the two rules §11 sets for itself. None is badged.

### G5 · Queue rows are part fiction

`QueueScreen.tsx:9-11,60` renders `{waitingTime(q.id, q.retries)} waiting`, where
`waitingTime` is `MOCK_WAIT[id] ?? retries*9+8` minutes. For a live operation the id never
matches, so every row claims **"8m waiting"** — an invented duration next to real data. The
adapter (`features/operations/adapters.ts:53-54`) also hard-codes `retries: 0` (so the row says
"0 retries" regardless) and `channel: "web"` (rendered as a channel badge, though the API
models no channel on an operation). `name` falls back to `booking_id ?? intent_id ??
operation_id` (`:45`), so the operator sees a raw UUID where a guest name belongs.

Also: the primary button says **"Finish booking" / "Re-booking"** but `finishQueue`
(`ConsolePage.tsx:538`) is a purely local acknowledgement. The label promises a server action
that does not happen.

### G6 · A failed or forbidden transcript read renders an empty conversation

`ConsolePage.tsx:664-666` falls back to the mock `MESSAGES` map when `transcriptQuery.data` is
absent; for a live session id that lookup misses, and `ConversationsScreen.tsx:212` renders
`messages[active.id] || []`. `transcriptQuery` is **not** in the `loadFailures` list
(`ConsolePage.tsx:612-618`). So a 403/500 on the most sensitive read in the product shows a
clean, empty, confident thread — the defect §11 says was fixed for the other five queries.

### G7 · Unbadged local-only state presented as settings

- **Agent guardrails** (`SettingsScreen.tsx:471`) — toggles flip `useState` only. No badge.
- **Agent knowledge base** (`:495`) — "Save" writes to `useState` only. No badge.
- **Business setup** (`:346`) — same. No badge.
- **Waitlist auto-book**, both the master toggle and the per-guest Auto/Manual chip
  (`ConsolePage.tsx:145-147`, `WaitlistScreen.tsx`) — local only. This one is worse than
  cosmetic: `auto_book_authorized` is a **consent grant recorded at enrollment** (spec 11 §20,
  Inv-1). The chip lets an operator appear to grant or revoke a guest's auto-book consent, and
  nothing is written.
- **Command palette customer search** (`CommandPalette.tsx:4,60`) — searches the mock
  `CUSTOMERS` array; selecting one focuses an id that does not exist in the live list.
- **Ended conversations** show `<Chip status="pending" />` for every session
  (`conversations/adapters.ts:50`), because `ConversationSessionSummary` carries no outcome.
  `prov: 'ai'` is likewise asserted (unrendered today, but wrong if ever surfaced).
- The create drawer's optimistic row asserts `prov: 'you', status: 'confirmed'`
  (`CreateBookingDrawer.tsx:110`) — transient, replaced by the refetch, but still an assertion.

---

## 4. Flows that exist in code but are not reachable, or only half-wired

### G8 · Verification is wired for exactly one call

`VerificationGate` is mounted only for cancel (`ConsolePage.tsx:843-853`). Everything else that
can demand a grant is unprotected:

- `getBookingDetails` throws on the typed 403 (`features/bookings/api.ts:26-39`) and **no
  caller catches it** — the gate never appears for a gated booking read.
- **The cancel flow's own pre-read drops the grant**: `mutations.ts:189` calls
  `getBookingDetails(bookingId)` with no `grantId`, while `grantId` is right there in the
  mutation variables. If the booking read is gated, the operator verifies, then the cancel
  still fails on the read.
- `getTranscript` accepts a `grantId` (`conversations/api.ts:35,42`) that nothing ever passes,
  so a transcript needing verification dead-ends (and, per G6, dead-ends *silently*).
- `submitReschedule` (`ConsolePage.tsx:463`) reads details without a grant too — latent until
  reschedule is enabled.

### G9 · Waitlist is one-quarter wired

Plan Phase 9 called for `enroll / cancelEnrollment / acceptOffer / declineOffer`. Only
`cancelEnrollment` is reachable. `acceptOffer` and `declineOffer` are implemented and **called
from nowhere** (`features/waitlist/api.ts:37-49`), even though the list already carries
`latest_offer_status` / `latest_offer_expires_at`. There is no `enroll` function at all, so the
console cannot put anyone on a waitlist. "Book now" (`ConsolePage.tsx:715`) opens an empty
create drawer — it does not prefill the guest, service or window.

### G10 · Operator actions on operations are unwired

`POST /api/booking-operations/{id}/{cancel|resume|resolve|alternatives}` are all in the
contract and none is called. Consequences: an `alternatives_required` operation cannot be
answered from the console; a stuck operation cannot be resumed; `manual_review` cannot be
resolved (hence the local-acknowledgement design). `validateChange`
(`features/bookings/submit.ts:35`) is implemented and unused pending reschedule.

### G11 · Conversations are read-only, with no live channel

No reply/compose (no endpoint), `takeOver` is local state only, and
`POST /api/conversation-sessions/{id}/{consent|transcript-turns}` are unwired. There is no SSE
or websocket anywhere, so "live" threads only refresh on the 30 s stale window or window focus
(`api/queryClient.ts:38-39`).

---

## 5. Screens with no backend at all (accepted, listed for completeness)

Deposits (spec 09 deferred), Campaigns (no spec owns it), Analytics KPI tiles and the
"who handled them" split, Settings → services / hours / deposit policy / guardrails /
knowledge base. Deposits, Campaigns, services, hours and deposit policy are correctly badged;
guardrails, knowledge and business setup are not (G7).

---

## 6. Auth, tenancy and audit — the spec-15 landing changes the picture

Spec 15 merged to `main` (commits `b3f3987`…`8b0b203`) **after** this guide was written, so §1's
"unchanged" row for tenant resolution is stale.

- **The dev shim is registered unconditionally.** `Program.cs:180` adds
  `TenantResolutionMiddleware` with no environment gate. Its own XML doc says "DEV/TEST-ONLY".
  It is correctly *subordinate* to a verified `X-Tenant-Context` (`TenantResolutionMiddleware.cs:28`),
  but where no signed context is present, an arbitrary `X-Tenant-Id` from any client still
  selects the tenant. In a deployed environment that is unauthenticated cross-tenant access.
  **Gate it on `IsDevelopment()` (or a config flag) before anything ships.**
- **The console has no login and the API has no `[Authorize]`.** Operator identity is
  `VITE_DEV_ACTOR` (`api/config.ts:41-53`) — i.e. the audit trail's actor is a build-time
  constant that the browser can change.
- `confirmation.verification_level` is hard-coded `"staff_confirmed"`
  (`buildSubmission.ts:113`, `submit.ts:107`) — documented, still open.
- The cancellation `reason_code` is hard-coded `"operator_request"` (`mutations.ts:203`); the
  operator never picks one, unlike the transcript purpose picker.
- Next client-side step for spec 15: swap `getTenantId()` for a signed `X-Tenant-Context`
  obtained from the web-origin flow. Isolated to `api/config.ts` + `api/client.ts` as designed.

---

## 7. Scale and performance

- **No queryable `starts_at`** (already documented). `ConsoleReadModel` pages `PageSize = 500`
  and scans to `MaxScan = 20_000` rows in memory for both the schedule and the customers list
  (`ConsoleReadModel.cs:32,38`), and caps the analytics breakdown the same way (`:334`). Every
  console screen degrades linearly with tenant history. A projected, indexed
  `bookings.starts_at` (spec 12 DDL) turns all three into SQL predicates.
- **No pagination anywhere** — every list endpoint takes `limit` only, no cursor. The console
  asks for 50–100 rows and shows a *Partial results* badge; there is no "load more".
- **The console fetches all five lists on mount**, regardless of which screen is open
  (`ConsolePage.tsx:204-208`).
- **Screen transitions still fake latency** — `go()` sets a 540 ms `setTimeout` skeleton
  (`ConsolePage.tsx:317-319`) unrelated to any request.

---

## 8. Routing / product-shell gaps

The whole console is still one route, `/dashboard`, with screen, selected day, open
conversation, open drawer and search all in component state (`router.tsx:20`). No deep link to
a booking, conversation or customer; no browser back; nothing survives a refresh; no week
navigation (G2). Fine for the wiring milestone, a real limitation for daily use.

---

## 9. Test and CI hygiene

### G14 · The integration suite is still non-deterministic 🔴 **NEW instance**

This run: `RetentionSweepTests.Sweep_PrunesRecord_AndEmitsAuditEvent_WhenTerminalOpAndPastDeadline`
failed at `RetentionSweepTests.cs:88` (`Assert.NotNull(auditRow)`), then **passed 6/6 in
isolation**. Same family as the documented self-poisoning preorder test and the fixture
migration race: three fixtures migrate one shared `Cordia_API_Test` database, xUnit runs
collections in parallel, and nothing resets state between runs. The suite will keep producing
failures that are indistinguishable from regressions until the fixture layer owns
per-run isolation (respawn/truncate, or a database per collection) and fixture migration is
serialised.

Also open: 2 skipped integration tests (`PreorderRecoveryAndStandaloneTests.StandaloneOrder_Conversion_NeedsBookingDelegatedRedesign`,
`WaitlistFlowTests.Accept_FinalizesHold_ConfirmsBooking_AndIsIdempotent`).

### Other

- **No browser/e2e coverage of the console.** 221 web tests cover adapters, hashing, client,
  mutations and the router; no test renders `ConsolePage` against a mocked API, which is
  precisely why G2, G5 and G6 are invisible to CI.
- `Microsoft.OpenApi` 2.4.1 carries a **high-severity advisory** (NU1903) on every API build.
- `CatalogController.Search` and the waitlist/menu/preorder actions still return `IActionResult`,
  so their bodies type as `never` and the console types them `unknown` (documented follow-up).

---

## 10. Corrections the guide itself needs

1. §1 "Tenant resolution — unchanged" → spec 15 has landed; describe the two-middleware order
   and the fact that the shim is not environment-gated.
2. §11 "Everything still on demo data carries a visible Demo data badge" → false (G5, G7).
3. §11 "Nothing is asserted that the API does not record" → false (G2, G5).
4. §11 Waitlist row "Live list + real removal" → say explicitly that enroll, offer
   accept/decline and the auto-book toggles are **not** wired.
5. §11 Verification row → say it is wired for cancel *only*, and that the cancel path's own
   booking read does not forward the grant.
6. §11 Create row → say the guest name is sent as `identity_ref` and no contact detail or
   resource is transported.
7. Add G2 and G4 — neither is mentioned anywhere.

---

## 11. Suggested order

| # | Item | Why first |
|---|---|---|
| 1 | **G4** commit + push + PR | everything else is at risk until then |
| 2 | **G2** real dates on the calendar | live data currently carries fabricated dates |
| 3 | **G6** transcript error state, **G5** queue fictions, **G7** badges | cheap; restores the two rules the doc claims |
| 4 | **G8** grant plumbed through `getBookingDetails` + transcript gate | correctness on a flow that is already "live" |
| 5 | Spec-15 gate on the tenant shim (§6) | must not ship as-is |
| 6 | **G3** guest identity contract decision | needs a spec answer, not a UI change |
| 7 | **G14** integration-suite isolation | otherwise every later regression is ambiguous |
| 8 | **G9/G10** waitlist + operation actions | completes the plan's Phase 9 |
| 9 | **G1** modify/reschedule fulfilment | largest, and the only one whose absence is already honestly surfaced |
| 10 | Projected `starts_at` + pagination (§7), routing (§8) | scale and product polish |
