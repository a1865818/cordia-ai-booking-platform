# Wiring the Cordia Web Console to the Backend API

**Status:** Guide · **Audience:** whoever connects `apps/web` to `apps/api` · **Last mapped:** 2026-08-07
(re-mapped against the full `apps/api` surface after specs 01–15 landed, then reconciled with the
2026-08-06 gap-closure pass — §17–§19 — which closed several gaps §12 still listed as open)

This document is the map of the API surface as it exists **today**, what the console
actually reaches, and what it deliberately stops on. It describes built behaviour, not a
plan: §1–§4 are the surface and the per-screen verdict, §5–§9 are the conventions the
wiring holds to, §10 is the endpoint reference, **§13 is the short answer to "what is left to wire"**, and
§11–§16 record specific closed and open items. **§20 restates §13's totals** after the one endpoint
added since the 2026-08-06 mapping.

> **TL;DR** — Specs 01–15 are implemented, but **not all of them surface over HTTP**. Roughly
> half the platform is a background lane (queue, outbox, fulfilment, capacity, deposits,
> notifications) reachable only through messages, so "the spec is finished" and "the console
> can show it" are different statements. Every screen whose data *has* an endpoint is wired:
> schedule, operations, sessions + transcript, waitlist, customers, funnel/overview, consent
> policy, catalog, plus booking create/cancel, waitlist enrolment and offers, operator
> follow-ups, menu catalog management, and the verification-grant sub-flow — plus, since
> 2026-08-06, reschedule, catalog discovery, waitlist locations, session consent state and
> declared availability (§19). Demo data survives only where no endpoint exists (deposits,
> campaigns, parts of settings and analytics), always behind a visible **Demo data** badge. Spec 15's signed `TenantContext`
> is live for the **agent/channel lane only** — the console still runs on the `X-Tenant-Id`
> dev shim, which is now gated to Development (§4, §12 gap 1). History lives in
> `thoughts/shared/{research,plans,reviews}/2026-08-04_frontend-api-wiring.md` — one
> consolidated file per category, each carrying the later passes (three-wiring-gaps,
> console-booking-guest-identity, console-reconciliation, and the 2026-08-07 hardening trio:
> residual-review-findings, capacity-deadlock, capacity-lock-order) as appended, dated parts.

---

## 1. Current state

| Layer | State |
|---|---|
| API REST endpoints | ✅ Live, tenant-scoped — **21 controllers**, incl. the console read endpoints (`Handlers/Console/`) and the spec-15 channel ingress (`Controllers/Channels/`) |
| Tenant resolution | ⚠️ Two mechanisms: signed `X-Tenant-Context` (spec 15, **agent lane only**) and the `X-Tenant-Id` dev shim the console uses — now **Development-gated** (§3) |
| Authentication | ❌ None. No `[Authorize]` anywhere; no sign-in, no profile endpoint. Operator identity is `X-Actor`/`X-Actor-Role`, i.e. client-asserted |
| CORS for `localhost:4200` | ✅ Allowed in `appsettings.Development.json` (`Cors:AllowedOrigins`) |
| Generated TS contract | ✅ `libs/api-contract/generated/typescript/index.ts` (from committed `openapi/v1.json`) |
| Typed fetch client (`fetchApi`) | ✅ `apps/web/src/api/client.ts` — tenant + correlation on every call, typed error classes, `X-Grant-Id`/`X-Actor`/`X-Reason-Code` options |
| Screens → backend | ✅ Wired via `src/features/*` (typed fetchers + adapters) and TanStack Query; see §11 |
| Mutations | ✅ Real: submit→poll for bookings; audited writes for waitlist, operations, menu, preorder, DLQ |
| Data-fetching library | ✅ `@tanstack/react-query` (`retry: false` on mutations) |
| DB seed/demo data | ❌ None — a fresh DB returns empty lists until you write data (empty renders as empty, not as demo rows) |

`apps/web/src/data/console.ts` remains only as the component type vocabulary plus the demo
datasets for the surfaces with no endpoint (always badged). The console lives on a single
route, `/dashboard` → `ConsolePage.tsx`, switching between 10 internal "screens"
(`today | queue | conversations | waitlist | customers | deposits | campaigns | analytics
| settings | more`).

---

## 2. Spec coverage ≠ console coverage

All 15 component specs have implementations, but the platform splits into a foreground lane
(HTTP) and a background lane (queue/outbox/worker). **A finished background spec exposes no
endpoint**, so the console cannot render it no matter how complete it is. This table is the
tie-breaker when someone asks "spec N is done, why isn't it on screen?".

| Spec | Where it lives | HTTP surface the console can reach |
|---|---|---|
| 01 Streaming voice | `apps/langgraph` | — (agent lane) |
| 02 Conversational agent | `apps/langgraph` | — (agent lane) |
| 03 Agent tool contracts | langgraph ↔ api | `/api/catalog/*`, `/api/verifications/*`, `/api/Bookings` (the agent's own tools) |
| 04 Submission & idempotency | `apps/api` | `POST /api/Bookings` → `GET /api/booking-operations/{id}` |
| 05 Queue / outbox / inbox | `apps/api` **background** | `POST /api/dead-letter-messages/replay` only |
| 06 Booking workflow & fulfilment | `apps/api` **background** | `GET /api/booking-operations` + the four follow-up actions |
| 07 Capacity & holds | `apps/api` **background** | `GET /api/availability` · `POST /api/availability/slots` (declared slots only; still **no holds endpoint**, and the read takes no hold — spec 07 `informational`, §19) |
| 08 Identity & manage booking | `apps/api` | `GET /api/Bookings`, `/{id}`, `changes:validate`, `cancellations`, `/api/verifications/*` (`modifications` exists but is **never called** — see §8) |
| 09 Payment / deposit link | `apps/api` **background**, policy **off** | — (see §12 gap 2) |
| 10 Notifications | `apps/api` **background** | `POST /api/notification-webhooks/delivery-receipts` (provider-facing, not for the UI) |
| 11 Waitlist / menu / pre-order | `apps/api` | `/api/waitlist/*`, `/api/menu/catalogs*`, `/api/catalog/items*`, `/api/preorder/sagas/*` |
| 12 Data model & persistence | `apps/api` | — (underlies every read) |
| 13 Observability & SLOs | `apps/api` | `/api/Health`, `/api/model-traces/{id}` |
| 14 Security & privacy | `apps/api` | `/api/consent-policies`, the gated transcript read, model-trace authz |
| 15 Tenant resolution & gateway | api + langgraph | `POST /api/channels/messaging` · `POST /api/channels/telephony` — **agent lane only**, not a console login |

---

## 3. Prerequisites (one-time dev setup)

1. **Generate the shared types** (generated dirs are gitignored):
   ```bash
   bun nx run shared-schemas:generate-types
   bun nx run api-contract:generate-types
   ```
2. **Run the API**: `bun nx serve api` → `https://localhost:8001` (and `http://localhost:8000`).
   Trust the dev TLS cert, or target the http port.
3. **Run the web app**: `bun nx serve web` → `http://localhost:4200`. Vite proxies `/api` →
   `VITE_API_PROXY_TARGET` (default `https://localhost:8001`, `secure:false`), so relative
   `/api/...` requests just work in dev.
4. **CORS**: `http://localhost:4200` is already allow-listed
   (`apps/api/src/Cordia.Api/appsettings.Development.json`). Through the Vite proxy CORS is
   moot anyway — requests are same-origin from the browser's view.
5. **Seed some data**: the DB starts empty. Create data via the write endpoints (submit a
   booking, enroll a waitlist entry, register/publish a menu catalog) using one consistent
   `X-Tenant-Id` (any string, e.g. `demo`), then read it back with the **same** tenant header
   — tenant scoping is an EF global query filter.
6. **Outside Development**, the console will not authenticate at all until §12 gap 1 is
   closed — see the next section before deploying it anywhere.

---

## 4. Tenant resolution, authentication & required headers

There is still **no authentication** (no `[Authorize]` anywhere). What changed with spec 15
is that the tenant is now resolvable two ways, and only one of them is trustworthy.

### The two mechanisms

**Signed context (`X-Tenant-Context`) — authoritative.** `TenantContextVerificationMiddleware`
verifies a compact HS256 JWS via `ITenantContextSigner`; on success the claimed `tenant_id`
becomes the request's tenant and is folded into the ambient correlation context before any
handler runs. Any failure (unknown/retired `kid`, bad signature, expiry, future `issued_at`
beyond skew, wrong `typ`, malformed) throws → **401**. No header ⇒ pass through, and
endpoints then fail closed at `TenantGuard.RequireTenant` (Inv-11).

**`X-Tenant-Id` — dev/test shim.** `TenantResolutionMiddleware` reads the header into the
request tenant, but runs **after** the verifier and never overrides a verified context
(Inv-13). It is registered only when `Tenancy:AllowHeaderShim` is true, defaulting to
`app.Environment.IsDevelopment()` — so outside Development it is **off unless explicitly
enabled**, and the API logs that once at startup. This header is trusted client input:
wherever it runs, any caller can name the tenant whose data they get.

**Who issues a signed context.** Only the two channel-ingress endpoints, and they resolve
from a *transport-attested* signal only — the called DID (`to`) for telephony, the
recipient/page/business id for messaging. Caller-mutable fields (`from`, `caller_name`,
`sender_id`, `sender_name`) are accepted for provider-payload shape and deliberately never
read. **There is no web-origin issuance path**, so the console has no way to obtain a context
(§12 gap 1).

**The claim set** (`TenantContextClaims`, mirrored by the shared `TenantContext` schema):
`tenant_id`, `channel`, `channel_endpoint_id`, `configuration_version`, `correlation_id`,
`session_id`, `allowed_agent_policy_ids`, `allowed_tool_policy_ids`, `issued_at`,
`expires_at`. Note what is *not* there: no operator identity, no role, and no venue display
name — a signed context alone would still not give the console a user or a business name
(§12 gap 11).

### Header table

| Header | When to send | Notes |
|---|---|---|
| `X-Tenant-Context` | Agent/channel lane | Signed compact JWS minted by `/api/channels/*`. Verified → authoritative tenant; invalid → **401**. The console never has one. |
| `X-Tenant-Id` | **Every console request** | Missing/empty → HTTP 400 "Missing tenant". Any non-empty string accepted. Development-gated (above). |
| `X-Correlation-Id` | Optional | Echoed back; else the server mints `corr_{guid}`. Must match `^[A-Za-z0-9_-]{1,64}$`. `fetchApi` always sends one. |
| `X-Actor`, `X-Actor-Role` | Operator/staff writes | Required on operation `resume`/`resolve`, all Menu owner actions, Pre-order recovery, DLQ replay, and **all four waitlist mutations** (enrol / cancel / accept / decline — each is an `IOperatorActionRequest` with `RequiredRole = operator`). Also one of the two ways to read a transcript. |
| `X-Reason-Code` | Audited operator actions | Required with the actor headers on `resume`/`resolve`, menu actions, **the waitlist mutations**, and the staff-role transcript read — `OperatorActionBehavior` deny-audits and 403s without all three. Never defaulted client-side — an audit row that always says the same thing records nothing. |
| `X-Grant-Id` (or `?grant_id=`) | Sensitive reads/writes | Spec-08 verification grant for `GET /api/Bookings/{id}`, transcript reads, modify/cancel, and pre-order convert-standalone. Header wins over query. **Single-use on mutations**: a grant presented to `POST /api/Bookings` for modify/cancel/reschedule is spent when the operation is created — an idempotent retry of the *same* request replays fine, but a second, different mutation needs a fresh grant. |
| `X-Provider-Message-Id` / `X-Provider-Call-Id` | Channel webhooks only | Provider's own correlation id, echoed when well-formed. Not used by the UI. |
| `X-Webhook-Secret` | Notification provider webhooks only | Not used by the UI. |


**`customer_ref` is minimised on every ungated list read (2026-08-09, spec 14 / Inv-12).** It was
documented as opaque and was not: `bookings.customer_id` resolves through
`customer_id → contact_ref → caller_id → phone_e164 → email`, and `waitlist_enrollments.customer_ref`
is the value `NotificationRecipientResolver` sends to. The console mints an opaque `contact_ref`, but
the **agent lane never does** — it carries `phone_e164` inline — so agent-created bookings put a raw
phone number on `GET /api/customers`, `GET /api/Bookings/schedule`, `GET /api/conversation-sessions`
and `GET /api/waitlist/enrollments`, all of which need nothing but tenant scope: no grant, no reason
code, no audit row. That was a lower bar than the transcript read of the same data class, and
`?query=` over the raw column was a working phone-prefix search.

`ContactMinimisation.Mask` now runs at the point of emission on all four: a phone leaves as `•••222`,
an email as `p•••@example.com`, and an opaque handle passes through unchanged. The search matches the
masked value. Grouping still uses the raw key, so per-guest counts are exact. The un-masked contact
remains behind the grant-gated `GET /api/Bookings/{bookingId}`. **Still open at the source:** minting
a `contact_ref` in the agent lane so the column stops being an address at all.


**Transcript authorization is an OR, not an AND**: `GET /api/conversation-sessions/{id}/transcript`
accepts **either** a spec-08 `read_sensitive` grant scoped to the session's customer **or** a
staff role (`X-Actor` + `X-Actor-Role` satisfying operator + `X-Reason-Code`). Consent
absent/declined ⇒ **409**. Allow *and* deny are both audited.

---

## 5. Per-screen map (wired vs. blocked)

| Screen | Needs | Endpoint(s) | Verdict |
|---|---|---|---|
| **Today** (weekly booking grid) | Bookings in a window | ✅ `GET /api/Bookings/schedule?from&to&limit` (`truncated` → Partial badge) | **Wired** (one unnamed lane — no resource model, §9) |
| **Queue** (needs-attention ops) | List operations + operator follow-ups | ✅ `GET /api/booking-operations?status&needs_attention&limit` + `POST /{id}/{cancel\|resume\|resolve\|alternatives}` | **Wired** (follow-ups gated by the row's raw status; local clear/dismiss stays local) |
| **Conversations** | Session list + transcript | ✅ `GET /api/conversation-sessions` + `/{id}/transcript` (purpose-gated, audited) | **Wired** |
| **Waitlist** | List; enroll/cancel/accept/decline | ✅ `GET /api/waitlist/enrollments` + all four actions incl. `POST /api/waitlist/enrollments` | **Wired** |
| **Customers** | List/search | ✅ `GET /api/customers?query&limit` | **Wired** (profile depth limited to the projection) |
| **Deposits** | Deposits + payouts | ❌ none — spec 09 is built but background-only and `IDepositPolicy` is `NoDepositPolicy` | **Blocked** — demo, badged (§12 gap 2) |
| **Campaigns** | Campaign list | ❌ none, no owning spec | **Blocked** — demo, badged |
| **Analytics** | KPIs/breakdowns | ✅ `GET /api/reporting/funnel` + `/overview` | **Partial** — funnel/overview live; unrecorded figures stay demo, badged |
| **Settings** | Policies/catalog/menu; hours/guardrails/knowledge | ✅ consent policy, published catalog items, **menu catalog management** (`POST /api/menu/catalogs` + review/approve/publish) | **Partial** — hours/guardrails/knowledge/services sketch have no endpoint; demo, badged |
| **Create / Cancel / Reschedule booking** (drawers) | Submit intent → poll op | ✅ full flow incl. canon.v1 hash, guest contact ref, verification grant | **Wired** — reschedule live since 2026-08-06: `ModifyBookingCommandHandler` fulfils modify/reschedule at `Finalizing`. The `RESCHEDULE_FULFILMENT_SUPPORTED` gate has been **removed** now that both its branches were unreachable. Guest contact travels as an opaque `contact_ref` (§14), not inline. |
| **Verification** (grant sub-flow) | Request → verify → grant | ✅ `/api/verifications` ×3 | **Wired** (cancel + gated transcript reads) |
| **Operator recovery** (DLQ, traces, pre-order) | Audited recovery commands | ✅ typed modules + screens (§16) | **Wired** — Settings → *Operator recovery* (`DeadLetterReplayPanel`, `ModelTraceViewerPanel`, `PreorderRecoveryPanel`); each driven by an operator-supplied id, no list endpoint invented (§13 A) |

---

## 6. The client (`apps/web/src/api/client.ts`)

`fetchApi<T>` is the only way the console talks to the API — never bare `fetch`.

- **Base URL**: `window.APP_CONFIG.API_URL` when present (runtime injection in hosted
  environments), otherwise relative `/api` paths so the Vite proxy forwards them in dev.
- **Always sent**: `Accept: application/json`, `X-Tenant-Id` (from `api/config.ts`), and
  `X-Correlation-Id` (freshly minted per request unless one is passed).
- **`Content-Type: application/json`** is set only when the body is a **string** — `FormData`,
  `Blob` and `URLSearchParams` carry their own type, and `FormData` needs the browser to
  generate its multipart boundary, which it only does when the header is absent.
- **Per-request extras**: `{ grantId }` → `X-Grant-Id`; `{ actor: true }` → `X-Actor` +
  `X-Actor-Role` (the configured dev identity); `{ actor: "someone" }` → that actor;
  `{ reasonCode }` → `X-Reason-Code`; `{ correlationId }` to reuse one.
- **Caller-supplied headers win** — they are the most specific intent at the call site.
- **Errors** are parsed into the most specific class the body supports (`api/problem.ts`):

| Server body | Thrown as | Carries |
|---|---|---|
| `ProblemDetails` | `ApiError` | `status`, `problem.title`/`detail` |
| `ValidationProblemDetails` (400) | `ApiValidationError` | `errors` map, `fieldMessages()` |
| snake_case `IdempotencyConflict` (409) | `ApiIdempotencyConflictError` | `existingOperationId` |

- **204 or an empty body** resolves to `undefined`, so callers of void endpoints need no guard.

Dev identity comes from `src/api/config.ts` (`VITE_DEV_*`, see `.env.example`). When a
web-origin signed context exists (§12 gap 1), only `config.ts`/`client.ts` change — the
screens are unaffected.

**Type the calls** using the generated contract — never import from `generated/**` by path,
use the package (`docs/frontend-types.md`):

```ts
import type { paths } from "@cordia/api-contract";

type FunnelResponse =
  paths["/api/reporting/funnel"]["get"]["responses"][200]["content"]["application/json"];

const funnel = await fetchApi<FunnelResponse>("/api/reporting/funnel?from=...&to=...");
```

Use Zod (`@cordia/shared-schemas/zod`) only for genuinely untrusted runtime input (webhooks,
SSE, localStorage) — not to type your own fetches.

---

## 7. Data fetching

`@tanstack/react-query` owns all server state (`api/queryClient.ts`). Two rules that are load-
bearing rather than stylistic:

- **Every query key is rooted at the tenant id** (`api/queryKeys.ts`), so the cache is keyed by
  the same scope the server enforces and switching tenants can never serve another tenant's
  cached rows. Keys carry ids and filters only — never PII. Filter objects are serialised as
  JSON so an operator-typed `&` or `=` cannot collide two different filter sets.
- **A grant is part of the transcript key.** A gated read fails without one, and react-query
  would otherwise serve that cached failure forever — the operator would complete step-up and
  see the same error.

Retry policy (`shouldRetry`): **never retry a 4xx** except 408/429. A 400 (bad request /
missing tenant), 403 (verification required), 409 (idempotency conflict or stale version) and
422 (hash mismatch) are decisions the server already made — retrying only delays the error the
operator needs to see. Transport failures and 5xx retry twice with capped backoff. Mutations
run with `retry: false` outright: several write endpoints take **no** idempotency key
(the waitlist *offer* actions, operation follow-ups, menu lifecycle, DLQ replay, pre-order recovery — enrolment now takes one, §12 gap 6), so a silent
retry could act twice. Queries use `staleTime: 30s` with refetch-on-focus.

---

## 8. Booking write flows (the important part)

Booking mutations are **not** simple POSTs — they submit an intent, return `202 Accepted` with
an operation id, and you **poll the operation** to a terminal state. `features/bookings/`
implements this: `buildSubmission.ts` (payload + canon.v1 hash), `submit.ts` (transport),
`useOperationStatus.ts` (poll), `mutations.ts` (submit→poll as one mutation).

### Create (`CreateBookingDrawer`)
1. `POST /api/Bookings` with `SubmitConfirmedIntentRequest` — `intent_id`, `intent_revision`,
   `revision: IntentRevision` wrapping a `BookingRequest` (`operation: "create"`), a
   `confirmation` carrying the **canon.v1 payload hash**, and an **`idempotency_key`**.
   → `202 OperationAcceptedResponse` (with `Location`) / `409 IdempotencyConflict` /
   `422` on hash mismatch.
2. Poll `GET /api/booking-operations/{operationId}` (`OperationStatusView`) until terminal.
3. Handle `400 ValidationProblemDetails`.

The drawer's services come from the **live** published catalog (`GET /api/catalog/items`), so
the submitted `service_id` is an id the server actually knows — see §9.

### Reschedule / modify (`RescheduleDrawer`, drag on the grid) — live since 2026-08-06
1. `GET /api/Bookings/{id}` → the current `version`, which becomes `target_booking_version`.
   For a **gated** booking this read answers `requires_verification` (403) — the staff-role headers
   authorize the mutation, not this read — so the console mounts a `VerificationGate` on that 403
   and replays the move with the grant. The grant then travels on **both** this read and the
   `POST /api/Bookings` in step 3; sending it on the read alone loops the operator through a fresh
   verification code on every attempt, because outside Development the asserted-role path is off
   and the write is refused without a grant of its own. See §17.
2. `POST /api/Bookings/{id}/changes:validate` → pre-flight. Refuses on `stale_version`,
   `capacity_unavailable` or `policy_failed` **before** an attempt identity is minted, so a
   refused change costs no operation id and no idempotency key. Authorized by `X-Actor` /
   `X-Actor-Role` (or a grant on the agent lane) — the same door as the mutation it precedes.
   Fails **open**: if the pre-flight itself is unreachable the reschedule proceeds, because the
   acceptance door re-checks version, capacity and policy authoritatively and an outage on an
   advisory read must not take down a working mutation.
3. `POST /api/Bookings` with `operation: "reschedule"`, `target_booking_id` and
   `target_booking_version` on the canonical payload → 202 → poll the operation.

**Not** `POST /api/Bookings/{id}/modifications`. That body (`SubmitModificationInput`) is
`additionalProperties: false` and carries **no field for the changed data** — it confirms a
revision that was already submitted, so it cannot transport a new time. The acceptance front door
is the only path that can, and `SubmitConfirmedIntentValidator` requires `target_booking_id` for
exactly the modify / cancel / reschedule operations. The endpoint exists and is documented in §10;
nothing in the console calls it (group C6).

Fulfilment: `AdvanceBookingOperationCommandHandler` routes `Modify`/`Reschedule` at `Finalizing`
to `ModifyBookingCommandHandler`, which amends the existing booking through
`IBookingProviderAdapter.ModifyBookingAsync` in one provider call, preserving the original
reservation until the amendment is secured (spec 08 §9.4.8). An uncertain provider outcome routes
to `reconciling` rather than reporting or blindly retrying (Inv-7).

The change carries the guest forward as `customer.contact_ref` (§14): the notification resolver
reads the destination from the revision *this* operation writes, not from the booking being moved,
so a reschedule without it would confirm to nobody.

### Cancel (`BookingDetailDrawer`)
- `POST /api/Bookings/{id}/cancellations` (`SubmitCancellationInput`: `confirmation`,
  `expected_booking_version`, `grant_id`, `cancellation_idempotency_key`, `reason_code`)
  → poll operation.

### Cross-cutting requirements
- **Idempotency key** on every booking write — generated per attempt and **reused across
  retries** of that attempt, so a retry is the same operation rather than a second one.
- **`expected_booking_version`** for modify/cancel (optimistic concurrency), read from
  `GET /api/Bookings/{id}`.
- **Verification grant sub-flow**: gated reads/writes return `requires_verification`.
  `features/verification/` implements `POST /api/verifications` → collect code →
  `POST /{id}/verify` → carry the returned `grant_id` as `X-Grant-Id`. A grant that
  authorizes a mutation through the acceptance door is **consumed by it** — retrying the
  same attempt is fine (the replay hands back the winner's operation), but a new mutation
  must step up again.

---

## 9. Shape reconciliation (how the mock vocabulary was resolved)

The hand-rolled types in `data/console.ts` did not match the API contract. Rather than
rewriting every component, `features/*/adapters.ts` map API DTOs → the shapes the components
expect. The four decisions worth knowing:

- **`Channel`** — the mock `phone | sms | web | instagram | whatsapp` was replaced by the
  ratified shared-schemas vocabulary (`email | sms | voice | web`). Rows the API does not
  attribute render `channel: null` rather than a guess.
- **Booking time model** — mock `day`/`start` (minutes-since-midnight) + string `staff`/`service`
  keys are derived from the API's ISO `starts_at`, `booking_id`, `booking_reference`,
  `party_size`. The grid's week comes from the live schedule window, not a frozen date.
- **Resources/staff do not exist.** `BookingRequest` has no staff/room/table field,
  `ScheduledBookingView` returns none, and there is no resources endpoint. The grid draws
  **one unnamed lane** (`features/bookings/resources.ts`) instead of the four invented people
  it used to stack every booking behind. It is a prop, not a constant, so real lanes drop in
  unchanged when a resources endpoint exists.
- **Bookable services come from the catalog.** `features/bookings/services.ts` builds them from
  `GET /api/catalog/items`; the mock `SERVICE_LIST` is now only a label lookup for ids the API
  returned. `duration_minutes` is omitted when the catalog states none — an invented duration
  is a durable claim about how long the venue is booked for.

**No API equivalent** exists for the mock `Deposit`, `Campaign`, or the full `AnalyticsData`
shape; those stay on badged demo data.

---

## 10. Endpoint reference

Base: `/api`. All require a tenant (`X-Tenant-Context` or the `X-Tenant-Id` shim) unless
noted. Route casing is literal — `/api/Bookings`, `/api/Ping`, `/api/Health` are capitalized;
the rest are kebab/lowercase.

| Verb | Path | Response | Notes |
|---|---|---|---|
| GET | `/api/Bookings?phone_e164&booking_reference&email` | `FindManageableBookingsResponse` | Identity lookup, **not** a list. Candidates require a venue display name to be set — a tenant without one gets none, deliberately (§15) |
| GET | `/api/Bookings/{bookingId}` | `GetBookingDetailsResponse` | May need `X-Grant-Id`; 403 if `requires_verification` |
| POST | `/api/Bookings` | `OperationAcceptedResponse` (202) / 400 / 403 / 409 / 422 | Create; submit confirmed intent. 403 on a mutation (`target_booking_id` set) without a valid grant or staff role — incl. a grant already spent by a *different* request, or an unknown target |
| POST | `/api/Bookings/{id}/changes:validate` | `ValidateBookingChangeResponse` | Pre-flight for a change. Wired into the reschedule flow (§8). Grant **or** `X-Actor` + `X-Actor-Role` — the same door as the mutation it precedes, so the console can run it |
| POST | `/api/Bookings/{id}/modifications` | `SubmitBookingModificationResponse` (202/400/403/409/422) | **Never wired (group C6.)** `SubmitModificationInput` is `additionalProperties: false` and carries no field for the changed data, so it cannot transport a new time — a reschedule rides `POST /api/Bookings` instead (§8) |
| POST | `/api/Bookings/{id}/cancellations` | `SubmitBookingCancellationResponse` (202/400/403/409) | Cancel |
| GET | `/api/Bookings/schedule?from&to&limit` | `BookingScheduleResponse` | Tenant-wide window read (Today grid). Defaults: `from` = start of UTC day, `to` = +7d, `limit` 1–500. Reports `truncated` |
| GET | `/api/booking-operations?status&needs_attention&limit` | `ListOperationsResponse` | Raw 16-value `status` per row (D1) — what gates the follow-ups. `status` is repeated or comma-separated; an unknown value is a **400**, never silently ignored |
| GET | `/api/booking-operations/work-items?limit` | `ListWorkItemsResponse` | **Added 2026-08-07 (§20).** The open manual-recovery queue, most overdue first: `reason`, `sla_deadline`, `assignee`, and the linked operation's **current** status. Tenant-scoped; a plain read, so no `X-Actor` and no audit row. Literal segment — it must be declared before `{operationId}`, and a test pins that it is not swallowed by it |
| GET | `/api/booking-operations/{operationId}` | `OperationStatusView` | Poll async op — **normalized** 9-value status only (D2) |
| POST | `/api/booking-operations/{id}/{cancel\|resume\|resolve\|alternatives}` | anon status (202) | Operator follow-ups. `resume`/`resolve` are `manual_review`-only and need `X-Actor` + `X-Actor-Role` + `X-Reason-Code`; `resolve` body `resolution ∈ {booked, failed_final}` (+`provider_ref` when booked); `alternatives` is `alternatives_required`-only; `cancel` is an idempotent **request**, any non-terminal status |
| GET | `/api/conversation-sessions?limit` | `ListConversationSessionsResponse` | Metadata only — no transcript text |
| GET | `/api/conversation-sessions/{sessionId}/transcript` | `TranscriptView` | Grant **or** staff role + reason (§4). 409 when consent is absent/declined. Allow and deny both audited |
| POST | `/api/conversation-sessions/{sessionId}/{consent\|transcript-turns}` | persisted results | Agent-lane writes |
| GET | `/api/customers?query&limit` | `ListCustomersResponse` | Case-insensitive substring on the **masked** customer ref; `limit` 1–200. Reports `truncated`. **`customer_ref` is minimised (2026-08-09)** — see the note below |
| GET | `/api/reporting/funnel?from&to` | `BookingFunnelView` | Defaults to the last 24 h |
| GET | `/api/reporting/overview?from&to` | `BookingOverviewResponse` | Defaults to the last 7 d. Per-day/channel/service aggregates; deliberately **no** agent-vs-human split (nothing records who handled a booking). Per-day `booked` counts **create/modify/reschedule** operations only — a cancellation reaching its terminal state is not a booking made that day |
| GET | `/api/catalog/items?query&category&max_results` | anon item list | **Published** items only. `max_results` default 20; out-of-range is a 400, not clamped |
| GET | `/api/catalog/items/{itemId}` | anon item | 404 when absent or unpublished |
| GET | `/api/consent-policies?channel` | `ConsentPolicyView` | `notice_text: null` is a real wire value (agent plays its default) |
| POST | `/api/verifications` · GET `/{id}` · POST `/{id}/verify` | verification responses | Spec-08 grant sub-flow |
| GET | `/api/waitlist/enrollments?active_only&limit` | `ListWaitlistEnrollmentsResponse` | `active_only` defaults **true**. Incl. `location_id`, `auto_book_authorized`, latest-offer fields |
| GET | `/api/waitlist/locations` | `WaitlistLocationsResponse` | The `location_id`s actually enrolled against — a **usage list, not a registry** (§12 gap 7). An unrecognised value warns rather than blocks: a venue's first enrolment at a location can never be in it |
| POST | `/api/waitlist/enrollments` | `{enrollment_id, status}` (201) | Operator action: `X-Actor` + `X-Actor-Role` + `X-Reason-Code`. Takes an optional **`idempotency_key`** in the body; absent, the server derives one from the material fields (§12 gap 6) |
| DELETE | `/api/waitlist/enrollments/{id}` | anon status | Terminal, idempotent; releases any live offer's hold. Operator action (`X-Actor*` + `X-Reason-Code`) |
| POST | `/api/waitlist/offers/{offerId}/{accept\|decline}` | anon status (200/404/409) | Accept finalizes the hold; decline releases it and re-matches. Operator action |
| GET | `/api/availability?from&to` | `AvailabilityResult` (200/400) | Declared slots with `free_capacity` = `total − held − booked`, floored at 0. Window ≤ 62 days. **Not owner-gated.** Spec 07 `informational`: no hold, so the console may only say "appears available" (§19) |
| POST | `/api/availability/slots` | `AvailableSlot` (200/400/403) | Owner-only, audited. Idempotent by the derived slot key. **This is what fills `capacity_slots`** — without it the read above answers `[]` for every tenant |
| GET | `/api/menu/catalogs` | catalog list | Owner-gated, audited. Every status, newest first, each with `age_seconds` (§12 gap 3) |
| POST | `/api/menu/catalogs` | `{catalog_id, status:"uploaded"}` (202) | Takes an artifact **reference**, not a file. `X-Actor*` + owner role |
| POST | `/api/menu/catalogs/{id}/{review\|edit\|approve\|publish}` | anon status (200/400/403/404/409) | D4 state machine, one legal step at a time. `X-Actor*` required |
| POST | `/api/guest-contacts` | `GuestContactResponse` (200/400) | Mints the opaque `contact_ref` the canonical payload carries. Called **before** the payload is hashed — the ref has to be inside the hash, and letting the server strip contact afterwards would make the stored payload stop re-hashing to its own recorded hash. Audited (`guest_contact.minted`, ref only — never the value); needs a tenant. Write-only by design: there is no GET, because turning a ref back into a phone number would re-create the exposure it removes |
| GET | `/api/tenant-profile/display-name` | `TenantDisplayNameResult` (200/400) | The venue name guests are shown. Readable by anyone — the shell needs it. Empty string when unset, which is a real answer |
| PUT | `/api/tenant-profile/display-name` | `TenantDisplayNameResult` (200/400/403) | Owner-only, audited, idempotent (tenant is the key). **Setting this is what makes the phone lookup return candidates** |
| GET | `/api/menu/catalogs/{id}/items` | `CatalogDraftItemsResult` (200/400/403/404) | **Draft-scoped**: the only read that sees a catalog which is not published. Returns the namespaced `{catalogId}:{rawId}` ids `/edit` keys on, plus the catalog's real status. Owner role + `X-Actor*` + `X-Reason-Code`; audited |
| POST | `/api/preorder/sagas/{id}/{convert-standalone\|cancel}` | `PreorderResolutionResult` | `X-Actor*` required; convert additionally needs the customer's `grant_id` |
| POST | `/api/model-traces` · GET `/{traceId}` | trace views | The **read** writes an audit row on allow *and* deny; `X-Actor*` required |
| POST | `/api/dead-letter-messages/replay` | `ReplayDeadLetterResult` | `X-Actor*`; reason travels in the **body** |
| POST | `/api/channels/messaging` | `TenantContextResponse` (200/400/422) | **Spec-15 ingress.** Resolves the tenant from `recipient_id` only and mints a signed context. No tenant header required — the tenant is the *output* |
| POST | `/api/channels/telephony` | `TenantContextResponse` (200/400/422) | Same, resolving from the called DID (`to`) only |
| POST | `/api/notification-webhooks/delivery-receipts` | — (202) | Provider-facing; `X-Webhook-Secret`; not for UI |
| GET | `/api/Ping?name` · `/api/Health` | `PingResult` · `HealthResponse` | No tenant required |

Full generated contract (types + `paths`): `libs/api-contract/generated/typescript/index.ts`
(source `libs/api-contract/openapi/v1.json`).

---

## 11. What is wired

The rules the console holds itself to, everywhere below: **a successful-but-empty response is
a real answer** (demo rows appear only while a list has *never* loaded, never to fill an empty
result or paper over an error); **nothing is asserted that the API does not record**; anything
still on demo data wears a visible **Demo data** badge; writes carry a stable idempotency key
where the endpoint takes one and are **never retried silently**; a failure is reported by name
with a retry, never as calm emptiness.

**Reads** — schedule (Today), operations list (Queue), sessions + purpose-gated transcript
(Conversations), waitlist enrollments, customers, funnel/overview (Analytics), consent policy +
published catalog items (Settings). Server-reported truncation renders a **Partial results** badge.

**Booking writes** — create (canon.v1 payload hash, guest **contact ref**, per-attempt operation identity
reused across retries, 202 → poll to terminal) and cancel (grant-gated, version-checked,
submit→poll as one mutation). **Reschedule is live as of 2026-08-06**
(the fulfilment gate has since been **removed**, both branches being unreachable): the engine is no longer create-only —
`AdvanceBookingOperationCommandHandler` routes `Modify`/`Reschedule` at `Finalizing` to
`ModifyBookingCommandHandler`, which amends the existing booking in one provider call and
preserves the original reservation until the amendment is secured (spec 08 §9.4.8). Note the
change request itself travels through the acceptance front door
(`POST /api/Bookings` with `operation: "reschedule"` + `target_booking_id`), **not** through
`POST /api/Bookings/{id}/modifications` — see §13 C6.

**Waitlist** — list, remove (optimistic with single-row rollback), offer accept/decline
(ticking-clock actionability), and **enrolment** (`POST /api/waitlist/enrollments`, the "Add to
waitlist" drawer): guest reference, service from the live published catalog, location (free
input with suggestions from recorded enrollments), party size, acceptable window, offer channel
from the ratified `Channel` vocabulary, offer lifetime. Auto-book is deliberately **not**
offered — it is a consent grant requiring recorded evidence (spec 11 §20) the console cannot
capture, and the drawer says so. The endpoint takes an optional **`idempotency_key`** (§12 gap 6):
the drawer mints one per attempt and re-mints it whenever any field it sends changes, so a retry
is a retry and an edit is a new enrolment. The mutation still never auto-retries — a retry is the
operator's deliberate act — and refetches the list on both outcomes.

The offer channel is restricted to the two the notification layer can actually deliver on
(SMS, email), and the guest reference is validated as a real destination: it is the only thing an
offer can be sent to, so a name-only enrolment would create offers that settle `no_destination`
with the console showing the row as normally enrolled.

**Operator follow-ups on operations** — on the Queue, gated by each row's raw 16-value status so
no button is offered whose call is certain to fail: `manual_review` rows offer *Send back for
checks* (resume → `reconciling`) and *Resolve…* (booked-with-provider-ref or failed, both
requiring the operator's reason — sent as `X-Reason-Code`, never defaulted);
`alternatives_required` rows offer *Waitlist this guest* / *Decline request*; every known
non-terminal row offers *Request cancellation* (reported as a **request**, not a result).
Success toasts echo the server's answer; a 409 is reported as the race it is and the board
refetches either way. The local *Clear from queue* / *Dismiss* remain a plainly-labelled local
acknowledgement.

**Menu catalog management** — in Settings, the owner review→approve→publish flow surfaced as the
D4 state machine, one legal step at a time, never one button. Register-for-ingestion takes an
artifact **reference** (no file-upload endpoint exists); each lifecycle response's
`{catalog_id, status}` is recorded per tenant in sessionStorage, and rows show only that last
**server-confirmed** status with only the legal next action. Review before the draft is
confirmable carries an explicit "may be refused" caveat (a draft may not exist yet). Extraction
itself is no longer unobservable: a failed one settles to `extract_failed` (§12 gap 8, closed).
Nor is it fictional any more — since 2026-08-17 the draft an owner reviews is what the real
LangGraph agent read out of the artifact, not the one hard-coded `Sample Dish` row every ingestion
used to yield (§12 gap 12, closed). The reference still has to be an `inline:`/`data:text/plain`
one, though: **gap 5 (no file upload) is untouched**.
All five actions require the `owner` role exactly; a non-owner session gets an explanation
instead of 403-certain buttons. Publishing invalidates the shared catalog query, so the create
drawer's bookable services update the moment a publish lands.

**Session identity** — the shell shows the actor, role and tenant `fetchApi` actually puts on the
wire (`features/session/identity.ts`), with a hint saying there is no sign-in yet. It previously
asserted a specific person and venue as fact in three hard-coded places, none of which any
endpoint returns.

**Operator recovery (new, 2026-08-05).** Settings → *Operator recovery* wires the three
endpoints that were live with no surface: dead-letter replay, the audited model-trace read, and
pre-order manual resolution. Each is driven by an id the operator supplies — none has a list
endpoint and none of the panels fakes one. Settings → *Menu catalog management* now also offers
**Review items** on an `in_review` catalog: a draft read, an inline editor, and a diff of exactly
what will be sent. See §13 for the constraints each one holds to.

**Still demo (badged):** Deposits, Campaigns, the analytics figures the API does not record
(channels/top-services/handled split), Settings' business setup / services sketch / hours /
deposit policy / guardrails / knowledge base, and the conversations demo threads before first load.

---

## 12. Backend gaps (stopped on, not worked around)

Items the wiring **stopped on** because the API cannot support them. None was papered over
client-side.

| # | Gap | Specific reason | Console consequence |
|---|---|---|---|
| ~~1~~ | ~~**No web-origin `TenantContext` issuance**~~ **CLOSED 2026-08-26** — `AuthController.cs` ships `POST /api/auth/login` (`:26`), `GET /api/auth/session` (`:52`), `POST /api/auth/logout` (`:66`); `LoginPage.tsx` + `RequireSession.tsx` consume them | *Was:* `POST /api/channels/{messaging,telephony}` resolve from a transport-attested channel address (called DID / recipient page id). A browser presents neither, and there is no login endpoint to mint a context from | The console must keep using the `X-Tenant-Id` shim, which `Tenancy:AllowHeaderShim` disables outside Development. **The console cannot authenticate against a non-dev deployment at all** without explicitly re-enabling unauthenticated cross-tenant access |
| ~~2~~ | ~~**No deposits/payments read endpoint**~~ **CLOSED 2026-08-26** — `DepositsController.cs` (`GET /api/deposits` `:25`, refund `:49`) and `PayoutsController.cs:19`. The `NoDepositPolicy` claim is inverted: `IDepositPolicy` resolves to `TenantDepositPolicyProvider` (`InfrastructureServiceCollectionExtensions.cs:208`) | *Was:* Spec 09 is implemented (`DepositSession`, `RequestPaymentCommand`, `ProcessDepositEventCommand`) but entirely in the background lane — events arrive over the queue, not HTTP. `IDepositPolicy` is registered as `NoDepositPolicy`, so nothing requests one | The Deposits screen has nothing real to show; it stays demo and badged. Correcting the earlier note: spec 09 is *built*, not deferred — it is unexposed and switched off |
| ~~3~~ | ~~**No catalog list/discovery read**~~ **CLOSED 2026-08-06** — `GET /api/menu/catalogs` lists the tenant's catalogs at any status with `age_seconds` | *Was:* every catalog read was published-scoped or id-addressed, so a catalog started in another session could not be found at all | The panel lists every catalog the tenant has, at any status, merged with the statuses this session confirmed in its own write responses — the more recent statement wins |
| ~~4~~ | ~~**No draft-catalog items read**~~ **CLOSED 2026-08-05** | It was smaller than it read: `VendorCatalogItems.ItemId` already stored the namespaced `{catalogId}:{rawId}` (`ApplyItemEditsAsync` keys on it directly), so no id scheme had to be designed. The only obstacle was that every read was published-scoped. Fixed by adding the draft-scoped, owner-gated `GET .../items` | Owner in-review edits are wired (§13 B1) |
| 5 | **No menu artifact upload endpoint** | `source_artifact_ref` references an already-stored artifact; the object-storage lifecycle is a known deferred item (specs README, #84). Gap 12 made extraction real but deliberately did **not** build storage: `InlineMenuArtifactTextResolver` reads `inline:`/`data:text/plain` only, and every other scheme settles `ExtractFailed` behind the same `IMenuArtifactTextResolver` port #84 will implement | "Register a menu" takes a reference string, not a file |
| ~~6~~ | ~~**No idempotency key on waitlist enrolment**~~ **CLOSED 2026-08-06** — reuses `idempotency_records`, no new DDL | *Was:* `EnrollWaitlistCommand` never touched the idempotency mechanism, so a re-POST created a second `wle_*` enrollment | The drawer mints one key per attempt and re-mints it whenever any field it sends changes, so a retry is a retry and an edit is a new enrolment; a blind re-send deduplicates server-side |
| ~~7~~ | ~~**No locations read endpoint**~~ **CLOSED 2026-08-06** — `GET /api/waitlist/locations` returns the ids actually in use (a usage list, not a registry) | *Was:* `location_id` is required on enrolment, but nothing listed a tenant's locations | The enrol drawer suggests the `location_id`s this venue has already enrolled against and **warns, never blocks,** on an unrecognised one — a new venue legitimately has none |
| ~~8~~ | **CLOSED 2026-08-06 — extraction has a failure state** | *Was:* `CatalogStateMachine` had nine D4 values and no `extract_failed`, so a failed extraction sat in `extracting` forever, visible only via the DLQ | D4 amended: `extract_failed` is a tenth terminal value. `IngestVendorMenuCommandHandler` settles it at the point of failure and `MenuIngestionMonitor` settles rows whose worker died. The console renders the verdict red and its own over-threshold guess amber (§17) |
| ~~9~~ | ~~**No resource/staff model**~~ **CLOSED 2026-08-26** — `ResourcesController.cs` (`GET`/`POST /api/resources`), `useStaffResources`, `StaffRosterPanel`, and `adapters.ts:145` reading `row.resource_id` | *Was:* No field on `BookingRequest`, none on `ScheduledBookingView`, no endpoint | The schedule grid draws one unnamed lane instead of inventing people (§9) |
| ~~10~~ | ~~**No availability read**~~ **CLOSED 2026-08-06** — `GET /api/availability` + `POST /api/availability/slots`; the write shipped with the read because the table had no writer | *Was:* spec 07 capacity/holds was background-only, so no endpoint exposed free slots | The create drawer shows an advisory *"appears available"* note (the read takes no hold, so it can go stale) and Settings has a Bookable-capacity panel that declares slots. A venue that has declared none gets `[]`, which the drawer states as *no declaration*, never as *fully booked* |
| ~~11~~ | ~~**No venue/business profile**~~ — **CLOSED 2026-08-05** | `tenant_display_names` (`tenant_id` PK + `display_name`) is now the source for spec 08's guest-facing `business_name`. Deliberately minimal — one field, not a general profile; addresses/branding/hours remain spec 12's to design | Phone-lookup candidates render (§13 B2). A tenant that has not set a name still gets none, on purpose: a slug must never be shown to a guest |
| ~~12~~ | **CLOSED 2026-08-17 — extraction reads the artifact** | *Was:* `IIsolatedMenuExtractor` was bound to `StubIsolatedMenuExtractor`, which returned the same fabricated `Sample Dish` row for every upload, so the review screen showed a draft that had nothing to do with the artifact and an owner could not tell | The real `LangGraphMenuExtractorClient` POSTs the resolved text to the LangGraph agent's `POST /internal/menu/extract`; the stub is now an explicit `LangGraph:UseStubExtractor` opt-in for dev/test. Only `inline:`/`data:text/plain` references resolve — **gap 5 stays open** (#84), so a real file still has nowhere to live |

Previously recorded and now closed: modify/reschedule fulfilment (spec 06/08 — §17, live since
2026-08-06) and `verification_level` vocabulary drift (spec 04 — the console now sends a ratified
member). Still open, and a spec decision rather than wiring: cancellation `reason_code` vocabulary
(spec 14).

---

## 13. What is left to wire — the short list

Read this first if you are asking "what do I still have to do?". Every endpoint in §10 is in
exactly one of the three groups below, so the list is complete by construction.

### A. Left to wire — console work only ✅ **DONE (2026-08-05)**

All three are wired. Each is driven by an id the operator arrived with — **none of the three
has a list endpoint, and none of the panels invents one.** They live under Settings →
*Operator recovery* (`components/platform/operations/`).

| # | Endpoint | Surface | The honest constraint it holds to |
|---|---|---|---|
| A1 | `POST /api/dead-letter-messages/replay` | `DeadLetterReplayPanel` | Echoes the server's `decision`. `AckedWithoutReadmit` means **nothing was dispatched** and is never reported as "replayed" (Inv-16); an unrecognised decision is shown verbatim rather than mapped. Reason travels in the **body**, not `X-Reason-Code` |
| A2 | `GET /api/model-traces/{traceId}` | `ModelTraceViewerPanel` | The read **writes an audit row on allow AND deny**, so: no TanStack query (it would refetch on focus/remount), no polling, no hover, no prefetch — a `useState` and one explicit click. The reason is collected **before** the read, because a refusal is audited too. A 403 renders as a refusal, not an empty view |
| A3 | `POST /api/preorder/sagas/{id}/{convert-standalone,cancel}` | `PreorderRecoveryPanel` | Convert is gated behind a real spec-08 grant obtained through `VerificationGate` — §21.4 makes the customer's agreement the precondition, not a form field. Cancel takes no grant. The grant is dropped after use (single-use server-side) |

All three: audited-write spine (`X-Actor`, `X-Actor-Role`), operator-chosen reason with
**nothing preselected**, `retry: false` (no idempotency key on any of them), and success that
echoes the server's own word. Transport and behaviour pinned in
`operatorRecovery.test.tsx`.

### B. Blocked on backend work ✅ **DONE (2026-08-05)**

| # | Endpoint | How it was unblocked |
|---|---|---|
| B1 | `POST /api/menu/catalogs/{id}/edit` | Added `GET /api/menu/catalogs/{catalogId}/items` — a **draft-scoped, owner-gated** read. `VendorCatalogItems.ItemId` already stored the namespaced `{catalogId}:{rawId}`, so no id scheme had to be designed; the obstacle was that every existing read filters `Status == Published`, which by construction excludes `in_review`. Wired into the D4 panel via `CatalogDraftEditor` + `DiffView`. **Also closes §12 gap 3**: the response carries the catalog's real status, so the panel no longer tracks only write-confirmed guesses |
| B2 | `GET /api/Bookings?phone_e164&…` | Two independent causes, both fixed. **(i)** `BaseRows` now LEFT-joins `bookings`, so `booking_reference` (written at finalize, and never a field of `BookingRequest`) is available. **(ii)** New `tenant_display_names` table (`tenant_id` PK + `display_name`) is the source for `business_name`. `GetBookingsByPhone_FindsABookingSubmittedThroughTheRealFrontDoor` is **un-skipped and passing** |

**The table ships with a way to fill it.** `GET`/`PUT /api/tenant-profile/display-name` (owner-only
to write, audited, idempotent) and a **Venue name** panel at the top of Settings. Without a writer
the table would have been one the platform only ever read: `BusinessNameAsync` would always answer
null and the phone lookup would still find nothing outside the test suite — the read and the write
had to ship together or the feature was not delivered.
`TheVenueNameSetOverHttpIsWhatMakesAPhoneLookupReturnACandidate` drives the whole loop over HTTP,
so deleting the endpoint fails a test rather than quietly re-opening the gap.

**On the `business_name` decision.** A tenant with no display name still gets **no candidates**,
deliberately. `business_name` is shown to a guest (spec 08 §4); deriving it from the tenant id
would put a slug like `guest-g6-3f9a…` in front of a customer, and relaxing the candidate
contract would change a ratified shape. Withholding is the only option that neither lies nor
edits someone else's contract, so setting a venue name is a real setup step —
`WithoutATenantDisplayNameNoCandidateIsProjected` pins it. The table is deliberately minimal: it
holds the one field two ratified contracts already require, not a general tenant profile, which
is spec 12's to design.

### C. Will never be wired — by design, not by omission (6 items)

These exist in `apps/api` but are **not the console's to call**. The blocker is not missing
work: each is either authenticated by a credential the browser must never hold, or it asserts
something only another party is entitled to assert. Wiring any of them would be a defect, so
they are excluded from every "N/N wired" count.

The last column is the important one. For each, there **is** a legitimate adjacent need — and
in every case it is served by a *different* endpoint that does not exist yet, not by this one.
That distinction is what stops "why can't we just call it?" being re-asked every few months.

| # | Endpoint | Why the console must not call it | Could this ever change? |
|---|---|---|---|
| C1 | `POST /api/channels/messaging` | **Auth bypass.** It *mints* a signed `TenantContext` from a transport-attested signal (`recipient_id`). The tenant is the endpoint's **output**, not its input (§4). A browser calling it would forge channel ingress and issue itself a context for any tenant it names — the exact thing the signed context exists to prevent | **No.** The console's real need is a *web-origin* issuance path (§12 gap 1) that authenticates an operator first. That is a different endpoint with a different trust model, not a relaxation of this one |
| C2 | `POST /api/channels/telephony` | Same, resolving from the called DID (`to`). Caller-mutable fields (`from`, `caller_name`) are deliberately never read, precisely so a caller cannot choose the tenant | **No.** Same as C1 |
| C3 | `POST /api/notification-webhooks/delivery-receipts` | Provider-facing, authenticated by `X-Webhook-Secret`. Calling it from the console means shipping that secret to the browser, where any user can read it and post arbitrary delivery receipts | **No.** If the console needs to *show* delivery state, that is a **read** endpoint over `notifications` — spec 10 exposes none today. Reading is not the same capability as asserting a receipt |
| C4 | `POST /api/conversation-sessions/{sessionId}/transcript-turns` | Agent-lane ingest. A transcript is **evidence of what was said** (Inv-1: evidence, not a command). The console writing turns would fabricate conversation history, and that history is what the audit trail and any later dispute rest on | **No.** If an operator needs to record something about a conversation, that is an *operator note* — a different record with a different meaning, attributed to the operator, not words attributed to the guest |
| C5 | `POST /api/conversation-sessions/{sessionId}/consent` | Consent is the **guest's**, captured in-channel with recorded evidence (spec 14 §3.1). An operator asserting it on the guest's behalf is precisely what that spec forbids, and it is the record that later proves recording/messaging was permitted | **No.** A guest who wants to consent must do so in-channel. An operator-facing surface could at most *display* the recorded `ConsentState`, which the session list already carries |

| C6 | `POST /api/Bookings/{id}/modifications` | **It cannot carry the change.** `SubmitModificationInput` is `additionalProperties: false` and has **no field for the changed data** — it confirms a revision that was already submitted, so there is no way to put a new time in it. A reschedule therefore goes through the acceptance front door (`POST /api/Bookings` with `operation: "reschedule"` and a `target_booking_id`), which is the only path that actually transports the new time; `SubmitConfirmedIntentValidator` requires `target_booking_id` for exactly the modify/cancel/reschedule operations | **Only if the contract changes.** This is not a trust boundary like C1–C5 — it is a shape problem. Were the input to gain the changed fields it would become wireable, but then it would duplicate the front door, which is why nothing is planned |

`GET /api/Ping` and `GET /api/Health` are diagnostics and are not counted in any group.
`POST /api/model-traces` (the **write**) is agent-lane, like the two `conversation-sessions`
writes in C4/C5 — only the read is the console's.

### The honest total

Counting the endpoints the console legitimately owns, and counting an endpoint as wired only
when **a screen reaches it**:

- **wired: every console-owned endpoint in §10.** Counting rows rather than a running total,
  because several §10 rows bundle sibling endpoints (the four operation follow-ups are one row,
  the three verification calls are one, the four menu lifecycle actions are one) and a single
  integer has silently drifted from that twice. The 2026-08-06 pass added: the catalog list
  (`GET /api/menu/catalogs`), the waitlist locations read, the availability pair
  (`GET /api/availability` + `POST /api/availability/slots`, one surface — the create drawer's
  advisory note and the Settings declaration panel), `POST /api/guest-contacts` (minted by the
  create drawer before it hashes the payload) and `POST /api/Bookings/{id}/changes:validate`
  (the reschedule pre-flight)
- **left to wire: 0**
- **never (C): 6** — C6 joined the list once it was established that the endpoint cannot
  transport a change at all

Groups A and B are empty. What remains is group C — six endpoints that must stay unwired for the
console to remain correct — plus the gaps in §12 that need backend work nobody has scoped, of
which the load-bearing one is **gap 1: the console cannot authenticate outside Development.**

**On completeness.** Every endpoint in §10 is in exactly one of the three groups *or* named
above as a diagnostic or an agent-lane write. That sentence used to be asserted without the
second clause, which made it false: `POST /api/Bookings/{id}/modifications`,
`POST /api/model-traces`, `GET /api/Health` and `GET /api/catalog/items/{itemId}` were in no
group at all. The first three now have homes; the fourth is genuinely unused — the list read
(`GET /api/catalog/items`) is sufficient for every surface, and fetching one item by id would
add a round-trip for data the console already holds.

---

## 14. Guest identity on a console-made booking

Closes the gap once recorded as "guest identity on staff-made bookings (spec 04/08/12)".

### What changed

`BookingRequest` (the Pydantic source, `apps/langgraph/cordia_agent/state.py`) gained an
optional `customer: CustomerRef | None`. `CustomerRef` already existed and was already
generated — `BookingDraft`/`CanonicalBookingDraft` carry one — so the agent and console lanes
now describe a guest the same way. Nothing was hand-edited under `libs/*/generated/`.

This implements spec 04 §4.2, which had already ratified `customer_ref` and
`notification_destination` **on the canonical hashed body**; neither had ever been built.

**Superseded by the retention ruling (owner decision 2026-08-06).** The console no longer sends
the number: it mints an opaque handle with `POST /api/guest-contacts` **before** it hashes the
payload, and sends `customer.contact_ref`. Inline `customer.phone_e164` survives only as a legacy
fallback for callers that have not minted a ref, and for the historical revisions that already
carry one.

The reason is the same reason `display_name` was never sent. `booking_intent_revisions` is
immutable and hashed — `payload_hash` binds the confirmation to it and the whole idempotency chain
(Inv-2/3) descends from that hash — so redacting contact afterwards makes the payload stop
re-hashing to its own recorded hash. Anything written there is permanent. Holding the contact in
`guest_contacts` and putting only a ref in the payload keeps every hash valid and turns erasure
into a row delete. The ref must exist *before* the hash, which is why the mint is a separate call
rather than something the server strips after the fact.

`display_name` is still not sent, for the unchanged reason: a name is not a notification
destination, so putting one in the payload would be permanent with no operational purpose. It goes
to `guest_contacts` with the rest of the contact, where it can be erased.

### Why the Pydantic source, and not a hand-added C# field

`SubmitConfirmedIntentCommandHandler` hashes the **re-serialized C# object**, not the request
JSON, and the generated `BookingRequest` has no catch-all for unknown properties. A field the
generated type cannot hold is dropped before hashing, so the recomputed hash differs and the
submission is rejected **422 Confirmation hash mismatch**. Regenerating is the only mechanism
that works — proven in `GuestIdentityOnBookingTests`, where three different phones produced one
identical server-side hash before the contract changed.

### Hash compatibility

canon.v1 omits null, `{}` and `[]`, so a payload without a guest hashes exactly as before. Nine
vectors were added to `tools/config/canon-v1-test-vectors.json` (82 → 91), with expected values
produced by the authoritative C# hasher and verified in all three languages. Four of them share
the `minimal_create` equivalence label, pinning that absent / `null` / `{}` / all-null-fields
customers cannot move an existing hash.

### Notification destination

`NotificationRecipientResolver` was a stub returning `SHA256(tenant|operation)` — every booking,
agent-made included, had a fictional recipient and `no_destination` was unreachable. It now reads
the guest from the operation's confirmed revision. Two values, deliberately:

| Field | Carries | Persisted / logged? |
|---|---|---|
| `RecipientRef` | one-way token derived from the **operation id** | yes — `notifications.recipient_ref`, keys dedup |
| `Destination` | the real E.164 / email | **no** — re-resolved per send, retry and reconcile |

Retry and reconcile call the same resolver rather than reading a stored value, so a resend
delivers where the first attempt did. With no guest, the previous placeholder handle is returned
with a null destination — nothing that worked before regresses.

`RecipientRef` stays derived from the **operation id**, exactly as the stub had it. It feeds
`NotificationIdentity.Derive(...)`, so deriving it from the destination instead would change every
`notification_id`; a message redelivered across that boundary would miss logical dedup and notify
the guest twice (Inv-9). The operation id is already part of that identity, so a
destination-derived token would buy nothing.

The guest is read from the payload's `customer` node **only** — not via
`CanonicalBookingPayload`'s tolerant whole-payload search, which recurses into every nested object,
stops at the first key match, and whose key list includes a bare `phone` beside explicit
`business`/`venue_name` support. Fine for a display projection; not for deciding where a message is
delivered, where a venue's own number could otherwise capture the send.

**Was a known limitation; now closed.** A booking with no guest used to be "sent" and settle as
delivered, because the resolver returned a placeholder handle rather than null and
`SendNotificationCommandHandler`'s `no_destination → InvalidRecipient` branch was unreachable.

The first attempt at the guard was reverted for a real reason: waitlist offer and expiry
notifications carry an `op_wl_*` id with no `booking_operations` row, so the booking-only resolver
found nothing for them and the tightened guard terminal-failed a working lane. The fix was to
teach the resolver the waitlist lane (it now reads the enrolment's `customer_ref`) rather than to
loosen the guard — see §17. With both lanes resolved, a null destination means what it says, and
`NoDestination_SettlesInvalidRecipient_AndNeverSends` pins that the notification settles
`invalid_recipient` and nothing is sent, rather than assumed fixed.

### Console

`CreateBookingDrawer` has a **Guest phone** field. It is optional and never blocks submission,
but warns when the value is not E.164: the server matches the stored value against the lookup
query by exact, case-insensitive equality with **no normalisation**, so `0400 111 222` would be
stored and then never found by a `+61400111222` search. The customer **name** remains a
screen-only label, and its hint says so explicitly rather than implying nothing is recorded.

### Retention gap — recorded here, since closed

*As written, this section described the pre-ruling shape:* the guest phone landed in an immutable,
replayed structure the retention sweep could not reach. That was a spec-12/spec-14 retention
question rather than an Inv-12 breach (Inv-12's scope is PAN/CVV/government-id/secrets).

**Ruled and built 2026-08-06** (§18): contact moved out of band into `guest_contacts`, the payload
carries only an opaque `contact_ref`, and erasure is a row delete that leaves every hash valid.
What remains is the *legacy* set — revisions written before the ruling still hold contact inline
and are immutable — which `retention.unerasable_contact_total` measures. That number should be
flat; if it rises, something is still writing the old shape.

---

## 15. Two defects found on the way — both now fixed

**Fixed — vacuous fixtures.** `ManageBookingReadEndpointTests` and `BookingReadRepositoryTests`
hand-seeded canonical payloads carrying `booking_id`, `business_name`, `booking_date` and
`approx_time`. The submission path can produce none of them, so the phone-lookup suite was green
while the endpoint did not work — for the agent lane too. Those fixtures now use real
`BookingRequest` fields (`booking_id` comes from the operation row; `booking_date`/`approx_time`
are derived from `starts_at`), and `GuestIdentityOnBookingTests` drives the real
`POST /api/Bookings` instead of seeding rows.

**Fixed — `GET /api/Bookings?phone_e164=` returns candidates.** It was blocked on two causes, both
now closed. `BookingReadRepository.ToProjection` only emits a `ManageBookingCandidate` when the
payload yields **both** `booking_reference` and `business_name`:

- `booking_reference` — `BaseRows` joined only `booking_operations × booking_intent_revisions` and
  never `bookings`, where the authoritative reference is written at finalize. It now LEFT-joins
  `bookings`.
- `business_name` — had no source anywhere in the platform (§12 gap 11). `tenant_display_names`
  now supplies it via `BusinessNameAsync`, set from Settings → Venue name.

The skipped test is un-skipped and passing. One deliberate behaviour remains, and it is not a gap:
a tenant that has set **no** display name still gets no candidates
(`WithoutATenantDisplayNameNoCandidateIsProjected`), because deriving a name from the tenant id
would show a guest a slug. The Venue name panel says so in as many words, which is why setting it
is presented as a real setup step rather than cosmetic.

Under the out-of-band contact shape (§14) the match is by `contact_ref` rather than by the number
itself: `GET /api/Bookings?phone_e164=` resolves the hint to the refs it maps to and matches those,
so an erased contact correctly stops being findable — that is what erasure means. Email hints
resolve the same way.

**Consequence: both acceptance criteria are delivered.** A console booking has a real, resolvable
notification destination, and it is findable by phone.

---

## 16. Operator recovery tools

Three endpoints that existed server-side with no console surface are wired as typed feature
modules under `apps/web/src/features/operations/`. All three are fail-closed on the operator
spine server-side, so none defaults a reason code — an audit row that always says the same thing
records nothing. None carries an idempotency key, so all inherit `retry: false`.

| Tool | Module | Endpoint | Notes |
|---|---|---|---|
| DLQ replay | `deadLetters.ts` | `POST /api/dead-letter-messages/replay` | Reason travels in the **body**, not `X-Reason-Code`. The console never mints a replay-command id — §6.1 identity is the server's. `AckedWithoutReadmit` is a real outcome in which nothing is dispatched; report the returned `decision`, never "replayed" (Inv-16). |
| Model-trace viewer | `modelTraces.ts` | `GET /api/model-traces/{traceId}` | A read that **writes an audit row on both allow and deny** — never poll it or fire it on hover. A 403 is an expected answer and must surface as a refusal, not an empty view. |
| Pre-order recovery | `preorderRecovery.ts` | `POST /api/preorder/sagas/{id}/convert-standalone` · `/cancel` | Convert requires the customer's spec-08 `grant_id`: §21.4 makes a pre-order surviving a dead booking conditional on the customer agreeing. Cancel takes no grant — nothing is being kept. |

Mutations live in `features/operations/mutations.ts` alongside the existing follow-ups and
invalidate the same keys; transport discipline is pinned in `recoveryTools.test.ts` and
`operatorRecovery.test.tsx`. All three are **surfaced** at Settings → *Operator recovery*
(`components/platform/operations/`, mounted in `SettingsScreen`). Each is driven by an id the
operator arrived with — from an alert, the logs or the audit trail — because no list endpoint
exists and rendering an empty "quarantined messages" table would assert the queue is clear, which
the console has no way to know.

---

## 17. Gap-closure pass (2026-08-06)

### R15 — `verification_level` was theatre. Fixed; it was three defects, not one.

1. **The submit validator only checked `NotEmpty`.** `MapVerificationLevel` falls through to
   `Unverified` for anything it does not recognise, so an off-vocabulary value was silently
   **downgraded** rather than rejected. `ConfirmationMappings.IsKnownVerificationLevel` already
   existed and the waitlist enrol validator already used it — the submit path did not. It does now,
   so an unknown level is a 400.
2. **The agent lane sent `"none"`**, which is not a member of the ratified vocabulary
   (`unverified | channel_verified | identity_verified | high_assurance`). Every agent submission
   therefore recorded the same assurance whether or not the customer had been verified. A test
   fixture was also passing `"sms"` — a *channel* — into the assurance field, which is how this
   reached production. `await_confirmation` now normalises through the vocabulary
   (`_normalise_verification_level`), coercing anything unrecognised to the honest floor rather
   than forwarding a value the API would reject.
3. **The level was client-asserted.** `CheckVerificationHandlers` records a real
   `VerificationLevel` on every `VerificationGrant`, but acceptance trusted the string the caller
   sent. It now derives the level from **the grant that actually authorized the request** — the
   `X-Grant-Id` header or a `grant_`-prefixed `confirmation.identity`, resolved through one shared
   path (`ResolveGrantId`) — falling back to the asserted value only when there is no grant. On a
   **create** (no authorization gate) a named grant's level is believed only when the grant belongs
   to the customer the payload names (`GetAssuranceLevelForCustomerAsync`), so a caller cannot
   borrow any live grant id to record `high_assurance` (Inv-11). The lookup is skipped for
   non-`grant_`-prefixed identities so the console's operator id does not put a guaranteed-miss
   query on the p95<300 ms acceptance path.

### Phone matching — normalised at both ends

A phone that reaches the canonical payload at all is inside an **immutable, hashed** structure, so
it can never be rewritten into canonical form afterwards without invalidating `payload_hash` and
every idempotency record derived from it (Inv-2/3). Normalisation therefore happens in the only two
places it can:

- **At capture** — before hashing. Under the current shape the console does not put the number in
  the payload at all: it mints a `contact_ref` and `GuestContactRepository` normalises on the way
  into `guest_contacts`. `normalisePhone` in `buildSubmission.ts` still runs on the legacy inline
  fallback, for callers that have not minted a ref.
- **At comparison** (`PhoneMatching` in `Cordia.Domain/Bookings`) — both sides, so a booking
  already stored as `"+61 400 111 222"` is still found by `"+61400111222"`.

The two implementations must agree, and both are **formatting-only**. Neither converts a national
trunk prefix to a country code: `0400111222` does **not** become `+61400111222`. That conversion
needs the tenant's country, which the platform records nowhere, and guessing it would silently
match one guest's booking to a stranger's number in any country whose national format collides.
The create drawer warns at capture instead.

**Now surfaced:** Settings → *Find a booking* (`BookingLookupPanel`) runs the lookup; see §15,
where the last two blockers are recorded as closed.

### Was blocked — `extract_failed` needed D4 amended, and it was

Frozen decision **D4** originally ratified exactly nine `catalog_status` values —
`uploaded, scanning, quarantined, extracting, draft_ready, in_review, approved, published,
superseded` — and `extract_failed` was not among them. Inventing a pg-enum value spec 12 had not
ratified is forbidden, so the gap could not be closed in the console or the API; it needed the
registry changed.

**Owner ruling 2026-08-06 amended D4** and appended a tenth, terminal value. It is now built:
`CatalogStatus.ExtractFailed`, migration `20260806005324_AddExtractFailedCatalogStatus`, legal
edges from both `scanning` and `extracting` in `CatalogStateMachine`, and no outgoing edges —
recovery is a fresh upload, which supersedes. Re-running extraction in place would mutate a
catalog an operator may already have judged.

### `no_destination` is now reachable — the guard is back, correctly this time

The resolver previously knew only the booking lane, so when the destination guard was first
applied every waitlist offer looked destination-less and the guard terminal-failed a working
lane. It was reverted, and §14 recorded that.

The resolver now reads **both** lanes:

- **Booking** — the guest on the operation's confirmed intent revision.
- **Waitlist** — `op_wl_*` operation → `WaitlistOffer.OperationId` → `EnrollmentId` →
  `WaitlistEnrollment.CustomerRef` / `NotificationChannel`. That id has no `booking_operations`
  row until accept, which is exactly why the booking lookup always missed it.

With both covered, `SendNotificationCommandHandler` checks `recipient.Destination` again, and a
booking or offer with nowhere to send settles `InvalidRecipient` / `no_destination` instead of
reporting a delivery that never happened (Inv-16).

**One honest subtlety.** `WaitlistEnrollment.CustomerRef` is captured as *"Name or contact
handle"* and validated only as non-empty — so it may be a person's **name**, which is not an
address. The resolver therefore classifies it: an email, or an international-format phone,
counts as deliverable; anything else does not. An enrollment identified only by a name genuinely
has nowhere to send, and now says so rather than pretending. The stated `notification_channel` is
also free text, so it is only honoured when it names a channel the platform can actually send on
**and** the channel agrees with the address shape — a stated `sms` beside an email address is
ignored rather than handing an email to the SMS adapter.

**Four integration fixtures were seeding no destination at all** (`CanonicalPayload = "{}"`, and
no offer/enrollment rows) while asserting successful delivery — they were testing delivery to
nothing. They now seed real destinations, so they exercise the delivery path they claim to.

### Catalog discovery closed (gap 3); stuck ingestion made visible WITHOUT the frozen enum (gap 8)

`GET /api/menu/catalogs` (owner-gated, audited) lists the tenant's catalogs at any status, newest
first. Until now the console tracked only catalogs started in the current browser session, so an
ingestion begun on another machine did not exist as far as the operator was concerned.

Each row carries **`age_seconds`** — time at its **current status**, not since upload. That
distinction is load-bearing now that age drives a terminal verdict: a catalog whose ingest message
waited in a queue backlog has been mid-ingestion for none of that wait, and ageing it from
creation would fail it the instant extraction began, discarding an extraction that was still
running.

Age is now the **pre-verdict** signal rather than a substitute for one. `extract_failed` exists
(D4 amended, above), so the console shows two different things and never merges them:

- **red** — the platform's own verdict: the row *is* `extract_failed`.
- **amber** — the console noticing a row is over the threshold in the window before the server's
  sweep reaches it. Still an age, still not a verdict.

Merging them would put the platform's authority behind a guess. The console flags
`scanning`/`extracting` over a threshold; it never flags `in_review` (waiting on a **human**, not
the platform) or a terminal status, however old, because sending an operator hunting for a problem
that is not there is its own defect.

### Phone lookup now has a console surface

`GET /api/Bookings` has worked all along with **no screen calling it**. Settings → *Find a
booking* searches by phone, reference or email.

Three deliberate restraints:

- **It does not search as you type.** Discovery reads another person's booking, so it is an
  operator action rather than an autocomplete.
- **A result is not authorization.** The panel says outright that finding a booking is not
  permission to change it — every mutation still needs its own spec-08 grant (§9.4.8).
- **It renders exactly the fields the contract returns** — `business`, `booking_reference`,
  `date`, `approx_time`, `party_size`. The minimised candidate carries **no lifecycle status**,
  so none is shown; an invented "Confirmed" would assert something the API never said. No contact
  details are disclosed pre-verification.

The phone box normalises with the same function the submission path uses, and an empty result
names **both** of its causes — no such booking, *or* no venue name set, in which case the
platform withholds every candidate by design and no search will ever return anything.

### Waitlist enrolment: duplication closed (gap 6), location typos surfaced (gap 7)

**Gap 6 — duplicate enrolment. Closed, and it needed no new DDL.** Enrolment had no duplicate
protection at all: an ambiguous network failure plus an operator retry created a second `wle_*`
row, so the guest sat on the waitlist twice — two offers, two holds against the same capacity,
and no error anywhere to notice it by.

It now reuses `idempotency_records`, which is already `unique(tenant_id, idempotency_key)` with an
ON CONFLICT DO NOTHING creator election — exactly the primitive required. The key is optional:
when the caller supplies none, one is derived from the enrolment's own material fields, so a blind
retry of an *identical* enrolment deduplicates while a deliberately different one still goes
through. Reusing a key for different details is an `IdempotencyConflictException` (Inv-3).

**Gap 7 — free-text `location_id`.** The matcher compares by exact string
(`e.LocationId == criteria.LocationId`), so a typo produces an enrolment nothing can ever match
and no error is raised anywhere. `GET /api/waitlist/locations` returns the location ids the venue
has **actually enrolled against**.

That is a **usage list, not a registry**, and the naming keeps the distinction: the platform has
no locations table, and creating one is spec 12's call. It is the only un-invented source
available, and it is enough to catch a typo. An unrecognised value **warns rather than blocks** —
a new venue legitimately has no locations yet, so the first enrolment can never be in the list.

### Availability (gap 10) — closed, by shipping the write with the read

Gap 10 asked for an availability endpoint. Investigating it turned up the real problem, which
was upstream: **`capacity_slots` had a repository read and no writer.** `AddAsync` had no caller
outside tests, so the table was empty in every deployment, and
`ReserveCapacityCommandHandler`'s tracked-path gate —

```csharp
var isTracked = slotId is not null
    && await slotRepo.GetAsync(tenantId, slotId, ct2) is not null;
```

— was **always false**. Every booking took the untracked path: no capacity check happened at all.
An availability read on its own would therefore have returned an empty list for every query, and
the console would have reported "nothing available" for a venue with free capacity — strictly
worse than the honest gap, because an empty list *looks* like an answer.

So both halves shipped together:

| Endpoint | Notes |
|---|---|
| `GET /api/availability?from=&to=` | Slots in the window with `free_capacity` = `total − held − booked`, the same arithmetic spec 07's atomic conditional write uses — the number shown is the number that will be tested. Floored at zero so an oversold slot never reads as negative. Not owner-gated: an operator taking a booking needs it |
| `POST /api/availability/slots` | Owner-only and audited — declaring capacity is a commercial statement about what the venue will honour. **Idempotent by the derived slot key**, so re-declaring does not duplicate or reset counts live bookings depend on |

The load-bearing detail is that authoring uses **`CapacitySlotKey.Derive`** — the same derivation
`ReserveCapacityCommandHandler` applies to a booking's canonical payload. Under any other key the
table would fill up and the tracked path would still never engage: capacity would remain
unenforced while *looking* enforced. `AnAuthoredSlotUsesTheKeyTheFulfilmentEngineLooksUp` pins it.

**An empty window is a real answer and is not "the venue is full."** No slot declared for a
window is a different fact from no capacity remaining, and neither the API nor the console
conflates them.

### Guest-phone retention: measured, not erased — and why that is the honest half

Contact data written into `booking_intent_revisions.canonical_payload` cannot be erased by the
retention sweep, and the reason is structural rather than an oversight: the payload is **hashed**,
`payload_hash` binds the confirmation to it, and the entire idempotency chain (Inv-2/3) descends
from that hash. Redacting the payload makes it stop re-hashing to its own recorded hash.

`SurveyUnerasableContactAsync` therefore **counts** rather than erases: for terminal operations
past the window, how many revisions still hold a guest phone, email or name. Before this the
exposure was not merely un-erasable, it was *unmeasurable* — "how much un-erasable guest data do
we hold?" had no answer, which is what made the ruling easy to defer. Detection looks only at the
`customer` node spec 04 §4.2 defines, so a venue's own phone in a business block is not counted;
an inflated figure would be useless for the decision it exists to inform.

**This was deliberately half a fix when written — the other half has since been ruled and built
(§18).** The options as they were weighed, with their real costs:

| Option | Cost |
|---|---|
| Redact aged terminal revisions, accept hash invalidation | Those payloads stop re-hashing to `payload_hash`. Nothing recomputes it *today* except a test, but the invariant is gone and any future verifier breaks |
| Store contact out-of-band, keep an opaque ref in the payload | The hash stays valid and erasure becomes a row delete. Rejected at the original design gate precisely because it needed a new table — which is exactly what created the permanent-PII problem it then had to solve. **This is the option that was chosen** (owner ruling 2026-08-06) and built: `guest_contacts` + `POST /api/guest-contacts` |
| Crypto-shredding — encrypt contact in-payload, delete the key | Hash stays valid, ciphertext stays put, erasure is key destruction. Correct and by far the largest build (key custody per guest/tenant) |

All three were spec 12/14 rulings; the owner took the second on 2026-08-06 and it is built.
**The retention gap was a direct consequence of choosing the inline-`customer` shape over the
opaque-ref shape at the guest-identity gate** — worth recording, because the cheaper option there
is what created the permanent-PII problem here, and the fix was to go back and pay for the option
originally declined.

### Reschedule/modify fulfilment now works — the console gate is open

The fulfilment engine was create-only. A modify or reschedule reaching `Finalizing` was
**refused** with a `PoisonMessageException`, deliberately: `FinalizeBookingCommandHandler` calls
`CreateBookingAsync` unconditionally, so routing one there would have put a second, disconnected
booking into the venue's system while the original stayed pending — and reported `booked` for it.

What landed:

- **`IBookingProviderAdapter.ModifyBookingAsync`** — the missing capability. The adapter had
  create and cancel and nothing else.
- **`ModifyBookingCommandHandler`**, mirroring the proven cancel protocol: guard → create-or-load
  a `modify` workflow step (Inv-5) → provider call **outside** the transaction (§12.6) → settle.
- The driver now **routes** `Finalizing` + `Modify`/`Reschedule` here instead of refusing.
- **The reschedule gate was flipped on, and has since been removed entirely**, so the drag-to-reschedule
  flow is live.

Three decisions worth recording:

1. **One provider call, not cancel-then-create.** §9.4.8 requires a modification to preserve the
   original reservation until a replacement is secured. Cancelling first surrenders the seat, so
   a guest whose new time turns out to be unavailable is left with nothing — worse than the
   modification simply failing.
2. **The booking's lifecycle is untouched.** A modified booking is still confirmed; what moved
   lives on the new intent revision, which the read projection already joins. Flipping the
   lifecycle would tell every reader it had been cancelled and recreated.
3. **An uncertain provider outcome reconciles, never reports or retries** (Inv-7), and the guest
   notification commits in the same transaction as the settlement (Inv-8) — a guest cannot be told
   "moved" before it is true. The notification reuses the ratified `cancel_or_modify_confirmed`
   event, which was designed for both; minting a new message type would need a registry entry that
   does not exist, and an unregistered type dead-letters.

### Turning reschedule on exposed two holes it had been hiding

Both were latent while the engine refused every modify. Making it work made them reachable.

**1. The acceptance front door authorized nothing.** Spec 08 §12 is explicit — *"every mutation
accept requires a valid `verification_grant` (or authenticated owner role) scoped to (tenant,
customer, target booking, action, expiry)"*. `POST /api/Bookings/{id}/cancellations` and
`.../modifications` both consume a grant. `POST /api/Bookings` required **neither**, and it is the
door a reschedule has to use: `SubmitModificationInput` is `additionalProperties: false` and has no
field for the changed data, so the manage-booking door cannot transport a new time. Anyone who knew
a booking id could move someone else's booking.

`SubmitConfirmedIntentCommandHandler` now authorizes any operation carrying a `target_booking_id`
(modify / reschedule / cancel — the exact set the validator already requires a target for). Two
ways in, matching the two lanes:

| Lane | Presents | Checked against |
|---|---|---|
| Agent | the grant id it already carries on `confirmation.identity`, or `X-Grant-Id` | `verification_grants` — tenant, customer, target booking, action, expiry |
| Console | `X-Actor` + `X-Actor-Role` | the GD-1 staff matrix; `operator` suffices — moving a booking is front-desk work |

The role path covers **modify/reschedule only**. Cancel already has a door that carries everything
it needs, and that door consumes a guest grant with no role alternative — accepting a role here
would make the acceptance endpoint the weaker way to perform the identical operation, which is how
an authorization requirement gets routed around instead of enforced. Reschedule has no such
alternative, which is the entire reason the role path exists.

A **create** is untouched: there is no existing booking to authorize against, and the agent lane
opens every new booking through this same door.

Three details that are load-bearing:

- **The check runs before the idempotency pre-check.** A replay hands back the winner's operation
  id, so an unauthorized caller must not reach that either. `AnUnauthorizedReplayOfAnAcceptedKey`
  `IsAlsoRefused` pins it.
- **The `confirmation.identity` fallback is not trusting what the caller said.** The id is looked
  up in `verification_grants` and must be unexpired and in scope; an invented one authorizes
  nothing. It is the same field the assurance derivation above already reads.
- **The console's half rests on the `X-Actor` shim**, which is only trustworthy in Development.
  That is the known console-authentication exception — not a second one. It is gated exactly like
  the tenant shim: `Authorization:AllowAssertedStaffRole` defaults to `IsDevelopment()`, so outside
  Development the role branch is **off** and a reschedule needs a real server-issued grant. The API
  now logs that posture once at startup and **warns loudly** when the flag is set explicitly outside
  Development, so nobody turns it on in production silently.
- **The pre-flight carries the same lock as the mutation.** `POST .../changes:validate` accepts the
  same grant-or-staff-role door, and its role branch is gated on the same flag. Ungated it stayed
  open in production after the mutation it precedes had closed, which left an existence-and-version
  oracle (`stale_version` vs `ok`) over every booking id in the tenant, reachable with a tenant
  header and nothing else. A pre-flight may be no stricter than the change it de-risks; it must not
  be looser either.

**No `VerificationGate` is offered UP FRONT for a reschedule, and that is the right answer.** The
console operator *is* the authorization for the write. Cancel takes the grant path because
`.../cancellations` consumes one structurally; asking for a step-up before every reschedule would
make an operator verify a guest in order to satisfy a check their own role already satisfies — a
gate that authorizes nothing, which is the same theatre R15 was about.

**But one appears when the platform asks for it (2026-08-09).** A reschedule must first read the
booking's current version, and `GET /api/Bookings/{id}` answers `requires_verification` for a gated
booking. The staff-role headers authorize the *mutation*; they do not authorize that *read* — so
with no gate at all, a gated booking could not be rescheduled by any route: the read threw, the
generic handler said "couldn't change this booking", and nothing on screen led anywhere. The gate is
therefore **reactive, never pre-emptive**: it is mounted only after the platform itself returns
`verification_required`, and the resulting grant travels on **both** the read and the write.

Both halves matter. `submitBooking` gained a `grantId` parameter for the second: the asserted-role
path is gated on `Authorization:AllowAssertedStaffRole`, which defaults to Development only, so
outside Development a reschedule presenting no grant is refused however good its actor headers are.
Forwarding the grant to the read alone turned the step-up into a loop — read succeeds, write 403s,
gate reappears — burning a verification code per attempt.

**2. Nothing ever cleared `ModificationPending`.** `SubmitBookingModificationCommandHandler` parks
the booking there so concurrent writers see a change in flight, and it was the **only** writer of
that status in the codebase. `ModifyBookingCommandHandler` deliberately left the lifecycle alone,
so a successfully amended booking read as "modification pending" **forever** while its operation
said `booked` — the two disagreeing about the same event, which is precisely the defect family the
spec-11 review named. Cancel had always closed its own pending state (`CancellationPending` →
`Cancelled`); the modify lane now does the same, version-guarded on a re-read inside the
transaction, and only when the booking was actually parked. A booking cancelled underneath the
amendment stays cancelled.

---

## 18. Post-implementation audit (2026-08-06) — what is actually left to worry about

Answering directly: **beyond the six group-C endpoints, the no-authentication-outside-Development
caveat, and gaps 2, 5 and 9 in the table below — deposits/payments, menu artifact upload, and the
resource/staff model — nothing is unbuilt.** Those three are whole capabilities the platform does
not have, not wiring the console is missing; they are listed under "Remaining §12 gaps" with what
each would take. Three further things remain *undecided*, and all three now have the engineering
half done — see "Made observable" below. What is left in each of those is a ruling, not code.

### The one that is not written down anywhere else

**No booking has ever been capacity-checked.** `capacity_slots` had a read and no writer, so the
table was empty and `ReserveCapacityCommandHandler`'s tracked-path gate was always false — every
booking took the untracked path. `POST /api/availability/slots` now makes capacity declarable,
but **nothing has declared any yet**, so the behaviour is unchanged until a venue does. Double-
booking is currently prevented by nothing at the capacity layer.

This is not a UI gap and it is not in §12. It is the most consequential finding of this pass — and
the reason it went unnoticed is that the untracked branch was **silent**. It is not any more:
`fulfillment.capacity.untracked_total` counts every booking held without a capacity check, tagged
`slot_not_declared` (the venue has not declared that slot) or `no_slot_key` (the payload had no
resource/time dimension to key on). The two need different fixes, so the counter distinguishes
them. The behaviour is deliberately unchanged; what changed is that "capacity is not being
enforced" is now a number rather than an assumption.

### The waitlist mutations were not authorized at all

`WaitlistController` carried a standing `Authorization is a TODO`, and its `ResolveActor` helper
turned a **missing** `X-Actor` into the literal string `"customer"` and proceeded. Any caller who
knew a tenant id could enrol a guest, cancel someone's enrolment, or accept/decline an offer — and
accepting an offer books a table, so the offer id was the only thing between an anonymous request
and a confirmed booking.

The worse half is the audit: a fabricated `"customer"` actor was written into `audit_events` as
though the platform had established who acted. A record that asserts more than the platform knew is
not a weaker trail, it is a misleading one.

All four mutations are now `IOperatorActionRequest` with `RequiredRole = operator` (owner satisfies
it — this is front-desk work, not an owner-only commercial decision), so `OperatorActionBehavior`
deny-audits and 403s without actor + role + reason. The console already sent actor and role; it now
sends a fixed per-action reason code. **The reads stay open** — an operator needs the list to work
from, and the exposure was never there.

**This is distinct from the accepted console-authentication caveat.** That caveat is about
*authentication*: the `X-Actor` shim is only trustworthy in Development. This was about
*authorization* — even given a perfectly trusted actor, no role was ever checked.

One subtlety worth keeping: the accept/decline validators deliberately carry **no** `NotEmpty(Actor)`
rule. Pipeline order is Validation → OperatorAction → Handler, so such a rule answers 400 and
returns *before* the behavior can deny-audit — turning an unauthorized caller away with no record of
having tried, which is the one outcome an authorization spine must never produce.
`AnAnonymousCallerCannotActOnAnOffer` asserts the 403 specifically to keep that rule from coming
back.

### The three open decisions were ruled on (2026-08-06) and two of them built

| Decision | Ruling | Status |
|---|---|---|
| `extract_failed` | **Amend D4** — add the tenth value | Built |
| Guest-contact retention | **Out-of-band store + opaque ref** | Built |
| Capacity default | **Keep live/unlimited** | Already the shipped behaviour; no work |

**D4 amended.** `catalog_status` gains `extract_failed` as a tenth, terminal value — appended,
never reordered, because a pg-enum's declared order is its sort order. The §5.2 diagram had always
modelled this terminal and the frozen enum could not express it, so a stalled extraction stayed in
`extracting` forever and "failed" was indistinguishable from "still working" in the only field that
records catalog state. `MenuIngestionMonitor` now *settles* rows into it rather than only counting
them, version-guarded so an extractor that finishes late still wins. Terminal, not retryable:
re-running extraction in place would mutate a catalog an operator may already have judged, so
recovery is a fresh upload, which supersedes. The console splits the two claims — a red banner for
the platform's verdict, an amber one for "over the threshold but the sweep hasn't reached it yet" —
because merging them would put the platform's authority behind a guess.

**Guest contact moved out-of-band.** New table `guest_contacts`; the canonical payload carries only
an opaque `contact_ref`. This was the option rejected at the original guest-identity gate for
needing a new table — and that choice is exactly what created the permanent-PII problem it now
resolves.

The load-bearing detail is *where the ref is minted*: the console calls
`POST /api/guest-contacts` **before** building the payload, because the payload is hashed on the
client and the ref has to be inside that hash. Letting the server strip contact afterwards would
make the stored payload stop re-hashing to its own `payload_hash` — the Inv-2/3 break the whole
change exists to avoid. `ErasureRemovesTheContactAndLeavesTheHashIntact` pins both halves: the row
is really deleted, and the payload is byte-for-byte untouched.

Consequences worth knowing:

- **Erasure is now a row delete.** The retention sweep deletes aged contacts instead of counting
  what it cannot reach. `retention.unerasable_contact_total` still measures the *legacy* inline
  revisions, which are immutable and stay un-erasable — that number should now be flat, and a
  rising one means something is still writing the old shape.
- **An erased guest stops being findable by phone**, and their notifications settle
  `no_destination`. That is what erasure means, not a regression.
- **Both shapes are read.** Revisions written before the ruling hold contact inline and cannot be
  migrated, so discovery and the notification resolver try the ref first and fall back.
- One row per guest per tenant, fingerprinted with a tenant salt — so "forget me" is one delete,
  and one tenant cannot probe for another's guests.

### Made observable — the engineering half of the three open decisions

Each of the three had the same shape: **the platform knew something and said nothing**, which is
what let all three sit undecided. A decision cannot be made on data nobody has. **The three
counters below** change no behaviour, and none of this touches a frozen decision — but the
monitor described further down *does*: it settles catalogs that were previously stuck in
`extracting` indefinitely into `extract_failed`, which is a terminal status change.

| Signal | Counter | What it makes decidable |
|---|---|---|
| Bookings held with no capacity check | `fulfillment.capacity.untracked_total` | Whether declaring capacity is urgent for this venue or theoretical |
| Un-erasable guest contact past retention | `retention.unerasable_contact_total` | Whether the exposure is a bounded legacy set or accumulating — which is what picks between the three §14 options |
| Catalogs settled as `extract_failed` by the sweeper | `menu.ingestion_stalled_total` | How often a worker dies mid-extraction without settling its own failure. The handler settles what it catches, so this counts only the cases it could not — a rising number means workers are dying, not that extraction is slow |

The third also gained a server-side detector (`MenuIngestionMonitorHostedService`, 15-minute
threshold matching the console's own `STALE_INGESTION_SECONDS` so the two never disagree).
Staleness was previously computed **only in the console**, so a stuck extraction was invisible
unless an operator happened to open the menu screen — and nobody watches a screen for something
that is supposed to finish on its own.

It **settles the row** into `extract_failed`, version-guarded so a late-finishing extractor still
wins the race and its result is kept. `SettlesAStalledCatalogAsExtractFailed` pins the settle and
`LeavesAnOldCatalogThatJustEnteredExtractingAlone` pins the clock — the second matters more, since
ageing from creation rather than from status entry would terminally fail healthy ingestions.

It is the **backstop**, not the main path: a synchronous extraction failure is settled by
`IngestVendorMenuCommandHandler` at the moment it is detected, because that handler already knows.
What reaches the monitor is the case it cannot cover — a worker that died mid-extraction and never
ran its own catch block.

### Remaining §12 gaps

| # | Gap | Why it still matters |
|---|---|---|
| 1 | No web-origin `TenantContext` issuance | **This is the excepted auth caveat.** The console cannot authenticate outside Development |
| 2 | No deposits/payments read; `NoDepositPolicy` registered | Spec 09 is built but unexposed and switched **off** — no deposit is ever requested, so the Deposits screen stays demo |
| 5 | No menu artifact upload | "Register a menu" takes a reference string, not a file. Object-storage lifecycle is a deferred item (#84). Extraction itself is real since 2026-08-17, which makes this the last thing standing between an owner and a menu they can actually hand over |
| 9 | No resource/staff model | The schedule draws one unnamed lane. Also limits capacity: a slot's resource dimension falls back to service/location |

### Decisions that are yours, not engineering work

**All three were ruled on 2026-08-06 and are closed.** Kept here as the record of what was
decided and what it costs.

1. **Retention → out-of-band store.** Built. New guest contact is erasable by row delete. The
   residual is historical: revisions written before the ruling hold contact inline and are
   immutable, so they stay un-erasable. `retention.unerasable_contact_total` measures exactly that
   set and should now be **flat** — a rising line means something is still writing the old shape.
2. **`extract_failed` → D4 amended.** Built. A stalled ingestion is now settled terminally instead
   of sitting in `extracting` forever.
3. **Capacity → keep live/unlimited.** No work: this was already the behaviour. A venue that
   declares slots gets them enforced; one that declares nothing stays always-available, and
   `fulfillment.capacity.untracked_total` says how much is going unchecked. Double-booking is
   therefore still prevented by nothing until a venue declares capacity — **by decision, not by
   omission.**

### Not defects

`verification_level`, phone matching, waitlist duplication, catalog discovery, `no_destination`,
booking lookup and modify/reschedule fulfilment all closed this pass — see §17. So did the two
holes that turning reschedule on exposed: the acceptance front door now authorizes mutations
(spec 08 §12, Inv-11), and the modify lane closes the `ModificationPending` state its own front
door opens. The six group-C endpoints remain deliberately unwired; wiring any of them would be a
defect, not progress.

**On the missing reschedule `VerificationGate`:** there is no *pre-emptive* one to wire — the
console operator's role is the authorization the server checks for the mutation. A **reactive** one
was added on 2026-08-09 for the case the role cannot cover: the booking-details read that precedes
the change answers `requires_verification` for a gated booking, and the role authorizes the write,
not that read. It is mounted only on that 403, and its grant travels on both calls — see §17.

---

## 19. Console halves of the 2026-08-06 backend pass

§17 and §18 record what landed server-side. Three of those endpoints shipped with **no console
surface at all**, which is its own kind of incomplete: a read nothing calls is indistinguishable
from a read that does not exist. This section is what the console now does with them, plus the
five shared primitives that came out of the work.

### Availability — advisory, and deliberately not a constraint

`GET /api/availability` is wired into the create drawer as a note beneath the day/time pickers,
and `POST /api/availability/slots` as an owner-gated **Bookable capacity** panel in Settings.

The wording is spec 07's `informational` register throughout — *"appears available"*, never
*"available"* — because the read takes **no hold**. Only a `held` result closes the race
(spec 07 §6.5), so a slot this shows as free can be taken before the booking is submitted.

Three restraints, and the middle one is the whole design:

- **It never narrows the pickers.** Filtering the time options by availability would hide
  bookable times on the strength of a number the platform never promised to honour, and the
  read can be stale by submission time. It informs the operator's choice; the operation still
  reports the real outcome.
- **An empty answer means "nothing declared", never "fully booked".** `capacity_slots` is filled
  only by the declaration panel, so a venue that has never used it returns `[]` for every query.
  Rendering that as no availability would be a confident falsehood for every such tenant — the
  exact reason this endpoint was withheld until it had a writer. The note says the venue has
  declared no capacity and that this is *not* the same as being full.
- **The read and the write shipped together in the console too**, for the same reason they did
  server-side. A read whose table nothing fills is not a feature.

The declaration panel offers **service and start time only**, and that restriction is
load-bearing. A slot's key is `{dimension}|{starts_at}` where the dimension is the *first
non-empty* of resource → service → location, and a booking derives its key the same way from its
canonical payload — which carries `service_id` and never a `resource_id`. A slot declared against
a resource is therefore keyed on something no booking can produce: it would be stored, listed as
available, and never matched or enforced, so the venue could oversell it with no error anywhere.
Offering a field whose only effect is to create unenforceable capacity is worse than not offering
it, so the panel says why the other two dimensions are absent rather than exposing them.

The response carries no `mode` field, though spec 07 §44 says every availability result does.
The console therefore hard-codes `informational` wording as a property of *this endpoint*, which
neither holds nor confirms anything. Adding `mode` to `AvailabilityResult` is spec 07's call, not
the console's; `features/bookings/availability.ts` is the one place that would have to honour it.

### Waitlist enrolment now sends its own idempotency key

`EnrollWaitlistRequest` gained an optional `idempotency_key`, and the enrol drawer mints one per
**attempt** and reuses it across every retry of that attempt.

This is strictly better than the server's derived fallback, which hashes the enrolment's own
material fields: two byte-identical requests look the same to it whether or not the operator
meant them to be, so it cannot tell "retried after an ambiguous failure" from "deliberately added
a second entry with the same details". A per-attempt key says *this is the same attempt*, which
is the fact that actually distinguishes them.

The key is rebuilt whenever a material field changes — not optional, since reusing one key for a
changed request is an idempotency conflict (Inv-3), so a fixed key would turn "correct the party
size and resubmit" into a 409. Reopening the drawer starts a new attempt, which is how a genuinely
separate entry with identical details now gets through without having to be disguised by editing
a field.

**The hash it is checked against was also too narrow (Inv-3).** It covered six of the enrolment's
twelve material fields — `notification_channel`, `offer_lifetime_seconds`, `deposit_terms`,
`auto_book`, `identity_ref` and `verification_level` were all persisted and acted on but not
hashed. So a same-key request differing only in one of those passed the conflict check, was
answered as a **replay**, and the change was dropped: enrol on SMS → response lost → correct the
channel to email → retry → 201 with the original id, guest still on SMS, nothing anywhere saying
so. The hash now covers every material field, and the drawer's attempt signature covers every
field it *sends* — deliberately not "every field the server hashes", so the two cannot silently
diverge again. Widening costs the derived-key path nothing: a blind retry re-sends identical
fields, so it still deduplicates; it only stops a different request being taken for that retry.

**A second server-side defect was fixed alongside it (Inv-16).** The replay path answered on key
and hash alone, without checking that the recorded enrolment still existed. So enrol → remove → re-enrol
with the same details returned the **cancelled** enrolment's id and wrote nothing: the console
toasted success, the `active_only` list showed nothing, and the guest was on no waitlist. The
1-hour derived-key window narrowed this but never closed it — a remove-then-re-add takes seconds.
The handler now verifies liveness before answering, and voids the spent record **conditionally on
it still pointing at the dead enrolment**, so a concurrent re-enrolment cannot delete the winner's
fresh record and duplicate the guest.

### Locations: the documented warning now exists

`GET /api/waitlist/locations` was already being called, but the drawer's own hint still said
*"There is no locations endpoint yet"*, and the *"an unrecognised location warns rather than
blocks"* behaviour that §17 describes existed in **no component**. It does now: an unrecognised
value warns and still submits, and the warning is suppressed entirely while the list is empty —
an empty list cannot tell a typo apart from a venue that has never enrolled anyone.

### Consent is finally visible

`ConversationSessionSummary` has always carried `consent_state` and `recording_allowed`, and
nothing rendered either — so whether a guest had consented, the record that later proves
messaging or recording was permitted, was invisible to the operator. The conversations list now
shows it. Display only: `POST /api/conversation-sessions/{id}/consent` is C5 and stays unwired,
because an operator asserting consent on a guest's behalf is what spec 14 §3.1 forbids.

### Five shared primitives (`components/ui/`)

Each replaces real duplication on a surface that already reaches a live endpoint. None of them
is speculative, and the four the design brief also proposed — `DepositStatusCard`, `HoldsPanel`,
`AvailabilityModeBadge`, `SlaBadge` — were **not** built: there is no deposits surface (gap 2),
no hold or availability-mode field on any response, and nothing anywhere records an SLA state.

| Primitive | What it closed |
|---|---|
| `PhoneInput` | The "is this number usable?" rule existed **twice with different definitions** (`!startsWith("+")` vs a real E.164 test), so the same number was flagged on one screen and accepted on another. **No country picker**: the platform records no tenant country, so prepending `+61` would guess — matching one guest's booking to a stranger's number in any country whose national format collides |
| `MoneyAmount` | The same `price_minor/100` conversion written twice, one copy without a null guard. Minor units, never floats; a missing currency renders the bare amount rather than inventing `A$` |
| `PermissionGate` | The owner check was copy-pasted verbatim, comment and all. It now ports `StaffRole.Satisfies` — which is **not** an equality test: `owner ⊇ operator`, so both hand-written copies would have refused an owner any operator-required action. Still a **UI hint, not a boundary**: the role is client-asserted and the server re-checks everything |
| `ConsentBadge` | Nothing rendered consent at all. An unrecognised state shows **verbatim** rather than as "Unknown" — the vocabulary is a pg-enum that can gain members before this file knows them |
| `Countdown` | One implementation of "how long is left", which `offerExpiryLabel` now delegates to. Always floors: rounding once put "expired" beside a live Accept button. Takes `now` as a **prop** so a list ticks from one clock and every row agrees with the actions above it |

---

## 20. Manual-recovery queue (2026-08-07)

One console-owned endpoint was added after the 2026-08-06 mapping, so §10 and §13's "left to
wire: 0" are restated here rather than left silently stale.

### `GET /api/booking-operations/work-items` — the queue nothing could read

`operations_work_items` is the platform's record that it has stopped making progress on an
operation and needs a person. Every give-up path writes one, in the same transaction as the state
change it accompanies: an ambiguous provider cancel, an exhausted retry budget, a poison message
about to be dead-lettered, an SLA breach, and — since this pass — a pre-order saga parked at §21.4
and a waitlist accept-reconcile that has run out of attempts.

**It had exactly two readers, both cross-tenant aggregates feeding metrics gauges**
(`fulfillment.manual_review.depth` and its oldest-age sibling), plus a by-operation-id lookup that
requires already knowing which operation you are looking for. So the depth gauge could show the
queue growing and nothing could say what was in it. A durable record nothing can enumerate is not
meaningfully better than no record.

The endpoint returns the open items for one tenant, ordered by SLA deadline — most overdue first,
because different reasons carry different deadlines and creation order is a different question.
Each row carries the linked operation's **current** status, resolved at read time: a work item stays
open until a person closes it, so the operation underneath may have moved or finished since, and an
operator needs the live answer to know whether the item is still real work.

**Console surface.** Folded into the existing Queue board rather than given a screen of its own —
the operations list already answers "what state is this in", and the work item answers "why was a
person called, and by when". Each row that has one shows the reason in operator language (the raw
value is a machine tag like `compensation_ambiguous` or `poison:invalid_state_transition`) and
flags an item past its deadline. An unparseable deadline is **not** rendered as overdue: a red flag
an operator cannot verify is worse than none.

**Not an operator action.** It is a plain read, so unlike `/resume` and `/resolve` it takes no
`X-Actor` spine and writes no audit row — reading a queue is not a decision. It is tenant-scoped by
an explicit predicate, and the linked operation's tenant is re-checked before its status is
surfaced (Inv-13).

### The totals, restated

- **wired:** every console-owned endpoint in §10, now including this one.
- **left to wire: 0.**
- **never (C): 6** — unchanged. Verified this pass: no file under `apps/web/src` calls any of
  `/api/channels/{messaging,telephony}`, `/api/notification-webhooks/delivery-receipts`,
  `/api/conversation-sessions/{id}/{consent,transcript-turns}`, or
  `/api/Bookings/{id}/modifications`. The console reaches
  `/api/conversation-sessions/{id}/transcript` — the audited **read** — and nothing adjacent to it.

§12's gap list is unchanged; gap 1 (no web-origin `TenantContext` issuance — the console cannot
authenticate outside Development) remains the load-bearing one.
