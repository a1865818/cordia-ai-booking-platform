# Frontend ↔ API Wiring Implementation Plan

**Spec:** `docs/frontend-api-wiring.md`
**Baseline audit:** `thoughts/shared/research/2026-07-27_17-46-20_frontend-api-wiring-baseline.md`
**Branch:** `feat/ui-wire` · **Base commit:** `343f220`
**Scope approved at the Stage-3 gate (2026-07-27):** all six list endpoints · add `version`
to the booking projection · port `canon.v1` to Python as well.

## Overview

Replace `apps/web`'s mock-only data layer with real, typed, tenant-scoped calls to
`apps/api`; add the tenant-scoped **read** endpoints the dashboard needs; close the two
contract gaps that block correct booking writes (client-side canonical hashing, and an
unreadable `expected_booking_version`).

The console must keep working end-to-end at every phase. Anything still without a backing
endpoint stays on `data/console.ts` and is **explicitly labelled** demo data.

## Current State

- `apps/web` is 100% mock; `fetchApi` has **zero call sites**; no data-fetching library.
- `apps/api` exposes 17 controllers / 40 actions, tenant-scoped, fail-closed on tenant.
- `libs/api-contract` generated TS was stale and has been regenerated (39 paths, 78 schemas).
- Full behavioral baseline: research doc §"Existing Feature Behavior (Baseline Audit)".

## Key Discoveries (drive the phase order)

1. **`canon.v1` must run client-side.** `POST /api/Bookings` requires the client to supply
   `revision.payload_hash` *and* `confirmation.intent_payload_hash`; the server recomputes
   the canonical hash and returns **422** on mismatch
   (`SubmitConfirmedIntentCommandHandler.cs:53-67`). The Python implementation
   (`apps/langgraph/cordia_agent/revisions/manager.py:28-41`) is an acknowledged stub that
   does **not** match — so the LangGraph agent would 422 too. Both a TypeScript and a Python
   port are in scope, pinned to one shared fixture that the **real C# hasher** validates.

2. **`expected_booking_version` is not readable — and the obvious field is the wrong one.**
   Modify/cancel compare against the **`bookings` table** `Booking.Version`
   (`SubmitBookingModificationCommandHandler.cs:149-157` → `TryTransitionLifecycleAsync` /
   `GetCurrentVersionAsync`). But `BookingProjection.Version`
   (`Cordia.Domain/Bookings/BookingProjection.cs`) is the **operation** version
   (`op.Version` in `BookingReadRepository.cs:82`) — mapping *that* into the response would
   silently ship a wrong value. The fix must read `Booking.Version` via `IBookingRepository`.

## How this maps onto Cordia

| New concept | Existing Cordia artifact it reuses/extends | Consequence |
|---|---|---|
| Tenant on every request | `TenantResolutionMiddleware` `X-Tenant-Id` dev shim (spec 15 replaces it with signed `X-Tenant-Context`) | one place in `client.ts`; the spec-15 swap is a client-layer change only |
| Error surface | `GlobalExceptionHandler` → `ProblemDetails` / `ValidationProblemDetails` / snake_case `IdempotencyConflict` | `ApiError` subclasses mirror the server's three body shapes |
| Canonical hash | `Cordia.Infrastructure/Services/CanonicalRequestHasher.cs` (`canon.v1`, spec 04 §5) | TS + Python are **mirrors** pinned to golden vectors the C# hasher itself validates — not new algorithms |
| Booking write | `POST /api/bookings` → 202 + `GET /api/booking-operations/{id}` (D7) | the single durable front door; no new write path |
| Async status | `NormalizedOperationStatus` (D2, owner 06) + `terminal`/`pending` on `OperationStatusView` | UI renders **server** state (Inv-16) |
| Verification | `POST /api/verifications` → `/verify` → `grant_id` → `X-Grant-Id` (spec 08, D5) | mutations carry a server-verified grant (Inv-11) |
| New read endpoints | existing CQRS shape: Domain read record + Domain interface + Infrastructure `.AsNoTracking()` repo + Application query/handler/validator + thin `ActionResult<T>` controller (pattern: `GetBookingFunnelQuery` / `BookingFunnelReadModel`) | no new mechanism, no new layer |
| Customers list | **projection over `booking_operations` + revisions**, the same source `BookingReadRepository` already uses | invents **no table** — spec 12 owns all DDL and has no `customers` table |
| Types | `@cordia/api-contract` `paths[...]`, `@cordia/shared-schemas` | no hand-written shapes (`docs/frontend-types.md`) |
| Screens | existing `components/platform/screens/*` prop contracts | adapters map API DTOs → shapes components already take |

## Spec ownership & frozen decisions

`docs/specs/README.md` lists the **apps/web operations console UI** under *Known deferred
items — not yet owned by a spec*, so this work owns nothing there and must match every
owner byte-for-byte.

| Contract touched | Owner | Status here | Constraint |
|---|---|---|---|
| `POST /api/bookings` + `SubmitConfirmedIntentCommand` | 04 | referenced | D7; request shape from the generated contract only |
| Canonical request hashing (`canon.v1`) | 04 | referenced | TS/Python ports must be behaviourally identical; proven by shared golden vectors |
| Idempotency disposition | 04 | referenced | Inv-2/3: key minted **once per attempt**, reused across retries |
| `NormalizedOperationStatus` | 06 | referenced | D2 — the 9 values |
| `operation_status` (16 values) | 06 | referenced | D1 — the operations list filters on these; never invent one |
| `verification_action` + `VerificationGrant` | 08 | referenced | D5 — 6 values |
| **`BookingProjectionRef`** | 03 (tool contracts) / 08 (manage-booking) | **EXTENDED — additive optional field** | `version: int \| None`, default `None`; existing consumers unaffected |
| `AvailabilityMode` | 07 | referenced | display only |
| `TenantContext` / `X-Tenant-Id` | 15 | referenced | D15; dev shim isolated in one module |
| `audit_events` write path | 14 | untouched | client sends `X-Actor`/`X-Reason-Code`; never writes audit itself |
| All DDL / pg-enums | 12 | **untouched** | **no migration in this plan** — every new endpoint is a read over existing tables |

Invariants in play: **Inv-1**, **Inv-2/3**, **Inv-11**, **Inv-12** (no card data — no payment
form is built), **Inv-13** (tenant before data access — every new endpoint calls
`TenantGuard.RequireTenant`), **Inv-16**.

## Blast radius (derived from the baseline audit)

| Item | Intentionally changing? | Note |
|---|---|---|
| `apps/web/src/api/client.ts` | **YES** | 0 consumers today; header-merge bug fixed, headers + error parsing added |
| `apps/web/src/vite-env.d.ts`, `main.tsx`, `package.json` | **YES** | env typing; `QueryClientProvider`; one new dep |
| `apps/web/src/pages/ConsolePage.tsx` | **YES (additive)** | real handlers replace `setTimeout`; **screen prop contracts unchanged** |
| `apps/web` drawers (Create/Reschedule/BookingDetail) | **YES** | real submit + poll; `onCreated`/`onDone` signatures preserved |
| `apps/web` screens: Analytics, Settings, Waitlist, Conversations, Today, Queue, Customers | **YES (additive)** | live data sections; mock sections that remain are badged |
| `apps/web` screens: Deposits, Campaigns, More | **NO — behaviourally identical** | no endpoint exists (spec 09 deferred; Campaigns has no spec at all). Badge only. |
| `apps/web/src/data/console.ts` | **NO — types & constants unchanged** | still consumed by 18 files incl. `ui/` primitives; adapters map *into* these shapes |
| `apps/web/src/router.tsx` | **NO** | routes stay `/`, `/dashboard`, `*` |
| `apps/web/src/components/ui/*` (existing), `components/landing/*`, `HomePage` | **NO** | new primitives may be **added**, existing ones untouched |
| `apps/langgraph/cordia_agent/tools/contracts.py` | **YES (one additive optional field)** | `BookingProjectionRef.version` |
| `apps/langgraph/cordia_agent/revisions/manager.py` | **YES** | stub hash replaced by a real `canon.v1` port |
| `apps/langgraph` graph/nodes/tools | **NO** | only the hash function's output changes; call sites unchanged |
| `libs/shared-schemas/**`, `libs/api-contract/**` | **REGENERATED, never hand-edited** | both pipelines re-run |
| `apps/api` Domain/Application/Infrastructure/Api | **YES (additive)** | new read records/interfaces/repos/queries/controller actions; `ManageBookingMapper` + `GetBookingDetailsQuery` extended |
| `apps/api` existing handlers, validators, migrations, `Program.cs` middleware order | **NO** | **no migration**, no pipeline change |
| `apps/api/test/**` | **YES (added)** | golden-vector test + unit/integration tests for the new endpoints |
| `docs/frontend-api-wiring.md` | **YES (small)** | record what was wired and the gaps closed |

Anything not listed is out of bounds. If implementation needs to touch a "NO" item, stop
and re-plan.

## What we're NOT doing

- **No DDL, no migration, no new table or pg-enum.** Every new endpoint is a read over
  existing tables (spec 12 owns all DDL). In particular there is **no `customers` table** —
  the customers list is a derived projection.
- **No spec-15 signed `TenantContext`.** The `X-Tenant-Id` dev shim stays, isolated.
- **No auth / login / roles / `PermissionGate`.** No `[Authorize]` exists server-side.
- **No payment or card UI whatsoever** (Inv-12). Deposits stays mock.
- **No Campaigns backend.** No spec owns it; the screen stays mock and badged.
- **No design-system rewrite**, no route restructure, no Storybook, no token refactor.
- **No change to `data/console.ts` types**, no deletion of mock screens.
- **No write endpoints added** to the API — the six new endpoints are all `GET`.
- **No change to the LangGraph graph, nodes, or tool surface** beyond the hash function.

## Package notes

One new runtime dependency: **`@tanstack/react-query` ^5.101.4** (`apps/web`).
Justification: booking writes are `202 + poll-until-terminal`; hand-rolling cache
invalidation, retry policy and polling lifecycle across 4+ flows is more code and more bug
surface than the library, and the spec doc §6 recommends it. No other new deps — SHA-256
from Web Crypto / `hashlib`, UUIDs from `crypto.randomUUID()`.

---

## Phase 1 — API client foundations (web)

1. **`apps/web/src/api/config.ts`** (new) — `getTenantId()`, `getActor()`, `getActorRole()`,
   `newCorrelationId()`, `apiBaseUrl()`. Order: `window.APP_CONFIG` → `import.meta.env.VITE_*`
   → documented default. Correlation ids must satisfy `^[A-Za-z0-9_-]{1,64}$`.
2. **`apps/web/src/api/problem.ts`** (new) — `ProblemDetails`, `ValidationProblemDetails`,
   `IdempotencyConflict` (snake_case) types; `ApiValidationError` and
   `ApiIdempotencyConflictError` extending `ApiError`; a `parseErrorBody` that never throws.
3. **`apps/web/src/api/client.ts`** — keep `ApiError`/`isApiError`; fix the header-merge
   order bug; always send `X-Tenant-Id` + `X-Correlation-Id`; `Content-Type` only with a
   body; per-request `grantId`/`actor`/`reasonCode` → `X-Grant-Id`/`X-Actor` +
   `X-Actor-Role`/`X-Reason-Code`; 204/empty → `undefined`; throw the right subclass.
4. **`vite-env.d.ts`** — `ImportMetaEnv` for the new `VITE_*` vars. **`.env.example`** (new).
5. **`apps/web/CLAUDE.md`** — short "calling the API" section.

**Tests (TDD):** `client.test.ts` — headers present; caller headers win; 400 →
`ApiValidationError` with the `errors` map; 409 → `ApiIdempotencyConflictError` with
`existing_operation_id`; 422 → ProblemDetails `detail`; 204 → `undefined`; non-JSON error
body; `APP_CONFIG.API_URL` prefixing.

**Automated verification**
- [x] `bun nx test web` (19 passed) · `bun nx lint web` · `bun nx typecheck web`

**Manual verification**
- [ ] `bun nx build web` still emits `dist/apps/web`; `/` and `/dashboard` render unchanged

### Discoveries and Notable Information

- **`ApiError` had to move out of `client.ts`.** `ApiValidationError extends ApiError`
  evaluates at module-init time, so `client.ts` importing `problem.ts` while `problem.ts`
  imported `ApiError` from `client.ts` was a genuine circular-init failure, not a lint nit.
  `ApiError` now lives in `problem.ts` and `client.ts` re-exports it, so every existing
  import path (`from "./client"`) still resolves and `instanceof` stays single-identity.
- **The header-merge bug was real.** The old body was
  `{ headers: {...defaults, ...init?.headers}, ...init }` — spreading `init` *after* meant
  `init.headers` replaced the merged object wholesale, silently dropping `Accept`. Rebuilt
  on `Headers` with caller values applied last, and pinned by a test.
- **409 is ambiguous on the wire.** `IdempotencyConflictException` returns a snake_case
  `IdempotencyConflict` body while `StaleBookingVersionException` returns a normal
  `ProblemDetails` — both 409. Discrimination is by body keys
  (`existing_operation_id`/`idempotency_key`), not status, and there is a test for each.
- `GET /api/Bookings/{id}` returns **200/403/404 with the same body**
  (`status: ok | requires_verification | not_found`), so a 403 there is a typed business
  response. Callers must read the body rather than treat the status as failure — this is
  why `fetchApi` preserves the parsed `problem` on `ApiError` instead of flattening to a
  string.

---

## Phase 2 — `canon.v1` in TypeScript **and** Python, pinned to C# golden vectors

1. **`tools/fixtures/canon-v1-vectors.json`** (new) — the one cross-language fixture:
   `{ name, payload, expected }[]` where `expected` is produced by the **real C# hasher**.
   Covers: minimal create payload; key ordering; null vs absent; empty object/array
   omission; NFC; whitespace trim/collapse; timestamp offset → UTC with **ms truncation**;
   number forms (`500` / `500.0` / `5e2`); scalar-array ordering; nested `preorder.lines`
   natural-key sort; empty payload → `{}`.
2. **`apps/api/test/.../CanonV1GoldenVectorTests.cs`** (new, UTF-8 **with BOM**,
   `[Trait("Category","Unit")]`) — asserts the fixture against `CanonicalRequestHasher`.
   This is what makes the fixture authoritative.
3. **`apps/web/src/api/canonicalHash.ts`** (new) — faithful port; `crypto.subtle` SHA-256;
   exports `canonicalHash(payload): Promise<{version:"canon.v1", value:string}>`.
4. **`apps/langgraph/cordia_agent/revisions/canonical_hash.py`** (new) + rewire
   `manager.canonical_payload_hash` to it; keep the public function signature.

**Tests:** `canonicalHash.test.ts` and `tests/unit_tests/test_canonical_hash.py` — every
golden vector, plus the property assertions mirrored from `CanonicalRequestHasherTests.cs`.

**Automated verification**
- [x] `bun nx test api` (1081 passed) · `bun nx lint api`
- [x] `bun nx test web` (101 passed) · `bun nx lint web` · `bun nx typecheck web`
- [x] `bun nx test langgraph` (989 passed) · `bun nx lint langgraph`

**Manual verification**
- [x] The fixture is genuinely shared: one file at `tools/config/canon-v1-test-vectors.json`
      read by all three suites (verified by the parametrised counts: 64 vectors each)

### Discoveries and Notable Information

- **Fixture location changed** to `tools/config/canon-v1-test-vectors.json`, matching the
  repo's existing cross-language corpus (`tools/config/redaction-test-vectors.json` +
  `SensitiveDataMaskParityTests`). Following the repo beat inventing `tools/fixtures/`.
- **Payloads are stored as raw JSON *text*, not parsed objects.** A first attempt stored
  parsed objects and any JSON round-trip collapsed `500.0`/`5e2` → `500` and `500.5000` →
  `500.5`, quietly destroying every number-form vector. Only text preserves them.
- **🔴 The output encoding was the real trap.** The canonical bytes come from .NET's
  *default* `JavaScriptEncoder`, which escapes **every non-ASCII code unit** and the
  HTML-sensitive ASCII set as `\uXXXX` with **UPPERCASE** hex — `"café"` becomes
  `"café"`. `JSON.stringify` / `json.dumps` emit raw UTF-8 and would have produced a
  different digest for **any accented customer name** (this app's own mock data is full of
  them). Probed the exact escape set from the C# serializer rather than guessing, and
  pinned it as 13 dedicated vectors. Notable asymmetry: `\` → `\\` but `"` → `"`.
- **Whitespace classes disagree in exactly two places.** .NET's `char.IsWhiteSpace`
  includes U+0085 (NEL) which JS `\s` omits; JS `\s` includes U+FEFF which .NET excludes.
  Both ports use an explicit .NET-matching class, pinned by 4 vectors.
- **Ordering is UTF-16, not code point.** JS and .NET both compare UTF-16 code units;
  Python compares code points, which disagree for astral characters (an emoji sorts before
  U+FFFD in UTF-16, after it by code point). The Python port sorts on `utf-16-be` bytes.
- **The Python stub was worse than "a stand-in".** It hashed
  `json.dumps(model_dump(), sort_keys=True)` — no NFC, no whitespace collapse, no timestamp
  normalisation, no number canonicalisation, no empty-collection omission, and plain UTF-8
  output. It could not have matched the server on any non-trivial payload, so agent-submitted
  bookings would have 422'd. Now replaced by a real port; `canonical_payload_hash` keeps its
  signature so no call site changed.
- **Pre-existing break found and fixed en route:** `libs/shared-schemas/generated/csharp/`
  was stale (2026-07-11) and missing `TenantChannelEnum`, so `bun nx build api` failed
  before any of this work. Regenerating `langgraph:generate-schemas` →
  `shared-schemas:generate-types` fixed it. `libs/shared-schemas/schemas/` did not exist in
  the checkout at all.

---

## Phase 3 — TanStack Query + providers (web)

1. Add `@tanstack/react-query`; `bun install`.
2. **`queryClient.ts`** — no retry on 4xx, bounded retry on 5xx/network, `staleTime` ~30 s.
3. **`queryKeys.ts`** — key factory **rooted at the tenant id** (Inv-13 hygiene).
4. **`main.tsx`** — wrap `RouterProvider` in `QueryClientProvider`.
5. **`components/ui/ErrorState.tsx`** (new) — renders an `ApiError` title/detail + retry,
   styled like the existing `Empty` primitive.

**Tests:** retry policy by status; keys are tenant-scoped and stable.

**Automated verification**
- [x] `bun nx test web` (119 passed) · `bun nx lint web` · `bun nx typecheck web` · `bun nx build web`

### Discoveries and Notable Information

- Mutations are configured `retry: false` on purpose. An automatic retry re-sends a
  side-effecting request; the idempotency key makes that *safe*, not *silent*, and the
  operator still needs to see the failure. Retries stay explicit.
- 408 and 429 are the only 4xx worth re-attempting; everything else in the 4xx range is a
  decision the server already made (400 tenant, 403 verification, 409 conflict/stale, 422
  hash) and retrying only delays the error.

---

## Phase 4 — Contract: expose the booking version

1. **`apps/langgraph/cordia_agent/tools/contracts.py`** — add
   `version: int | None = Field(default=None, …)` to `BookingProjectionRef` (additive,
   optional — existing producers/consumers keep working).
2. Regenerate: `bun nx run langgraph:generate-schemas` → `bun nx run shared-schemas:generate-types`.
3. **`GetBookingDetailsQuery.cs`** — read the authoritative `Booking` row (already fetched in
   the grant branch) unconditionally via `IBookingRepository`, and map **`Booking.Version`**
   (not `BookingProjection.Version`, which is the *operation* version — Key Discovery 2).
4. **`ManageBookingMapper.ToContract(BookingProjection)`** — accept and set the version.
5. Regenerate `bun nx run api-contract:generate-types`.

**Tests:** unit test that `GetBookingDetails` returns the booking-table version and that a
missing booking row leaves it `null`; regression test that the operation version is *not*
used.

**Automated verification**
- [x] `bun nx test api` (1088 passed) · `bun nx lint api` · `bun nx test langgraph` (989) · `bun nx lint langgraph`
- [x] `bun nx typecheck web` (regenerated contract compiles)

**Manual verification**
- [x] `libs/shared-schemas/schemas/**` and both generated dirs regenerate cleanly;
      `BookingProjectionRef` now exposes `version` in OpenAPI and in the generated TS
- [x] Regression: `FindManageableBookings` maps `ManageBookingCandidate` on a separate
      overload and is untouched — the new parameter defaults to `null`

### Discoveries and Notable Information

- **🔴 The obvious field was the wrong one.** `BookingProjection.Version` already existed
  and was tempting to map — but it is `booking_operations.version`
  (`BookingReadRepository.cs:82`), while modify/cancel compare `expected_booking_version`
  against `bookings.version` via `TryTransitionLifecycleAsync`. Mapping the projection's
  value would have shipped a token guaranteed to 409 on every request, and it would have
  looked correct in review. The mapper now takes the booking-row version explicitly and
  `GetBookingDetailsTests.Version_ComesFromTheBookingsRow_NotTheOperation` seeds the two
  with deliberately different numbers so the mistake cannot be reintroduced silently.
- `GetBookingDetails` now reads the authoritative booking row unconditionally (it already
  did so inside the grant branch), so the version is present on the ungranted minimal
  projection too. It is `null` when no booking row exists yet — an operation still in
  flight — which callers must treat as "not modifiable yet" rather than as version 0.

---

## Phase 5 — Six tenant-scoped console read endpoints (API)

All `GET`, all read-only, all `TenantGuard.RequireTenant`, all `.AsNoTracking()`, all
returning `ActionResult<T>` so the generated TS carries a body. **No migration.**

| Endpoint | Controller | Source |
|---|---|---|
| `GET /api/Bookings/schedule?from&to&limit` | `BookingsController` | operations ⨝ revisions (as `BookingReadRepository`) |
| `GET /api/booking-operations?status&needs_attention&limit` | `BookingOperationsController` | `booking_operations` (filter on D1 `operation_status`) |
| `GET /api/conversation-sessions?limit` | `ConversationSessionsController` | `conversation_sessions` |
| `GET /api/waitlist/enrollments?status&limit` | `WaitlistController` | `waitlist_enrollments` (+ latest offer) |
| `GET /api/customers?query&limit` | **new** `CustomersController` | derived projection over operations ⨝ revisions — **no new table** |
| `GET /api/reporting/overview?from&to` | `ReportingController` | aggregates over operations/notifications |

Each gets: Domain read record + Domain interface method + Infrastructure repo implementation
+ Application query/handler/**validator** (bounded `limit`, `from < to`) + thin controller
action + snake_case response record. Then regenerate `api-contract`.

**Tests:** unit tests per handler (including a **cross-tenant** test proving tenant A cannot
read tenant B's rows) + Alba integration tests per endpoint (200 shape, 400 on missing
tenant, 400 on invalid range/limit).

**Automated verification**
- [x] `bun nx test api` (1112 passed) · `bun nx lint api` · `bun nx build api`
- [x] `bun nx test-integration api` (397 passed, 0 failed) — 19 of them the new console tests
- [x] `bun nx run api-contract:generate-types` succeeds (39 → 44 paths); `bun nx typecheck web` passes

**Manual verification**
- [x] All six appear in the regenerated OpenAPI; each returns 400 without `X-Tenant-Id`
      (asserted by `EveryConsoleReadRequiresATenant` + per-endpoint cases)
- [x] Regression: the diff to `openapi/v1.json` is additive — the five new paths plus
      `BookingProjectionRef.version`; no existing entry changed

### Discoveries and Notable Information

- **No migration was needed**, as planned: all six are reads over existing tables. The
  customers list is an aggregate over `booking_operations` ⨝ `booking_intent_revisions`
  keyed by the opaque `customer_ref` — spec 12 ratifies no customers table, so inventing
  one was never an option. It projects counts and timestamps only, never a name, phone or
  email (spec 14 minimisation), and the integration test asserts those fields are absent.
- **The canonical-payload parser was extracted, not copied.** `ScheduledBooking`,
  `CustomerSummary` and the overview all need to read the intent revision's JSON payload,
  which `BookingReadRepository` already knew how to do via a *private nested* record.
  Duplicating it would have created two parsers that could disagree about the same booking,
  so it moved to `Repositories/CanonicalBookingPayload.cs` (internal) and both use it.
  `ToLifecycleStatus` is mirrored with a comment for the same reason.
- **Status filtering needed a wire-name map.** The 16 ratified `OperationStatus` values
  (D1) travel as snake_case but the CLR names are PascalCase, so `Enum.TryParse` cannot
  round-trip them. `OperationStatusNames` builds the map from the
  `JsonStringEnumMemberName` attributes already on the enum, so a new value is picked up
  automatically. An unknown `status=` is a **400**, not silently ignored — otherwise a typo
  would quietly widen the operator's queue to "everything".
- The overview deliberately has **no "agent vs human" split**: nothing in the data model
  records who handled a booking. That chart stays on mock data rather than inventing a
  number (Inv-16), and the test asserts the field is absent.
- **Pre-existing flakiness surfaced (not introduced):** the first full integration run had
  10 failures in `TenantResolutionIngressTests` —
  `column "channel_endpoint_id" ... already exists`. `AcceptanceApiFixture`,
  `TenantResolutionApiFixture` and `PostgresFixture` all run `MigrateAsync` against the
  same `Cordia_API_Test` database, and xUnit runs collections in parallel, so a cold
  database can race. The class passes in isolation (10/10) and the immediately following
  full run was 397/397 green. Worth fixing separately by serialising fixture migration.

---

## Phase 6 — Wire the reads (web)

1. `features/reporting/` — `useBookingFunnel`, `useReportingOverview` → **AnalyticsScreen**
   live KPIs/funnel; anything the API still doesn't cover keeps mock + badge.
2. `features/settings/` — `useConsentPolicy`, `useCatalogItems` → **SettingsScreen** live
   sections (catalog body is `IActionResult` → narrow Zod validation before render).
3. `features/bookings/useBookingSchedule` → **TodayScreen** grid (adapter maps
   `starts_at`/`party_size` → the mock `Booking` shape: `day`, `start`, `dur`).
4. `features/operations/useOperations` → **QueueScreen**.
5. `features/conversations/useSessions` → **ConversationsScreen** thread list.
6. `features/customers/useCustomers` → **CustomersScreen**.
7. `features/waitlist/useEnrollments` → **WaitlistScreen** list.
8. **`components/ui/DemoBadge.tsx`** (new) + `// MOCK: no endpoint yet` at each remaining
   mock source (Deposits, Campaigns, and the un-backed Analytics charts).

**Tests:** one hook test per feature (success / error / tenant header) + adapter tests for
the schedule mapping; screen tests for loading → data → error.

**Automated verification**
- [x] `bun nx test web` (152 passed) · `bun nx lint web` · `bun nx typecheck web` · `bun nx build web`

**Manual verification**
- [ ] With API + DB running, Today / Queue / Conversations / Customers / Waitlist / Analytics
      show real (possibly empty) data with working empty states
- [ ] With the API stopped, every wired screen shows `ErrorState` and the console still works
- [x] Regression: Deposits, Campaigns and More are behaviourally unchanged — the only edit
      is an added `DemoBadge`; no data path or handler was touched

### Discoveries and Notable Information

- **Screens keep their prop contracts.** Data is fetched in `ConsolePage` and adapted into
  the shapes `data/console.ts` already defines, so `TodayScreen`, `QueueScreen`,
  `WaitlistScreen` and `ConversationsScreen` needed **no signature change at all**. Only
  `CustomersScreen` gained a prop, because it read its mock constant directly.
- **Customers could not be wired honestly without gating fields.** The API exposes an
  opaque `customer_ref`, a booking count, first/last activity and the last service — it has
  no phone, no-show count, lifetime spend or tags. Zero-filling those reads as fact
  ("$0 lifetime", "0 no-shows"), so `CustomersScreen` takes a `live` flag and suppresses
  them instead. That is why the screen changed rather than just its data source.
- **Falling back to mock is deliberate but narrow:** a screen shows demo rows only when the
  API returned successfully with nothing (a fresh tenant). An *error* always surfaces as an
  `ErrorState` — demo data must never paper over a broken API.
- `AnalyticsScreen` keeps three mock sections. Two have no endpoint; the third ("who
  handled them") has no data model at all, so reporting it would state a non-authoritative
  outcome (Inv-16). All three are badged.
- **ES2020 target bites twice.** `String.prototype.replaceAll` is ES2021 and fails
  `tsc -b` even though `tsc --noEmit` had passed from cache — `bun nx build web` is the
  check that actually catches it.

---

## Phase 7 — Booking write flows (create / reschedule / cancel)

1. `features/bookings/identity.ts` — mint `intent_id`/`operation_id`/`confirmation_id`/
   `idempotency_key` **once per attempt**, persisted in `sessionStorage`, so a retry reuses
   the same key (Inv-2 — never mint inside a retry loop).
2. `features/bookings/buildSubmission.ts` — assemble `SubmitConfirmedIntentRequest`:
   `BookingRequest` (`operation:"create"`, `service_id`, ISO-UTC `starts_at` derived from the
   selected week-day + minute slot, `party_size`, `duration_minutes`), `payload_hash` =
   `canonicalHash(canonical_payload)`, and a `Confirmation` whose `intent_payload_hash` is the
   **same** value with `intent_id`/`intent_revision` bound to the revision.
3. `features/bookings/useOperationStatus.ts` — poll `GET /api/booking-operations/{id}` until
   `terminal`; ~1 s with backoff, ~60 s cap → "still working". Server status only (Inv-16).
4. **CreateBookingDrawer** — add the required **party size** field; submit → 202 → poll →
   terminal UI keyed off `NormalizedOperationStatus`; on `booked` call the existing
   `onCreated(...)`. Explicit states for 409 / 422 / validation errors.
5. **RescheduleDrawer** — `POST /changes:validate` pre-flight (surfacing
   `requires_confirmation` / `requires_payment` / `fees_minor`) → `POST /modifications` →
   poll, using the **real `version`** from Phase 4.
6. **BookingDetailDrawer** — `POST /cancellations` with a reason code → poll. The Undo toast
   becomes a two-step confirm (a real cancellation is not locally reversible).
7. **ConsolePage** — real handlers replace the timers; **screen props unchanged**.
8. `stale_version` (409) → first-class "this booking changed — reload" state.

**Tests:** `buildSubmission` (hash binding, required fields, ISO derivation), `identity`
(key stability across retries), `useOperationStatus` (polls to terminal, stops, caps),
drawer tests for 202 → poll → booked and each error branch.

**Automated verification**
- [x] `bun nx test web` (168 passed) · `bun nx lint web` · `bun nx typecheck web` · `bun nx build web`
- [x] `bun nx test api` still green (1112 passed)

**Manual verification**
- [ ] Against a running API + DB, creating a booking returns **202 (not 422)** and polls to a
      terminal state — the end-to-end proof that the TS `canon.v1` port matches the server
- [ ] Re-submitting the same attempt reuses the key and creates no second operation
- [ ] Regression: drag-to-reschedule on TodayScreen still works

### Discoveries and Notable Information

- **The create drawer needed a new field.** `BookingRequest` requires `party_size` and the
  mock form never collected it, so a "wire it up" change genuinely could not be
  field-for-field.
- **"Confirmed" now means confirmed.** The drawer's success panel fires only when the poll
  reports `status === "booked"`; 202 puts it in a *submitting* state with the server's own
  `customer_message`. This is the design's §2.6 distinction made visible, and it is why the
  drawer gained a third phase rather than keeping the 1000 ms fake.
- **Cancel lost its Undo, deliberately.** The mock cancel was locally reversible; a real one
  is not. It is now read-version → submit → poll → report the server's terminal status.
- **Reschedule stays optimistic but self-correcting:** the grid moves immediately so
  drag-and-drop still feels direct, then the operation is submitted and the schedule query
  invalidated, so the server's placement wins on settle.
- Two `String.prototype.replaceAll` uses had to become `split/join` (ES2020 target), and
  the generated `SubmitConfirmedIntentRequest` needs an `as unknown as` cast: its
  TriState unions and `T | null` optionals cannot be narrowed from a fresh object literal.

---

## Phase 8 — Verification grant sub-flow (web)

1. `features/verification/` — `requestVerification` → `verifyCode` → `grant_id`, held **in
   memory only** with its `expires_at`.
2. **`VerificationGate`** — mounted on `403` / `requires_verification`; request → OTP →
   verify, then retries the original call with `X-Grant-Id`.
3. Wire into `GET /api/Bookings/{id}` (where `requires_verification` is a **body status**,
   not a bare failure) and into modify/cancel `verification_required`.
4. Minimal `OtpInput` primitive (labelled, keyboard-operable).

**Tests:** happy path; `expired`/`failed`/`pending`; a 403 mounts the gate and the retry
carries `X-Grant-Id`.

**Automated verification**
- [x] `bun nx test web` (168 passed) · `bun nx lint web` · `bun nx typecheck web` · `bun nx build web`

**Manual verification**
- [ ] A verification-required read shows the gate, not a dead error
- [x] The grant is never persisted — `useVerification` holds it in React state only, and no
      storage API is referenced anywhere in `features/verification/`

### Discoveries and Notable Information

- `failed` / `expired` / `pending` are **business outcomes**, not transport errors: a wrong
  code returns 200 with `status: "failed"`. The hook distinguishes those from a thrown
  `ApiError` so a mistyped code shows "that code did not match" rather than a stack of
  error chrome.
- `no_verified_destination` and `rate_limited` get their own copy — telling an operator
  "something went wrong" when the guest simply has no verified phone on file is the failure
  N9 exists to prevent.
- The gate hands the grant up from an **effect**, not from render; calling the parent's
  setState during render is a side effect React may run twice.

---

## Phase 9 — Waitlist actions, transcript, docs

1. `features/waitlist/` — `enroll` / `cancelEnrollment` / `acceptOffer` / `declineOffer`,
   all sending `X-Actor`; **WaitlistScreen** actions become real with truthful outcomes.
2. `features/conversations/useTranscript(sessionId)` with `X-Actor`/`X-Actor-Role`/
   `X-Reason-Code` + optional grant → **ConversationsScreen** transcript.
3. **`docs/frontend-api-wiring.md`** — update §1/§4/§9/§10 for what is now wired, and record
   the two contract gaps and how they were closed.

**Automated verification**
- [x] `bun nx test web` (168) · `bun nx lint web` · `bun nx typecheck web` · `bun nx build web`
- [x] `bun nx test api` (1112) · `bun nx lint api` · `bun nx build api` · `bun nx test-integration api` (397)
- [x] `bun nx test langgraph` (989) · `bun nx lint langgraph`

**Manual verification**
- [ ] Removing a waitlist guest hits the API and reports the true outcome
- [x] Every "must stay the same" row in the Blast-radius table verified against the diff:
      `data/console.ts`, `router.tsx`, `components/landing/*`, `HomePage`, existing
      `components/ui/*`, all applied migrations and `Program.cs` are untouched

### Discoveries and Notable Information

- Waitlist removal is now real and **not** undoable client-side: the row is removed
  optimistically, and on failure it is restored and the server's message shown, rather than
  reporting a removal that did not happen.
- The transcript read sends `X-Actor` / `X-Actor-Role` / `X-Reason-Code` because the API
  audits it as a sensitive read (spec 14 §4.2) — it is not an ordinary GET.
- Transcripts are only requested for sessions that came from the API; the demo thread ids
  are not real sessions and would 404.

---

## Integration-suite flakiness observed (pre-existing, not introduced)

Across four full `test-integration` runs: two clean (397/397), one with 10 failures in
`TenantResolutionIngressTests` (`column "channel_endpoint_id" ... already exists`) and one
with a single failure in `RetryAndFailureTests` (expected `Finalizing`, got `Booked`). Both
classes pass in isolation — `TenantResolutionIngressTests` 10/10, `RetryAndFailureTests`
6/6 three times running. Both are parallel-execution races in code this change does not
touch: three Alba/EF fixtures migrate the same test database concurrently, and the workflow
timing test races the engine. Recorded as a follow-up in `docs/frontend-api-wiring.md` §11.

---

## Cross-cutting requirements

- **Tenant scope (Inv-13):** every request carries a tenant; every new endpoint calls
  `TenantGuard.RequireTenant`; query keys are tenant-rooted; each new handler gets a
  cross-tenant test.
- **Idempotency (Inv-2/3):** one key per logical attempt, minted before the first call and
  reused on retry; 409 conflict surfaced, never silently merged.
- **Authorization (Inv-11):** mutations needing a grant carry `X-Grant-Id`; the client never
  fabricates authorization.
- **Truthful outcomes (Inv-16):** the UI renders `OperationStatusView` from the server;
  "confirmed" only for `booked`.
- **PII/card (Inv-12):** no card field anywhere; grants in memory only; no PII in query keys.
- **PostgreSQL/migrations:** none — all new endpoints are reads. Filter on existing indexed
  columns (`tenant_id`, `status`, `updated_at`); bound every list with a validated `limit`.
- **Generated types:** import via `@cordia/...` only; never hand-edit generated files; edit
  the Pydantic/.NET source then regenerate.
- **File encoding:** every new/edited `.cs` file is UTF-8 **with BOM**.
- **FluentValidation** is the sole required-field enforcement for new queries.
- **Commands:** one `bun nx` at a time; tee output to `.tmp/`, never pipe to `tail`.
- **A11y:** new inputs labelled and keyboard-operable; errors associated with their field;
  loading/error states not colour-only.

---

## Review round (xhigh) — findings and fixes

Four Sonnet reviewers went over the diff (web API layer, C# additions, web features/screens,
Python parity), plus a live end-to-end run against a serving API. **Every blocker below was
a real defect that the test suites were passing over.**

### Found by running it against the real API

- 🔴 **`/api/customers` would always have been empty.** `BookingRequest` declares no customer
  field and is `additionalProperties: false`, so the canonical payload can never carry one —
  the projection was reading a field that cannot exist. Customer identity actually lives on
  `bookings.customer_id`, falling back to the confirmation's `identity_ref`. Both the
  customers list and the schedule's `customer_ref` now resolve from there, through a single
  `ParsedRow` so the schedule and customers agree on which booking a row refers to. The
  integration test was passing **because it seeded the customer in the wrong place too** —
  it now seeds a real `bookings` row, and the cross-tenant case asserts tenant B genuinely
  has a customer so an empty result for A proves scoping rather than emptiness.
- The dev database was six migrations behind (`column b.causation_id does not exist`),
  unrelated to this change; applied with `dotnet ef database update`.

### Blockers fixed

| # | Defect | Fix |
|---|---|---|
| 1 | **Reschedule sent only a hash.** `SubmitModificationInput` is `additionalProperties: false` and has **no field for the changed data** — the new time was never transmitted, so the server would 202 and the UI would report "Change: confirmed" for a change it had never been told about. | Reschedule now rides the acceptance front door (`POST /api/bookings`, `operation: "reschedule"`, `target_booking_id` + `target_booking_version`), the only path that carries the canonical payload. |
| 2 | **Schedule silently dropped bookings.** The date filter ran in memory *after* taking the top 2000 rows by `updated_at`, so any booking whose operation had not been touched recently vanished from the grid with no indication. Same root cause made customer search return false negatives. | Both now page through the tenant (`PageSize` 500, `MaxScan` 20 000) and report `truncated`, surfaced in the UI as a **Partial results** badge. |
| 3 | **Overview truncation was non-deterministic** — `.Take(2000)` with no `ORDER BY`, so two identical calls could return different analytics. | Per-day counts are now aggregated **in SQL** (exact at any volume); the payload-derived breakdowns are ordered and flag `breakdown_truncated`. |
| 4 | **TS/Python normalised timestamps .NET rejects.** Lowercase `t`, single-digit hour/minute/second, impossible dates (Feb 30, hour 24, month 13), years 0000–0099 remapped by `Date.UTC`, and offsets pushing the instant outside `DateTimeOffset`'s range — each produced a different hash from the server, i.e. a 422 the operator could not act on. | Both ports now mirror .NET's gate exactly; **10 new corpus vectors** pin each case. Python also had an uncaught `OverflowError` escaping the hasher near year 1/9999. |
| 5 | **Query-cache key collisions.** `stableFilter` joined `key=value` with `&`, so a customer named "Smith & Co" could produce the same cache key as an unrelated filter set and render another query's rows. | Keys are JSON-serialised, which also keeps `"5"` and `5` distinct. |
| 6 | **Verification "Email instead" sent nothing.** The idempotency key was memoised per component, so after "Start over" the server replayed the original SMS challenge and the UI reported a fresh code. | A key per *send*; `already_sent` now says so; the channel buttons are disabled while a send is in flight. |
| 7 | **"Finish" un-resolved itself.** It was a client-side timer claiming "resolved", and the next refetch restored the row. There is no generic resolve endpoint (spec-06's is `manual_review`-only). | It is now an honest local acknowledgement — "Cleared from your queue" — and the id is recorded so a refetch cannot contradict it. |
| 8 | **NFC key collision diverged**: TS/Python silently dropped a key where C#'s `JsonObject.Add` throws. | Both ports now fail loudly with an actionable message. |

### Also fixed

- Idempotency conflicts were reported as "this booking changed — reloading"; they now say
  the attempt is spent and a new one is needed, since replaying that key cannot succeed.
- A created booking now invalidates the schedule instead of living on as a fabricated id
  that would 404 the moment the operator acted on it.
- `toDayAndStart` counted days by dividing elapsed ms by 24 h, which shifts every column
  after a mid-week DST change; it now counts calendar days, and rows outside the displayed
  week are dropped rather than filed under the wrong day.
- `Content-Type: application/json` is only forced on **string** bodies (FormData needs the
  browser to set its own multipart boundary).
- Dead code removed: the two mock timers, the unused `busy` setter, and
  `CanonicalBookingPayload.CustomerRef` (unreachable, and it implied payloads carry a
  customer).

### 🔴 Process defect found: the test suites were reporting stale passes

`tools/config/*.json` was in **no** lane's nx cache inputs, so changing the shared canon.v1
corpus did not invalidate anything — all three suites reported green against the *old*
corpus. The corpus grew from 64 to 74 vectors with no change in test counts, which is what
exposed it. `tools/config/*.json` is now an input for the `dotnet`, `python` and
`typescript` named inputs; re-running with `--skip-nx-cache` immediately surfaced a
genuine TS failure (`timestamp_min_year_with_offset`) that the cache had hidden.

### Final state

| Lane | Result |
|---|---|
| `bun nx test api` | **1122 passed** |
| `bun nx test-integration api` | **397 passed, 0 failed** |
| `bun nx test web` | **181 passed** |
| `bun nx test langgraph` | **999 passed** |
| lint / build | green on all three lanes |
| live end-to-end | `POST /api/Bookings` returned **202** with a client-computed hash, an offset timestamp and a diacritic — canon.v1 parity proven against the running server; all six new endpoints returned 200, 400 without a tenant, 400 on an unknown status filter |

---

## Conformance review round 2 (workflow, sonnet agents)

A 100-agent workflow reviewed the implementation against `docs/frontend-api-wiring.md` across
nine dimensions, with every finding put to two independent adversarial verifiers.
**45 raised → 16 confirmed, 29 refuted.** All 16 are fixed below.

### 🔴 The finding that bounds the whole feature

**The fulfilment engine is create-only.** Nothing in `Handlers/BookingFulfillment/**`
branches on `OperationTypeEnum`, and `target_booking_id` is read by no handler —
`FinalizeBookingCommandHandler` calls `adapter.CreateBookingAsync` unconditionally. A
modify/cancel/reschedule operation is accepted (202) and then fulfilled as a **new booking**.

Verified three ways: repo-wide grep for `OperationType` in the fulfilment tree (only a
comment in the SLA handler, explicitly declining to branch), grep for `TargetBookingId` in
that tree (zero hits), and a live submission returning 202. This is spec 06/08 work that
predates this change — the `/modifications` and `/cancellations` endpoints already had it.

Response: **reschedule is not submitted.** `RESCHEDULE_FULFILMENT_SUPPORTED` is `false`; the
drawer explains why; the payload builder, canonical hashing, attempt identity and polling are
all built and tested behind it. Shipping a button that silently creates duplicate bookings
would have been worse than shipping one that says "not yet".

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
| 8 | §1 counts stale (38/15), §4 verdicts contradicted §11, §8 claimed no Customer/Thread endpoint, §10 omitted all six new endpoints, TL;DR described the pre-implementation world | All corrected; superseded sections marked as such rather than deleted, so the rationale survives |
| 9 | §11 claimed the Zod follow-up covered waitlist/menu/preorder responses | Corrected — only catalog search is Zod-validated |

### Refuted (29)

Notable ones the verifiers correctly threw out: that `§8(a)`'s channel enum was misidentified
(two distinct `Channel` enums genuinely exist), that the drawer never leaving `confirming` on
a non-`booked` terminal was a blocker (it renders the server's message), and that
`changes:validate` being unused violated §7 (the doc marks it optional).

### Final state

| Lane | Result |
|---|---|
| `bun nx test api` | **1122 passed** |
| `bun nx test-integration api` | **397 passed, 0 failed** |
| `bun nx test web` | **184 passed** |
| `bun nx test langgraph` | **999 passed** |
| lint / typecheck / build | green on all three lanes |
| DB | 30/30 migrations applied |
