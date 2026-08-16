# Backend Gaps — what the console still cannot show, and what to build

**Status:** Plan · **Audience:** whoever builds `apps/api` toward a complete end-to-end web app ·
**Mapped:** 2026-08-13 (against `apps/web/src` and the 21 controllers in `apps/api/src/Cordia.Api/Controllers`)

This is the complement to [`frontend-api-wiring.md`](./frontend-api-wiring.md). That document
answers *"which existing endpoints does the console reach?"* — and its answer is **all of them**
(§13: "left to wire: 0"). This document answers the other question: **which endpoints do not
exist yet**, derived by walking every piece of demo data left in `apps/web` back to the reason
it is still demo.

The two are not interchangeable. "Left to wire: 0" and "the app is complete" are different
statements, and the distance between them is exactly what is written down here.

> **TL;DR** — One gap blocks deployment at all (**no web-origin authentication** — the console
> runs on a Development-gated tenant shim). Two whole screens have no backend (**deposits**,
> **campaigns**). Five Settings panels persist nothing. The rest are **data-model** gaps rather
> than missing controllers — no resource/staff model, no booking provenance, no attendance
> state — and each of those blocks two or three visible surfaces at once, which is why they
> rank above the endpoints that merely read them.

---

## 1. Where the demo data actually is

There is exactly one mock module: **`apps/web/src/data/console.ts`** (649 lines). Every other
file either reads a live endpoint or falls back to this one. Two markers exist in the code and
are honest about which is which — `<DemoBadge>` (fabricated) and `<PartialBadge>` (the server
itself reported the list truncated).

Not every reference to `console.ts` means "this screen is fake". There are four distinct tiers,
and conflating them overstates the gap:

| Tier | What it means | Constants |
|---|---|---|
| **Pure demo** | The screen never calls the API; the whole feature has no backend | `DEPOSITS`, `PAYOUT`, `CAMPAIGNS`, `SETTINGS.hours`, `SETTINGS.guardrails`, `SETTINGS.knowledge`, `SETTINGS.deposit`, `ANALYTICS*.kpis`, `ANALYTICS*.handled`, `BUSINESS_TYPES` / `TENANT` |
| **Fallback demo** | A live query drives the screen; the mock shows only while it has never produced data — which includes failure — always badged | `CUSTOMERS`, `THREADS`, `MESSAGES`, `ANALYTICS*.perDay` / `.channels` / `.topServices` |
| **Label lookup** | The **ids are real and server-issued**; only the display name is resolved locally | `SERVICES`, used by `bookings/adapters.ts:124`, `customers/adapters.ts:76`, `waitlist/adapters.ts:69`, `AnalyticsScreen.tsx:262` |
| **Dead** | Superseded; nothing reads it on a live path | `BOOKINGS`, `QUEUE`, `WAITLIST` (all seeded empty — server rows only), `SERVICE_LIST` (the create drawer books against `GET /api/catalog/items`) |

The label-lookup tier is worth stating plainly, because it looks worse than it is: a live
booking carries a real `service_id` and the console maps it to a readable name through the mock
table, **falling back to the raw id when it misses** (`service?.name ?? serviceKey`). It labels
an id; it never mints one. That distinction is enforced in `features/bookings/services.ts`.

---

## 2. The gaps

### P0 — blocks running outside a dev machine

#### G1. No web-origin authentication *(= `frontend-api-wiring.md` §12 gap 1)*

The console sends `X-Tenant-Id`, a dev shim the API disables outside Development via
`Tenancy:AllowHeaderShim`. Spec 15's signed `X-Tenant-Context` is *verified* by the API but only
ever *issued* at channel ingress — `POST /api/channels/{messaging,telephony}` resolve the tenant
from a transport-attested address (called DID, recipient page id) that a browser does not have
and must not be able to assert. There is no `[Authorize]` anywhere in the API, no sign-in and no
profile endpoint; operator identity is the client-supplied `X-Actor` / `X-Actor-Role` pair.
`MoreScreen` has an `onSignOut` and no sign-in to match it, and its session card renders
`VITE_DEV_*` environment values.

**Needed:** an operator-authenticating issuance path — `POST /api/auth/login` returning a signed
tenant context plus actor and role, `GET /api/auth/session`, `POST /api/auth/logout`.

This is *not* a relaxation of the channel endpoints (see §13 C1/C2 — the tenant is their
**output**, not their input). It is a different endpoint with a different trust model.

**Also unblocks:** `PermissionGate` currently gates on a role the client asserted about itself.
With a real session it gates on something the server issued.

**This is the only item that stops the app being deployable.** Everything below is feature
completeness.

---

### P1 — whole screens with no backend

#### G2. Deposits and payouts *(= §12 gap 2)*

`DepositsScreen.tsx` imports `DEPOSITS` and `PAYOUT` and calls nothing. The earlier framing —
"spec 09 is deferred" — is wrong: spec 09 is **built** (`DepositSession`,
`RequestPaymentCommand`, `ProcessDepositEventCommand`) and lives entirely on the background
lane, reachable over the queue and not over HTTP. On top of that, `IDepositPolicy` is registered
as `NoDepositPolicy`, so **no deposit is ever requested in the first place**.

That makes this two pieces of work, in order:

1. Register a real deposit policy, so deposit sessions exist at all.
2. Expose reads: `GET /api/deposits?status&from&to`, `GET /api/payouts/summary`, plus a refund
   action.

**Knock-on, all currently honest-but-empty:** `Booking.dep` is hard-`0` on every live row
(`bookings/adapters.ts:154` — "reading one from the hand-written mock catalog would assert a
payment hold the platform never took"); the Analytics "Deposits captured" KPI is fabricated; and
`MoreScreen` deliberately shows no money figure at all, having previously shown `PAYOUT.held`,
"the most credible fabrication in the product".

#### G3. Campaigns

`CampaignsScreen.tsx` is entirely local, and unlike everything else in this document **no spec
owns it**. It needs a product decision before an endpoint. The shape the screen already implies:
`GET /api/campaigns`, `POST /api/campaigns/{id}/{approve,reject,schedule}`, and an audience-size
estimate. The approval gate is the load-bearing part — one of the locked guardrails is *"never
send marketing without owner approval"*, so the endpoint must model held-for-approval as a
first-class state, not a client-side flag.

---

### P2 — Settings: six panels, five persisting nothing

`SettingsScreen.tsx` is the densest concentration of gaps in the app. Live today: venue display
name, consent policy (read-only), published catalog items, the menu-catalog lifecycle, capacity
slots, and the three operator-recovery panels. Missing:

| # | Panel | Missing endpoint | Why it matters beyond the panel |
|---|---|---|---|
| G4 | Business setup | `GET`/`PUT /api/tenant-profile` carrying `business_type` | `tenant-profile` exposes `display-name` **only** — deliberately minimal (§13 B2). Business type drives `resourceLabel` / `resourceLabelPlural`, which name every lane in the schedule grid |
| G5 | Hours | `GET`/`PUT /api/tenant-profile/hours` | **Load-bearing.** `TIME_SLOTS` is a fixed 08:00–20:00 list whose own comment says anything narrower "would be a constraint invented here", and `dayBounds()` reads the mock. `TodayScreen` also cannot show a "Closed" pill for a day the venue does not open |
| G6 | Services | `duration_minutes` on `CatalogItem`, or a real offerings endpoint | Bookable services already come from `GET /api/catalog/items` — but the item carries **no duration** (`services.ts:63`), so the create drawer omits `duration_minutes` rather than defaulting it. No per-service deposit amount either |
| G7 | Deposit policy | `GET`/`PUT /api/deposit-policy` | Depends on G2 |
| G8 | Agent guardrails | `GET`/`PUT /api/agent-policy/guardrails` | The toggles write nothing today. A switch that appears to disarm a safety rule while persisting nothing is worse than no switch |
| G9 | Knowledge base | `GET`/`POST`/`PUT /api/agent-knowledge` | Also needs the escalation → *needs an answer* link: an escalated question never becomes a KB row, so the "needs answer" count is authored by hand |
| G10 | Waitlist auto-book (venue) | a venue-level setting endpoint | `WaitlistScreen.tsx:54`. The **per-guest** consent flag is live and correctly read-only; the **venue** switch is not saved |

---

### P3 — data-model gaps that keep live screens thin

These are not "add a controller". They add a field to a ratified contract — Pydantic source →
JSON Schema → C# and TS (see [`type-generation.md`](./type-generation.md)) — which is why they
cost more and rank here despite blocking more surfaces than P2 does.

#### G11. No resource / staff model *(= §12 gap 9)*

Neither `BookingRequest` nor `ScheduledBookingView` carries a resource, so the schedule grid
draws **one unnamed lane** (`bookings/adapters.ts:136`: "this is a grid coordinate, not an
assignment") where the demo had four technicians.

`GET /api/tenant-resources` exists but does not close this: it resolves a spoken name to a
declared identifier for the agent's slot extractor, over *"the only two kinds a booking payload
carries"* — service and location. It is not a staff registry.

**Needed:** a resource field on the booking payload and the schedule projection, plus
`GET`/`POST /api/resources`.

#### G12. No booking provenance

`prov: "unknown"` on every live row (`bookings/adapters.ts:151`). Nothing records whether the
agent or a person made a booking, which is why the Analytics *"Who handled them"* split is
badged **"not recorded server-side"** and why no row can say "Booked by AI". `GET
/api/reporting/overview` documents the same refusal in its own XML comment.

Cheap relative to its visibility — one attributed column written at finalize.

#### G13. No no-show / attendance state

No-show rate is a headline KPI *and* a customer tag (`'No-show risk'`) *and* the justification
for the "require a deposit from customers with a past no-show" guardrail. None of the three is
derivable today. This needs a lifecycle transition on the booking, not just a report over one.

#### G14. Headline KPIs

`GET /api/reporting/funnel` owns the stage counts and `GET /api/reporting/overview` owns per-day
/ per-channel / per-service. Missing: `GET /api/reporting/kpis` with **prior-period deltas** —
every KPI card renders one (`+12%`, `-1.8pt`).

Sequencing matters: of the four cards, one needs G2 (deposits captured), one needs G12
(AI-handled %) and one needs G13 (no-show rate). Built before those, the endpoint can honestly
return exactly one card.

#### G15. Customer profile depth

`GET /api/customers` projects `customer_ref`, `booking_count`, first/last activity and
`last_service_id` — spec 14 minimisation. The screen suppresses the rest rather than zero-filling
it, on the stated grounds that a "$0 lifetime" reads as a fact and not as "unknown". Missing:
name, phone, channel, tags, operator notes, lifetime spend, no-show count.

Separately and more sharply: the projection carries **no origin metadata**, so bookings a
console operator made can collapse into a row for the operator themselves. `isOperatorRef()`
(`customers/adapters.ts`) labels that row rather than fixing it, and its comment is explicit
that the fix needs either an origin field on `CustomerSummary` or a ratified way to attach a
guest to a staff-made booking.

**Needed:** `GET /api/customers/{ref}` for depth, a notes/tags write, and an origin field.

#### G16. Conversation summary depth

`ConversationSessionSummary` carries no outcome, no provenance and no preview text — so
`Thread.status` and `Thread.prov` are deliberately left unset (setting them "meant every ended
session was labelled pending and every thread claimed the agent had handled it") and every list
row shows an empty preview until it is opened and its transcript loads.

Two adjacent joins are also missing: `Booking.transcript` is always `null` (no booking ↔ session
link), and `Booking.channel` is always `null` (the schedule projection carries no channel), so
the grid cannot show how a booking arrived.

Note the boundary: the fix is a **richer read**. Writing turns or consent from the console stays
forbidden (§13 C4/C5) — a transcript is evidence of what was said, and consent is the guest's.

#### G17. Notification delivery state

Spec 10 exposes a provider-facing **write** (`POST /api/notification-webhooks/delivery-receipts`,
secret-authenticated) and **no read**. If the console should ever show "confirmation sent" or
"delivery failed", that is a new read endpoint over `notifications`. §13 C3 is explicit that
reading is not the same capability as asserting a receipt.

#### G18. Menu artifact upload *(= §12 gap 5)*

`source_artifact_ref` references an already-stored artifact and the object-storage lifecycle is a
known deferred item, so "Register a menu" takes a reference string rather than a file.

---

## 3. Suggested build order

1. **G1 — auth.** Nothing ships without it, and it is independent of everything else.
2. **G12 + G13 — provenance and attendance.** Two columns that between them unblock three of the
   four KPI cards, the Analytics handled-by split, per-booking attribution and the customer risk
   tag. The best ratio of schema change to surfaces recovered.
3. **G5, then the rest of P2.** Hours first, because `TIME_SLOTS`, `dayBounds()` and
   `TodayScreen`'s closed-day state are all waiting on it. Guardrails (G8) and the knowledge base
   (G9) are ordinary CRUD after that.
4. **G2 — deposits.** Turn the policy on, then add the reads. Recovers the Deposits screen, the
   deposits KPI, `Booking.dep` and the `MoreScreen` figure.
5. **G11 — resources.** The largest schema change and the largest visual payoff: the grid stops
   being one lane and becomes a schedule.
6. **G14, G15, G16** — the reads that the earlier model work makes answerable.
7. **G3 — campaigns.** Needs a spec before code.

G17 and G18 are genuinely optional for an end-to-end app and can sit until something asks for
them.

---

## 4. Two non-goals, so they are not re-proposed

**The six §13-C endpoints must stay unwired.** Each is either authenticated by a credential the
browser must never hold (`X-Webhook-Secret`) or asserts something only another party may assert
(channel ingress minting a tenant context; a guest's consent; words attributed to a guest).
Wiring any of them would be a defect, not progress. In every case there is a legitimate adjacent
need served by a *different* endpoint that does not exist yet — G1 for the channel pair, G17 for
delivery receipts, an operator-note record for transcript turns.

**`POST /api/Bookings/{id}/modifications` cannot carry a change.** `SubmitModificationInput` is
`additionalProperties: false` with no field for the changed data. Reschedule correctly goes
through the acceptance front door (`POST /api/Bookings` with `operation: "reschedule"` and a
`target_booking_id`), which is the only path that transports a new time.
