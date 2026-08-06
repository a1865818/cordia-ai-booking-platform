---
date: 2026-08-04
reviewers: /code-review xhigh (sonnet subagents) · a 100-agent conformance workflow · two independent diff reviewers · three parallel reviewers
range: 343f220..77b4fd8 on feat/ui-wire (six rounds across four implementation passes)
status: all findings closed
consolidates: the six review rounds previously recorded inside the per-pass plan documents plus the standalone gap-closure review
---

# Review record — frontend ↔ API wiring

Six review rounds ran over the `feat/ui-wire` work. This is all of them in one place, oldest first.
The implementation record is `../plans/2026-08-04_frontend-api-wiring.md`; the baselines are
`../research/2026-08-04_frontend-api-wiring.md`.

> **Note on `docs/frontend-api-wiring.md` references.** Section numbers cited below (§1, §4, §7, §11)
> describe that guide **as it stood when each round ran**. §11–§13 have since been removed and the
> guide is back to its pre-implementation §1–§10 form.

| # | Round | Method | Raised → confirmed | Fixed in |
|---|---|---|---|---|
| 1 | Initial wiring — xhigh | 4 sonnet reviewers + a live run against a serving API | 8 blockers, all real | pass-1 range |
| 2 | Initial wiring — conformance | 100-agent workflow, 9 dimensions, 2 adversarial verifiers per finding | 45 → **16** confirmed, 29 refuted | pass-1 range |
| 3 | Cancel fulfilment + mutations | 2 independent reviewers over the diff | 9 real defects | pass-2 range |
| 4 | Cancel fulfilment — conformance | audit against the wiring doc's stated rules | 15 confirmed | pass-2 range |
| 5 | Gap closure — xhigh | `/code-review xhigh`, sonnet subagents | 15, **all real, none refuted** | `0cc7431` |
| 6 | Console truthfulness | 3 parallel reviewers, then a second round | 5 + 7 defects | pass-4 range |

**The one theme worth carrying forward:** in rounds 5 and 6, a majority of findings were *fresh
instances of the very defect class the pass was written to remove* — including two introduced by the
fixes themselves. A green suite predicted none of them. See "What this says about the work" at the end.

---

## Round 1 — initial wiring, xhigh (4 reviewers + a live API run)

Four Sonnet reviewers went over the diff (web API layer, C# additions, web features/screens, Python
parity), plus a live end-to-end run against a serving API. **Every blocker below was a real defect
that the test suites were passing over.**

### Found by running it against the real API

- 🔴 **`/api/customers` would always have been empty.** `BookingRequest` declares no customer field and
  is `additionalProperties: false`, so the canonical payload can never carry one — the projection was
  reading a field that cannot exist. Customer identity actually lives on `bookings.customer_id`,
  falling back to the confirmation's `identity_ref`. Both the customers list and the schedule's
  `customer_ref` now resolve from there, through a single `ParsedRow` so the schedule and customers
  agree on which booking a row refers to. The integration test was passing **because it seeded the
  customer in the wrong place too** — it now seeds a real `bookings` row, and the cross-tenant case
  asserts tenant B genuinely has a customer so an empty result for A proves scoping rather than
  emptiness.
- The dev database was six migrations behind (`column b.causation_id does not exist`), unrelated to
  this change; applied with `dotnet ef database update`.

### Blockers fixed

| # | Defect | Fix |
|---|---|---|
| 1 | **Reschedule sent only a hash.** `SubmitModificationInput` is `additionalProperties: false` and has **no field for the changed data** — the new time was never transmitted, so the server would 202 and the UI would report "Change: confirmed" for a change it had never been told about. | Reschedule now rides the acceptance front door (`POST /api/bookings`, `operation: "reschedule"`, `target_booking_id` + `target_booking_version`), the only path that carries the canonical payload. |
| 2 | **Schedule silently dropped bookings.** The date filter ran in memory *after* taking the top 2000 rows by `updated_at`, so any booking whose operation had not been touched recently vanished from the grid with no indication. Same root cause made customer search return false negatives. | Both now page through the tenant (`PageSize` 500, `MaxScan` 20 000) and report `truncated`, surfaced as a **Partial results** badge. |
| 3 | **Overview truncation was non-deterministic** — `.Take(2000)` with no `ORDER BY`, so two identical calls could return different analytics. | Per-day counts aggregated **in SQL** (exact at any volume); payload-derived breakdowns ordered and flagged `breakdown_truncated`. |
| 4 | **TS/Python normalised timestamps .NET rejects.** Lowercase `t`, single-digit hour/minute/second, impossible dates (Feb 30, hour 24, month 13), years 0000–0099 remapped by `Date.UTC`, and offsets pushing the instant outside `DateTimeOffset`'s range — each produced a different hash from the server, i.e. a 422 the operator could not act on. | Both ports mirror .NET's gate exactly; **10 new corpus vectors** pin each case. Python also had an uncaught `OverflowError` escaping the hasher near year 1/9999. |
| 5 | **Query-cache key collisions.** `stableFilter` joined `key=value` with `&`, so a customer named "Smith & Co" could produce the same cache key as an unrelated filter set and render another query's rows. | Keys are JSON-serialised, which also keeps `"5"` and `5` distinct. |
| 6 | **Verification "Email instead" sent nothing.** The idempotency key was memoised per component, so after "Start over" the server replayed the original SMS challenge and the UI reported a fresh code. | A key per *send*; `already_sent` now says so; channel buttons disabled while a send is in flight. |
| 7 | **"Finish" un-resolved itself.** It was a client-side timer claiming "resolved", and the next refetch restored the row. There is no generic resolve endpoint (spec-06's is `manual_review`-only). | Now an honest local acknowledgement — "Cleared from your queue" — and the id is recorded so a refetch cannot contradict it. |
| 8 | **NFC key collision diverged**: TS/Python silently dropped a key where C#'s `JsonObject.Add` throws. | Both ports now fail loudly with an actionable message. |

### Also fixed

- Idempotency conflicts were reported as "this booking changed — reloading"; they now say the attempt
  is spent and a new one is needed, since replaying that key cannot succeed.
- A created booking now invalidates the schedule instead of living on as a fabricated id that would
  404 the moment the operator acted on it.
- `toDayAndStart` counted days by dividing elapsed ms by 24 h, which shifts every column after a
  mid-week DST change; it now counts calendar days, and rows outside the displayed week are dropped
  rather than filed under the wrong day.
- `Content-Type: application/json` is only forced on **string** bodies (FormData needs the browser to
  set its own multipart boundary).
- Dead code removed: the two mock timers, the unused `busy` setter, and
  `CanonicalBookingPayload.CustomerRef` (unreachable, and it implied payloads carry a customer).

### 🔴 Process defect: the test suites were reporting stale passes

`tools/config/*.json` was in **no** lane's nx cache inputs, so changing the shared canon.v1 corpus did
not invalidate anything — all three suites reported green against the *old* corpus. The corpus grew
from 64 to 74 vectors with no change in test counts, which is what exposed it. `tools/config/*.json`
is now an input for the `dotnet`, `python` and `typescript` named inputs; re-running with
`--skip-nx-cache` immediately surfaced a genuine TS failure (`timestamp_min_year_with_offset`) the
cache had hidden.

### Final state after round 1

| Lane | Result |
|---|---|
| `bun nx test api` | **1122 passed** |
| `bun nx test-integration api` | **397 passed, 0 failed** |
| `bun nx test web` | **181 passed** |
| `bun nx test langgraph` | **999 passed** |
| lint / build | green on all three lanes |
| live end-to-end | `POST /api/Bookings` returned **202** with a client-computed hash, an offset timestamp and a diacritic — canon.v1 parity proven against the running server; all six new endpoints returned 200, 400 without a tenant, 400 on an unknown status filter |

---

## Round 2 — initial wiring, conformance workflow (45 raised → 16 confirmed)

A 100-agent workflow reviewed the implementation against `docs/frontend-api-wiring.md` across nine
dimensions, with every finding put to two independent adversarial verifiers. **45 raised → 16
confirmed, 29 refuted.**

### 🔴 The finding that bounds the whole feature

**The fulfilment engine is create-only.** Nothing in `Handlers/BookingFulfillment/**` branches on
`OperationTypeEnum`, and `target_booking_id` is read by no handler — `FinalizeBookingCommandHandler`
calls `adapter.CreateBookingAsync` unconditionally. A modify/cancel/reschedule operation is accepted
(202) and then fulfilled as a **new booking**.

Verified three ways: repo-wide grep for `OperationType` in the fulfilment tree (only a comment in the
SLA handler, explicitly declining to branch), grep for `TargetBookingId` in that tree (zero hits), and
a live submission returning 202. This is spec 06/08 work that predates this change — the
`/modifications` and `/cancellations` endpoints already had it.

**Response: reschedule is not submitted.** `RESCHEDULE_FULFILMENT_SUPPORTED` is `false`; the drawer
explains why; the payload builder, canonical hashing, attempt identity and polling are all built and
tested behind it. Shipping a button that silently creates duplicate bookings would have been worse
than shipping one that says "not yet". *(The cancel half of this was then closed by pass 2; modify /
reschedule remains open as R1.)*

### Fixed

| # | Confirmed gap | Fix |
|---|---|---|
| 1 | **`RescheduleDrawer` fabricated success on a 950 ms timer** — "Moved… the customer gets an SMS with the new time" rendered before anything was asked of the server (raised by 3 dimensions) | Timer and local `done` phase removed; the drawer no longer claims an outcome it cannot know |
| 2 | **`requires_verification` was undetectable** — a 403 from `GET /api/Bookings/{id}` carries a typed body with no `detail`/`title`, so it degraded to `Request … failed: 403` and the built `VerificationGate` was unreachable | `ApiVerificationRequiredError` added (mirroring the idempotency-conflict class), the typed body preserved, and the write paths report an actionable message |
| 3 | **Mock mirrors flashed unbadged fabricated rows** — `useState(() => [...BOOKINGS/QUEUE/WAITLIST])` showed invented bookings until the first fetch resolved | Seeded empty; the query's own pending state is what the operator sees |
| 4 | **Every live booking claimed "Booked by AI"** — the exact fact §11 says is not recorded | `Booking.prov` gained `'unknown'`; `ProvTag` renders nothing for it; the adapter no longer guesses |
| 5 | **Conversations substituted mock threads for a genuinely empty live list**, unbadged | Falls back only while the query has *never* returned; badged when it does |
| 6 | **Reschedule sent `party_size: 0`**, durably recording a reschedule for nobody | Reads the real party size from the booking projection already being fetched |
| 7 | Settings' Services/Guardrails/Knowledge sections were unbadged local state | Badged, consistent with Hours/Deposit |
| 8 | Wiring-doc §1 counts stale (38/15), §4 verdicts contradicted §11, §8 claimed no Customer/Thread endpoint, §10 omitted all six new endpoints, TL;DR described the pre-implementation world | All corrected; superseded sections marked as such rather than deleted, so the rationale survives |
| 9 | §11 claimed the Zod follow-up covered waitlist/menu/preorder responses | Corrected — only catalog search is Zod-validated |

### Refuted (29)

Notable ones the verifiers correctly threw out: that `§8(a)`'s channel enum was misidentified (two
distinct `Channel` enums genuinely exist), that the drawer never leaving `confirming` on a
non-`booked` terminal was a blocker (it renders the server's message), and that `changes:validate`
being unused violated §7 (the doc marks it optional).

### Final state after round 2

api **1122** · integration **397 / 0 failed** · web **184** · langgraph **999** · lint/typecheck/build
green on all three lanes · DB 30/30 migrations applied.

---

## Round 3 — cancel fulfilment + `useMutation` (two independent diff reviewers)

Both reviewers confirmed the structural claims — provider call outside the transaction, no
`CreateBookingAsync` on the cancel path, tenant scoping intact, capacity release idempotent on the
right counter, `ConsolePage.tsx` free of the earlier duplication. Nine real defects were found and fixed.

### API

1. **(HIGH) The booking transition's version guard was discarded.** `TryTransitionLifecycleAsync`
   returns rows-affected, and `0` means a concurrent writer moved the row. The result was ignored, so
   a version race would release the seat and emit `notification.cancel_confirmed.v1` for a booking
   that never became `cancelled` — with the operation already terminal, nothing would re-drive it. Now
   a bounded re-read-and-retry, and a hard `StaleBookingVersionException` that rolls the whole
   transaction back (including the operation's move to `Booked`) so the message redelivers. Provider
   cancellation is idempotent, so re-running is safe.
2. **The unit suite could not have caught it.** `ArrangeHappyPath` never stubbed
   `TryTransitionLifecycleAsync`, so Moq returned `0` — every happy-path test was asserting against a
   booking that never transitioned. Stubbed explicitly, plus two new tests: one pinning the roll-back,
   one pinning that losing the race to a writer that reached the *same* end state settles normally
   rather than retrying.

### Web

3. **(HIGH) Optimistic rollback reached beyond its own row.** `onError` restored the entire
   pre-removal snapshot, so an overlapping removal that had already succeeded was resurrected —
   showing a guest as still waiting after the server confirmed they were gone. Now re-inserts only its
   own row, at its original index, and only if it is not already back. The regression test was verified
   to fail against the old implementation before the fix was restored.
4. **(HIGH) The reschedule idempotency key was not payload-scoped.** `modify:${id}` plus a
   fire-and-forget call with no in-flight guard meant two drags of the same booking could bind one key
   to two different canonical payloads (Inv-3). Key is now `modify:${id}|${startsAt}|${dur}`, and a
   `rescheduleInFlight` ref rejects the overlap.
5. Create's conflict handling was narrower than cancel's — only the typed idempotency body freed the
   attempt key, so a bare `409` left it bound forever. Both now use `isRetryableWithNewAttempt`.
6. A cancel accepted with no `operation_id` returned `settled: true` with a `null` status, which
   rendered as "Cancelling" on a finished mutation. It now claims nothing.
7. `closeDrawer()` fired on every success, including a settled *refusal* (`rejected`/`failed`) and an
   unfinished poll. A new `cancelled` flag on `CancelBookingResult` distinguishes "terminal" from
   "terminal **and** successful"; the drawer only closes on the latter.
8. `CANCEL_STATUS_COPY` was missing `alternatives_required`/`waitlisted`, and the fallback silently
   borrowed the create copy — an operator cancelling a booking could have been shown "That time isn't
   available". Both now have explicit cancel wording.
9. `pollToTerminal`'s `signal` was dead at both call sites: navigating away left the loop firing for
   the rest of the 60 s budget. Both callers now pass an `AbortController` aborted on unmount, and the
   abort clears the pending timer instead of merely skipping the next fetch.

**Not fixed, noted only:** `ReleaseFinalizedByOperationAsync` has no defence-in-depth catch for the
`ck_capacity_slots_nonneg` check constraint, unlike `ReserveAsync`'s overcommit handler. It is
unreachable unless the idempotency guards above are already violated.

---

## Round 4 — cancel fulfilment, conformance against the wiring doc (15 confirmed)

A third review audited the whole implementation against the document's stated rules. Fifteen defects
confirmed and fixed, grouped by what they had in common.

**The `Booked`-means-two-things trap (four defects).** D1 gives a cancel no terminal of its own, so it
settles at `OperationStatus.Booked`. Four places read `Booked` as "a booking was made": both read
models' `ToLifecycleStatus` (a cancelled booking read back as *confirmed*), the funnel's confirmed
count (cancelling *raised* it), and — inverted — the web adapter, which tested lifecycle `cancelled`
before the failure statuses, so a hard-failed booking rendered as a calm "Cancelled". Each now tests
the operation type first.

**Cancel-shaped data in create-shaped code (three).** A cancellation's canonical payload has no
`starts_at`, so its row claimed the booking's dedupe slot in `ListScheduleAsync` and was then dropped
by the window filter — the booking vanished from the grid the moment a cancel was *submitted*, while
the guest still held the table. `ReconcileOperationCommandHandler` reads the `finalize` step a cancel
never writes, and its lookups prove a booking *exists*, which says nothing about whether a
cancellation landed; an ambiguous cancel could never resolve, never exhaust its budget, and its
not-proven path would have *recreated* the booking. Cancels now escalate to manual review. And the
already-cancelled short-circuit still queued `notification.cancel_confirmed.v1`, so a duplicate cancel
re-notified the guest.

**The client-only guard (one).** Modify/reschedule fell through to `FinalizeBookingCommand`, which
creates unconditionally. The only protection was `RESCHEDULE_FULFILMENT_SUPPORTED` in the web app —
the HTTP endpoints and the LangGraph lane were unguarded. Now a `PoisonMessageException` at the
driver, which dead-letters into the operator work-item queue.

**Silent dishonesty in the UI (four).** None of the console's five queries read `isError`, so an API
outage rendered as a confident empty week; `CustomersScreen` substituted six fabricated customers —
names, phone numbers, lifetime spend — with no badge; `AnalyticsScreen` badged a *failed* load as "no
activity in this window", stating as fact something it could not know; and the booking drawer asserted
a channel (`b.channel || 'phone'` turned a deliberate `null` into "Booked via Phone") and a deposit the
API never records.

**canon.v1 parity (two).** Verified by running the real .NET encoder rather than reasoning about it —
the same discipline that caught the uppercase-escape trap originally. `System.Text.Json` writes
`\b \t \n \f \r` as short escapes, and `DateTimeOffset.TryParse` both tolerates surrounding whitespace
and rejects offsets past ±14:00. Eight new corpus vectors (82 total), all three implementations agreeing.

**Housekeeping (one each).** `ConsentState.NotRequired` shipped as `"notrequired"`, not the declared
`"not_required"`; a shared `EnumWireNames.ToWireName` now reads the generated attribute. Truncation
reported a limit-capped page as complete, and `Skip`/`Take` paged over a non-unique `ORDER BY UpdatedAt`.
`vite-env.d.ts` ended with `export {}`, making its `ImportMetaEnv` declarations module-local and
therefore inert.

### The trap worth remembering

**`OperationTypeEnum.Cancel` is the ZERO value** of the generated enum (quicktype orders members
alphabetically), so an unset `OperationType` reads as a cancellation. Six existing reconciler tests
built operations without setting it and began failing the moment the handler branched on type.
Production sets it explicitly at all three creation sites, so this was a test artifact — but every
future `== Cancel` check inherits the same hazard.

---

## Round 5 — gap closure, `/code-review xhigh` (15 findings, all real)

Range `124a631..77a168b` (9 phases). **15 findings, all real, all fixed in `0cc7431`. No finding was
refuted.** The suite was green before the review (302 web tests, `tsc -b --noEmit` clean), so every one
of these was behaviour the tests did not cover — which is the useful thing to know about them.

### The two that mattered most

Both **re-introduced the class of defect this branch exists to remove**, and both had the same root
cause: *a value outliving its context by one render, with something irreversible happening in that
window*. Clearing in a `useEffect` looks correct and is too late.

**1. Week navigation showed real bookings under the wrong dates.** `week` recomputes the instant
`scheduleWindow` changes, but the new window's query starts at `data === undefined`, so
`if (scheduleQuery.data) setBookings(liveBookings)` did not fire. The header read `MON 10 … SUN 16`
while every row was still last week's; opening one showed a date the booking is not on. If the new
week's fetch failed, the wrong-week rows stayed indefinitely. This is *exactly* the `9–15 June` defect
Phase 1 was written to remove, reintroduced by Phase 1's own navigation feature.

**2. Switching conversations fired an unauthorised audited read.** `useTranscript` subscribes during
render; the reset effect keyed on `convOpen` runs after. So one render escaped with the **new** session
id and the **previous** conversation's reason code and step-up grant — an audited
`read_sensitive_transcript` row the operator never authorised for that subject, using a grant issued
for someone else. Directly contrary to Inv-11 and spec 14 §2, and contrary to the comment sitting
immediately above it.

**Fix for both:** store the owner alongside the value and compare at render
(`features/bookings/scoped.ts`), so no frame exists in which the two disagree. Extracted as pure
helpers precisely so the rule is pinned by tests rather than asserted in a comment (`scoped.test.ts`,
7 cases).

### The rest

| # | Finding | Fix |
|---|---|---|
| 3 | *Book now* silently substituted the first catalogue service when the enrolment's `service_id` was absent, then **submitted it as a durable booking** | Reports the unmatched service; chooses nothing |
| 4 | `dismissQueue` stored a bare id while the filter matched `id\|status`, so a dismissed row reappeared instantly and Undo had nothing to undo *(pre-existing)* | Both paths use `dismissalKey` |
| 5 | The read-only consent chip was coloured by the venue toggle the same commit badged "not saved" — flipping it visually withdrew every guest's recorded grant | Live rows colour from `w.auto` alone |
| 6 | Shim-gate tests booted hosts without their sibling fixture's DB bootstrap (fails on a clean machine), and nothing asserted `UseSetting` still out-ranks `appsettings.Test.json` | Depends on the fixture; adds a precedence assertion so the Inv-13 guard cannot silently degrade |
| 7 | `TestIds` reached only 2 of 9 files exposed to the PAN false positive — ~85 raw hex ids left at ~0.6% each | Applied across all 9 |
| 8 | Expiry rounded to the nearest minute while actionability compared instants → live **Accept** beside the word "expired" | Shared threshold, floors throughout |
| 9 | Actionability and countdown frozen at fetch time → expired offer kept its button until a refetch | Recomputed against a ticking clock |
| 10 | A comment claimed reschedule mounts a verification gate; none is ever mounted | Comment corrected to describe what actually happens, and why it is acceptable while reschedule is disabled |
| 11 | The create drawer still searched mock `CUSTOMERS` unlabelled — the same thing the palette was fixed for in the same range | Live customers, demo rows labelled |
| 12 | Shared mutation left other rows' Accept enabled but swallowed the click silently | Any decision in flight disables all offer buttons |
| 13 | Date labels followed the host locale while their tests asserted English | `LABEL_LOCALE` pinned |
| 14 | `shiftWeekWindow` lacked the malformed-instant guard its sibling `buildWeek` has — `RangeError` inside a state updater | Same guard |
| 15 | `TestIds` XML summary documented an id format the code does not produce, and contradicted its own remarks | Corrected |

**Verification after fixes:** web **302** · api **1176** · integration **419 passed, 2 skipped,
0 failed** · all lints 0 warnings · all builds clean.

---

## Round 6 — console truthfulness (three parallel reviewers, then a second round)

### Round 6a — what three parallel reviewers found

The contract/security/test-quality pass was clean. The other two found five real defects, two of them
independently:

- **`BookingDetailDrawer` printed "With: Linh Pham · Technician · Bay 1" for every booking.**
  `staffById("s1")` matched the mock `STAFF[0]` exactly — not even the fallback arm — so the drawer
  named an invented person one click away from a grid that had just correctly named none. The
  resource-collapse fix had not been propagated to the drawer.
- **`BookingDetailDrawer` showed a fabricated "Idempotency key"**, derived by `idemKey()` from a
  character sum of the booking's *display* id. It had no relationship to the real key — and it is
  exactly the field an engineer would quote in an incident.
- **Every live customer's profile asserted "Prefers: Web chat."** `CustomerSummary` has no channel
  field; the adapter fills `channel: "web"`, and unlike the neighbouring stats block this row was never
  gated on `live`.
- **Queue and Waitlist showed confident empty states before loading and after failing.** "Nothing needs
  you right now" is styled as good news, and it rendered directly underneath the page's own "Couldn't
  load the queue" banner.
- **The Analytics header always read "Last 7 days"** whatever window was selected — the same
  label-contradicts-the-data defect as the frozen `WEEK` literal.

`STAFF`, `staffById`, the `Staff` interface, `WEEK` and `DAY_OPTS` were all dead afterwards and were
deleted, so the next reader cannot reintroduce them.

### Round 6b — seven more, including one that hid real data

- 🔴 **The grid framed its columns from mock operating hours.** `dayBounds()` read `SETTINGS.hours` —
  the same constant SettingsScreen badges *"no operating-hours endpoint yet"*. Two consequences: a live
  Sunday was labelled **"Closed"** on the strength of a constant, and the grid rendered only 9am–6pm,
  so **a real 20:30 booking was positioned outside the visible columns and could not be seen at all**.
  A schedule that silently omits a booking is precisely how the double-booking defect happened before.
  Replaced with `dayWindow()`, derived from the bookings themselves so every booking is guaranteed
  inside the frame; the "Closed" pill is gone.
- **`NOW_MINS = 13*60+15`** — a hard-coded 1:15 PM — drew the "now" line on *every* day and drove the
  "remaining" stat. Now the real clock, and only on the day that is actually today.
- **The "clients" tile rendered the booking count under a second name**, asserting a distinct fact
  never computed, between two correct figures that made it read as trustworthy. Removed.
- **`TodayScreen` had no `loaded` gate** — the one console screen still able to say "No bookings" while
  the request was in flight. Given the same treatment as Queue and Waitlist.
- **The new `email` channel had no `CHAN_CONFIG` entry**, so `Chan` fell back to the browser icon: the
  label said *Email* and the icon said *Web*. Introduced by the truthfulness fix itself.
- **The conversation composer implied a reply capability that does not exist** — the Send button had no
  `onClick`, and no send-message endpoint exists in the 50-operation contract.
- **`StatusBar` showed a permanent `9:41`.** Now the device's real clock, ticking on minute boundaries.

---

## What this says about the work

Across six rounds the same failure recurs: **the console telling an operator something the platform
does not know.** Round 2's findings 3–6, round 4's "silent dishonesty" group, round 5's findings 3, 5,
9 and 11, and the whole of round 6 are all instances of it.

Three things are worth carrying into any future pass:

1. **Fixing a class of defect does not stop new instances entering through the fixes themselves.**
   Round 5's two severe findings and round 6b's `email`-icon defect were each *introduced by the
   change that closed the previous instance*.
2. **A green suite predicted none of it.** Round 5 was explicit: all 15 findings were behaviour the
   tests did not cover. The absence of any test rendering `ConsolePage` is what let the shell, the
   More screen and the drawers stay fabricated through two full passes — which is why a page-level
   render test was made a deliverable rather than a follow-up.
3. **Verify parity by running the other implementation, not by reasoning about it.** Both canon.v1
   rounds (1·#4 and 4) found real divergences that reading the C# source had missed.


---

<!-- Consolidated from 2026-08-05_three-wiring-gaps.md (2026-08-06): the file below was merged into this record verbatim. -->

---
date: 2026-08-05
reviewers: Stage 5 multi-agent workflow (4 Sonnet dimension reviewers + 2 adversarial Sonnet verifiers per finding; 20 agents total)
range: unstaged working tree on feat/ui-wire (the three-gap closure pass, on top of the staged 2026-08-04 work)
status: all confirmed findings fixed; all gates green (425 web tests, lint 0, typecheck, build)
note: /code-review itself is user-invocation-only in this harness — the user should run
  `/code-review xhigh` for the official pass; this record is the spec-loop Stage 5 review.
---

# Review record — the three remaining wiring gaps

Four dimensions (react-correctness, truthfulness, contract conformance, test quality),
every non-nit finding put to two independent adversarial verifiers. **5 unique findings
confirmed (2 blockers), 1 contested, 0 nits.** All fixed in the same working tree.

## Confirmed and fixed

| # | Sev | Finding | Fix |
|---|---|---|---|
| 1 | 🔴 | **An open resolve/resume form was not re-gated against the row's live status.** The board refetches under an open form (every follow-up invalidates the operations query; window focus refetches), so `manual_review → reconciling` left the form rendered with an enabled submit for a call now certain to 409 — the exact rule the buttons themselves obey. | Both form branches re-check the live capability (`rowForm === 'resolve' && showResolve`), and an effect closes a form whose row no longer permits its kind. Pinned by a rerender test. |
| 2 | 🔴 | **"Waitlist this guest" was offered on pre-order `alternatives_required` rows.** The server unconditionally refuses to route a pre-order to the waitlist (spec 11 §21.4); `OperationSummary.is_preorder` was in hand and dropped by the adapter. | `isPreorder` carried through `QueueItem`; `operationActions` gained `canWaitlistAlternatives`; the row offers decline only, with "Pre-order — can't be waitlisted". Pinned at adapter, screen and page level. |
| 3 | 🟡 | **The menu panel's owner gate was case-insensitive while the server's `StaffRole.Satisfies` is a strict `==` against `"owner"`** — a session configured `"Owner"` saw the full write UI and every action would 403. | Exact-case compare, with a comment naming the server rule. Pinned by an `"Owner"`-role test. |
| 4 | 🟡 | **Retrying a failed menu registration replayed the failing render's closure** — an operator who corrected the artifact reference and clicked *Try again* silently resubmitted the old value. | Retry goes through a ref that always points at the latest submit, so it sends what the box now shows. Pinned by a correct-the-typo-then-retry test. |
| 5 | 🟡 | **The new ConsolePage wiring had zero test coverage** (follow-up handlers, toast branches, `serverBusyId`, `openEnroll`, `knownLocations`). | Four page-level tests: resolve end-to-end with the server-echoed toast, pre-order decline round-trip, cancellation-as-request toast, and the enrol drawer opening from the waitlist primary. |

## Contested (split verdict) — judged and handled

- **Enrol drawer's selected service not reconciled against a refetched catalog** (one
  verifier refuted as parity-with-CreateBookingDrawer, one confirmed the stale-submit path).
  Handled with the minimal safe guard: `canSubmit` now requires the selected id to still be
  in the current options, so a vanished service blocks submission and the operator re-picks.
  Deliberately **no** silent fallback to `options[0]` — silently substituting a service was
  itself a confirmed defect in review round 5 of the prior pass.

## Verification after fixes

`bun nx test web` **425/425** · `bun nx lint web` 0 warnings · `bun nx typecheck web` clean ·
`bun nx build web` clean. Backend untouched (`git status`: nothing under `apps/api`,
`apps/langgraph`, `libs/`, `docs/specs/` beyond two pre-existing, pre-session `project.json`
reformat diffs).

## Stage 6 — regression diff against the 2026-08-05 baseline audit

Baseline: `../research/2026-08-05_three-wiring-gaps.md` §"Existing Feature Behavior".

| Baseline item | Contract | Result |
|---|---|---|
| B1 waitlist reads + removal (optimistic, single-row rollback) + offer accept/decline + consent chip | must stay | **Unchanged** — zero modified lines in those functions; all pre-existing tests green. Enrolment is additive (new fn, new drawer, header primary). |
| B2 queue local clear/dismiss (`dismissalKey = id\|status`, undo, pruning), StatusPill, `loaded` gating | must stay | **Unchanged** — diff hunks show no modified lines in `finishQueue`/`dismissQueue`/`dismissalKey`; the local buttons keep their labels/titles and are now pinned by tests. Follow-ups are additive and visually separated ("Platform actions"). |
| B3 settings consent + published-catalog read panels, existing badges | must stay | **Unchanged** — SettingsScreen diff is one import plus the new panel insertion after the read-only catalog panel. |
| B4 shared machinery (client.ts, attempt identity, queryKeys, mutation conventions) | must stay | **Unchanged** — client/identity/queryKeys untouched; `bookings/mutations.ts` additive only. |
| B5 suites | must stay green | 425/425 web (was 302 at baseline); api/langgraph suites untouched by construction (no file changed). |
| Enrolment surface | intentionally added | Matches plan Phase 1 exactly; no optional consent/deposit/verification field is ever sent. |
| Queue follow-ups | intentionally added | Matches plan Phase 2 + review fixes 1–2; local ack semantics preserved verbatim. |
| Menu management | intentionally added | Matches plan Phase 3 + review fixes 3–4; no edit action (backend gap), no status polling (backend gap), server stays the enforcer. |
| Invariants: Inv-13 (tenant on every call), Inv-16 (server-echo only), Inv-11 (no authz bypass; role gate defers to server), Inv-1 (writes via durable endpoints only), Inv-12 (no card data anywhere) | must hold | **Hold** — every new call rides `fetchApi`; no toast claims an outcome the server did not answer; nothing disables a server check; no payment fields exist. Inv-2/3 are N/A by server design on these endpoints (no idempotency keys — recorded as backend gap 4 with the no-silent-retry discipline in their place. |

No unexplained deviation: every behavioural change traces to a plan phase or a review fix
above. Manual checklist (needs a running API): enrol a guest end-to-end and see the row
appear; resolve a real `manual_review` operation as booked and see the booking id; publish
a catalog as `owner` and watch the create drawer's service list change; confirm a
non-owner session sees the explanation panel.


---

<!-- Consolidated from 2026-08-06_console-reconciliation-and-components.md (2026-08-06): the file below was merged into this record verbatim. -->

---
date: 2026-08-06
branch: feat/ui-wire
base_commit: 77b4fd8
plan: thoughts/shared/plans/2026-08-06_console-reconciliation-and-components.md
research: thoughts/shared/research/2026-08-06_02-57-45_console-reconciliation-and-components.md
status: complete
---

# Review & validation — console reconciliation, waitlist idempotency, availability, five components

Review method: five independent reviewers (idempotency, web-correctness, honesty/Inv-16, tests,
cross-app contracts) at high effort, each finding then handed to a separate agent instructed to
**refute** it. 25 findings raised, **22 refuted, 3 confirmed**. All three are fixed below.

## Confirmed findings — all fixed

### 🔴 B1 — the enrolment request hash covered 6 of 12 material fields (Inv-3)

`EnrollWaitlistCommandHandler` hashed `customer_ref, service_id, location_id, party_size,
acceptable_from, acceptable_to` and **not** `notification_channel, offer_lifetime_seconds,
deposit_terms, auto_book, identity_ref, verification_level` — all of which are persisted and
acted on (`MatchWaitlistOfferCommandHandler` reads the lifetime; `NotificationRecipientResolver`
reads the channel).

So a same-key request differing only in an unhashed field passed the conflict check, was answered
as a **replay**, and the change was silently dropped. The console made it reachable: enrol on SMS
→ response lost → operator corrects the channel to email → *Retry* → same key (the drawer's
signature omitted the same fields) → hash still matches → 201 with the original id. The drawer
toasts success while the guest stays on SMS, and the board renders neither field, so there is no
path to notice.

Both the code and the docs asserted the opposite ("the same-key / different-payload conflict
(Inv-3) comes along with it via the request hash"), and the existing conflict test only ever
varied `PartySize`, which was inside the hash — so it passed throughout and proved nothing about
the other six.

**Fixed** on both sides: the hash now covers every material field, and the drawer's attempt
signature covers every field it sends (deliberately "everything sent", not "everything hashed",
so the two cannot silently diverge again). Widening costs the derived-key path nothing — a blind
retry re-sends identical fields — it only stops a *different* request being taken for that retry.
New `ReusingAKeyForADifferentValueOfANYMaterialFieldIsAConflict` covers channel, lifetime and
auto-book; a new web test covers the change-the-channel-and-retry flow.

### 🔴 B2 — the slot panel offered dimensions no booking can match

`CapacitySlotKey.Derive` keys a slot `{dimension}|{starts_at}` where the dimension is the **first
non-empty** of resource → service → location, and a booking derives its key the same way from its
canonical payload — which carries `service_id` and **never** a `resource_id`.

The panel presented all three as interchangeable ("at least one of the three"). An owner
following its own placeholder (`chair-1`) would declare a slot keyed on a resource, which no
booking can produce: stored, listed by the availability note as "appears available (3 left)",
never matched, never enforced, silently oversellable.

**Fixed:** the panel declares against **service and start time only**, and says why the other two
are not offered. Offering a field whose only effect is to create unenforceable capacity is worse
than not offering it.

### 🟡 B3 — same root cause as B1, client side

Folded into the B1 fix.

## Defects found in concurrently-landed work (fixed here)

Both were introduced by the operator-action change that landed during this session.

1. **`WaitlistController.Enroll` read `X-Actor-Role`/`X-Reason-Code` and never forwarded them** —
   it passed 15 of the command's 17 arguments, leaving `ActorRole`/`ReasonCode` at `""`, so
   `OperatorActionBehavior` would have 403'd **every** console enrolment.
2. **`Actor` `NotEmpty` rules on the accept/decline validators pre-empted the authorization
   layer.** Pipeline order is Validation → OperatorAction → Handler, so a missing actor answered
   400 from validation and returned *before* the behavior could deny-audit it (spec 14 §3.1
   step 5). An unauthorized caller was turned away **with no record of having tried** — the one
   outcome an authorization spine must never produce. Enrol and cancel already omitted the rule;
   accept/decline were the outliers.

## A defect found by self-review, not by the reviewers

The expired-derived-key path. Nulling `existing` when a derived key aged past its 1-hour window
was **not** enough to let the new enrolment through: the expired record still occupied
`unique(tenant_id, idempotency_key)`, so the creator election lost on conflict and the
lost-election path handed back the **old** enrolment id — the window had no effect at all until a
background retention sweep happened to prune the row. Correctness cannot depend on a sweep having
run. An expired derived key is now treated as a spent fence and voided, exactly like a cancelled
one, with a test that ages the record rather than waiting an hour.

## Verification

| Gate | Result |
|---|---|
| `bun nx typecheck web --skip-nx-cache` | ✅ 0 errors |
| `bun nx test web --skip-nx-cache` | ✅ **564 passed**, 40 files |
| `bun nx lint web --skip-nx-cache` | ✅ 0 warnings, 0 errors |
| `bun nx build web --skip-nx-cache` | ✅ built |
| `bun nx test api --skip-nx-cache` | ✅ **1268 passed** |
| `bun nx test-integration api --skip-nx-cache` (exclusive) | ✅ **506 passed, 0 failed, 2 skipped** (508 total, 3m36s) |

### On the integration suite — a measurement artifact, corrected

Mid-session the suite appeared to shrink from 483 executed tests to 327, which I initially
reported as a possible silent regression. It was not. **Two concurrent
`dotnet test --filter Category=Integration` runs** (PIDs 2032 and 15632) were executing against
the same build output and the same Postgres test database. That produced DLL-lock build failures,
truncated totals, and runs executing differently-built binaries — which is also why the same two
authorization tests flipped between "expected 403, got 400" and "expected 400, got 403" between
runs. A near-exclusive run reported **505 total / 500 passed / 2 skipped** in 4m09s, confirming
the suite is intact.

Three Messaging failures (deposit-expiry, outbox end-to-end, reconciliation) appeared in that
run. The exclusive re-run — gated on zero `testhost` processes — returned **506 passed, 0 failed,
2 skipped**, so all three were concurrency artifacts, not regressions.

The lesson is worth keeping: on this repo a test result taken while another run is in flight is
not evidence in either direction. Gate on
`until [ "$(pwsh -c '(Get-Process testhost -EA SilentlyContinue|Measure-Object).Count')" = "0" ]`
before trusting a verification run.

## Regression diff against the Stage 1 baseline

| Baseline item | Required | Verified |
|---|---|---|
| Live-operation replay disposition (spec 04) | unchanged | ✅ `ReplayingALiveEnrollmentStillReturnsTheRecordedId`; the three pre-existing enrolment idempotency tests still pass |
| `normalisePhone` formatting-only, never infers a country | unchanged | ✅ untouched; `PhoneInput` warns, never rewrites |
| Booking submit/cancel canonical hashing, `AttemptIdentity` | unchanged | ✅ untouched |
| Demo-badged screens stay badged | unchanged | ✅ deposits/campaigns untouched |
| Group-C endpoints uncalled | unchanged | ✅ no call sites added |
| `offerExpiryLabel` behaviour | unchanged | ✅ delegates to `remainingLabel`; its existing tests pass unmodified |
| Waitlist enrolment idempotency | intentionally changed | ✅ per plan, plus the two holes above |
| Inv-13 tenant scope | unchanged | ✅ new keys rooted at tenant; enrolment liveness re-checks `TenantId` |

## Not fixed, deliberately

- **The console cannot authenticate outside Development** (§12 gap 1) — excluded by instruction.
- **`AvailabilityResult` carries no `mode`**, though spec 07 §44 says every availability result
  does. That is spec 07's contract to change; the console hard-codes `informational` wording and
  `features/bookings/availability.ts` is the single place that would honour a real field.
- **The agent lane calls `POST /api/availability/candidates`, which no controller implements**
  (`foreground.py:148`). Every test covering it uses a fake API, so CI cannot see it. Real, but
  spec 03/07 backend work and outside this scope.
- **`Microsoft.OpenApi` 2.4.1 carries a known high-severity advisory** (GHSA-v5pm-xwqc-g5wc,
  `NU1903` on every `apps/api` build). It is **transitive** via `Swashbuckle.AspNetCore` 10.1.7,
  so closing it means either bumping Swashbuckle or pinning `Microsoft.OpenApi` explicitly. Not
  done here: this is the package that generates `openapi/v1.json`, which the entire
  `api-contract` → web type pipeline descends from, so changing it is its own change with its own
  verification — not a side effect of a console pass.
