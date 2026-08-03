# Wiring the Cordia Web Console to the Backend API

**Status:** Guide · **Audience:** whoever connects `apps/web` to `apps/api` · **Last mapped:** 2026-07-27

> **Update (2026-07-27, branch `feat/ui-wire`):** the wiring described here has been
> implemented. See §11 for what is live, the two contract gaps that had to be closed
> first, and what remains on mock data.

This document explains how to replace the web console's mock data with real calls to
the .NET API, exactly what can be wired today, what is blocked on missing endpoints,
and the step-by-step plan to do it. It is based on a full audit of both sides.

> **TL;DR (original assessment, now superseded — see §11)** — When this was written the UI
> was 100% mock data and the API exposed almost no **list/collection reads**, so the
> dashboard screens were blocked on new endpoints rather than on spec 15. Those six list
> endpoints have since been added and the screens wired; §11 records what is actually live.
> The remaining limiter is no longer the read surface but the **fulfilment engine**, which
> implements only the create path — see §11's "Blocking gap".

---

## 1. Current state

The left column is the state this guide was written against; the right is the state after
the wiring described in §11.

| Layer | Was (2026-07-24) | Now (2026-07-27) |
|---|---|---|
| API REST endpoints | Live, tenant-scoped (38 actions / 15 controllers) | **46 actions / 18 controllers** — six console read endpoints added (§11) |
| Tenant resolution | `X-Tenant-Id` header (dev shim) | spec 15 has **landed**: `TenantContextVerificationMiddleware` runs first and a verified `X-Tenant-Context` is authoritative. The header shim survives for dev, but is now **registered only when `Tenancy:AllowHeaderShim` is true** (default: Development only) — see §12 |
| CORS for `localhost:4200` | Allowed in `appsettings.Development.json` | unchanged |
| Generated TS contract | present | regenerated; 44 paths |
| Typed fetch client (`fetchApi`) | existed but **not called anywhere** | the single front door for every API call |
| Screens → backend | **Not connected** — all mock | Today, Queue, Conversations, Customers, Waitlist, Analytics, Settings read live data |
| Mutations (create/cancel/reschedule) | `setTimeout` + local `setState` | TanStack `useMutation` (`features/bookings/mutations.ts`); create/cancel submit + poll for real and are fulfilled end-to-end; **reschedule is blocked on fulfilment, see §11** |
| Data-fetching library | none | `@tanstack/react-query` |
| DB seed/demo data | none | unchanged — a fresh tenant returns empty lists |

The whole console still lives on a single route, `/dashboard` → `ConsolePage.tsx`, which
switches between 10 internal "screens" (`today | queue | conversations | waitlist |
customers | deposits | campaigns | analytics | settings | more`). `apps/web/src/data/console.ts`
remains the source of the *component shapes* and of the demo data still used by the screens
that have no endpoint.

---

## 2. Prerequisites (one-time dev setup)

1. **Generate the shared types** (generated dirs are gitignored):
   ```bash
   bun nx run shared-schemas:generate-types
   bun nx run api-contract:generate-types
   ```
2. **Run the API**: `bun nx serve api` → listens on `https://localhost:8001`
   (and `http://localhost:8000`). Trust the dev TLS cert, or target the http port.
3. **Run the web app**: `bun nx serve web` → `http://localhost:4200`. Vite proxies
   `/api` → `VITE_API_PROXY_TARGET` (default `https://localhost:8001`, `secure:false`),
   so relative `/api/...` requests just work in dev.
4. **CORS**: `http://localhost:4200` is already allow-listed
   (`apps/api/src/Cordia.Api/appsettings.Development.json`). Add any other origin to
   `Cors:AllowedOrigins` there. (When you go through the Vite proxy, CORS is moot anyway —
   requests are same-origin from the browser's view.)
5. **Seed some data**: the DB starts empty. Create data via the write endpoints
   (submit a booking, enroll a waitlist entry, upload/publish a menu catalog) using one
   consistent `X-Tenant-Id` (any string works, e.g. `demo`), then read it back with the
   **same** tenant header — tenant scoping is enforced by an EF global query filter.

---

## 3. Authentication & required headers

There is **no real auth** yet (no `[Authorize]` anywhere). Access is gated only by header
shims that are *trusted client input* in dev:

| Header | When to send | Notes |
|---|---|---|
| `X-Tenant-Id` | **Every data request** | Missing/empty → HTTP 400 "Missing tenant". Any non-empty string accepted. Replaced by spec-15 signed context later. |
| `X-Correlation-Id` | Optional | Echoed back; else server mints `corr_{guid}`. Must match `^[A-Za-z0-9_-]{1,64}$`. Useful for log correlation. |
| `X-Actor`, `X-Actor-Role`, `X-Reason-Code` | Operator/staff writes | **Mandatory** on booking-operation `resume`/`resolve`, all Menu owner actions, Preorder, dead-letter replay. |
| `X-Grant-Id` (or `?grant_id=`) | Sensitive reads | Spec-08 verification grant for `GET /api/Bookings/{id}` and transcript reads when `requires_verification`. |
| `X-Webhook-Secret` | Provider webhooks only | Not used by the UI. |

**Implication:** the console needs a tenant value in scope. For now, hard-code/env one
dev tenant (see §5 client change). When spec 15 lands, this header is swapped for a
signed context obtained from the authenticated web-origin flow — a client-layer change,
not a rewrite of the screens.

---

## 4. What can be wired today vs. what is blocked

The API is **command-oriented**: it accepts intents/commands, runs async operations, and
lets you look records up **by id or by customer identity** — it does **not** currently
expose tenant-wide **list** endpoints for the dashboard. Here is the honest per-screen map.

> **Superseded by §11.** The verdicts below describe the API *before* the six console read
> endpoints were added. Every "Blocked" row except Deposits and Campaigns has since been
> unblocked; see §11 for the current per-screen status. Kept for the rationale.

| Screen | Needs | Endpoint(s) available | Verdict (pre-implementation) |
|---|---|---|---|
| **Today** (weekly booking grid) | List all bookings for the tenant/week | ❌ none — `GET /api/Bookings` is an **identity lookup** (`phone_e164`/`booking_reference`/`email`), not a tenant list | **Blocked** — needs a "list bookings by date range" endpoint |
| **Queue** (failed/attention ops) | List booking operations needing attention | ❌ none — `GET /api/booking-operations/{operationId}` is by-id only | **Blocked** — needs a "list operations by status" endpoint |
| **Conversations** (thread list + transcript) | List sessions/threads; then transcript | ⚠️ transcript exists (`GET /api/conversation-sessions/{sessionId}/transcript`); **no list-sessions endpoint** | **Partial** — transcript wireable per-id; thread list blocked |
| **Waitlist** (entries + actions) | List enrollments; enroll/cancel/accept/decline | ⚠️ actions exist (enroll/cancel/accept/decline); **no list endpoint** | **Partial** — actions wireable; listing blocked |
| **Customers** | List/search customers, profile | ❌ none | **Blocked** — no customer endpoint |
| **Deposits** | List deposits + payout summary | ❌ none (spec 09 payment deferred) | **Blocked** (by design) |
| **Campaigns** | List marketing campaigns | ❌ none | **Blocked** — no campaign endpoint |
| **Analytics** | KPIs, per-day, channel/service breakdowns | ⚠️ `GET /api/reporting/funnel?from&to` only | **Partial** — funnel wireable; rest blocked |
| **Settings** | Services/hours/policies/guardrails/knowledge | ⚠️ `GET /api/consent-policies`, `GET /api/catalog/items`, Menu CRUD | **Partial** — catalog/menu/consent wireable; hours/guardrails/knowledge have no endpoint |
| **Create / Reschedule / Cancel booking** (drawers) | Submit intent → poll op | ✅ full support (see §7) | **Wireable now** |
| **Verification** (grant sub-flow) | Request code → verify → grant | ✅ `POST /api/verifications`, `GET`, `.../verify` | **Wired** — drives the cancel flow (§11) |

**Bottom line (as assessed then):** the *action* surface could be wired immediately; the
*dashboard read* surface needed new list/query endpoints first. Those endpoints were
subsequently built (§11), so this bottom line no longer describes the current state.

---

## 5. Client changes (`apps/web/src/api/client.ts`)

The current `fetchApi` sends only `Accept: application/json` and throws a bare `ApiError`
on non-2xx. Wiring needs three additions:

1. **Send `X-Tenant-Id` on every request.** Source it from an env var
   (`import.meta.env.VITE_DEV_TENANT_ID`) or `window.APP_CONFIG` so it swaps cleanly for
   spec-15 auth later.
2. **Parse `ProblemDetails` / `ValidationProblemDetails`** from error bodies so the UI can
   show the real message (and detect `409 IdempotencyConflict`, `422`, `requires_verification`).
3. **Set `Content-Type: application/json`** for writes (the wrapper doesn't today; POST
   callers must send a body with this header).

Sketch:

```ts
const TENANT = import.meta.env.VITE_DEV_TENANT_ID ?? "demo";

export async function fetchApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(resolveUrl(path), {
    ...init,
    headers: {
      Accept: "application/json",
      "X-Tenant-Id": TENANT,               // dev shim → spec-15 signed context later
      ...(init?.body ? { "Content-Type": "application/json" } : {}),
      ...init?.headers,
    },
  });
  if (!res.ok) {
    let detail = `Request to ${path} failed: ${res.status}`;
    try {
      const problem = await res.json();      // ProblemDetails / ValidationProblemDetails
      if (problem?.detail || problem?.title) detail = problem.detail ?? problem.title;
    } catch { /* non-JSON error body */ }
    throw new ApiError(res.status, detail);
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T);
}
```

**Type the calls** using the generated contract (never import from `generated/**` by path —
use the package). Per `docs/frontend-types.md`:

```ts
import type { paths } from "@cordia/api-contract";

type FunnelResponse =
  paths["/api/reporting/funnel"]["get"]["responses"][200]["content"]["application/json"];

const funnel = await fetchApi<FunnelResponse>("/api/reporting/funnel?from=...&to=...");
```

Use Zod (`@cordia/shared-schemas/zod`) only for genuinely untrusted runtime input
(webhooks, SSE, localStorage) — not to type your own fetches.

---

## 6. Data-fetching approach

There is no react-query/SWR today; server state is plain `useState` in `ConsolePage`, and
"loading" is a fake 540ms `setTimeout`. Two options:

- **Recommended: add `@tanstack/react-query`.** Gives caching, loading/error state,
  refetch, and mutation lifecycle for the operation-polling flows (§7) essentially for
  free. Wrap `ConsolePage` in a `QueryClientProvider`; replace each `useState(() => [...MOCK])`
  with a `useQuery`, and each `setTimeout` mutation with a `useMutation`.

  > **How the writes ended up structured.** Booking writes are `202 Accepted` + poll-to-terminal,
  > not plain POSTs, so `features/bookings/mutations.ts` puts the *whole* submit→poll sequence
  > inside `mutationFn`. That keeps `isPending` honest — true for as long as the operation is
  > genuinely in flight — and moves invalidation into a single `onSettled` instead of every call
  > site. Create is the exception: it returns at the 202 so the drawer can watch the intermediate
  > states with `useOperationStatus`, which is what drives its progress UI. Waitlist removal is
  > the textbook optimistic case (`onMutate` snapshot → `onError` rollback → `onSettled`
  > refetch). Mutations inherit `retry: false` from the shared `QueryClient`: a write is never
  > silently re-sent behind the operator's back.
- **Minimal: hand-rolled `useEffect` + `fetchApi`** with local `loading`/`error`/`data`
  state. Fine for a first read-only screen; gets painful once you add operation polling
  and cache invalidation.

Given the async-operation polling that booking writes require, react-query is the better fit.

---

## 7. Booking write flows (the important part)

Booking mutations are **not** simple POSTs — they submit an intent, return `202 Accepted`
with an operation id, and you **poll the operation** to a terminal state. The current
drawers collect far less than these payloads require, so this is real new work.

### Create (`CreateBookingDrawer`)
1. `POST /api/Bookings` with `SubmitConfirmedIntentRequest` — requires `intent_id`,
   `intent_revision`, `revision: IntentRevision` wrapping a `BookingRequest`
   (`operation: "create"`), a `confirmation`, and an **`idempotency_key`**.
   → `202 OperationAcceptedResponse` (with `Location` header) or `409 IdempotencyConflict`.
2. Poll `GET /api/booking-operations/{operationId}` (`OperationStatusView`) until terminal.
3. Handle `400 ValidationProblemDetails`.

### Reschedule / modify (`RescheduleDrawer`)
1. *(optional)* `POST /api/Bookings/{id}/changes:validate` → surfaces
   `requires_confirmation` / `requires_payment` / fees.
2. `POST /api/Bookings/{id}/modifications` (`SubmitModificationInput`: `confirmation`,
   **`expected_booking_version`**, `grant_id`, `intent_revision`,
   `modification_idempotency_key`) → poll operation.

### Cancel (`BookingDetailDrawer`)
- `POST /api/Bookings/{id}/cancellations` (`SubmitCancellationInput`: `confirmation`,
  `expected_booking_version`, `grant_id`, `cancellation_idempotency_key`, `reason_code`)
  → poll operation.

### Cross-cutting requirements the mock shapes don't carry
- **Idempotency key** on every write (generate + persist per attempt for safe retries).
- **`expected_booking_version`** for modify/cancel (optimistic concurrency) — not in the
  mock `Booking` type; must come from a real booking read.
- **Verification grant sub-flow**: reads/writes may return `requires_verification`. That
  needs a new UI: `POST /api/verifications` → collect code → `POST .../verify` →
  use returned `grant_id` as `X-Grant-Id`. No such UI exists today.

---

## 8. Shape mismatches to reconcile

The mock types in `console.ts` are hand-rolled and **do not** match the API contract.
Wiring must translate, ideally in a per-screen adapter that maps API DTOs → the shapes the
components already expect (least churn), or refactor components to the generated types.

- **`Channel` enum differs.** Mock: `phone | sms | web | instagram | whatsapp`.
  Shared-schemas: `email | sms | voice | web`. Pick a canonical mapping.
- **Booking time model differs.** Mock `Booking` uses `day: number` + `start: number`
  (minutes-since-midnight) + string `staff`/`service` keys. API `ManageableBooking` /
  `BookingProjectionRef` use ISO `starts_at`, `booking_id`, `booking_reference`, `party_size`.
- **No API equivalent** exists for mock `Deposit`, `Campaign`, or the full `AnalyticsData`
  shape — those stay on mock data until endpoints exist. (`Customer` and `Thread`-as-a-list
  now have endpoints — §11 — though the customer projection deliberately carries far less
  than the mock type: no phone, no-show count, lifetime spend or tags.)
- **No API equivalent for provenance.** Nothing records whether a booking was made by the
  agent or by staff, so live rows render no "Booked by AI"/"Booked by you" tag at all rather
  than guessing one.

---

## 9. Recommended phased plan

**Phase 0 — Foundations** (unblocks everything, low risk)
- Generate types (§2). Add `X-Tenant-Id` + error parsing to `fetchApi` (§5).
- Add react-query + `QueryClientProvider` (§6). Add a `VITE_DEV_TENANT_ID` env var.

**Phase 1 — Read what exists** (prove the pipe end-to-end)
- Wire **Analytics → `GET /api/reporting/funnel`** (the one clean read).
- Wire **Settings → `GET /api/consent-policies`** and **`GET /api/catalog/items`**.
- Keep the unbacked screens on mock data behind a clear `// MOCK: no endpoint yet` note.

**Phase 2 — Booking write flows** (the high-value path)
- Implement the operation-polling helper (submit → poll `OperationStatusView` → terminal).
- Wire Create / Reschedule / Cancel drawers to the real endpoints (§7), including
  idempotency keys and `expected_booking_version` from a real booking read
  (`GET /api/Bookings/{id}`).
- Build the verification grant sub-flow UI (`/api/verifications`).

**Phase 3 — Waitlist & conversations (partial)**
- Wire waitlist **actions** (enroll/cancel/accept/decline); list stays mock until an
  enrollments-list endpoint exists.
- Wire conversation **transcript** by session id; thread list stays mock.

**Phase 4 — Unblock the dashboard reads (API work, separate from spec 15)**
- Add tenant-scoped **list/query** endpoints the console needs: bookings-by-date-range
  (Today), operations-by-status (Queue), sessions list (Conversations), waitlist list,
  customers, and the richer analytics aggregates. Then swap those screens off mock data.

**Later — Spec 15**
- Replace the `X-Tenant-Id` dev shim in `fetchApi` with the signed `TenantContext` obtained
  from the authenticated web-origin flow. Screen code is unaffected.

---

## 10. Endpoint reference (for wiring)

Base: `/api`. All require `X-Tenant-Id` unless noted. Route casing is literal
(`/api/Bookings`, `/api/Ping` are capitalized; the rest are kebab/lowercase).

| Verb | Path | Response | Notes |
|---|---|---|---|
| GET | `/api/Bookings?phone_e164&booking_reference&email` | `FindManageableBookingsResponse` | Identity lookup, **not** a list |
| GET | `/api/Bookings/schedule?from&to&limit` | `BookingScheduleResponse` | Tenant-wide list by date range; `truncated` flag |
| GET | `/api/booking-operations?status&needs_attention&limit` | `ListOperationsResponse` | Unknown `status` → 400 |
| GET | `/api/conversation-sessions?limit` | `ListConversationSessionsResponse` | Metadata only, never transcript text |
| GET | `/api/waitlist/enrollments?active_only&limit` | `ListWaitlistEnrollmentsResponse` | Includes latest offer state |
| GET | `/api/customers?query&limit` | `ListCustomersResponse` | Derived projection; no PII |
| GET | `/api/reporting/overview?from&to` | `BookingOverviewResponse` | Per-day exact; breakdowns may be `breakdown_truncated` |
| GET | `/api/Bookings/{bookingId}` | `GetBookingDetailsResponse` | Carries `version` for modify/cancel. 200/403/404 share one body — a 403 is the typed `requires_verification` status, not a ProblemDetails |
| POST | `/api/Bookings` | `OperationAcceptedResponse` (202) / `IdempotencyConflict` (409) | Create; submit confirmed intent |
| POST | `/api/Bookings/{id}/changes:validate` | `ValidateBookingChangeResponse` | Pre-flight for modify |
| POST | `/api/Bookings/{id}/modifications` | `SubmitBookingModificationResponse` (202/409/422) | Reschedule/modify |
| POST | `/api/Bookings/{id}/cancellations` | `SubmitBookingCancellationResponse` (202/409) | Cancel |
| GET | `/api/booking-operations/{operationId}` | `OperationStatusView` | Poll async op |
| POST | `/api/booking-operations/{id}/{cancel\|resume\|resolve\|alternatives}` | anon status objects | Operator follow-ups (`X-Actor*` required on resume/resolve) |
| GET | `/api/catalog/items?query&category&max_results` | anon item list | |
| GET | `/api/catalog/items/{itemId}` | anon item | |
| GET | `/api/reporting/funnel?from&to` | `BookingFunnelView` | Separated funnel counts (see also `/overview`) |
| POST | `/api/verifications` · GET `/{id}` · POST `/{id}/verify` | verification responses | Grant sub-flow |
| POST | `/api/waitlist/enrollments` · DELETE `/{id}` · POST `/offers/{id}/{accept\|decline}` | anon status | `X-Actor` required |
| GET | `/api/conversation-sessions/{sessionId}/transcript` | `TranscriptView` | Per-id; may need grant |
| POST | `/api/conversation-sessions/{sessionId}/{consent\|transcript-turns}` | persisted results | |
| POST | `/api/menu/catalogs` + `/{id}/{review\|edit\|approve\|publish}` | anon status | `X-Actor*` required |
| POST | `/api/preorder/sagas/{id}/{convert-standalone\|cancel}` | anon status | `X-Actor*` required |
| GET | `/api/consent-policies?channel` | `ConsentPolicyView` | |
| POST | `/api/model-traces` · GET `/{traceId}` | trace views | `X-Actor*` on read |
| POST | `/api/dead-letter-messages/replay` | `ReplayDeadLetterResult` | `X-Actor*` required |
| POST | `/api/notification-webhooks/delivery-receipts` | — | Provider-facing; `X-Webhook-Secret`; not for UI |
| GET | `/api/Ping?name` · `/api/Health` | `PingResult` · `HealthResponse` | No tenant required |

Full generated contract (types + `paths`): `libs/api-contract/generated/typescript/index.ts`
(source `libs/api-contract/openapi/v1.json`).

---

## 11. Implementation status (2026-07-27)

Plan: `thoughts/shared/plans/2026-07-27_frontend-api-wiring.md` ·
Baseline: `thoughts/shared/research/2026-07-27_17-46-20_frontend-api-wiring-baseline.md`

### 🟠 Remaining gap: the fulfilment engine handles create and cancel, not modify/reschedule

Found while reviewing this work, and it bounds what "wired" can mean for manage-booking.

**The original defect:** nothing in `apps/api/src/Cordia.Application/Handlers/BookingFulfillment/**`
branched on `OperationTypeEnum`. `FinalizeBookingCommandHandler` called
`adapter.CreateBookingAsync` unconditionally, for *every* operation type — so a `modify`,
`cancel` or `reschedule` operation was accepted (202) and then fulfilled as if it were a
brand-new booking, creating a **second, disconnected booking** instead of acting on the target.

**Cancel is now fulfilled end-to-end.** `AdvanceBookingOperationCommandHandler` branches at
`Finalizing` on `OperationType == Cancel` and dispatches `CancelBookingCommand` instead of
`FinalizeBookingCommand`. `CancelBookingCommandHandler`
(`.../BookingFulfillment/CancelBooking/`) calls `adapter.CancelBookingAsync` — never
`CreateBookingAsync` — outside the transaction (design §12.6), then in one staged transaction:

- operation `Finalizing → Booked` under a version guard (D1 has no `cancelled` operation
  status; the *booking* carries the cancellation), reason `cancelled` /
  `already_cancelled` / `cancelled_no_provider_effect`;
- booking → `cancelled` (a transition, never a delete — MB-Inv-6);
- **capacity released** via `ReleaseFinalizedByOperationAsync` against the *originating*
  booked operation's `Finalized` hold → `Released`, decrementing `booked_capacity`;
- outbox `notification.cancel_confirmed.v1` (state before notification — Inv-8) and an
  `operation.cancelled` audit event.

An ambiguous provider outcome routes to `Reconciling` and touches nothing else (Inv-7); an
already-cancelled booking short-circuits without re-invoking the provider (§9.4.4); redelivery
is safe (Inv-5). Covered by 17 unit tests (`CancelBookingTests`) and 5 Postgres integration
tests (`CancelFulfilmentTests`) asserting the booking row, the slot's `booked_capacity`, the
hold status, the notification, and that exactly one booking row exists afterwards.

**Modify and reschedule are still create-only — and now refused rather than mis-fulfilled.**
`revision.canonical_payload.target_booking_id` is read by no handler, and
`FinalizeBookingCommandHandler` still creates unconditionally. `AdvanceBookingOperationCommandHandler`
therefore throws `PoisonMessageException` when a `Modify`/`Reschedule` operation reaches
`Finalizing`, so it dead-letters into the operator work-item queue instead of silently creating
a duplicate booking. That guard is server-side on purpose: `RESCHEDULE_FULFILMENT_SUPPORTED`
protects the console, but the HTTP endpoints and the LangGraph lane reach the same path.
Consequence: **reschedule is not offered.** The console builds, hashes, and can
submit a valid `operation: "reschedule"` intent — verified live returning 202 — but submitting
would put a duplicate booking in the venue's system, so `RESCHEDULE_FULFILMENT_SUPPORTED`
(`apps/web/src/features/bookings/submit.ts`) is **false** and the drawer explains why. Flip that
one constant when the engine handles modify/reschedule; everything else is built and tested.

This is spec 06/08 work, not frontend wiring. It predates this change — the
`/modifications` and `/cancellations` endpoints already had it.

### Integration suite: a self-poisoning preorder test (found 2026-07-28, not fixed)

`RecoverySweep_FindsStuckSaga_...` began failing partway through a long session, then failed in
isolation too. It is **not** a flake and not caused by any change here:

`PreorderSagaRepository.FindStuckAsync` is a cross-tenant recovery sweep — `IgnoreQueryFilters()`,
`OrderBy(UpdatedAt)` ascending, `Take(StuckSweepBatchSize)` where the batch is **500**. Every run of
the test seeds a saga backdated an hour and nothing ever cleans it up, so the shared
`Cordia_API_Test` database accumulates stale sagas across runs. Once the count passed 500 (measured:
**526**), the sweep filled its batch with older rows and never reached the one the test had just
seeded. The test's own `Assert.True(swept >= 1)` still passed — it swept 500 *other* sagas — so the
failure surfaced one line later, on the outbox assertion, which reads as unrelated.

Deleting the accumulated rows restores a green suite (414 passed), which confirms the diagnosis. The
underlying weakness is that the integration database is never reset between runs, so any test whose
assertion depends on a **bounded** query over unbounded accumulated state will eventually fail on a
developer machine and pass in CI. Worth fixing at the fixture level rather than by periodically
truncating tables by hand.

### In-flight bookings were invisible on the schedule (closed 2026-07-28)

The highest-severity defect the conformance pass left open, and it was real.

`ConsoleReadModel.Parse()` resolved a booking id from
`payload.target_booking_id ?? operation.booking_id ?? payload.booking_id` and **dropped the row when
all three were null**. `BookingOperation.BookingId` is assigned in exactly one place —
`FinalizeBookingCommandHandler`, from the provider reference — and a create's canonical payload
carries neither `booking_id` nor `target_booking_id`. So every operation in `accepted`,
`validating`, `reserving_capacity`, `held`, `awaiting_payment` and `finalizing` was discarded before
the window filter.

**The consequence was a double-booking.** A booking submitted for Thursday 09:30 that sat in `held`
for forty seconds did not appear on the grid at all, so a second operator saw the slot as free and
booked over a guest who already held it.

Why no test caught it: the schedule fixtures built their canonical payload with
`PayloadFor(startsAt, serviceId, bookingId)`, which bakes a `booking_id` into the payload. Real
create payloads have none, so every schedule assertion had been exercising the already-finalised
case only. The fix is pinned by `InFlightCreatePayload`, a payload shaped like the one the API
actually stores, across all six in-flight statuses.

The fix keeps those rows and tells the truth about them:

- `ParsedRow` now carries a **nullable** `BookingId` plus a non-null `Key` (`BookingId ??
  OperationId`) used for de-duplication and grouping — an unfinalised operation's only stable handle
  is its operation id.
- `ScheduledBooking.BookingId` / `booking_id` became **nullable**. The row does not invent an id:
  cancel and modify address a booking, and sending an operation id would 404.
- `lifecycle_status` is **null** for an unfinalised operation. Every value of
  `BookingLifecycleStatus` (`confirmed`, `modification_pending`, `cancellation_pending`,
  `cancelled`) presupposes a booking row exists, and the old `_ => "confirmed"` fallback reported an
  in-flight create as **confirmed** — the fabricated outcome Inv-16 exists to prevent. That arm was
  unreachable while the rows were being dropped; keeping them made it live, and a test now pins it.
- The console marks such a row `isBooking: false`, and the detail drawer offers neither Cancel nor
  Reschedule for it, explaining that it is still being confirmed.

The customer projection benefits for free: `BuildCustomerResolverAsync` already fell back to the
confirmation's `identity_ref` when no booking row exists (spec 04 §8.8), so a guest whose only
booking is in flight now appears in `/api/customers` instead of being absent.

Also closed: the *Partial results* badge this document already claimed. The server-side half landed
in the conformance pass (a limit-capped page reports `truncated: true`, and paging has a tie-break);
the console now actually reads the flag on both the schedule and the customers list.

### Verification and audit purposes (closed 2026-07-28)

Two items the conformance pass deferred, and what auditing them actually turned up.

**The console's cancel could never have worked.** `SubmitBookingCancellationCommandHandler`
consumes a `cancel` verification grant unconditionally (correctly — Inv-11), and nothing in the app
could obtain one: `features/verification/**` was fully implemented and imported by no page, drawer,
or hook. Worse than the "403 you can't resolve" this looked like from the outside — `grant_id` is
`NotEmpty`-validated, so the console's `grant_id: ""` was rejected **structurally as a 400**, before
the authorization spine ran. There was no 403 to recover from, which is why a submit-then-verify
design would not have worked either. The gate is now opened *before* the first submit. The
integration tests that covered cancellation all seeded a grant row straight into the database, so
the console's actual request shape had never been exercised in any lane.

**A verification 403 now says what to do about it.** `VerificationRequiredException` maps to a
ProblemDetails carrying `code: "verification_required"`, the D5 `action`, and `target_booking_id`
(RFC 9457 extensions). The client matches on `code`, never on `title` — the title is prose for a
human, and a client that branches on copy breaks when copy changes. `AuthorizationFailedException`
deliberately does **not** carry the code: it is also a 403, but it means "your role does not permit
this", which no amount of guest verification fixes. Note this required dropping a hand-rolled
`JsonSerializerOptions` from the handler — it bypassed ASP.NET's ProblemDetails converter and buried
extensions under a nested `extensions` node instead of flattening them per the RFC.

**Transcript reads now record a purpose the operator chose.** The reason code was
`"operator_review"`, hard-coded in the fetcher, on a query inheriting `refetchOnWindowFocus: true` —
so alt-tabbing back to a tab left open on a conversation wrote another `read_sensitive_transcript`
row with the same reason as a genuine dispute investigation. The operator now picks a purpose before
the read happens; the query never refetches on focus or reconnect and never goes stale. Seven
purposes are ratified server-side (`SensitiveReadPurpose`) and enforced by FluentValidation, so an
invented code is a 400 rather than an unfalsifiable audit row.

The allowlist is deliberately narrow. It is **not** applied to `IOperatorActionRequest.ReasonCode`
(DLQ replay, catalog review, manual recovery), which carries open-ended operational narrative like
`provider_outage_recovered`; not to the booking `reason_code` business field (`customer_request`,
`complaint`), a different vocabulary saying why the *guest* changed a booking; and not to model-trace
reads, which are referenced to an incident (`incident_42`) rather than drawn from a category list —
a closed vocabulary there would replace a specific pointer with a vague label. Spec 14 owns
`AuditEventEnvelope.reason_code` and types it `str | None`; ratifying the vocabulary in the Pydantic
contract would bind all three lanes and is that spec's decision, not this one's.

### Conformance pass against this document

A second review audited the implementation against every rule stated above. Fifteen defects
were found and fixed; the ones that changed behaviour visible to an operator:

- **A cancelled booking read back as "confirmed".** Both read models mapped
  `OperationStatus.Booked` to lifecycle `confirmed` before testing the operation type, and a
  successful cancel terminates at `Booked`. A guest who cancelled and then looked their
  booking up was told it was confirmed. The `Cancel` arm now precedes the generic one.
- **A cancellation removed the booking from the schedule grid.** A cancel's canonical payload
  has no `starts_at`, but its row (newest activity) claimed the booking's dedupe slot and was
  then dropped by the window filter, so the booking vanished the instant a cancellation was
  *submitted* — the slot looked free while the guest still held it. Slot and lifecycle are now
  resolved from different rows.
- **A hard-failed booking rendered as "Cancelled"** (`toBookingStatus` tested lifecycle before
  the failure statuses, and the server maps `rejected`/`expired`/`failed_final` onto lifecycle
  `cancelled` too). An operator reads that as "the guest cancelled" and never investigates.
- **A duplicate cancel re-notified the guest.** The already-cancelled short-circuit still wrote
  `notification.cancel_confirmed.v1`; the outbox write is now guarded (the audit event is not —
  it is internal and carries `reason=already_cancelled`).
- **An ambiguous cancel could never resolve.** `ReconcileOperationCommandHandler` is hard-coded
  to the create path's `finalize` step and its lookups prove a booking *exists*, which says
  nothing about a cancellation; its not-proven path recreates the booking. A cancel now
  escalates to manual review instead (`cancel_reconcile_requires_review`).
- **Cancellations inflated the funnel** — `confirmed` counted every `Booked` operation
  regardless of type, so cancelling a booking raised the confirmed figure.
- **A failed fetch rendered as a confident empty list.** None of the console's five queries read
  `isError`, so an API outage showed an empty week rather than an error; `CustomersScreen`
  substituted six fabricated customers — names, phone numbers, lifetime spend — with no badge.
  Failures are now named on screen, and the demo rows are badged.
- **Reaching the row limit reported `truncated: false`**, presenting a partial week as complete;
  and `Skip`/`Take` paged over a non-unique `ORDER BY UpdatedAt`, so tied rows could be skipped
  or duplicated between pages.
- **`canon.v1` diverged from the server in two cases** — see below.
- Live bookings asserted a channel (`b.channel || 'phone'` turned a deliberate `null` into
  "Booked via Phone") and a deposit read from the mock catalog. Both are now omitted, matching
  the `ProvTag` treatment.
- `ConsentState.NotRequired` shipped as `"notrequired"`, not the declared `"not_required"`.
  A shared `EnumWireNames.ToWireName` now reads the generated attribute.
- `vite-env.d.ts` ended with `export {}`, making it a module — so its `ImportMetaEnv`
  declarations never augmented the global and every `import.meta.env.VITE_*` read silently fell
  back to `any`.

### Two contract gaps this guide did not anticipate

1. **`canon.v1` has to run in the client.** `POST /api/bookings` requires the caller to
   supply `revision.payload_hash` **and** `confirmation.intent_payload_hash`; the server
   recomputes the canonical hash and rejects a mismatch with **422**
   (`ConfirmationHashMismatchException`), and a DB trigger requires the two to be equal.
   There was no conforming non-C# implementation: the Python one
   (`cordia_agent/revisions/manager.py`) was a documented stub that could not have matched,
   so the LangGraph lane would have 422'd too.

   Closed by porting `canon.v1` to **TypeScript** (`apps/web/src/api/canonicalHash.ts`) and
   **Python** (`cordia_agent/revisions/canonical_hash.py`), both pinned to a shared corpus
   at `tools/config/canon-v1-test-vectors.json` whose expected hashes are produced and
   asserted by the authoritative C# hasher. Two traps are worth knowing: the canonical
   bytes come from .NET's default `JavaScriptEncoder`, which escapes every non-ASCII code
   unit as `\uXXXX` with **uppercase** hex (`JSON.stringify` does not, which would break
   any accented name); and the whitespace class is .NET's `char.IsWhiteSpace`, which
   includes U+0085 and excludes U+FEFF — the opposite of JavaScript's `\s` on both counts.

   The conformance pass found two more, both verified by running the real encoder on
   net10.0 and now pinned as corpus vectors (82 total):

   - **`System.Text.Json` writes `\b \t \n \f \r` as two-character escapes**, not `\uXXXX`
     (`` and other control characters stay numeric). The ports emitted `\uXXXX`
     universally. Whitespace collapsing hides `\t\n\f\r` inside *values* — but
     `char.IsWhiteSpace('')` is **false**, so a backspace survives into the encoder,
     and object *keys* are never whitespace-collapsed, so all five matter. A payload
     containing one hashed differently on client and server and was rejected 422.
   - **`DateTimeOffset.TryParse` disagreed with the anchored ISO regex in both directions**:
     it tolerates surrounding whitespace (`" 2026-06-15T18:00:00Z"` is a timestamp to the
     server, a plain string to the ports) and it rejects offsets past ±14:00 (`+15:00` is a
     plain string to the server, a normalised UTC instant to the ports). Both now match.

2. **`expected_booking_version` was not readable.** Modify and cancel require it, but
   `BookingProjectionRef` had no version field. Worse, the obvious candidate was wrong:
   `BookingProjection.Version` is the *operation* version, while the handlers compare
   against `bookings.version`. Closed by adding an optional `version` to
   `BookingProjectionRef` (Pydantic → regenerated C#/TS) sourced from `IBookingRepository`.
   It is `null` when no authoritative booking row exists yet — treat that as "not
   modifiable yet", not as version 0.

### Endpoints added for the console (all `GET`, all read-only, no migration)

| Verb | Path | Response |
|---|---|---|
| GET | `/api/Bookings/schedule?from&to&limit` | `BookingScheduleResponse` |
| GET | `/api/booking-operations?status&needs_attention&limit` | `ListOperationsResponse` |
| GET | `/api/conversation-sessions?limit` | `ListConversationSessionsResponse` |
| GET | `/api/waitlist/enrollments?active_only&limit` | `ListWaitlistEnrollmentsResponse` |
| GET | `/api/customers?query&limit` | `ListCustomersResponse` |
| GET | `/api/reporting/overview?from&to` | `BookingOverviewResponse` |

`/api/customers` is a **derived projection** over booking operations and their confirmed
intent revisions — spec 12 ratifies no customers table, so none was invented. It exposes
the opaque `customer_ref`, a booking count and timestamps; no name, phone or email.

### Per-screen status

| Screen | Status |
|---|---|
| Analytics | **Live** — funnel + per-day/channel/service. KPI tiles and the "who handled them" split stay mock (nothing records who handled a booking; reporting one would breach Inv-16) |
| Today | **Live** — schedule grid from `/api/Bookings/schedule`, dated from the window the rows were fetched for, with week navigation. Until 2026-08-04 the grid fetched the real week and labelled it from a frozen `9–15 June` literal (§12) |
| Queue | **Live** — operations needing attention. Rows show only what `OperationSummary` carries: no retry count, no channel, no waiting time, and the platform's own status rather than a blanket "Failed". The primary action is labelled *Clear from queue*, which is what it does (§12) |
| Conversations | **Live** — session list + per-session transcript (sensitive read: sends actor + reason code). A failed or gated transcript read shows an error or the verification gate, never an empty thread; live threads claim neither an outcome nor "handled by AI" (§12) |
| Customers | **Live list** — stats the API does not model (no-shows, lifetime spend, phone, tags) are suppressed rather than shown as zeros |
| Waitlist | **Live list + real removal + real offer accept/decline** (all audited against `X-Actor`). Per-guest auto-book is the guest's recorded consent and renders **read-only**; *Book now* prefills from the enrollment. Enrolling from the console is still not offered — enrollment is guest-initiated (§12) |
| Settings | **Partly live** — consent policy and menu catalog; hours and deposit policy badged as demo |
| Create | **Live** — 202 + poll to terminal, real idempotency key, drawer confirms only on a `booked` status. `confirmation.identity` carries the **operator**, not the typed guest name; the name is a local label and the drawer says so (§12) |
| Cancel | **Live end-to-end** — real version token + reason code, 202 + poll to terminal; the engine cancels with the provider, moves the booking to `cancelled`, releases the capacity hold and emits `notification.cancel_confirmed.v1`. Ambiguous provider outcomes route to `Reconciling` rather than guessing |
| Reschedule | **Disabled** — payload/hashing/identity/polling all built and tested, but not submitted while fulfilment would create a duplicate booking (blocking gap above). Would go through `POST /api/bookings` with `operation: "reschedule"`, **not** `/modifications`: that endpoint's body has no field for the changed data |
| Verification | **Live for cancel and for gated transcript reads** — clicking Cancel opens `VerificationGate` in the booking drawer (request → code → grant, grant held in memory only), and the cancellation is submitted with that grant. It is obtained *before* the first submit because `grant_id` is `NotEmpty`-validated server-side: an empty grant is a 400 nobody can act on, not a 403 that can be recovered from. A grant that expires between issue and use returns a 403 carrying `code: verification_required`, which puts the gate back rather than dead-ending |
| Deposits, Campaigns | **Mock, badged** — spec 09 payment surface is deferred; no spec owns Campaigns |

Everything still on demo data carries a visible **Demo data** badge, so a plausible number
cannot be mistaken for the venue's own. Two rules the console holds to throughout:

- **A successful-but-empty response is a real answer.** Screens fall back to demo rows only
  while a list has *never* loaded, never to fill in an empty result.
- **Nothing is asserted that the API does not record.** Live bookings render no
  "Booked by AI"/"Booked by you" tag (no provenance exists), no channel, and the customers
  list suppresses no-show/spend/phone/tags rather than showing zeros.

### Known follow-ups

- `CatalogController.Search` and the waitlist/menu/preorder actions return `IActionResult`,
  so the generated contract types their bodies as `never`. Only the **catalog search**
  response is Zod-validated today (`features/settings/api.ts`); the waitlist action responses
  are typed `unknown` and their bodies are simply not read. Returning `ActionResult<T>` would
  let the types flow through and remove the need for either.
- `AcceptanceApiFixture`, `TenantResolutionApiFixture` and `PostgresFixture` all migrate the
  same test database and xUnit runs collections in parallel, so a cold database can race
  (`column ... already exists`). Serialising fixture migration would fix it.
- Spec 15: replace the `X-Tenant-Id` dev shim in `src/api/config.ts` with the signed
  `TenantContext`. No screen code is affected.
- **No queryable booking start time.** `starts_at` exists only inside the intent revision's
  canonical-payload JSON, so `/api/Bookings/schedule` and `/api/customers` page through the
  tenant's operations and filter in memory, capped at 20 000 rows and reporting `truncated`
  (the console shows a *Partial results* badge). A projected, indexed start column would let
  both push their predicate into SQL.
- **`SubmitModificationInput` carries no changed data**, so `/modifications` can only
  confirm an already-submitted revision. Worth either documenting as such or giving it a
  payload; today any caller expecting it to apply a change gets a silent no-op.
- **`/api/booking-operations/{id}/resolve` is `manual_review`-only** and needs a reason code,
  so the console's queue "finish" is a local acknowledgement rather than a server
  resolution. A general operator-acknowledgement endpoint would close that gap.
- **`validateChange` (`POST /changes:validate`) is implemented but unused** — §7 marks the
  pre-flight optional, and there is no reschedule form to gate on it while reschedule is
  disabled. It activates with the reschedule flow.
- **`confirmation.verification_level` is hard-coded to `"staff_confirmed"`** on every
  console-originated write. The validator only requires the field to be non-empty, so this is
  accepted, but a real operator identity/level should replace it when auth lands (spec 14).

---

## 12. Gap-closure pass (2026-08-04)

Audit: [`frontend-api-wiring-gaps.md`](./frontend-api-wiring-gaps.md) ·
Plan: `thoughts/shared/plans/2026-08-03_frontend-wiring-gap-closure.md` ·
Baseline: `thoughts/shared/research/2026-08-03_22-54-00_frontend-wiring-gap-closure.md`

A second audit checked §11's claims against the code. Most held. What follows is what did not,
and what closing it turned up. The two rules §11 sets for itself — *a successful-but-empty
response is a real answer*, and *nothing is asserted that the API does not record* — are now
genuinely held rather than approximately held.

### 🔴 The grid showed live bookings under fabricated dates

The worst of it, and unmentioned anywhere before. `weekWindow()` fetched the **real** current
week; every date on screen came from `WEEK` in `data/console.ts`, a frozen literal reading
*9–15 June, today = Thursday*. So a booking on the real Monday rendered in a column labelled
`MON 9 June`, and the same constant dated the detail drawer, the reschedule drawer and the
create drawer's day picker. `WEEK.today = 3` meant every week highlighted a Thursday.

Dates now derive from the window the rows were actually fetched for (`features/bookings/week.ts`).
`todayIndex` is **null** in any week that does not contain today rather than guessing a column,
the week is navigable (day arrows roll over; *Today* moves the window as well as the column), and
the create drawer anchors its submitted instant to the **displayed** week — it used
`startOfWeek(new Date())`, so a booking made while viewing next week was silently submitted for
this one. `WEEK` still exists; it just no longer reaches a live surface.

### Things the console asserted that the API does not record

- **The queue invented a waiting time.** `MOCK_WAIT[id] ?? retries * 9 + 8` minutes — and since
  no live id is in that map, every row read *"8m waiting"*. Alongside a hard-coded `0 retries`
  and a `web` channel badge, none of which `OperationSummary` models. All three are gone; the row
  shows the real last-updated age.
- **Every queue row wore a red *Failed* chip**, although the board also carries `manual_review`,
  `alternatives_required` and `compensation_required`. Four different situations, styled as one.
- **Every conversation claimed *"handled by AI"*** with an AI badge, and every ended session
  showed *Pending* — the session summary carries neither provenance nor outcome.
- **A failed or forbidden transcript read rendered an empty conversation.** The pane fell back to
  the mock `MESSAGES` map, which has no entry for a real session id, so the most sensitive read in
  the product failed to a clean, confident, silent blank. It was the one query outside
  `loadFailures`.
- **Three settings panels and the waitlist consent chip were unbadged demo data.** Business setup,
  agent guardrails and the knowledge base all held local state behind controls that appeared to
  save. The waitlist chip was the sharpest: `auto_book_authorized` is the **guest's** consent,
  recorded at enrollment (spec 11 §20, Inv-1), with no endpoint to change it — yet it was
  clickable, so an operator could appear to grant or withdraw a guest's consent while nothing was
  written. It is read-only on live rows now.
- **Cmd-K searched six fabricated customers** and focused an id that exists nowhere.

### Verification could not reach two of the calls that need it

`useCancelBooking` obtained a `cancel` grant and then read the booking's version **without it**.
`GET /api/Bookings/{id}` answers `requires_verification` as a typed 403, so on a gated booking the
operator completed step-up and the cancellation still failed — on the version lookup, before
anything was submitted. Transcripts could not be verified at all: `getTranscript` accepted a
`grantId` nothing ever passed.

Both are wired. The grant is part of the transcript **query key**, without which react-query
served the cached 403 after step-up succeeded. An authorization 403 is deliberately *not* treated
as recoverable — `AuthorizationFailedException` omits the `verification_required` code because no
amount of guest verification fixes it.

### The console was writing a guest's name into a minimised field

The create drawer sent the typed name as `confirmation.identity`. Spec 04 documents that field as
*"verified identity, or the `anonymous_session` sentinel"*, and the platform surfaces it as the
opaque `customer_ref` on `/api/customers` — which spec 14 minimises precisely so it cannot carry a
person's name. Cancel and reschedule already sent the operator; only create did not.

It now sends the operator. The canonical hash is unaffected because `identity` lives on the
confirmation rather than on `revision.canonical_payload` — **pinned by a test**, since had it been
otherwise this would have been a 422 and a broken idempotency replay.

**Open for spec 04/08/12:** there is no ratified way to attach a *guest* to a staff-made booking.
`BookingRequest` has no customer field at all. Until one exists, console-created bookings have no
notification destination and cannot be found by `GET /api/Bookings?phone_e164=`. The drawer keeps
a name field as a local label and says so, rather than collecting one that goes nowhere.

### Waitlist offers were unreachable for an upstream reason

`acceptOffer`/`declineOffer` had existed since the wiring work and were called from nowhere. The
cause was in the projection: `WaitlistEnrollmentSummary` resolved the latest offer server-side and
returned only its *status* and *expiry* — a caller could see that an offer existed and had nothing
to address. `latest_offer_id` is now exposed (additive, no DDL) and the board can answer offers.

Deliberately **not** optimistic, unlike removal: accepting finalizes a capacity hold and confirms a
booking server-side, and both actions can legitimately 409 when the guest answered first. An offer
past its expiry is treated as unanswerable even when the row still says `offered` — the sweeper may
not have run, and a button certain to 409 is worse than no button.

### 🔒 The `X-Tenant-Id` shim was registered in every environment

`TenantResolutionMiddleware` documents itself as DEV/TEST-ONLY and was always on. Ordering was the
only control — it never overrides a verified `X-Tenant-Context` — but that says nothing about a
request carrying **no** signed context: wherever the shim runs, such a caller simply names the
tenant whose data they receive. Fine on a developer machine; unauthenticated cross-tenant access
anywhere else.

Registration is now conditional on `Tenancy:AllowHeaderShim`, defaulting to `IsDevelopment()`.
Middleware order is unchanged. With the shim off and no verified context, a request fails closed at
`TenantGuard.RequireTenant`. Four integration tests prove the same request returns **200 with the
shim on and 400 with it off**, that a tenant-less request fails closed either way, and that a
malformed signed context is refused with 401 rather than downgrading into the shim. The Alba
fixtures run as environment `Test`, so the flag is set in `appsettings.Test.json` — the test host
opts in explicitly rather than the production default being loosened to keep tests green.

### The flaky integration test was misdiagnosed here

§11 attributed `RetentionSweepTests` to shared-database accumulation. That was wrong: the test
already used a unique tenant per run. The real mechanism is a **PAN-detector false positive**.
`AuditEventSanitizationInterceptor` masks `subject_ref` through `SensitiveDataMask.Sanitize`, and
the test seeded `ikey-{Guid:N}`. A hex GUID is drawn from `0-9a-f`, so ~1.8% contain a run of 13+
digits and **~0.6% — about 1 in 166, measured over 300 000 draws — additionally embed a Luhn-valid
window**. Those are stored masked, and a lookup by the raw key finds nothing. A per-run lottery on
a random id, which is exactly why it "failed in a full run and passed 6/6 in isolation": isolation
was never the variable.

The interceptor states that "system-minted values carry no maskable shape and pass byte-identical".
**That does not hold for hex ids** — in production roughly one audit row in 166 whose subject is a
hex id loses the value linking it back to its record. Whether the detector should exempt
system-minted id shapes is **spec 14's** decision, so the behaviour is pinned by tests
(`SensitiveDataMaskFalsePositiveTests`) and raised rather than fixed by weakening a fail-closed PAN
detector. Tests use `TestIds.New`, whose ids cannot reach a 13-digit run. The suite is now green
twice consecutively (418 passed, 2 skipped).

The **preorder** self-poisoning test described in §11 is a genuinely different mechanism and is
untouched.

### `bun nx typecheck web` was a no-op

`apps/web/tsconfig.json` is solution-style (`{ "files": [], "references": [...] }`), so
`tsc --noEmit` compiled **zero files** and exited 0 whatever the code said. Not theoretical: a real
dangling reference passed `typecheck` and was caught only by `bun nx build web`. The target now runs
`tsc -b --noEmit`.

### Still open after this pass

- **Modify/reschedule fulfilment** (the blocking gap above) — spec 06/08, unchanged.
- **Guest identity on a staff-made booking** — needs a spec 04/08/12 decision, as above.
- **The PAN-detector false positive** on hex ids — spec 14's call.
- Operator actions on operations (`resume`/`resolve`/`alternatives`), conversation writes,
  enrolment from the console, deposits/campaigns, analytics KPIs, routing/deep links, pagination
  and a projected indexed `starts_at` — all as recorded in §11's follow-ups.
