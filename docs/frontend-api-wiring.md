# Wiring the Cordia Web Console to the Backend API

**Status:** Guide · **Audience:** whoever connects `apps/web` to `apps/api` · **Last mapped:** 2026-07-24

This document explains how to replace the web console's mock data with real calls to
the .NET API, exactly what can be wired today, what is blocked on missing endpoints,
and the step-by-step plan to do it. It is based on a full audit of both sides.

> **TL;DR** — The UI is 100% mock data today; nothing calls the API. You do **not** need
> spec 15 to start wiring: the API already resolves tenant from a dev `X-Tenant-Id`
> header and CORS already allows `http://localhost:4200`. The real limiter is that the
> API is **command/write + lookup-by-id** oriented — it exposes almost none of the
> **list/collection reads** the dashboard screens need. So the *write/detail* flows
> (create / reschedule / cancel a booking, waitlist actions, verification) can be wired
> now; most *list* screens (Today grid, Queue, Conversations list, Customers, Deposits,
> Campaigns, most of Analytics) are blocked on new endpoints, not on spec 15.

---

## 1. Current state

| Layer | State |
|---|---|
| API REST endpoints | ✅ Live, tenant-scoped (38 actions across 15 controllers) |
| Tenant resolution | ✅ `X-Tenant-Id` header (dev shim; replaced by signed context in spec 15) |
| CORS for `localhost:4200` | ✅ Allowed in `appsettings.Development.json` |
| Generated TS contract | ✅ `libs/api-contract/generated/typescript/index.ts` (from committed `openapi/v1.json`) |
| Typed fetch client (`fetchApi`) | ✅ Exists at `apps/web/src/api/client.ts` — ⚠️ **not called anywhere** |
| Screens → backend | ❌ **Not connected.** Every screen reads `src/data/console.ts` mock constants |
| Mutations (create/cancel/reschedule) | ❌ Simulated with `setTimeout` + local `setState` in `ConsolePage.tsx` |
| Data-fetching library | ❌ None (no react-query/SWR); only raw `fetch` |
| DB seed/demo data | ❌ None — a fresh DB returns empty lists / 404s until you write data |

The entire "backend" the UI talks to today is one file: **`apps/web/src/data/console.ts`**.
The whole console lives on a single route, `/dashboard` → `ConsolePage.tsx`, which switches
between 10 internal "screens" (`today | queue | conversations | waitlist | customers |
deposits | campaigns | analytics | settings | more`).

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

| Screen | Needs | Endpoint(s) available | Verdict |
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
| **Verification** (grant sub-flow) | Request code → verify → grant | ✅ `POST /api/verifications`, `GET`, `.../verify` | **Wireable now** (needs new UI) |

**Bottom line:** the *action* surface (booking lifecycle, waitlist actions, verification,
menu/catalog management) can be wired now. The *dashboard read* surface needs new
list/query endpoints on the API before those screens can leave mock data. Track that as
API work separate from spec 15.

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
- **No API equivalent** exists for mock `Customer`, `Deposit`, `Campaign`, `Thread`
  (as a list), or the full `AnalyticsData` shape — those stay on mock data until endpoints exist.

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
| GET | `/api/Bookings/{bookingId}` | `GetBookingDetailsResponse` | May need `X-Grant-Id`; 403 if `requires_verification` |
| POST | `/api/Bookings` | `OperationAcceptedResponse` (202) / `IdempotencyConflict` (409) | Create; submit confirmed intent |
| POST | `/api/Bookings/{id}/changes:validate` | `ValidateBookingChangeResponse` | Pre-flight for modify |
| POST | `/api/Bookings/{id}/modifications` | `SubmitBookingModificationResponse` (202/409/422) | Reschedule/modify |
| POST | `/api/Bookings/{id}/cancellations` | `SubmitBookingCancellationResponse` (202/409) | Cancel |
| GET | `/api/booking-operations/{operationId}` | `OperationStatusView` | Poll async op |
| POST | `/api/booking-operations/{id}/{cancel\|resume\|resolve\|alternatives}` | anon status objects | Operator follow-ups (`X-Actor*` required on resume/resolve) |
| GET | `/api/catalog/items?query&category&max_results` | anon item list | |
| GET | `/api/catalog/items/{itemId}` | anon item | |
| GET | `/api/reporting/funnel?from&to` | `BookingFunnelView` | Only analytics endpoint |
| POST | `/api/verifications` · GET `/{id}` · POST `/{id}/verify` | verification responses | Grant sub-flow |
| POST | `/api/waitlist/enrollments` · DELETE `/{id}` · POST `/offers/{id}/{accept\|decline}` | anon status | No list endpoint |
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
