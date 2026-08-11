# Frontend ↔ API Wiring — consolidated implementation plan

**Spec/guide:** `docs/frontend-api-wiring.md` · **Branch:** `feat/ui-wire` · **Base commit:** `343f220`
**Baseline research:** [`../research/2026-08-04_frontend-api-wiring.md`](../research/2026-08-04_frontend-api-wiring.md)
**Review record:** [`../reviews/2026-08-04_frontend-api-wiring.md`](../reviews/2026-08-04_frontend-api-wiring.md)
**Status:** all eight workstreams complete · 17 commits on `feat/ui-wire`

This is the single implementation record for the wiring work. It merges the eight plans that were
executed in sequence:

| Part | Original plan | Date | Phases |
|---|---|---|---|
| **1** | Frontend ↔ API wiring | 2026-07-27 | 9 |
| **2** | Cancel fulfilment + `useMutation` refactor | 2026-07-27 | 4 |
| **3** | Verification wiring + operator reason codes | 2026-07-28 | 6 |
| **4** | Gap closure (G1–G14) | 2026-08-03 | 9 |
| **5** | Console truthfulness gap closure (R2–R8) | 2026-08-04 | 5 |
| **6** | Residual review findings (8 items) | 2026-08-07 | 8 |
| **7** | Capacity lock-order deadlock + waitlist accept limbo | 2026-08-07 | 3 |
| **8** | Global capacity lock order, state ownership, ManualReview | 2026-08-07 | 7 |

Parts 6–8 are **backend hardening**, not new console wiring — they are recorded here because they
ran on the same branch, against the same guide, and part 8 added the one endpoint (`GET
/api/booking-operations/work-items`) that `docs/frontend-api-wiring.md` §20 had to restate §13's
totals for.

Each part kept its own phase numbering; they are namespaced below (P1·Phase 1, P4·Phase 3, …).
The six review rounds that ran over these parts are **not** repeated here — they are consolidated in
the review record, and each part links to its rounds.

---

## Overview — what the whole arc did

Replace `apps/web`'s mock-only data layer with real, typed, tenant-scoped calls to `apps/api`; add the
tenant-scoped **read** endpoints the dashboard needs; close the two contract gaps that block correct
booking writes (client-side canonical hashing, and an unreadable `expected_booking_version`); make the
fulfilment engine actually cancel; and then — twice — remove everything the console was asserting that
the platform does not record.

The console had to keep working end-to-end at every phase. Anything still without a backing endpoint
stays on `data/console.ts` and is **explicitly labelled** demo data.

> **Note on `docs/frontend-api-wiring.md` references.** Section numbers cited below (§11, §12, §13)
> describe that guide **as it stood during this work**. Those sections have since been removed and the
> guide is back to its pre-implementation §1–§10 form, so the rules they stated are reproduced here
> rather than left as pointers.

The two rules the console holds itself to, which Parts 4 and 5 exist to enforce:

1. **A successful-but-empty response is a real answer.** Screens fall back to demo rows only while a
   list has *never* loaded, never to fill in an empty result.
2. **Nothing is asserted that the API does not record.**

## Key discoveries that drove the phase order

1. **`canon.v1` must run client-side.** `POST /api/Bookings` requires the client to supply
   `revision.payload_hash` *and* `confirmation.intent_payload_hash`; the server recomputes the
   canonical hash and returns **422** on mismatch (`SubmitConfirmedIntentCommandHandler.cs:53-67`).
   The Python implementation (`revisions/manager.py:28-41`) was an acknowledged stub that did **not**
   match — so the LangGraph agent would 422 too. Both a TypeScript and a Python port were in scope,
   pinned to one shared fixture that the **real C# hasher** validates.
2. **`expected_booking_version` is not readable — and the obvious field is the wrong one.**
   Modify/cancel compare against the **`bookings` table** `Booking.Version`
   (`SubmitBookingModificationCommandHandler.cs:149-157`). But `BookingProjection.Version` is the
   **operation** version (`BookingReadRepository.cs:82`) — mapping *that* would silently ship a wrong
   value. The fix must read `Booking.Version` via `IBookingRepository`.
3. **The fulfilment engine dispatched purely on `OperationStatus`, never on `OperationType`.** A
   cancel therefore ran the create path and produced a *second* booking. (Part 2.)
4. **The calendar was dated by a mock constant while showing live rows.** `WEEK` pinned *9–15 June,
   today = Thursday*; `weekWindow()` fetched the **real** current week. (Part 4, Phase 1.)
5. **The console's cancel could never succeed.** `features/verification/**` was fully built and
   imported by no page, drawer or hook; and `grant_id` is `NotEmpty`-validated, so the console's
   `grant_id: ""` was a **400**, not a recoverable 403. (Part 3.)
6. **The two rules held for the ten screens but not for the shell, the More tab or the drawers.**
   (Part 5.)

## How this maps onto Cordia

| New concept | Existing Cordia artifact it reuses/extends | Consequence |
|---|---|---|
| Tenant on every request | `TenantResolutionMiddleware` `X-Tenant-Id` dev shim (spec 15 replaces it with signed `X-Tenant-Context`) | one place in `client.ts`; the spec-15 swap is a client-layer change only |
| Error surface | `GlobalExceptionHandler` → `ProblemDetails` / `ValidationProblemDetails` / snake_case `IdempotencyConflict` | `ApiError` subclasses mirror the server's three body shapes |
| Canonical hash | `CanonicalRequestHasher.cs` (`canon.v1`, spec 04 §5) | TS + Python are **mirrors** pinned to golden vectors the C# hasher itself validates — not new algorithms |
| Booking write | `POST /api/bookings` → 202 + `GET /api/booking-operations/{id}` (D7) | the single durable front door; no new write path |
| Async status | `NormalizedOperationStatus` (D2, owner 06) + `terminal`/`pending` on `OperationStatusView` | UI renders **server** state (Inv-16) |
| Verification | `POST /api/verifications` → `/verify` → `grant_id` → `X-Grant-Id` (spec 08, D5) | mutations carry a server-verified grant (Inv-11) |
| New read endpoints | existing CQRS shape: Domain read record + Domain interface + Infrastructure `.AsNoTracking()` repo + Application query/handler/validator + thin `ActionResult<T>` controller | no new mechanism, no new layer |
| Customers list | **projection over `booking_operations` + revisions**, the same source `BookingReadRepository` already uses | invents **no table** — spec 12 owns all DDL |
| Cancel with the provider | `IBookingProviderAdapter.CancelBookingAsync` — already on the interface and every test fake | no adapter contract change |
| Staged provider call | `CompensateOperationCommandHandler`'s shape: load → create/load `WorkflowStep` → call provider **outside** the txn → persist in one txn | new handler mirrors a proven pattern |
| Cancel notification | `NotificationEvent.CancelOrModifyConfirmed` — already a ratified pg-enum value with identity + template entries | one new message type, no new enum |
| Web writes | `@tanstack/react-query` `useMutation` | no new package |
| Real calendar dates | `weekWindow()` / `toDayAndStart()` — already the *only* correct date logic in the app | `WEEK` stops driving live surfaces; one date source, not two |
| Week navigation | the `scheduleWindow` `useMemo` becomes state; `queryKeys.bookings.schedule(from,to)` already keys per window | react-query caches each week for free |
| Shell identity from real config | `api/config.ts` `getTenantId()` / `getActor()` / `getActorRole()` — already the values sent as headers | the shell shows what the platform attributes the session to; swaps for free when spec-15 lands |
| Badging an unbacked surface | `components/ui/DemoBadge.tsx` (`DemoBadge` / `PartialBadge`) | no new component, no new convention |
| Omit rather than fabricate | `prov: "unknown"`, `channel: null`, `dep: 0`, `lifecycle_status: null`, the deleted `MOCK_WAIT` | house ruling; applied to resources and the deposit panel |
| Types | `@cordia/api-contract` `paths[...]`, `@cordia/shared-schemas` | no hand-written shapes |
| Screens | existing `components/platform/screens/*` prop contracts | adapters map API DTOs → shapes components already take |

**Governing directive:** integrate with the single front door; never add a parallel implementation.

## Spec ownership & frozen decisions

`docs/specs/README.md` lists the **apps/web operations console UI** under *Known deferred items — not
yet owned by a spec*, so this work owns nothing there and must match every owner byte-for-byte.

| Contract touched | Owner | Status | Constraint |
|---|---|---|---|
| `POST /api/bookings` + `SubmitConfirmedIntentCommand` | 04 | referenced | D7; request shape from the generated contract only |
| Canonical request hashing (`canon.v1`) | 04 | referenced | TS/Python ports behaviourally identical; proven by shared golden vectors |
| Idempotency disposition | 04 | referenced | Inv-2/3: key minted **once per attempt**, reused across retries |
| `Confirmation.identity` / `verification_level` | 04 / 08 | referenced | changed *what the console puts in*, never the contract |
| `NormalizedOperationStatus` | 06 | referenced | D2 — the 9 values |
| `operation_status` (16 values) | 06 | referenced | D1 — never invent one; a cancel terminates at `Booked` |
| `OperationStateMachine` edge set | 06 | **EXTENDED — one edge** | `Validating → Finalizing`, so a cancel skips capacity it does not need |
| Cancellation lifecycle `pending → cancelled` | 08 §5.3 | **implemented as specified** | incl. §9.4.4 "already cancelled ⇒ no provider re-invoke" |
| `verification_action` + `VerificationGrant` | 08 | referenced | D5 — 6 values |
| **`BookingProjectionRef`** | 03 / 08 | **EXTENDED — additive optional field** | `version: int \| None`, default `None`; existing consumers unaffected |
| Console read projections (`ConsoleProjections.cs`) | this branch | **owns** | no DDL — spec 12 owns all DDL |
| `NotificationEvent` | 10 | referenced | `cancel_or_modify_confirmed` already ratified |
| `BookingLifecycleStatus` | 12 / 08 | referenced | all four values already exist |
| `audit_events` write path | 14 | referenced | client sends `X-Actor`/`X-Reason-Code`; never writes audit itself |
| Sensitive-read purposes, `customer_ref` minimisation | 14 | referenced | Inv-12 + minimisation |
| `TenantContext` / `X-Tenant-Id` | 15 | referenced | D15; dev shim isolated in one module, then gated |
| Waitlist enrollment + offer lifecycle, auto-book consent | 11 | referenced | Inv-1 (§20 silent auto-book prohibited) |
| All DDL / pg-enums | 12 | **untouched** | **no migration anywhere in this arc** |

**Invariants in play:** Inv-1, Inv-2/3/4, Inv-5, Inv-7, Inv-8, Inv-11, Inv-12, Inv-13, Inv-16,
MB-Inv-6. **Frozen decisions:** D1, D2, D5, D7, D15, D17 — none altered.

## What we're NOT doing (consolidated)

- **No DDL, no migration, no new table or pg-enum** anywhere in this arc. In particular there is **no
  `customers` table** — the customers list is a derived projection.
- **No modify/reschedule fulfilment** (R1 — spec 06/08). It needs "preserve the original reservation
  until the replacement is secured" (MB-Inv-5), a separate vertical. `RESCHEDULE_FULFILMENT_SUPPORTED`
  stays `false` and the driver dead-letters the operation rather than mis-fulfilling it.
- **No guest identity capture on a staff booking** (R9 — spec 04/08/12). We fix the *misuse*; we do not
  invent a field.
- **No server-side resource/staff model** (R10 — spec 06/12).
- **No R15 server or agent half**: tightening `SubmitConfirmedIntentValidator` with
  `IsKnownVerificationLevel`, and fixing langgraph's `"none"`. Both are spec-04 vocabulary decisions
  with a ~20-site langgraph blast radius.
- **No auth / login / roles / `PermissionGate`.** No `[Authorize]` exists server-side.
- **No payment or card UI whatsoever** (Inv-12). Deposits stays demo.
- **No Campaigns backend.** No spec owns it.
- **No refund/payment compensation on cancel** (spec 09, deferred).
- **No booking-lifecycle state machine** — none exists; adding one is spec-08 work.
- **No operator actions on operations** (`resume`/`resolve`/`alternatives`/`cancel`) and **no
  conversation writes** (reply, consent, transcript-turns).
- **No routing restructure / deep links / URL state**, no pagination or cursors, no projected indexed
  `starts_at` (spec-12 DDL), no `IActionResult → ActionResult<T>` sweep (would regenerate the contract).
- **No design-system rewrite**, no Storybook, no token refactor, no route restructure.
- **No change to `data/console.ts` types**, no deletion of mock screens (they still consume them).
- **No change to the LangGraph graph, nodes or tool surface** beyond the hash function.

## Package notes

One new runtime dependency across the whole arc: **`@tanstack/react-query` ^5.101.4** (`apps/web`),
added in P1·Phase 3. Booking writes are `202 + poll-until-terminal`; hand-rolling cache invalidation,
retry policy and polling lifecycle across 4+ flows is more code and more bug surface than the library.
No other new deps — SHA-256 from Web Crypto / `hashlib`, UUIDs from `crypto.randomUUID()`.

---

# Part 1 — Wire the console to the live API (2026-07-27)

**Scope approved at the Stage-3 gate:** all six list endpoints · add `version` to the booking
projection · port `canon.v1` to Python as well.

## Blast radius

| Item | Intentionally changing? | Note |
|---|---|---|
| `apps/web/src/api/client.ts` | **YES** | 0 consumers today; header-merge bug fixed, headers + error parsing added |
| `vite-env.d.ts`, `main.tsx`, `package.json` | **YES** | env typing; `QueryClientProvider`; one new dep |
| `pages/ConsolePage.tsx` | **YES (additive)** | real handlers replace `setTimeout`; **screen prop contracts unchanged** |
| drawers (Create/Reschedule/BookingDetail) | **YES** | real submit + poll; `onCreated`/`onDone` signatures preserved |
| screens: Analytics, Settings, Waitlist, Conversations, Today, Queue, Customers | **YES (additive)** | live data sections; remaining mock sections badged |
| screens: Deposits, Campaigns, More | **NO — behaviourally identical** | no endpoint exists. Badge only |
| `data/console.ts` | **NO — types & constants unchanged** | still consumed by 18 files incl. `ui/` primitives |
| `router.tsx` | **NO** | routes stay `/`, `/dashboard`, `*` |
| `components/ui/*` (existing), `components/landing/*`, `HomePage` | **NO** | new primitives may be **added** |
| `cordia_agent/tools/contracts.py` | **YES (one additive optional field)** | `BookingProjectionRef.version` |
| `cordia_agent/revisions/manager.py` | **YES** | stub hash replaced by a real `canon.v1` port |
| `apps/langgraph` graph/nodes/tools | **NO** | only the hash function's output changes |
| `libs/shared-schemas/**`, `libs/api-contract/**` | **REGENERATED, never hand-edited** | both pipelines re-run |
| `apps/api` Domain/Application/Infrastructure/Api | **YES (additive)** | new read records/interfaces/repos/queries/controller actions |
| `apps/api` existing handlers, validators, migrations, `Program.cs` middleware order | **NO** | **no migration**, no pipeline change |

## P1·Phase 1 — API client foundations (web)

1. **`api/config.ts`** (new) — `getTenantId()`, `getActor()`, `getActorRole()`, `newCorrelationId()`,
   `apiBaseUrl()`. Order: `window.APP_CONFIG` → `import.meta.env.VITE_*` → documented default.
   Correlation ids must satisfy `^[A-Za-z0-9_-]{1,64}$`.
2. **`api/problem.ts`** (new) — `ProblemDetails`, `ValidationProblemDetails`, `IdempotencyConflict`
   (snake_case) types; `ApiValidationError` and `ApiIdempotencyConflictError` extending `ApiError`; a
   `parseErrorBody` that never throws.
3. **`api/client.ts`** — keep `ApiError`/`isApiError`; fix the header-merge order bug; always send
   `X-Tenant-Id` + `X-Correlation-Id`; `Content-Type` only with a body; per-request
   `grantId`/`actor`/`reasonCode` → `X-Grant-Id`/`X-Actor` + `X-Actor-Role`/`X-Reason-Code`;
   204/empty → `undefined`; throw the right subclass.
4. **`vite-env.d.ts`** — `ImportMetaEnv` for the new `VITE_*` vars. **`.env.example`** (new).
5. **`apps/web/CLAUDE.md`** — short "calling the API" section.

**Tests (TDD):** `client.test.ts` — headers present; caller headers win; 400 → `ApiValidationError`
with the `errors` map; 409 → `ApiIdempotencyConflictError` with `existing_operation_id`; 422 →
ProblemDetails `detail`; 204 → `undefined`; non-JSON error body; `APP_CONFIG.API_URL` prefixing.

**Automated verification**
- [x] `bun nx test web` (19 passed) · `bun nx lint web` · `bun nx typecheck web`

**Manual verification**
- [ ] `bun nx build web` still emits `dist/apps/web`; `/` and `/dashboard` render unchanged

### Discoveries

- **`ApiError` had to move out of `client.ts`.** `ApiValidationError extends ApiError` evaluates at
  module-init time, so `client.ts` importing `problem.ts` while `problem.ts` imported `ApiError` from
  `client.ts` was a genuine circular-init failure, not a lint nit. `ApiError` now lives in `problem.ts`
  and `client.ts` re-exports it, so every existing import path still resolves and `instanceof` stays
  single-identity.
- **The header-merge bug was real.** The old body was `{ headers: {...defaults, ...init?.headers},
  ...init }` — spreading `init` *after* meant `init.headers` replaced the merged object wholesale,
  silently dropping `Accept`. Rebuilt on `Headers` with caller values applied last, and pinned by a test.
- **409 is ambiguous on the wire.** `IdempotencyConflictException` returns a snake_case
  `IdempotencyConflict` body while `StaleBookingVersionException` returns a normal `ProblemDetails` —
  both 409. Discrimination is by body keys (`existing_operation_id`/`idempotency_key`), not status, and
  there is a test for each.
- `GET /api/Bookings/{id}` returns **200/403/404 with the same body** (`status: ok |
  requires_verification | not_found`), so a 403 there is a typed business response. Callers must read
  the body rather than treat the status as failure — which is why `fetchApi` preserves the parsed
  `problem` on `ApiError` instead of flattening to a string.

## P1·Phase 2 — `canon.v1` in TypeScript **and** Python, pinned to C# golden vectors

1. **`tools/config/canon-v1-test-vectors.json`** (new) — the one cross-language fixture:
   `{ name, payload, expected }[]` where `expected` is produced by the **real C# hasher**. Covers:
   minimal create payload; key ordering; null vs absent; empty object/array omission; NFC; whitespace
   trim/collapse; timestamp offset → UTC with **ms truncation**; number forms (`500` / `500.0` / `5e2`);
   scalar-array ordering; nested `preorder.lines` natural-key sort; empty payload → `{}`.
2. **`CanonV1GoldenVectorTests.cs`** (new, UTF-8 **with BOM**, `[Trait("Category","Unit")]`) — asserts
   the fixture against `CanonicalRequestHasher`. This is what makes the fixture authoritative.
3. **`api/canonicalHash.ts`** (new) — faithful port; `crypto.subtle` SHA-256; exports
   `canonicalHash(payload): Promise<{version:"canon.v1", value:string}>`.
4. **`cordia_agent/revisions/canonical_hash.py`** (new) + rewire `manager.canonical_payload_hash` to
   it; keep the public function signature.

**Tests:** `canonicalHash.test.ts` and `tests/unit_tests/test_canonical_hash.py` — every golden vector,
plus the property assertions mirrored from `CanonicalRequestHasherTests.cs`.

**Automated verification**
- [x] `bun nx test api` (1081) · `bun nx test web` (101) · `bun nx test langgraph` (989) · all lints
- [x] The fixture is genuinely shared: one file read by all three suites (parametrised counts: 64 each)

### Discoveries

- **Fixture location changed** to `tools/config/canon-v1-test-vectors.json`, matching the repo's
  existing cross-language corpus (`tools/config/redaction-test-vectors.json` +
  `SensitiveDataMaskParityTests`). Following the repo beat inventing `tools/fixtures/`.
- **Payloads are stored as raw JSON *text*, not parsed objects.** A first attempt stored parsed objects
  and any JSON round-trip collapsed `500.0`/`5e2` → `500` and `500.5000` → `500.5`, quietly destroying
  every number-form vector. Only text preserves them.
- **🔴 The output encoding was the real trap.** The canonical bytes come from .NET's *default*
  `JavaScriptEncoder`, which escapes **every non-ASCII code unit** and the HTML-sensitive ASCII set as
  `\uXXXX` with **UPPERCASE** hex. `JSON.stringify` / `json.dumps` emit raw UTF-8 and would have
  produced a different digest for **any accented customer name** (this app's own mock data is full of
  them). Probed the exact escape set from the C# serializer rather than guessing, and pinned it as 13
  dedicated vectors. Notable asymmetry: `\` → `\\` but `"` → `"`.
- **Whitespace classes disagree in exactly two places.** .NET's `char.IsWhiteSpace` includes U+0085
  (NEL) which JS `\s` omits; JS `\s` includes U+FEFF which .NET excludes. Both ports use an explicit
  .NET-matching class, pinned by 4 vectors.
- **Ordering is UTF-16, not code point.** JS and .NET both compare UTF-16 code units; Python compares
  code points, which disagree for astral characters. The Python port sorts on `utf-16-be` bytes.
- **The Python stub was worse than "a stand-in".** It hashed `json.dumps(model_dump(), sort_keys=True)`
  — no NFC, no whitespace collapse, no timestamp normalisation, no number canonicalisation, no
  empty-collection omission, and plain UTF-8 output. It could not have matched the server on any
  non-trivial payload, so agent-submitted bookings would have 422'd.
- **Pre-existing break found and fixed en route:** `libs/shared-schemas/generated/csharp/` was stale
  (2026-07-11) and missing `TenantChannelEnum`, so `bun nx build api` failed before any of this work.
  `libs/shared-schemas/schemas/` did not exist in the checkout at all.

## P1·Phase 3 — TanStack Query + providers (web)

1. Add `@tanstack/react-query`; `bun install`.
2. **`queryClient.ts`** — no retry on 4xx, bounded retry on 5xx/network, `staleTime` ~30 s.
3. **`queryKeys.ts`** — key factory **rooted at the tenant id** (Inv-13 hygiene).
4. **`main.tsx`** — wrap `RouterProvider` in `QueryClientProvider`.
5. **`components/ui/ErrorState.tsx`** (new) — renders an `ApiError` title/detail + retry.

**Automated verification**
- [x] `bun nx test web` (119) · lint · typecheck · build

### Discoveries

- Mutations are configured `retry: false` on purpose. An automatic retry re-sends a side-effecting
  request; the idempotency key makes that *safe*, not *silent*, and the operator still needs to see the
  failure. Retries stay explicit.
- 408 and 429 are the only 4xx worth re-attempting; everything else in the 4xx range is a decision the
  server already made (400 tenant, 403 verification, 409 conflict/stale, 422 hash) and retrying only
  delays the error.

## P1·Phase 4 — Contract: expose the booking version

1. **`cordia_agent/tools/contracts.py`** — add `version: int | None = Field(default=None, …)` to
   `BookingProjectionRef` (additive, optional).
2. Regenerate: `langgraph:generate-schemas` → `shared-schemas:generate-types`.
3. **`GetBookingDetailsQuery.cs`** — read the authoritative `Booking` row unconditionally via
   `IBookingRepository`, and map **`Booking.Version`** (not `BookingProjection.Version`).
4. **`ManageBookingMapper.ToContract(BookingProjection)`** — accept and set the version.
5. Regenerate `api-contract:generate-types`.

**Automated verification**
- [x] `bun nx test api` (1088) · `bun nx test langgraph` (989) · lints · `bun nx typecheck web`
- [x] `BookingProjectionRef` exposes `version` in OpenAPI and the generated TS
- [x] Regression: `FindManageableBookings` maps `ManageBookingCandidate` on a separate overload and is
      untouched — the new parameter defaults to `null`

### Discoveries

- **🔴 The obvious field was the wrong one.** `BookingProjection.Version` already existed and was
  tempting to map — but it is `booking_operations.version`, while modify/cancel compare
  `expected_booking_version` against `bookings.version` via `TryTransitionLifecycleAsync`. Mapping the
  projection's value would have shipped a token guaranteed to 409 on every request, and it would have
  looked correct in review. `GetBookingDetailsTests.Version_ComesFromTheBookingsRow_NotTheOperation`
  seeds the two with deliberately different numbers so the mistake cannot be reintroduced silently.
- The version is `null` when no booking row exists yet — an operation still in flight — which callers
  must treat as "not modifiable yet" rather than as version 0.

## P1·Phase 5 — Six tenant-scoped console read endpoints (API)

All `GET`, all read-only, all `TenantGuard.RequireTenant`, all `.AsNoTracking()`, all returning
`ActionResult<T>` so the generated TS carries a body. **No migration.**

| Endpoint | Controller | Source |
|---|---|---|
| `GET /api/Bookings/schedule?from&to&limit` | `BookingsController` | operations ⨝ revisions |
| `GET /api/booking-operations?status&needs_attention&limit` | `BookingOperationsController` | `booking_operations` (filter on D1 `operation_status`) |
| `GET /api/conversation-sessions?limit` | `ConversationSessionsController` | `conversation_sessions` |
| `GET /api/waitlist/enrollments?status&limit` | `WaitlistController` | `waitlist_enrollments` (+ latest offer) |
| `GET /api/customers?query&limit` | **new** `CustomersController` | derived projection — **no new table** |
| `GET /api/reporting/overview?from&to` | `ReportingController` | aggregates over operations/notifications |

Each gets: Domain read record + Domain interface method + Infrastructure repo + Application
query/handler/**validator** (bounded `limit`, `from < to`) + thin controller action + snake_case
response record. Then regenerate `api-contract`.

**Automated verification**
- [x] `bun nx test api` (1112) · lint · build
- [x] `bun nx test-integration api` (397 passed, 0 failed) — 19 of them the new console tests
- [x] `api-contract:generate-types` succeeds (39 → 44 paths); `bun nx typecheck web` passes
- [x] Each returns 400 without `X-Tenant-Id` (`EveryConsoleReadRequiresATenant` + per-endpoint cases)
- [x] Regression: the `openapi/v1.json` diff is additive — five new paths plus
      `BookingProjectionRef.version`; no existing entry changed

### Discoveries

- **No migration was needed**, as planned. The customers list is an aggregate over
  `booking_operations` ⨝ `booking_intent_revisions` keyed by the opaque `customer_ref` — spec 12
  ratifies no customers table. It projects counts and timestamps only, never a name, phone or email
  (spec 14 minimisation), and the integration test asserts those fields are absent.
- **The canonical-payload parser was extracted, not copied.** `ScheduledBooking`, `CustomerSummary` and
  the overview all need to read the intent revision's JSON payload, which `BookingReadRepository`
  already knew how to do via a *private nested* record. Duplicating it would have created two parsers
  that could disagree about the same booking, so it moved to `Repositories/CanonicalBookingPayload.cs`
  (internal) and both use it. `ToLifecycleStatus` is mirrored with a comment for the same reason.
- **Status filtering needed a wire-name map.** The 16 ratified `OperationStatus` values (D1) travel as
  snake_case but the CLR names are PascalCase, so `Enum.TryParse` cannot round-trip them.
  `OperationStatusNames` builds the map from the `JsonStringEnumMemberName` attributes already on the
  enum. An unknown `status=` is a **400**, not silently ignored — otherwise a typo would quietly widen
  the operator's queue to "everything".
- The overview deliberately has **no "agent vs human" split**: nothing in the data model records who
  handled a booking. That chart stays on mock data rather than inventing a number (Inv-16).
- **Pre-existing flakiness surfaced (not introduced):** the first full integration run had 10 failures
  in `TenantResolutionIngressTests` — `column "channel_endpoint_id" ... already exists`. Three fixtures
  run `MigrateAsync` against the same `Cordia_API_Test` database and xUnit runs collections in
  parallel, so a cold database can race.

## P1·Phase 6 — Wire the reads (web)

1. `features/reporting/` — `useBookingFunnel`, `useReportingOverview` → **AnalyticsScreen**.
2. `features/settings/` — `useConsentPolicy`, `useCatalogItems` → **SettingsScreen** (catalog body is
   `IActionResult` → narrow Zod validation before render).
3. `features/bookings/useBookingSchedule` → **TodayScreen** grid.
4. `features/operations/useOperations` → **QueueScreen**.
5. `features/conversations/useSessions` → **ConversationsScreen** thread list.
6. `features/customers/useCustomers` → **CustomersScreen**.
7. `features/waitlist/useEnrollments` → **WaitlistScreen** list.
8. **`components/ui/DemoBadge.tsx`** (new) + `// MOCK: no endpoint yet` at each remaining mock source.

**Automated verification**
- [x] `bun nx test web` (152) · lint · typecheck · build
- [x] Regression: Deposits, Campaigns and More behaviourally unchanged — the only edit is an added
      `DemoBadge`; no data path or handler touched

**Manual verification**
- [ ] With API + DB running, the six wired screens show real (possibly empty) data
- [ ] With the API stopped, every wired screen shows `ErrorState` and the console still works

### Discoveries

- **Screens keep their prop contracts.** Data is fetched in `ConsolePage` and adapted into the shapes
  `data/console.ts` already defines, so `TodayScreen`, `QueueScreen`, `WaitlistScreen` and
  `ConversationsScreen` needed **no signature change at all**. Only `CustomersScreen` gained a prop,
  because it read its mock constant directly.
- **Customers could not be wired honestly without gating fields.** The API exposes an opaque
  `customer_ref`, a booking count, first/last activity and the last service — no phone, no-show count,
  lifetime spend or tags. Zero-filling those reads as fact ("$0 lifetime", "0 no-shows"), so
  `CustomersScreen` takes a `live` flag and suppresses them instead.
- **Falling back to mock is deliberate but narrow:** a screen shows demo rows only when the API returned
  successfully with nothing. An *error* always surfaces as an `ErrorState`.
- `AnalyticsScreen` keeps three mock sections. Two have no endpoint; the third ("who handled them") has
  no data model at all (Inv-16). All three are badged.
- **ES2020 target bites twice.** `String.prototype.replaceAll` is ES2021 and fails `tsc -b` even though
  `tsc --noEmit` had passed from cache — `bun nx build web` is the check that actually catches it.

## P1·Phase 7 — Booking write flows (create / reschedule / cancel)

1. `features/bookings/identity.ts` — mint `intent_id`/`operation_id`/`confirmation_id`/
   `idempotency_key` **once per attempt**, persisted in `sessionStorage`, so a retry reuses the same key
   (Inv-2 — never mint inside a retry loop).
2. `features/bookings/buildSubmission.ts` — assemble `SubmitConfirmedIntentRequest`; `payload_hash` =
   `canonicalHash(canonical_payload)`, and a `Confirmation` whose `intent_payload_hash` is the **same**
   value with `intent_id`/`intent_revision` bound to the revision.
3. `features/bookings/useOperationStatus.ts` — poll `GET /api/booking-operations/{id}` until `terminal`;
   ~1 s with backoff, ~60 s cap → "still working". Server status only (Inv-16).
4. **CreateBookingDrawer** — add the required **party size** field; submit → 202 → poll → terminal UI
   keyed off `NormalizedOperationStatus`; on `booked` call the existing `onCreated(...)`.
5. **RescheduleDrawer** — `POST /changes:validate` pre-flight → `POST /modifications` → poll, using the
   **real `version`** from Phase 4.
6. **BookingDetailDrawer** — `POST /cancellations` with a reason code → poll. The Undo toast becomes a
   two-step confirm.
7. **ConsolePage** — real handlers replace the timers; **screen props unchanged**.
8. `stale_version` (409) → first-class "this booking changed — reload" state.

**Automated verification**
- [x] `bun nx test web` (168) · lint · typecheck · build · `bun nx test api` still green (1112)

**Manual verification**
- [ ] Creating a booking returns **202 (not 422)** and polls to a terminal state — the end-to-end proof
      that the TS `canon.v1` port matches the server
- [ ] Re-submitting the same attempt reuses the key and creates no second operation

### Discoveries

- **The create drawer needed a new field.** `BookingRequest` requires `party_size` and the mock form
  never collected it, so a "wire it up" change genuinely could not be field-for-field.
- **"Confirmed" now means confirmed.** The drawer's success panel fires only when the poll reports
  `status === "booked"`; 202 puts it in a *submitting* state with the server's own `customer_message`.
- **Cancel lost its Undo, deliberately.** The mock cancel was locally reversible; a real one is not.
- **Reschedule stays optimistic but self-correcting:** the grid moves immediately, then the operation is
  submitted and the schedule query invalidated, so the server's placement wins on settle.
- Two `String.prototype.replaceAll` uses had to become `split/join` (ES2020), and the generated
  `SubmitConfirmedIntentRequest` needs an `as unknown as` cast: its TriState unions and `T | null`
  optionals cannot be narrowed from a fresh object literal.

## P1·Phase 8 — Verification grant sub-flow (web)

1. `features/verification/` — `requestVerification` → `verifyCode` → `grant_id`, held **in memory only**
   with its `expires_at`.
2. **`VerificationGate`** — mounted on `403` / `requires_verification`; request → OTP → verify, then
   retries the original call with `X-Grant-Id`.
3. Wire into `GET /api/Bookings/{id}` and into modify/cancel `verification_required`.
4. Minimal `OtpInput` primitive (labelled, keyboard-operable).

**Automated verification**
- [x] `bun nx test web` (168) · lint · typecheck · build
- [x] The grant is never persisted — `useVerification` holds it in React state only, and no storage API
      is referenced anywhere in `features/verification/`

### Discoveries

- `failed` / `expired` / `pending` are **business outcomes**, not transport errors: a wrong code returns
  200 with `status: "failed"`. The hook distinguishes those from a thrown `ApiError` so a mistyped code
  shows "that code did not match" rather than a stack of error chrome.
- `no_verified_destination` and `rate_limited` get their own copy — telling an operator "something went
  wrong" when the guest simply has no verified phone on file is the failure N9 exists to prevent.
- The gate hands the grant up from an **effect**, not from render; calling the parent's setState during
  render is a side effect React may run twice.

## P1·Phase 9 — Waitlist actions, transcript, docs

1. `features/waitlist/` — `enroll` / `cancelEnrollment` / `acceptOffer` / `declineOffer`, all sending
   `X-Actor`.
2. `features/conversations/useTranscript(sessionId)` with `X-Actor`/`X-Actor-Role`/`X-Reason-Code` +
   optional grant → **ConversationsScreen** transcript.
3. **`docs/frontend-api-wiring.md`** — update §1/§4/§9/§10, and record the two contract gaps and how
   they were closed.

**Automated verification**
- [x] web (168) · api (1112) · integration (397) · langgraph (989) · all lints and builds
- [x] Every "must stay the same" row in the blast-radius table verified against the diff

### Discoveries

- Waitlist removal is real and **not** undoable client-side: the row is removed optimistically, and on
  failure it is restored and the server's message shown.
- The transcript read sends `X-Actor` / `X-Actor-Role` / `X-Reason-Code` because the API audits it as a
  sensitive read (spec 14 §4.2) — it is not an ordinary GET.
- Transcripts are only requested for sessions that came from the API; the demo thread ids are not real
  sessions and would 404.

## Part 1 reviews

Two rounds: an **xhigh** pass (4 reviewers + a live API run, 8 blockers + a process defect) and a
**100-agent conformance workflow** (45 raised → 16 confirmed, 29 refuted). Both are written up as
rounds 1 and 2 of the review record. Round 2's headline finding — *the fulfilment engine is
create-only* — is what Part 2 exists to fix.

**Part 1 final state:** api **1122** · integration **397 / 0 failed** · web **184** · langgraph **999**
· lint/typecheck/build green · DB 30/30 migrations applied.

---

# Part 2 — Cancel fulfilment + `useMutation` refactor (2026-07-27)

Fixes the two caveats Part 1 left open:

1. A `cancel` operation is accepted but fulfilled by the **create** path — it calls `CreateBookingAsync`
   and never touches the booking's lifecycle.
2. The web write flows are hand-rolled `fetchApi` calls rather than `useMutation`.

## Current state (verified, not assumed)

`SubmitBookingCancellationCommandHandler` creates a **separate** operation (`OperationType = Cancel`,
`Status = Accepted`, `BookingId` = target) and flips the booking row to `CancellationPending`, then
emits the generic `booking.command.requested.v1`.

From there the operation is driven by `AdvanceBookingOperationCommandHandler`, which dispatches
**purely on `OperationStatus`** — it never reads `OperationType`. So a cancel runs
`Accepted → Validating → ReservingCapacity → Held → Finalizing → Booked`, reserving capacity it does
not need and calling `adapter.CreateBookingAsync` at the end. Net effect: a cancel **creates a second
booking** and leaves the original `CancellationPending` forever.

Verified by: grep for `OperationType` across `Handlers/BookingFulfillment/**` (one hit — a comment in
the SLA handler declining to branch); grep for `TargetBookingId` there (zero);
`CancellationPending → Cancelled` is written nowhere in `Cordia.Application`/`Infrastructure`.

## Key discovery: no new enum value, no migration

The earlier assumption that this needs an `OperationStatus.Cancelled` + pg-enum migration is **wrong**.
A cancel is its own operation, so:

- the **operation** terminates at `Booked` — the generic "this operation succeeded" terminal (D1 has no
  `cancelled` operation status, and D2's `NormalizedOperationStatus` has none either);
- the **booking row** carries the cancellation: `CancellationPending → Cancelled`
  (`BookingLifecycleStatus` already has all four values);
- `NotificationEvent.CancelOrModifyConfirmed` **already exists** as a ratified pg-enum value, with a
  `NotificationIdentity` entry and a template-resolver entry — only the emission is missing.

So **no DDL, no pg-enum change, no migration.**

## Blast radius

| Item | Changing? | Note |
|---|---|---|
| `AdvanceBookingOperationCommandHandler` | **YES** | one branch at `Finalizing` on operation type |
| `ValidateOperationCommandHandler` | **YES** | cancel ops route to `Finalizing`, not `ReservingCapacity` |
| `OperationStateMachine` | **YES** | one added edge `Validating → Finalizing` |
| `Handlers/BookingFulfillment/CancelBooking/**` | **NEW** | the cancel step |
| `ApplicationServiceCollectionExtensions` | **YES** | register `notification.cancel_confirmed.v1` |
| `FinalizeBookingCommandHandler` | **NO** | untouched — cancel gets its own handler |
| `ReserveCapacityCommandHandler`, `RequestPayment`, `Compensate`, `Reconcile` | **NO** | |
| `SubmitBookingCancellationCommandHandler` | **NO** | acceptance already correct |
| Any migration / pg-enum / DDL | **NO** | |
| `apps/web` write flows (`ConsolePage`, `CreateBookingDrawer`) | **YES** | moved onto `useMutation` |
| `apps/web` read hooks, adapters, screens · `apps/langgraph` | **NO** | |

## P2·Phase 1 — Cancel fulfilment (API)

1. **`OperationStateMachine`**: add the `Validating → Finalizing` edge, with a comment that it exists
   for operations that reserve no capacity (cancel).
2. **`ValidateOperationCommandHandler`**: after the policy gate, route `OperationTypeEnum.Cancel` to
   `Finalizing` (reason `"validated_cancel"`) instead of `ReservingCapacity`.
3. **`AdvanceBookingOperationCommandHandler`**: at `OperationStatus.Finalizing`, dispatch
   `CancelBookingCommand` when `op.OperationType == Cancel`, else `FinalizeBookingCommand`.
4. **`CancelBookingCommandHandler`** (new), mirroring `CompensateOperationCommandHandler`:
   - guards: tenant match, `Status == Finalizing`, `OperationType == Cancel`;
   - idempotent `WorkflowStep` of type `"cancel"`;
   - resolve the target booking via `op.BookingId`; **already `Cancelled` ⇒ succeed without calling the
     provider** (spec 08 §9.4.4);
   - `adapter.CancelBookingAsync(ProviderAccount, booking.ProviderReference)` **outside** the txn;
   - success (`Created`/`AlreadyExists`/`NotFound`) ⇒ one txn: step → `Succeeded`; booking →
     `Cancelled` version-guarded; operation `Finalizing → Booked` (reason `"cancelled"`); outbox
     `notification.cancel_confirmed.v1`; audit `operation.cancelled`;
   - no provider reference ⇒ same success path, reason `"cancelled_no_provider_effect"`;
   - `Ambiguous`/`Transient`/`PermanentError`/`AuthError` ⇒ `Finalizing → Reconciling` (Inv-7),
     return `Wait`;
   - booking row missing ⇒ `PoisonMessageException` (a cancel with no target is a defect).
5. **Register** `notification.cancel_confirmed.v1` → `NotificationEvent.CancelOrModifyConfirmed`.

**Automated verification**
- [x] `bun nx test api` (1141) · lint · build · `bun nx test-integration api` (402, up from 397)
- [x] A cancel drives to `booked` **and** leaves the booking `cancelled`, with no second booking created
      — `CancelFulfilmentTests` counts the tenant's booking rows after the cancel settles
- [x] Regression: a create still finalizes through `ReservingCapacity → Held → Finalizing`

### Discoveries

- **`JobEnvelope` is ambiguous** between `Cordia.Domain.Generated` and `Cordia.Domain.Messaging`. The
  handler cannot carry a bare `using Cordia.Domain.Generated;`; `OperationTypeEnum` is fully qualified.
  Existing fulfilment handlers do the same.
- **A "lost race" must write nothing.** When the version-guarded `Finalizing → Booked` transition
  returns `rows == 0`, another worker already committed the cancellation; the handler returns
  immediately rather than continuing to the booking update, capacity release, outbox and audit writes.
  Doing them anyway would double-release capacity and emit a second notification.
- **Two test doubles implement `ICapacityReservation` by hand** — `NoCapacityReservation` and
  `CapstoneNoCapacityReservation`. Adding a member to the interface breaks them at compile time.
- Guard order matters: the tenant / `Finalizing` / `Cancel` checks return `Wait`, but a missing
  `BookingId` or booking row is a **defect**, not a race, so it throws `PoisonMessageException`.

## P2·Phase 2 — Capacity release on cancel (selected at the gate)

Adds the spec-07 surface to release a finalized hold and decrement `BookedCapacity`, plus the lookup
from booking → original operation.

**Automated verification**
- [x] `bun nx test api` · `bun nx test-integration api` — the integration test asserts the slot's
      `booked_capacity` goes 2 → 0 and the hold flips to `Released`

### Discoveries

- **The cancel operation holds no capacity of its own** — it never passes through `ReservingCapacity`.
  The hold to release belongs to the *originating* booked operation, so
  `IBookingOperationRepository.GetBookedOperationForBookingAsync` finds it: status `Booked`, type in
  {Create, Reschedule, Modify}, most recently updated first.
- **Release decrements `BookedCapacity`, not `HeldCapacity`.** A finalized hold has already moved from
  held to booked; decrementing the wrong counter would leave the slot permanently oversubscribed.
  `ReleaseFinalizedByOperationAsync` uses a guarded `ExecuteUpdateAsync`
  (`HoldStatus.Finalized → Released`) so a redelivery releases at most once.
- A booking with no reserved capacity releases nothing and still cancels cleanly.

## P2·Phase 3 — Truthful cancel messaging (web)

`normalizedStatusLabel` renders `booked` as "Confirmed" — correct for a create, wrong for a cancel.
Make the label take the operation type so a cancel reports "Cancelled", and mark the booking row
cancelled from the server's response rather than from a local guess.

**Automated verification**
- [x] `bun nx test web` (191) · lint · typecheck

### Discoveries

- **D1 defines no `cancelled` operation status.** A successful cancel terminates at `booked` — the
  generic "this operation succeeded" terminal — with the *booking* carrying the cancellation. Reusing
  the create copy would have told an operator their cancellation was "Confirmed", the opposite of what
  happened. `CANCEL_STATUS_COPY` overrides only the statuses that differ and falls back to the create
  wording for the rest, so a status neither map covers still renders as words rather than a raw enum.

## P2·Phase 4 — `useMutation` for the write flows (web)

1. **`useCreateBooking`** — `mutationFn` builds + submits; the caller keeps polling via
   `useOperationStatus`.
2. **`useCancelBooking`** — `mutationFn` reads the authoritative version, submits, polls to terminal;
   `onSettled` invalidates the booking + operation query keys in one place.
3. **`useRemoveFromWaitlist`** — `onMutate` cancels in-flight refetches and snapshots, `onError` rolls
   back, `onSettled` invalidates.

Mutations stay `retry: false`.

**Automated verification**
- [x] `bun nx test web` (191) · lint (0/0) · typecheck · build
- [x] Cancel reports the server's terminal outcome phrased for a cancellation; waitlist removal restores
      the snapshot on failure

### Discoveries

- **The submit→poll sequence belongs *inside* `mutationFn`, not around it.** That was the original
  objection to the hook, and it turned out to be the wrong shape: putting the whole sequence in
  `mutationFn` makes `isPending` cover the operation's real duration and moves invalidation into a
  single `onSettled`.
- **Create is the deliberate exception**: it returns at the 202 so the drawer can watch intermediate
  states with `useOperationStatus`. Folding the poll in would hide exactly the states the operator is
  meant to see.
- **Hook ordering bites.** `useRemoveFromWaitlist(setWaitlist)` must be declared *after* the `waitlist`
  useState it closes over.
- **Slice-based Python edits over a large TSX file are fragile.** A multi-edit `src.index()`-anchored
  script silently duplicated ~230 lines of `ConsolePage.tsx`. Asserting line boundaries before
  splicing — or editing via the Edit tool — is the reliable route.

## Part 2 reviews

Two rounds: **two independent diff reviewers** (9 real defects, two HIGH on the version guard and the
optimistic rollback) and a **conformance pass against the wiring doc** (15 confirmed, grouped around
the `Booked`-means-two-things trap). Rounds 3 and 4 of the review record — including the trap worth
remembering, that `OperationTypeEnum.Cancel` is the **zero value** of the generated enum.

---

# Part 3 — Verification wiring + operator reason codes (2026-07-28)

**Gate answers:** reason code → *server allowlist in the API*; verification scope → *cancel +
grant-backed details read*.

## What these two items actually were

Not polish. The baseline audit found:

1. **The console's cancel could never succeed.** `SubmitBookingCancellationCommandHandler` consumes a
   `cancel` grant unconditionally (correctly — Inv-11) and nothing in the app could obtain one:
   `features/verification/**` was fully built and imported by no page, drawer, or hook. A repo-wide grep
   returned exactly one hit — a code comment.
2. **The audit trail recorded a purpose the operator never chose**, on the console's only PII read, and
   re-recorded it on every window focus.

## Baseline (verified, not assumed)

- `POST /api/bookings/{id}/cancellations` → `ConsumeGrantAsync(..., Cancel, ...)` inside the same
  transaction that elects the idempotency record and transitions the booking.
- `grant_id` is `NotEmpty`-validated ⇒ the console's `grant_id: ""` was a **400**, not a 403.
- `GET /api/bookings/{bookingId}` returns the **same** `minimal` projection with or without a grant.
- `GET /api/conversation-sessions/{id}/transcript` authorizes on **either** a `read_sensitive` grant
  **or** staff role + non-empty reason code. The console uses the staff path, which is correct.
- No reason-code allowlist existed anywhere in C#, Python, or TypeScript.

## Phases

**P3·Phase 1 — Machine-readable verification 403 (api) ✅**
`WriteProblemAsync` gained an optional `extensions` argument; `VerificationRequiredException` now emits
`code: "verification_required"`, the D5 `action` (via `EnumWireNames.ToWireName`), and
`target_booking_id`. `AuthorizationFailedException` deliberately does not.
- [x] build · lint · `bun nx test api` (1170) · `bun nx test-integration api` (405, +3)

**P3·Phase 2 — Classify it in the client (web) ✅**
`toApiError` maps a 403 carrying `code === "verification_required"` to `ApiVerificationRequiredError`,
exposing `action` / `targetBookingId`. The existing typed `status === "requires_verification"` branch is
untouched and still matched first.
- [x] `bun nx test web` · typecheck · lint

**P3·Phase 3 — Mount VerificationGate in the cancel flow (web) ✅**
Clicking Cancel opens the gate in the booking drawer; `onGranted` submits the cancellation with the
grant. `useCancelBooking` now takes `{ bookingId, grantId }`. A grant that expires between issue and use
returns the Phase-1 403, which re-opens the gate. Closing the drawer clears a pending gate.
- [x] `bun nx test web` — the grant reaches `submitCancellation` · typecheck · lint · build
- [ ] *Manual:* cancelling prompts for verification, sends a code, and completes on a valid code

**P3·Phase 4 — Operator purpose on the transcript read (web) ✅**
`getTranscript` requires an explicit purpose — the `"operator_review"` default is gone.
`useTranscript(sessionId, reasonCode)` is disabled until one is chosen and sets `staleTime: Infinity`,
`refetchOnWindowFocus: false`, `refetchOnReconnect: false`. The operator picks from seven labelled
purposes before the read happens.
- [x] `bun nx test web` (216) · typecheck · lint · build
- [ ] *Manual:* selecting a conversation does not fetch until a purpose is chosen; alt-tabbing does not
      issue a second request

**P3·Phase 5 — Reason-code vocabulary (api) ✅ — narrowed**
`SensitiveReadPurpose` (`Cordia.Domain/Enums`): seven ratified values, enforced by FluentValidation on
the transcript read. Scoped deliberately — see discoveries.
- [x] `bun nx test api` — 19 unit tests over the vocabulary and both validators
- [x] `bun nx test-integration api` — the existing `dispute_review` fixture still passes

**P3·Phase 6 — Documentation ✅**
`docs/frontend-api-wiring.md` §11: the Verification row now says "Live for cancel" with the reason the
grant is obtained up front, and a new subsection records what the audit turned up.

### Discoveries

- **The console's cancel was not "unverifiable", it was malformed.** The plan assumed a 403 the client
  could recover from. `grant_id` is `NotEmpty`-validated, so `grant_id: ""` is rejected **structurally
  as a 400** before the authorization spine runs — there is no 403 to catch. This inverted the design:
  the grant must be obtained *before* the first submit. Found by writing the integration test and
  getting 400 where 403 was expected.
- **A rejected cancel leaves nothing behind.** `ElectCreatorAsync`, the lifecycle transition, and
  `ConsumeGrantAsync` all run in one transaction, so a refused grant rolls back the idempotency record
  too. The attempt identity therefore survives reject → verify → retry even though adding `grant_id`
  changes the canonical request hash (Inv-2). Pinned by
  `SubmitCancellation_WrongGrant_LeavesTheBookingAndTheKeyUntouched`.
- **ProblemDetails extensions need the framework serializer.** `WriteProblemAsync` passed a hand-rolled
  `JsonSerializerOptions`, which bypasses ASP.NET's `ProblemDetails` converter and serializes the type
  as a plain POCO — burying `code`/`action` under a nested `"extensions"` node no RFC-9457 client looks
  in. Dropping the explicit options flattens them.
- **One repo, three unrelated things called "reason code".** Sizing the allowlist turned up: the *audit
  purpose* on a sensitive read (`X-Reason-Code`); the *operational narrative* on an operator action
  (`provider_outage_recovered` on DLQ replay); and the *business reason* a guest changed a booking
  (`customer_request`, `complaint`). A flat allowlist over `IOperatorActionRequest` would have conflated
  all three. The vocabulary is scoped to the first.
- **Model traces are referenced, not categorised.** The first draft applied the allowlist to
  `GetModelTraceQuery` too and three integration tests failed on `incident_42` — which is exactly right:
  a trace read points at an incident. A closed list would replace a specific pointer with a vague label,
  so that surface stays free-form. The asymmetry is pinned by a test.
- **`operator_review` is deliberately not ratified.** It was the old hard-coded constant; admitting it
  would let the exact behaviour being fixed pass validation.
- **Existing cancellation coverage never exercised the console's path.** Every integration test seeded a
  `VerificationGrant` row straight into the database, so the no-grant request shape the console actually
  sent had no coverage in any lane.
- **Vitest does not typecheck.** After `useCancelBooking`'s signature changed, four tests kept passing
  while calling it with the old `string` argument — destructuring a string yields `undefined` for every
  field and the mocks did not care. `bun nx build web` (which runs `tsc -b` over tests) caught it;
  `bun nx typecheck web` alone did not.

### What we did NOT do

- Did not wire the gate onto `GET /api/bookings/{bookingId}`. The gate answer selected it, but the
  server returns a byte-identical projection with or without a grant — the operator would see no new
  data, only an extra prompt and an audit row. Recorded as the one deliberate deviation from the gate
  answer; worth revisiting when spec 08 differentiates the projection.
- Did not ratify the vocabulary in the Pydantic contract (spec 14 owns it; that binds all three lanes).
- Did not touch `IOperatorActionRequest.ReasonCode` or the booking `reason_code` business field.
- Did not change the transcript's server-side authorization spine.

---

# Part 4 — Gap closure, G1–G14 (2026-08-03)

Bring the console into full conformance with the two rules, and close the reachability gaps in flows
already claimed as live (verification, waitlist). Plus one security fix the spec-15 landing exposed.

**Current state at start:** `feat/ui-wire` uncommitted (50 modified + 42 untracked). All lanes green
except one known-flaky integration test. Six console read endpoints live; create and cancel live
end-to-end; reschedule built but gated off.

## Blast radius (against the pass-2 baseline)

| Baseline item | Verdict |
|---|---|
| 2·B1 five list queries + `loadFailures` + demo-only-on-`undefined` | **intentionally extended** (transcript joins the pattern); the five existing behaviours must not change |
| 2·B2 `WEEK`-driven date labels | **intentionally changed** |
| 2·B3 queue `dismissalKey` state-aware dismissal | **must stay byte-for-byte** |
| 2·B3 fabricated wait/retries/channel + button label | **intentionally changed** |
| 2·B4 transcript purpose gate, `staleTime: Infinity`, no refetch-on-focus, live-session-only | **must stay byte-for-byte** |
| 2·B4 transcript error path, thread `status`/`prov` | **intentionally changed** |
| 2·B5 `useRemoveFromWaitlist` optimistic rollback + its 2 tests | **must stay byte-for-byte** |
| 2·B6 canon.v1 hash binding, attempt-identity reuse, `retry: false`, terminal wording, reschedule gate | **must stay byte-for-byte** |
| 2·B6 create `identityRef`; cancel pre-read grant | **intentionally changed** |
| 2·B7 gate-before-submit, in-memory grant, match on `code` | **must stay byte-for-byte** |
| 2·B8 consent-policy + catalog live panels | **must stay byte-for-byte** |
| 2·B9 header set, shim subordination, `TenantGuard` fail-closed | **must stay**; shim *registration* intentionally gated |
| `data/console.ts` **types** | may gain optional fields; **no field removed, no demo constant deleted** |
| Generated `libs/*/generated/**`, applied migrations, `Program.cs` middleware **order** | **untouched** |
| langgraph lane | **untouched** |

## P4·Phase 1 — Real calendar dates + week navigation (G2) 🔴

1. New `features/bookings/week.ts`: `buildWeek(weekStartIso)` → `{ dows, doms, month, rangeLabel,
   todayIndex | null, dates: Date[] }`, derived entirely from the window the schedule query actually
   used. `todayIndex` is **null** when the displayed week does not contain today.
2. `scheduleWindow` becomes state with `goToPrevWeek / goToNextWeek / goToThisWeek`. The query key
   already includes `from`/`to`, so each week caches independently.
3. Thread the derived week (not `WEEK`) into TodayScreen's headers/range label, the ConsolePage header
   subtitle, both drawers' date labels, and the create drawer's day options.
4. Demo screens keep importing `WEEK` — it is not deleted, only removed from every live surface.
5. Guard: `toIsoInstant` in the create/reschedule paths must use the **displayed** week start, not
   `startOfWeek(new Date())`, or a booking made while viewing next week lands in the wrong week.

**Automated verification**
- [x] `bun nx test web` — new `week.test.ts` covers a month-boundary week, a week not containing today
      (`todayIndex === null`), a DST-crossing week, and `toIsoInstant(displayedWeekStart, …)` landing on
      the intended date — **233 passed** (was 221)
- [x] typecheck · lint (0 warnings) · build

### Discoveries

- 🔴 **`bun nx typecheck web` was a no-op — every phase's type gate was false-green.**
  `apps/web/tsconfig.json` is solution-style (`{ "files": [], "references": [...] }`), so `tsc --noEmit`
  compiled **zero files** and exited 0 no matter what. Proven by dropping `const x: number = "nope"`
  into `src/` — `tsc --noEmit` exited **0**; `tsc -b --noEmit` exited **2** and named the file. Not
  theoretical: a real `DAY_OPTS` reference left in `RescheduleDrawer` passed `typecheck` and was only
  caught by `bun nx build web`. Target changed to `tsc -b --noEmit`.
- `todayIndex` is deliberately **nullable**. The old `WEEK.today = 3` meant every week highlighted a
  Thursday; a week that does not contain today must highlight nothing rather than guess a column.
- Day arrows now **roll over** between weeks instead of disabling at Monday/Sunday, and "Today" jumps
  the *window* as well as the column.
- `buildWeek` falls back to the current week on an unparseable window rather than rendering
  `Invalid Date` across the whole grid.

## P4·Phase 2 — Stop asserting what the API doesn't record: queue + conversations (G5, G6)

1. **Queue:** delete `MOCK_WAIT` / `waitingTime`. Render only what `OperationSummary` carries —
   `updated_at` as a real relative age, the ratified status label, and the operation type. Drop the
   `retries` chip and channel badge for live rows; make `QueueItem.retries`/`channel` optional.
2. Rename the primary action to **"Clear from queue"** with a tooltip explaining it is a local
   acknowledgement. Keep `dismissalKey` semantics exactly.
3. Use a readable identifier for `name`, never a bare UUID presented as a guest name.
4. **Conversations:** make `Thread.status` and `Thread.prov` optional; live threads set neither.
5. Add `transcriptQuery` to the failure surface: a failed/forbidden transcript renders `ErrorState`
   with retry inside the thread pane — never an empty message list.

**Automated verification**
- [x] `bun nx test web` — **250 passed** (was 233) · typecheck · lint · build

### Discoveries

- **Two more fabrications surfaced while implementing, both fixed here.**
  - `QueueScreen` rendered `<Chip status="failed" />` for **every** row. The board carries
    `manual_review`, `alternatives_required` and `compensation_required` alongside genuine failures —
    colouring four situations identically red tells the operator they are the same one. Live rows now
    show the platform's own status via `operationStatusLabel` in a neutral pill.
  - `ConversationThread`'s header hard-coded *"· handled by AI"* and an **AI** badge on every thread.
    Now shown only when the row actually carries `prov`, which live rows no longer do.
- `Thread.status`/`prov` and `QueueItem.retries`/`channel` became **optional** rather than being given
  honest defaults — there is no honest default.
- The mock `MESSAGES` fallback needed an explicit empty entry for a live session, not just an error
  prop: `messages[id] || []` would otherwise still fall through to the mock map.
- `Element.prototype.scrollIntoView` does not exist in jsdom, so any test rendering the conversation
  thread threw. Stubbed once in `src/test-setup.ts`; this unblocks component tests for every screen.

## P4·Phase 3 — Badge every remaining demo surface (G7)

1. `DemoBadge` on **Business setup**, **Agent guardrails**, **Agent knowledge base**.
2. **Waitlist auto-book (Inv-1 / spec 11 §20):** `auto_book_authorized` is a recorded consent grant and
   there is no endpoint to change it. Render it **read-only** for live rows with an explanation, and
   badge the master toggle as demo.
3. **Command palette:** search the live customers list; fall back to demo names only when the query has
   never loaded, and label those results "Demo".

**Automated verification**
- [x] `bun nx test web` — **260 passed** (was 250) · typecheck · lint · build

### Discoveries

- The waitlist auto-book chip was the sharpest instance of the whole class: it looked like a venue
  preference but is the **guest's** consent, recorded at enrollment. Live rows now render it as a
  read-only state whose tooltip says whose decision it was.
- `WaitlistItem.isLive` is the discriminator. An `isLive` flag beats "is the id a UUID"-style sniffing,
  and it is the same shape `Booking.isBooking` already uses.
- The palette's empty-array case matters as much as the undefined one: `customers: []` is a real answer
  and must **not** fall back to demo names.
- The palette selects on `onMouseDown`, not `onClick` — deliberate, so selection wins the race against
  the input's blur. The test had to match.

## P4·Phase 4 — Verification reaches every gated call (G8) 🔒

1. **Cancel pre-read forwards the grant** — `getBookingDetails(bookingId, grantId)` in
   `useCancelBooking`. Same for `submitReschedule`'s read (latent, but wrong).
2. A `verification_required` error from a **booking-detail read** mounts `VerificationGate` and retries
   with the grant.
3. A `verification_required` error from a **transcript read** mounts the gate for `read_sensitive`, then
   retries with `X-Grant-Id`.
4. Grants stay **in memory only**, obtained before the first submit, matched on `code` never `title`.

**Automated verification**
- [x] `bun nx test web` — **267 passed** (was 260) · typecheck · lint · build

### Discoveries

- **The cached-failure trap.** Forwarding the grant is not enough on its own: the transcript query keys
  on the session id, so react-query would serve the *cached 403* after step-up succeeded and the
  operator would see the same error they just resolved. The grant is now part of the query key, which
  makes obtaining one a genuinely new query.
- **Two 403s that must not be conflated.** `VerificationRequiredException` carries
  `code: "verification_required"` and is recoverable; `AuthorizationFailedException` is also a 403 but
  means "your role does not permit this", and mounting the gate for it would loop the operator through a
  flow that cannot succeed. Pinned by a test that goes through the real `toApiError` classifier.
- The transcript grant is cleared alongside the purpose whenever the open conversation changes — a grant
  is scoped to the subject it was issued for.
- Reschedule's booking read still has no grant to forward because that flow never obtains one. Left as a
  comment rather than unreachable UI.

## P4·Phase 5 — Waitlist offers become real (G9)

1. Surface `latest_offer_status` / `latest_offer_expires_at` on the row.
2. Wire **Accept** / **Decline** to the existing `acceptOffer`/`declineOffer`, modelled on
   `useRemoveFromWaitlist`. Actions render **only** when an actionable offer exists.
3. "Book now" prefills the create drawer with the enrollment's service, party size and window.
4. No `enroll` UI — enrollment is a guest-initiated flow; out of scope.

**Automated verification**
- [x] `bun nx test web` — **289 passed** (was 267) · typecheck · lint · build
- [x] `bun nx build api` · lint · `bun nx test api` (1170) · `bun nx test-integration api` (414, 2
      skipped, 0 failed)
- [x] `api-contract:generate-types` — `latest_offer_id` present in the contract

### Discoveries

- 🔴 **The offer actions were unreachable for a reason nobody had noticed: the projection exposed no
  offer id.** `WaitlistEnrollmentSummary` resolved the latest offer server-side and returned only its
  *status* and *expiry* — so a caller could see that an offer existed and had nothing to address. Added
  `LatestOfferId` to the projection + `latest_offer_id` to the view (this branch **owns** the console
  projections; additive, no DDL) and regenerated the api-contract TS. **Deviation from the plan**, which
  predicted no contract regeneration.
- **Not optimistic, unlike removal.** Accepting finalizes a capacity hold and confirms a booking
  server-side; both actions can legitimately 409 when the guest answered first or the offer expired. The
  row is left alone until the server decides, and a 409 is phrased as the race it is.
- An offer whose `expires_at` has passed is treated as **not actionable even when the row still says
  `offered`** — the expiry sweeper may not have run, and rendering Accept there guarantees a 409.
- "Book now" moves the grid to the week containing that window first — otherwise a prefilled day index
  would point at the same weekday of whatever week was on screen.
- The integration suite passed **414/416 with zero failures** this run, where the pre-work baseline had
  one failure. Confirms `RetentionSweepTests` is non-deterministic rather than broken.

## P4·Phase 6 — Create-drawer identity honesty (G3, web half) 🔒

1. Send `identityRef: getActor()` — the operator **is** the confirming party, matching cancel and
   reschedule. Stops a plain-text guest name being written into the minimised `customer_ref` path.
2. The guest-name field stays as a **local label only**, and the UI says so.
3. Assert in a test that `confirmation.identity` no longer carries user-typed free text and that
   **`revision.payload_hash` is byte-identical** to before.
4. Record the contract gap in §11 as an explicit open item for spec 04/08/12.

**Automated verification**
- [x] `bun nx test web` — **292 passed** (was 289) · typecheck · lint · build

### Discoveries

- The hash really is unaffected, and it is now **pinned by a test** rather than reasoned about:
  `identity` lives on the confirmation, not on `revision.canonical_payload`. Had it been on the payload,
  this change would have been a 422 and a broken idempotency replay.
- The create drawer was also the last place asserting provenance: its optimistic row claimed
  `prov: 'you'`. Now `unknown`, matching what the server's row will say.
- The guest-name field is **kept**, because an operator needs a label on the grid, but its hint now
  states plainly that it is local. Silently continuing to collect a field that goes nowhere was the
  alternative, and it is worse.
- **Still open for spec 04/08/12:** there is no ratified way to attach a guest to a staff-made booking.
  Until one exists, console bookings have no notification destination and cannot be found by
  `GET /api/Bookings?phone_e164=`.

## P4·Phase 7 — Gate the `X-Tenant-Id` dev shim (API) 🔒 Inv-13

1. Register `TenantResolutionMiddleware` **only** when `app.Environment.IsDevelopment()` or an explicit
   `Tenancy:AllowHeaderShim` config flag is true (default **false** outside Development). Middleware
   **order is unchanged**; only registration is conditional.
2. The verified-signed-context path is untouched, and `TenantGuard.RequireTenant` still fails closed.
3. Test-host fixtures keep working by setting the flag in test configuration rather than weakening the
   default.

**Automated verification**
- [x] `bun nx build api` · lint (new `.cs` UTF-8 **with BOM**) · `bun nx test api` (1170)
- [x] `bun nx test-integration api` — **418 passed, 2 skipped, 0 failed**; four new
      `TenantHeaderShimGateTests` prove the same request returns **200 with the shim on and 400 with it
      off**, that a request with no tenant fails closed either way, and that a malformed signed context
      is refused rather than downgrading into the shim

### Discoveries

- **`IsDevelopment()` alone would have broken every integration test**: the Alba fixtures boot the host
  as environment `Test`, not `Development`. Resolved with a `Tenancy:AllowHeaderShim` flag that
  *defaults* to `IsDevelopment()` and is set true in `appsettings.Test.json` — the test host opts in
  explicitly, rather than the production default being loosened to keep tests green.
- Putting the flag in `appsettings.Test.json` rather than in each fixture matters: at least six test
  classes build their own ad-hoc `AlbaHost`, and patching them individually would have left whichever
  one was missed silently depending on a default that no longer holds.
- The disabled path **logs once at startup**. A silently absent shim looks identical to a broken tenant
  lookup, and that is a bad thing to debug at 2am.
- The test asserting a malformed `X-Tenant-Context` returns **401 rather than falling through to the
  header** is the one that matters most: without it, a forged context could downgrade into the dev shim
  instead of being refused.

## P4·Phase 8 — Make the integration suite deterministic (G14)

> **The diagnosis going in was wrong.** See discoveries — the real cause is a PAN-detector false
> positive on random hex ids, and it is not an isolation problem at all.

**Automated verification**
- [x] `bun nx test-integration api` — full suite green **twice consecutively** (418 passed, 2 skipped,
      0 failed, both runs, `--skip-nx-cache`)
- [x] `bun nx test api` — **1176 passed** (4 new mask-behaviour tests)

### Discoveries

- 🔴 **The documented diagnosis was wrong, and the real one is a product finding.** `RetentionSweepTests`
  already used a unique tenant per run, so accumulation could not have been the cause. The actual
  mechanism: `AuditEventSanitizationInterceptor` masks `subject_ref` through `SensitiveDataMask.Sanitize`,
  and the test seeds `iKey = $"ikey-{Guid.NewGuid():N}"`. A hex GUID is drawn from `0-9a-f`, so **~1.8%
  contain a run of 13+ digits and ~0.6% (1 in 166, measured over 300k draws) additionally embed a
  Luhn-valid window** — those are stored masked, and the test's lookup by the raw key finds nothing.
  That is a per-run lottery on a random id, which is exactly why it "failed in a full run and passed 6/6
  in isolation". Isolation was never the variable.
- The interceptor's own comment states that "system-minted values carry no maskable shape and pass
  byte-identical". **That assumption does not hold for hex ids.** In production roughly one audit row in
  166 whose subject is a hex id loses the value linking it to its record. Whether the detector should
  exempt system-minted id shapes is **spec 14's** decision — so the behaviour is now *pinned by tests*
  (`SensitiveDataMaskFalsePositiveTests`) and raised, rather than quietly "fixed" by weakening a
  fail-closed PAN detector.
- Fix applied at the test level: `TestIds.New(prefix)` mints hex ids interleaved with a letter every four
  characters, so no digit run can reach 13. Pinned by a 5,000-draw test asserting both that they survive
  sanitisation byte-identical **and** that they stay unique, plus a test asserting real PANs (plain and
  `4-4-4-4`) are still masked.
- The detector is narrower than the "13+ digits" its summary implies: it window-scans for a Luhn-valid
  13–19 sequence. My first pinning test used a non-Luhn digit run and correctly did **not** mask.
- The **preorder** self-poisoning test is a genuinely different mechanism (a bounded 500-row
  cross-tenant sweep over unbounded accumulated state) and is untouched here.

## P4·Phase 9 — Update `docs/frontend-api-wiring.md` (§11–§12)

The spec-15 tenancy landing, the two rules now genuinely held, the waitlist/verification/create rows,
and G2/G4 which appeared nowhere.

**Automated verification**
- [x] All lanes green: web (**292**) · api (**1176**) · integration (**418 passed, 2 skipped, 0
      failed**, twice consecutively) · langgraph (**1007**) · all lints · all builds

### Discoveries

- §11's per-screen rows were edited in place rather than appended to, so the table cannot be read as
  still-true-as-written; §12 carries the narrative.
- Each gap-audit finding was **marked up, not deleted** — carrying its status (`✅ CLOSED`,
  `◐ HALF-CLOSED`, `🟠 STILL OPEN`, `🟠 OUT OF SCOPE`) so the trail from finding to fix survived.
- The audit's own two mistakes were recorded: the G14 diagnosis, and the false-green `typecheck` it
  never caught because the target it used was a no-op.

## P4·Stage 6 — regression diff against the pass-2 baseline

| Baseline item | Verdict | Evidence |
|---|---|---|
| 2·B1 five list queries + `loadFailures` + demo-only-on-`undefined` | ✅ unchanged | all five hooks present; `loadFailures` still 5 entries; the `data === undefined` fallback rule intact at 4 sites |
| 2·B3 `dismissalKey` = `id\|status ?? reason`, state-aware dismissal, bounded set | ✅ unchanged | `ConsolePage.tsx:100-102, 325-331` byte-identical |
| 2·B4 transcript purpose gate, `staleTime: Infinity`, no refetch on focus/reconnect, live-session-only | ✅ unchanged | `features/hooks.ts:70-73` |
| 2·B5 `useRemoveFromWaitlist` optimistic-with-rollback + its 2 tests | ✅ untouched | no change to `onMutate`/`onError`/`splice` logic |
| 2·B6 canon.v1 hash binding, attempt-identity reuse, `retry: false`, cancel wording, reschedule gate | ✅ unchanged | `intent_payload_hash: payloadHash`; `retry: false`; `RESCHEDULE_FULFILMENT_SUPPORTED = false` |
| 2·B7 gate-before-submit, in-memory grant, match on `code` not `title` | ✅ unchanged | `setCancelAwaitingGrant(id)` before submit; `body.code === "verification_required"` |
| 2·B8 consent-policy + catalog live panels | ✅ unchanged | `SettingsScreen.tsx:339-340` |
| 2·B9 header set; shim subordinate to a verified context; `TenantGuard` fail-closed | ✅ unchanged | 8 `headers.set` calls identical; guard intact; **registration** gated, ordering not |
| 2·B10 suites green | ✅ improved | web 221 → **292**; api 1170 → **1176**; integration 413/1-failed → **418/0-failed** twice; langgraph **1007** unchanged |

**Intentionally changed** (each traced to a phase): 2·B2 calendar dates (P4·1); 2·B3 queue fabrications
+ button label (P4·2); 2·B4 transcript error path and thread `status`/`prov` (P4·2); 2·B6 create
`identityRef` and the cancel pre-read grant (P4·4, P4·6); 2·B8 badges on the three unbadged panels
(P4·3); 2·B9 shim registration (P4·7).

**Invariants re-checked:** Inv-2/3 (canonical hash unmoved — pinned by test), Inv-11 (grants still
server-issued, in-memory, pre-submit), Inv-12 / spec-14 minimisation (**strengthened**), Inv-13
(**strengthened** — shim no longer reachable in production), Inv-16 (**strengthened** — six classes of
fabricated assertion removed). None weakened.

**Deviations from the plan, both recorded rather than silent:**
1. `apps/web/project.json` typecheck target fixed (P4·1) — outside the blast radius, but the plan's own
   verification depended on it.
2. `latest_offer_id` added to the console projection + api-contract regenerated (P4·5).

## Part 4 review

One round: `/code-review xhigh` over the 9-phase range — **15 findings, all real, none refuted**, fixed
in `0cc7431`. Round 5 of the review record. The two severe ones both re-introduced the class of defect
this part set out to remove, from the same root cause: a value outliving its context by one render.
Both are now scoped-to-owner and compared at **render** (`features/bookings/scoped.ts`).

---

# Part 5 — Console truthfulness gap closure, R2–R8 (2026-08-04)

**Base:** `77b4fd8`. Make the two rules **actually hold across the whole surface**, not just the ten
screens — and add the page-level test that would have caught these, and will catch the next ones.

## Blast radius

**Intentionally changed**

| File | Change |
|---|---|
| `console/Sidebar.tsx` | operator identity → config-derived + demo badge |
| `console/Header.tsx` | workspace button → tenant-derived + demo badge |
| `screens/MoreScreen.tsx` | live waitlist count via prop; badge unbacked rows; drop fabricated money + campaign badge |
| `pages/ConsolePage.tsx` | pass identity/counts/resources/catalog services down; drop `campCount` literal |
| `drawers/CreateBookingDrawer.tsx` | remove deposit panel; remove staff picker; services from live catalog |
| `drawers/RescheduleDrawer.tsx` | remove staff picker |
| `screens/TodayScreen.tsx` | `resources` becomes a prop; single lane for live data |
| `features/bookings/adapters.ts` | stop importing `STAFF`; lane id becomes a constant |
| `features/bookings/buildSubmission.ts`, `submit.ts` | `verification_level` → `"unverified"` |
| `features/customers/adapters.ts` + `screens/CustomersScreen.tsx` | flag + explain the operator row |
| `features/conversations/adapters.ts` + `data/console.ts` | `email` channel becomes representable |
| `screens/AnalyticsScreen.tsx` | drop the `WEEK.today` accent |
| `data/console.ts` | delete dead `DAY_OPTS`; add `email` to `Channel`/`chMeta` |
| **new** `pages/ConsolePage.test.tsx` | page-level regression test |

**Must stay byte-for-byte the same:** `api/client.ts`, `api/problem.ts`, `api/config.ts` resolution
order, `api/queryClient.ts`; `api/canonicalHash.ts` and the `canon.v1` corpus (the payload *shape* does
not change, only the `service_id` **value**); `features/verification/**`,
`features/bookings/mutations.ts` cancel/offer flows; `features/bookings/week.ts`, `scoped.ts`,
`identity.ts`, `useOperationStatus.ts`; **all of `apps/api`**; **all of `apps/langgraph`**;
`libs/*/generated/**`.

## Two judgment calls made explicitly

1. **Resource lanes — collapse, not badge.** The grid rendered four invented people and stacked every
   live booking in lane one. Badging that would leave three empty lanes named after people who do not
   exist. The house ruling is *omit*, so `TodayScreen` renders **one unnamed lane**. Reversible:
   `resources` becomes a prop, so a real resources endpoint later passes real lanes with no further
   change.
2. **Empty catalog blocks booking, it does not fall back to mock.** Rule 1 says a successful-but-empty
   response is a real answer. If the venue's catalog is empty there is nothing bookable, and submitting
   a mock `service_id` is the exact defect being closed. The mock list is used **only** for labelling
   pre-existing rows, never for submission.

## Phases

**P5·Phase 1 — Shell identity and the More screen**
`Sidebar` / `Header` / `MoreScreen` render `getActor()` / `getActorRole()` / `getTenantId()` instead of
"Maya Chen" / "Bella Nails · Owner", each with a badge explaining there is no profile endpoint.
`MoreScreen` takes `waitlistCount` as a prop from live data; the deposits row drops its money sub-label
and is badged; the campaigns row drops `badge: 1`. `ConsolePage` deletes `const campCount = 1` — a nav
badge is a claim that something needs attention, and demo data must not make it.

**P5·Phase 2 — Create drawer: deposit, live catalogue, verification level**
Delete the deposit panel from the form body. Service options come from `useCatalogItems()`:
loaded-and-non-empty → real `item_id`s; not-loaded / empty / errored → the existing empty state,
submission disabled. `verification_level: "unverified"`.

**P5·Phase 3 — Resource dimension**
`TodayScreen` takes `resources: Resource[]`; `STAFF` import removed; `yToStaffIdx`, `gridH`, `staffY`,
label column, row stripes and card lookups all read the prop. `ConsolePage` passes a single lane. Both
drawers' staff pickers removed. `adapters.ts` uses a module constant lane id.

**P5·Phase 4 — Customers operator row and small assertions**
`customers/adapters.ts` flags the row whose `customer_ref === getActor()`; `CustomersScreen` renders it
with an explanatory chip instead of as a guest. `data/console.ts` adds `email` to `Channel` + `chMeta`
and deletes dead `DAY_OPTS`. `conversations/adapters.ts`: `email` maps to `email`. `AnalyticsScreen`
drops the `WEEK.today` accent.

**P5·Phase 5 — `ConsolePage` render test (the gap that let all of this through)**
New `pages/ConsolePage.test.tsx` following the `mutations.test.tsx` provider pattern, pinning: a failed
list load names the failure and does **not** render a confident empty screen; a successful-but-empty
response renders empty, **not** demo rows; demo-backed surfaces render a `Demo data` badge; the shell
renders the configured actor/tenant; no fabricated nav badge.

**Automated verification (all phases)**
- [x] `bun nx typecheck web` · `bun nx lint web` · `bun nx test web` — new tests pass, all 302 existing
      stay green
- [x] `submission.test.ts` stays green (payload shape and `canon.v1` hashes unchanged)

### Discoveries

**`verification_level` was never stored as sent.** `"staff_confirmed"` is not a member of
`ConfirmationMappings.MapVerificationLevel`'s vocabulary, and `SubmitConfirmedIntentValidator` checks the
field for `NotEmpty()` only — so it fell through `_ => VerificationLevel.Unverified` and was **stored as
`unverified`**. The console had been asserting an assurance level the platform discarded. Sending
`unverified` makes the wire match the storage and is independently the truthful answer for a staff
booking. `IsKnownVerificationLevel` exists precisely to stop this and is wired into the **waitlist** edge
validator, not the booking one. Tightening the booking validator would break the langgraph lane, which
sends `"none"` / `"sms"` / `"session"` across ~20 test sites — none ratified. Recorded as **R15** for
spec 04; not fixed here.

**`duration_minutes` is safely omissible.** `BookingRequest` has `"required": ["operation"]` only, and
the C# validator has no rule for duration. Omitting it when the catalog states none is correct, and
better than defaulting: a default would durably record how long the venue is booked for on the strength
of a guess.

**A regression I introduced, then fixed.** Making `services` the live catalog changed its array identity
on every refetch, and `useCatalogItems` inherits `refetchOnWindowFocus`. With `services` still in the
reset effect's dependency array, **alt-tabbing away and back wiped the open create form** and re-minted
`attemptKey`, throwing away the operation identity and idempotency key the form was about to submit
under (Inv-2/3). It had been inert only because `services` was previously stable `useState`. Fixed with
a `servicesRef` + a `chooseInitialService` callback + a catch-up effect guarded on `svc === ''`. Pinned
by two tests.

## Part 5 reviews

Two rounds (5 defects, then 7 including one that **hid real data** — the grid framing its columns from
mock operating hours, positioning a real 20:30 booking outside the visible columns). Round 6 of the
review record. `STAFF`, `staffById`, the `Staff` interface, `WEEK` and `DAY_OPTS` were all dead
afterwards and were deleted, so the next reader cannot reintroduce them.

---

# Cross-cutting requirements (all parts)

- **Tenant scope (Inv-13):** every request carries a tenant; every new endpoint calls
  `TenantGuard.RequireTenant`; query keys are tenant-rooted; each new handler gets a cross-tenant test.
  Part 4 *narrows* tenant resolution, never widens it.
- **Idempotency (Inv-2/3/4):** one key per logical attempt, minted before the first call and reused on
  retry; 409 conflict surfaced, never silently merged. The canonical payload's *shape* never changed
  across the arc, so key↔payload binding is unaffected and the `canon.v1` corpus stays green.
- **Authorization (Inv-11):** mutations needing a grant carry `X-Grant-Id`; grants are server-issued,
  in memory only, obtained before the first submit. The client never fabricates authorization.
- **Queue safety (Inv-5):** the cancel step is duplicate-safe — the `WorkflowStep` is create-or-load,
  every transition is version-guarded, and an already-`Cancelled` booking short-circuits without a
  second provider call.
- **Inv-7:** ambiguous provider outcomes go to `Reconciling`, never a blind retry.
- **Inv-8:** the booking transition and the notification outbox row commit in the **same** transaction,
  so the notification can never precede the state.
- **Truthful outcomes (Inv-16):** the UI renders `OperationStatusView` from the server; "confirmed" only
  for `booked`; every surface either states a fact the platform records, or is badged, or is omitted.
- **PII/card (Inv-12):** no card field anywhere; grants in memory only; no PII in query keys; `identity`
  carries the operator, never the guest.
- **Inv-1:** no UI control may imply authority the platform does not grant (the removed staff picker and
  deposit panel are exactly this).
- **PostgreSQL/migrations:** none, anywhere. Filter on existing indexed columns; bound every list with a
  validated `limit`.
- **Generated types:** import via `@cordia/...` only; never hand-edit generated files; edit the
  Pydantic/.NET source then regenerate.
- **File encoding:** every new/edited `.cs` file is UTF-8 **with BOM**, or `bun nx lint api` fails.
- **FluentValidation** is the sole required-field enforcement for new queries (no `[Required]`).
- **Commands:** one `bun nx` at a time; tee output to `.tmp/`, never pipe to `tail`.
- **A11y:** new inputs labelled and keyboard-operable; errors associated with their field; loading/error
  states not colour-only.

# Final state

| Lane | Start of arc | End of arc |
|---|---|---|
| `bun nx test web` | 1 (`router.test.tsx`) | **302+** (19+ files) |
| `bun nx test api` | 1170 | **1176** |
| `bun nx test-integration api` | 413 / 1 failed / 2 skipped | **419 passed, 2 skipped, 0 failed** |
| `bun nx test langgraph` | 1007 | **1007** (unchanged) |
| OpenAPI contract | 39 paths | 44 paths / 50 operations |
| lint / typecheck / build | green (typecheck was a **no-op** — fixed in P4·1) | green on all three lanes |

**Still open, all spec-level:** R1 (modify/reschedule fulfilment — spec 06/08), R9 (guest identity on a
staff-made booking — spec 04/08/12), R10 (resource model — spec 06/12), R11
(`SubmitModificationInput` payload — spec 08), R12 (PAN detector — spec 14), R13 (cancel `reason_code`
vocabulary — spec 14), R14/R15 (`verification_level` vocabulary — spec 04). Plus the structural work
that was always out of scope: routing and deep links, pagination, a projected indexed `starts_at`, real
auth, operator actions on operations, waitlist enrolment and conversation writes.


---

<!-- Consolidated from 2026-08-05_three-wiring-gaps.md (2026-08-06): the file below was merged into this record verbatim. -->

# Plan — Close the three remaining frontend-wiring gaps

**Date:** 2026-08-05 · **Branch:** feat/ui-wire · **Scope:** `apps/web` ONLY (backend read-only)
**Research:** `../research/2026-08-05_three-wiring-gaps.md` (baseline audit + server contracts)

Approval note: scope, constraints and deliverables were fixed in detail by the user's /goal
directive (apps/web only; stop on backend gaps; standing truthfulness rules; verification
commands; doc deliverable; then /code-review xhigh). That directive is the Stage 3 approval
for this plan. No spec Open Decision is triggered: no new PII field, no card data, no auth
change, no contract this work owns.

## How this maps onto Cordia

| New concept | Existing Cordia artifact it reuses | Consequence |
|---|---|---|
| Waitlist enrol call | `features/waitlist/api.ts` + `fetchApi {actor:true}` | same audited-write path as cancel/accept/decline |
| Enrol mutation | `bookings/mutations.ts` (waitlist mutations already live there) | `onSettled` invalidates `queryKeys.waitlist.all()`; `retry:false` inherited |
| Enrol drawer | `CreateBookingDrawer` pattern (`Drawer`, `Field`, three-state services, inline `ErrorState`) | consistent form language; live catalog as the service source |
| Channel choices | `@cordia/shared-schemas` `Channel` enum | ratified vocabulary, not invented strings |
| Operation follow-up calls | new `features/operations` write fns via `fetchApi` (`actor`/`reasonCode` options already exist) | headers exactly as the server's `OperatorActionBehavior` demands |
| Per-status eligibility | `operations/adapters.ts` (already owns status vocabulary) | queue rows already carry the raw 16-value status |
| Local ack vs server action | existing `dismissalKey` local clear stays untouched | the distinction the goal demands is preserved, not merged |
| Catalog lifecycle calls | new `features/menu/api.ts` via `fetchApi` | D4 state machine surfaced action-by-action |
| Catalog session tracking | `sessionStorage` pattern from `bookings/identity.ts` | last **server-confirmed** status only; never a guessed one |
| Role gating for menu | `features/session/identity.ts` (`sessionIdentity()`) | no button that 403s for certain; server stays the enforcer |

## Spec ownership & frozen decisions

| Contract | Owner | This work is | Constraint |
|---|---|---|---|
| Waitlist enrollment/offer lifecycle, auto-book consent | 11 | referencer | §20: silent auto-book prohibited; console cannot mint `consent_evidence_id` → auto-book is **not offered** |
| `operation_status` (16) / normalized (9) | 06 (D1/D2) | referencer | eligibility decided from the list's raw status only |
| resume/resolve operator spine | 06/08 | referencer | X-Actor/-Role/-Reason mandatory; resolution ∈ {booked, failed_final} |
| `catalog_status` | 11 (D4) | referencer | transitions offered exactly per the machine |
| `X-Tenant-Id` shim | 15 | referencer | already in `fetchApi` |
| Actor identity/role | (dev shim) | referencer | role is config truth; never fabricated per-call |

## Blast radius (from the baseline audit)

**Intentionally changing:**
- `features/waitlist/api.ts` (+`enrollGuest`), `bookings/mutations.ts` (+`useEnrollGuest`, `enrollFailureMessage`)
- `features/operations/api.ts` (+4 write fns), `features/operations/adapters.ts` (+eligibility helpers), new `features/operations/mutations.ts`
- new `features/menu/api.ts`, `features/menu/catalogs.ts`, `features/menu/mutations.ts`
- new `components/platform/drawers/EnrollWaitlistDrawer.tsx`
- new `components/platform/settings/MenuCatalogPanel.tsx`; `SettingsScreen.tsx` (mount panel)
- `QueueScreen.tsx` (per-status server actions, resolve/resume inline forms)
- `ConsolePage.tsx` (enrol drawer state + waitlist header primary + queue mutation wiring)
- `docs/frontend-api-wiring.md` (deliverable)
- new tests for each flow incl. failure paths

**Must stay byte-for-byte / behaviourally identical:**
- Local clear/dismiss semantics (`dismissalKey = id|status`, undo, pruning)
- Waitlist removal optimistic flow + offer accept/decline flows + their tests
- The five read queries, `loadFailures` banner, demo-fallback rules, `loaded` gating
- Create/cancel/reschedule flows, canon.v1, attempt identity
- Settings' consent + published-catalog read panels and all existing badges
- `apps/api`, `apps/langgraph`, `libs/**`, `docs/specs/**` — untouched

**Type-generation blast radius: none.** No Pydantic/OpenAPI change; generated dirs read-only.

## What we're NOT doing

- No auto-book enrolment (no consent evidence capture exists — spec 11 §20).
- No `deposit_terms` (spec 09 deferred), no `verification_level` claim (R15), no `identity_ref`.
- No catalog **edit** action (draft item ids unreadable — backend gap #2).
- No file upload for menus (no endpoint — backend gap #3).
- No polling/guessing of catalog extraction progress (no read — backend gap #1).
- No new API endpoints, no server changes, no role fabrication to pass the owner check.
- No new packages.

## Phases

### Phase 1 — Waitlist enrolment
1. `enrollGuest` in `waitlist/api.ts`: POST body {customer_ref, location_id, service_id,
   notification_channel, party_size, offer_lifetime_seconds, acceptable_from, acceptable_to},
   `actor: true`. Response treated as unknown.
2. `useEnrollGuest` + `enrollFailureMessage` in `bookings/mutations.ts`; onSettled →
   invalidate waitlist. **Never auto-retried**; manual retry only after the list refetch
   (no idempotency key server-side — the drawer copy says so).
3. `EnrollWaitlistDrawer`: guest reference (required; recorded verbatim), service from live
   catalog (3-state), location (required text; datalist from live enrollments' `location_id`s),
   party size, acceptable window (to > from client-mirrored), notification channel (ratified
   enum), offer lifetime select. Auto-book explicitly not offered (copy explains §20).
   Inline `ErrorState` on failure (`ApiValidationError` renders field messages).
4. `ConsolePage`: drawer state; Header primary "Add to waitlist" on the waitlist screen.
5. Tests: api fn (path/headers/body), mutation success + failure, drawer validation gating +
   failure path, no-catalog states.
- **Automated:** `bun nx test web` ✅ 370/370 · `bun nx lint web` ✅ 0 warnings (typecheck+build also run early: ✅)
- Discoveries: the generated `EnrollWaitlistRequest` marks `auto_book`/`deposit_terms` as
  required (OpenAPI-generation artifact) — body built as a literal with a documented cast,
  the `buildSubmission` precedent. `mutation.reset` is read through a ref in the drawer's
  reset effect (mutation object identity changes per render; repo has no eslint-disable
  precedent). `vi.mock` hoisting means the transport tests use `vi.importActual`.

### Phase 2 — Operator follow-ups on operations
1. `operations/api.ts`: `requestOperationCancellation(id)` (no headers),
   `resumeOperation(id, reasonCode)` (`actor:true`+reasonCode),
   `resolveOperation(id, {resolution, providerRef?}, reasonCode)`,
   `respondToAlternatives(id, response)`.
2. `operations/adapters.ts`: `TERMINAL_OPERATION_STATUSES` + `operationActions(status)` →
   {canCancel, canResume, canResolve, canRespondAlternatives} per the audited guards.
3. `features/operations/mutations.ts`: four mutations; onSettled invalidates operations
   (+bookings for resolve/alternatives); named failure messages (409 → "the operation moved
   on — refreshing").
4. `QueueScreen`: per-status server actions beside the untouched local Clear/Dismiss;
   resolve = inline form (booked+provider_ref | failed_final, reason required, never
   defaulted); resume = inline reason form; alternatives = two buttons; per-row busy.
   Success toasts state exactly what the server answered (cancel = *requested*, not done).
5. Tests: eligibility for all 16 statuses; api fns (headers/bodies); mutation success/failure;
   QueueScreen renders the right actions per status, resolve gating, failure path.
- **Automated:** `bun nx test web` ✅ 396/396 · `bun nx lint web` ✅ (typecheck+build also ✅)
- Discoveries: mocked-fetch tests must mint a fresh `Response` per call (a body reads once);
  react-query `isError` needs `waitFor` after a rejected `mutateAsync`. The resume confirm
  button intentionally reuses the opener's label — the opener is replaced by the form, so
  only one exists at a time.

### Phase 3 — Menu catalog management
1. `features/menu/api.ts`: upload/review/approve/publish (all `actor:true` + fixed per-action
   `reasonCode` e.g. `menu_upload`); no edit (backend gap).
2. `features/menu/catalogs.ts`: D4 legal-next-action helper; per-tenant sessionStorage of
   {catalogId, lastConfirmedStatus, versionLabel, updatedAt} updated **only from server
   responses**.
3. `MenuCatalogPanel` in Settings: role gate (actions only when session role is `owner`;
   otherwise an explanation, no 403-certain buttons); upload form (artifact **reference**,
   honest copy); tracked catalogs with last-confirmed status + only legal actions; review may
   409 while extraction runs (named, retryable — status genuinely unknowable, said so on the
   panel); publish copy: supersedes the live catalog and changes what is bookable; publish
   success invalidates `settings.catalogItems` (shared with the create drawer).
4. Tests: api fns (headers incl. role/reason), catalogs helper (legal actions per status,
   storage update-from-response-only), panel (role gating, upload success/failure, 409 review
   path, publish invalidation).
- **Automated:** `bun nx test web` ✅ 415/415 · `bun nx lint web` ✅ (typecheck+build also ✅)
- Discoveries: the panel's tracked list can never show `draft_ready` (no response ever
  confirms it — only review's `in_review` does), so review is offered from the pre-draft
  statuses with an explicit "may be refused" caveat. Version labels survive transitions by
  upsert (transition responses carry no label). Role gate compares the session role
  case-insensitively; the server stays the enforcer.

### Phase 4 — Docs + full verification
1. Update `docs/frontend-api-wiring.md`: correct stale claims (§1 "not connected", §4
   verdicts, §10 waitlist "No list endpoint"); add a dated "What is wired as of 2026-08-05"
   section + a separate **Backend gaps** list (the five from the research doc, each with the
   specific reason).
2. Full gate, one at a time, teed to `.tmp/`:
   `bun nx test web` → `bun nx lint web` → `bun nx typecheck web` → `bun nx build web`.
3. Stage 5: `/code-review xhigh`; fix findings; Stage 6 regression diff vs the baseline.
- Do **not** commit.
- **Status:** ✅ Docs updated (§11 wired list, §12 backend gaps — six, with reasons).
  ✅ Full gate green: 425/425 tests · lint 0 · typecheck · build. `/code-review` proved
  user-invocation-only in this harness, so Stage 5 ran as a 20-agent review workflow
  (4 dimensions × Sonnet, 2 adversarial verifiers per finding): 5 confirmed findings
  (2 blockers) + 1 contested, all fixed and pinned by new tests — see
  `../reviews/2026-08-05_three-wiring-gaps.md`, which also carries the Stage 6 regression
  diff (clean; no unexplained deviation). The official `/code-review xhigh` remains for the
  user to run.

## Cross-cutting requirements

- Tenant scope: every call rides `fetchApi` (Inv-13). Actor attribution on every audited write.
- Idempotency: none of these endpoints takes a key — so **no silent retry anywhere**; enrol
  documents the duplicate risk; queue/menu repeats are state-machine-guarded server-side.
- Inv-16 everywhere: report only what the server answered; no local row mutation on
  follow-ups (refetch decides); catalog status only as last server-confirmed.
- Failure by name with retry; empty-after-load is rendered as a real answer.

## Package notes
No new packages.


---

<!-- Consolidated from 2026-08-05_console-booking-guest-identity.md (2026-08-06): the file below was merged into this record verbatim. -->

# Plan: a real guest identity on a console-made booking

**Research:** `thoughts/shared/research/2026-08-05_21-07-32_console-booking-guest-identity.md`
**Base commit:** 77b4fd8 · **Branch:** feat/ui-wire

## Gate decisions (user, 2026-08-05)

1. **Guest home** — nested `customer: CustomerRef | None` on `BookingRequest`, reusing the
   already-ratified `CustomerRef` model. Console populates `customer_id` + `phone_e164` only,
   never `display_name`.
2. **Notification destination** — replace the `NotificationRecipientResolver` stub minimally so
   acceptance #2 is genuinely delivered.
3. **Operator tools** — wire all three (DLQ replay, model-trace viewer, preorder recovery).
   These are **web-side** wiring of endpoints that already exist, so "backend in scope for this
   one change only" still holds.

## How this maps onto Cordia

| New concept | Existing artifact it reuses | Consequence |
|---|---|---|
| Guest on a booking | `CustomerRef` (`state.py:162`) — already ratified, already generated into `SharedTypes.cs` | No new type. Mirrors `CanonicalBookingDraft.customer` exactly, so both lanes agree. |
| Guest in the canonical payload | spec 04 §4.2 `customer_ref` / `notification_destination` | Implements ratified-but-missing fields; not a new contract. |
| Phone lookup | `CanonicalBookingPayload.PhoneNames` + recursive `TryFindProperty` | **Zero reader changes** — nested `customer.phone_e164` is already found. |
| Booking aggregate | `FinalizeBookingCommandHandler.BuildBookingAggregate` | Already reads `customer_id`/`phone_e164` recursively — starts producing a real `Booking.CustomerId`. |
| Notification destination | `INotificationRecipientResolver` + `IBookingIntentRepository` | Stub → reads the operation's confirmed revision; falls back to today's hash when no guest. |
| Optionality | canon.v1 omits null/`{}`/`[]` (`CanonicalRequestHasher.cs:91-102`) | Existing payloads hash identically; all existing corpus vectors stay valid. |

## Spec ownership & frozen decisions

| Contract | Owner | Status here |
|---|---|---|
| `IntentRevisionPayload` field set, canon.v1 | **04** | **Owned** — may change (implementing §4.2) |
| canon.v1 algorithm | **04** | Untouched; corpus **grows only** |
| Recipient resolution | **10** (§13 open decision) | **Changed by explicit user decision** — stub only |
| Redaction / retention / `audit_events` | **14** | **Referenced — must not change** |
| All DDL / pg-enums | **12** | **No new table, column, or enum** |
| Manage-booking discovery | **08** | Reader unchanged; its test stops being vacuous |

Invariants in play: **Inv-1** (confirmation binds a hash), **Inv-2/3/4** (idempotency,
atomic acceptance), **Inv-12** (PAN/CVV — unaffected), **Inv-13** (tenant scope).

## Phases

### Phase 1 — RED: prove the drop-and-422
- Integration test: POST `/api/bookings` whose `canonical_payload` carries
  `customer.phone_e164`, hash computed over the JSON **as sent** (what `buildSubmission.ts` does).
- MUST fail 422: the generated `BookingRequest` has no catch-all, so the server drops
  `customer`, re-serializes without it (`SubmitConfirmedIntentCommandHandler.cs:58`), and the
  recomputed hash differs.
- Second RED test: submit → then `GET /api/bookings?phone_e164=` → not found.
- Automated: `bun nx test-integration api`

### Phase 2 — Pydantic contract + regeneration
- `state.py`: add `customer: CustomerRef | None = None` to `BookingRequest`. Move `CustomerRef`
  above `BookingRequest` (it is currently defined after it).
- Regenerate: `langgraph:generate-schemas` → `shared-schemas:generate-types` → `api-contract:generate-types`.
- Phase 1 tests go GREEN with **no handler change**.
- Automated: `test langgraph`, `lint langgraph`, `test api`, `test-integration api`, `lint api`

### Phase 3 — canon.v1 corpus + the sanitiser proof
- Add vectors to `tools/config/canon-v1-test-vectors.json` exercising `customer` — expected
  values produced by the **authoritative C# hasher**, then proven identical in TS and Python.
- Verify `tools/config/*.json` is in the nx cache inputs for all three lanes (known trap).
- **Trap-2 proof test**: round-trip a payload containing a phone through `POST /api/bookings`,
  re-read the stored `canonical_payload` from the DB, re-hash it, assert it still equals
  `payload_hash`. Proves no sanitiser mutates it (the interceptor's scope is `Entries<AuditEvent>()`).
- Automated: `test api`, `test web`, `test langgraph`

### Phase 4 — Notification destination (spec 10 stub → real)
- `NotificationRecipientResolver` resolves the operation → its confirmed revision → the guest;
  returns that as `RecipientRef` with the existing channel/locale defaults. Falls back to the
  current deterministic hash when no guest is present, so **no existing behaviour regresses**.
- Duplicate-delivery test (same notification identity twice → one row) and a cross-tenant test.
- Automated: `test api`, `test-integration api`

### Phase 5 — Console sends the guest (web)
- `buildSubmission.ts`: optional `customer` on `BookingDraft`; emit `customer` only when a phone
  is present; **never** emit `display_name`.
- `CreateBookingDrawer`: capture guest phone; keep the name local-label-only.
- Update `submission.test.ts` (currently asserts `customer_ref` is excluded) deliberately.
- Automated: `test web`, `lint web`, `typecheck web`, **`build web`**

### Phase 6 — De-vacuous the phone-lookup tests
- Rewrite `ManageBookingReadEndpointTests.SeedManageableBookingAsync` to seed a payload the real
  submission path can actually produce (drop `booking_id`/`business_name`/`booking_date`/`approx_time`).
- Same for `BookingReadRepositoryTests`.
- Automated: `test-integration api`

### Phase 7 — Operator tools (web only)
- DLQ replay → `POST /api/dead-letter-messages/replay` (needs `X-Actor` + `X-Reason-Code`)
- Model-trace viewer → `GET /api/model-traces/{traceId}`
- Preorder recovery → `POST /api/preorder/sagas/{sagaId}/convert-standalone` and `/cancel`
- Automated: `test web`, `lint web`, `typecheck web`, `build web`

### Phase 8 — Docs
- Update `docs/frontend-api-wiring.md`. **Do not commit.**

## Blast radius

**Intentionally changing**
- `BookingRequest` (Pydantic + all generated derivatives) — additive optional field
- `NotificationRecipientResolver` — stub → real, with fallback
- `Booking.CustomerId` via `BuildBookingAggregate` — now a real guest handle when present
- `buildSubmission.ts` / `CreateBookingDrawer` / `submission.test.ts`
- The two vacuous test fixtures
- `docs/frontend-api-wiring.md`

**Must stay byte-for-byte the same**
- `CanonicalRequestHasher` (all three languages) and every existing corpus vector
- `SubmitConfirmedIntentCommandHandler` election/disposition logic
- `CanonicalBookingPayload` / `BookingReadRepository` matching logic
- `AuditEventSanitizationInterceptor`, `SensitiveDataMask`, `RetentionSweepHandler`
- `SendNotificationCommandHandler` state machine and dedup
- Every EF migration; no new DDL

## What we're NOT doing

- No new table, column, or pg-enum (spec 12 owns DDL).
- Not implementing spec 04's flat `customer_ref` / `notification_destination` names — the
  nested `CustomerRef` carries the same data and needs no reader changes.
- Not resolving spec 10's channel-preference / PII-minimisation open decisions — only the stub.
- Not touching the agent lane's dialog flow; `BookingDraft.customer` already exists and its
  projection to `BookingRequest` is what changes.
- Not sending `display_name` from the console (commit 882042c deliberately removed that).

## Known cost, accepted at the gate

The guest phone lands in `booking_intent_revisions.canonical_payload`, which is **immutable and
replayed**, and `RetentionSweepHandler` only anonymises `ConversationSession.CustomerRef`. So the
phone is **not reachable by the retention sweep**. This is a real erasure gap, accepted
deliberately; it is a spec-12/spec-14 retention question, not an Inv-12 breach.

## Discoveries and Notable Information

**Phase 1 (RED) — the drop is total, and provable.** All 6 tests failed 422. The decisive
evidence: three tests sent *different* phones yet the server computed the *identical* hash
`38a2efb7c201d1ae32fa31027012d47bdd7525fb68899042c45a90ed5559a577`, proving `customer` was
dropped wholesale before hashing rather than mangled. 419 pre-existing tests stayed green.

**Phase 2 — no handler change needed.** After regenerating, 5 of the 6 went green with zero C#
edits. `CustomerRef` had to move above `BookingRequest` in `state.py` (Pydantic resolves the
annotation at model-build time).

**Phase 3 — trap 2 is dead, proven not reasoned.** `StoredCanonicalPayload_StillHashesToPayloadHash`
round-trips three phones through the real endpoint, re-reads the jsonb, and re-hashes it —
including `+4539578000000808`, a **Luhn-valid 16-digit run**, exactly the shape the PAN detector
exists to catch. All three still equal `payload_hash`. The sanitising interceptor's scope is
`ChangeTracker.Entries<AuditEvent>()`; `BookingIntentRevision` never passes it.

Corpus 82 → 91. The four backward-compat vectors did not fail when first added — they already
hashed to `minimal_create`'s `61b504c7…`, which *is* the compatibility proof. The nx cache-input
trap from the canon.v1 memory is already fixed (`tools/config/*.json` appears in 3 named inputs).

**Phase 4 — the stub hid an unreachable branch.** Because it never returned null, the
`no_destination → InvalidRecipient` path in `SendNotificationCommandHandler` could never fire.
Splitting `RecipientRef` (tokenised, persisted) from `Destination` (real, never persisted) was
forced by `RenderedMessage.MinimizedRecipient`'s documented contract — "masked/tokenised
reference". Retry and reconcile had to re-resolve, or a resend would render a message with
nowhere to go.

**Phase 6 — two defects behind one vacuous fixture.**
1. *Fixed:* the fixtures baked `booking_id`/`business_name`/`booking_date`/`approx_time` into a
   create payload. Removing the first three still passes, because the operation row supplies
   `booking_id` and `starts_at` derives the other two.
2. *Blocked:* `ManageBookingCandidate` needs `booking_reference` **and** `business_name`.
   `business_name` has no source anywhere — no tenant/business entity, and `TenantContextView`
   carries no display name. Verified by seeding a fully-booked operation with a real `Booking`
   row: still `{"status":"none","candidates":[]}`. Inventing the table is a spec-12 decision, so
   this change stopped, as the brief required.

### Acceptance, stated plainly

| Criterion | Delivered? |
|---|---|
| Have a notification destination | **Yes** — resolved from the confirmed revision, for the agent lane too |
| Findable via `GET /api/Bookings?phone_e164=` | **No** — guest half done and matchable; candidate rendering blocked on a missing `business_name` source (spec 12) |

### Stage 5 — /code-review xhigh, and what it changed

The review returned 15 findings. Four were real defects in **this** change; all four fixed:

1. **Inv-9 dedup break.** Deriving `RecipientRef` from the destination changed every
   `notification_id`, so a message redelivered across the deploy would miss logical dedup and
   notify the guest twice. **Reverted to the operation-derived token** — the operation id is
   already in the identity, so the destination-derived variant bought nothing.
2. **Greedy destination parse.** The resolver used `CanonicalBookingPayload`'s recursive
   whole-payload search, whose key list includes a bare `phone` beside `business`/`venue_name`
   support — a venue phone could capture the send. **Now reads the `customer` node only.**
3. **Write-only `Destination`.** Nothing consumed it. **The SMS adapter now uses it** (and never
   logs it — it is the raw number).
4. **Retired-service submission** (`CreateBookingDrawer`): `service` fell back to
   `serviceOptions[0]` while `submit()` still sent `svc`, so a refetch retiring the pick would
   durably record one service's id with another's duration. **Fallback removed**, submission
   blocked, `role="alert"` shown, regression test added.

**One review finding I fixed and then deliberately un-fixed.** The reviewer was right that the
`no_destination → InvalidRecipient` branch is still dead. I tightened the guard to check
`Destination` — and it broke 5 integration tests, for a good reason: **waitlist offer/expiry
notifications carry an `op_wl_*` id with no `booking_operations` row**, and their destination
lives on `WaitlistEnrollment`, which this resolver does not read. The tightened guard
terminal-failed a working lane. Reverted, documented at the guard, in the docs, and pinned by
`NoDestination_StillSends_BecauseTheResolverAlwaysReturnsAHandle`. Closing it properly needs
spec 10 §13's recipient-store decision.

The remaining 10 findings are pre-existing issues in earlier `feat/ui-wire` work (catalog
`[]`-on-parse-failure, the 50-item cap, `SERVICES` label lookup, `QueueScreen` form reset,
`serve-log.mjs` Windows signal handling, `identity.test.ts` env stubbing, operator row in the
customer autocomplete, dead `campCount` prop, duplicate `dayWindow` export). Left untouched:
out of scope for this change and not caused by it.

### Final verification (all green)

Post-review, all lanes re-run:

`langgraph:generate-schemas` · `shared-schemas:generate-types` · `api-contract:generate-types` ·
`test api` **1197** · `test-integration api` **427 passed / 3 skipped** · `lint api` ·
`test langgraph` **1016** · `lint langgraph` · `test web` **447** · `lint web` ·
`typecheck web` · `build web`

Not committed, per the brief.


---

<!-- Consolidated from 2026-08-06_console-reconciliation-and-components.md (2026-08-06): the file below was merged into this record verbatim. -->

---
date: 2026-08-06
branch: feat/ui-wire
base_commit: 77b4fd8
research: thoughts/shared/research/2026-08-06_02-57-45_console-reconciliation-and-components.md
status: complete
review: thoughts/shared/reviews/2026-08-06_console-reconciliation-and-components.md
---

> **Outcome.** All phases shipped. Three deviations from the plan as written, each forced by
> something the plan could not have known:
> 1. **Phase 1 grew.** The plan assumed the command's `IdempotencyKey` was reachable over HTTP.
>    It was not — `EnrollWaitlistRequest` had no such field and the controller passed 15 of 17
>    arguments — so the API surface had to change (additive, nullable) and `api-contract` types
>    were regenerated.
> 2. **Two defects in concurrently-landed work had to be fixed** to reach green: the enrol
>    controller never forwarded `X-Actor-Role`/`X-Reason-Code` (every enrolment would 403), and
>    stale `Actor` validator rules on accept/decline turned a deny-audited 403 into an unaudited
>    400.
> 3. **Review and self-review found three more holes** in this work — a too-narrow request hash,
>    an expired-derived-key path that never actually released its fence, and a capacity panel
>    offering dimensions no booking can match. All fixed; see the review doc.

# Console reconciliation, waitlist idempotency, availability & five components

## How this maps onto Cordia

| New concept | Existing Cordia artifact it reuses/extends | Consequence |
|---|---|---|
| Enrolment replay liveness | `IIdempotencyRepository` election primitive (spec 04) + `WaitlistEnrollment.CancelledAt` | A **conditional** void, mirroring the repo's `VersionTokenUpdate` optimistic idiom — no new disposition, no DDL |
| Caller-supplied enrolment key | `EnrollWaitlistCommand.IdempotencyKey` (already exists, unreachable over HTTP) | Additive nullable body field; the console mints it the same way `AttemptIdentity` does for bookings |
| Availability read | `features/bookings/services.ts` (catalog → bookable) + the drawer's existing 3-state `services` contract | `toAvailableSlots` + `useAvailability`, **advisory only** |
| Slot declaration | `IOperatorActionRequest` owner-gated audited command | A minimal Settings panel, so the read is not a window onto an empty table |
| Five components | `components/ui/` named-export + `designSystem.test.tsx` rule-pinning convention | No new design system |

**Governing directive:** integrate with existing mechanisms; never add a parallel implementation.

## Spec ownership & frozen decisions

| Contract | Owner | Status here |
|---|---|---|
| Idempotency disposition | 04 | **referenced** — live-operation replay behaviour unchanged byte-for-byte |
| `AvailabilityMode` wording | 07 | **referenced** — console uses `informational` wording only |
| `waitlist_enrollments` / `capacity_slots` DDL | 12 | **untouched — no migration in this work** |
| Staff roles (`owner` ⊇ `operator`, GD-1) | 14/08 | **referenced** — `PermissionGate` mirrors `StaffRole.Satisfies` |

Invariants in play: **Inv-2, Inv-3, Inv-6, Inv-13, Inv-16**. Defect 1 is the Inv-16 breach this
plan closes.

## Blast radius

**Intentionally changing:**
`EnrollWaitlistCommandHandler`, `IIdempotencyRepository` (+ impl), `WaitlistController` enroll
action, `EnrollWaitlistRequest`; web `features/waitlist/api.ts`, `features/bookings/mutations.ts`
(enrol hook signature), `EnrollWaitlistDrawer`, `ConsolePage` (locations comment + availability
wiring), `CreateBookingDrawer` (phone field + availability), `BookingLookupPanel` (phone field),
`CatalogDraftEditor` + `SettingsScreen` (money), `VenueNamePanel` + `MenuCatalogPanel`
(permission), `ConversationsScreen` + `conversations/adapters.ts` (consent), `WaitlistScreen`
(countdown), `api/queryKeys.ts`, `features/hooks.ts`, `docs/frontend-api-wiring.md`.

**Must stay byte-for-byte the same:**
- The **live-operation** replay path (`WaitlistFlowTests` all three tests must pass unchanged).
- `normalisePhone` itself — formatting-only, never infers a country (docs §17).
- Booking submit/cancel canonical hashing and `AttemptIdentity` (Inv-2/3).
- Every screen currently on badged demo data stays badged.
- The five never-wire (C) endpoints stay uncalled.

**Out of bounds:** no DDL/migration; no Pydantic change; no auth work; no
`/api/availability/candidates` invention; no DepositStatusCard / HoldsPanel /
AvailabilityModeBadge / SlaBadge.

## Phases

### Phase 1 — API: enrolment replay liveness + accept a caller key
RED→GREEN. Add `TryVoidAsync(tenantId, key, expectedOperationId) → int`; move the replay
decision inside the transaction; on a same-hash hit load the enrolment and, when it is **not
live**, conditionally void the spent record and fall through to a fresh enrolment. Add
`idempotency_key` to `EnrollWaitlistRequest` and thread it.
- Automated: `bun nx test api`, `bun nx lint api`, `bun nx test-integration api`

### Phase 2 — Web: send the key; make locations honest
`enrollGuest` takes an attempt key and sends `idempotency_key`; correct the stale comment in
`api.ts`; correct the drawer hint and `ConsolePage` comment; warn (never block) on a location the
tenant has not used.
- Automated: `bun nx test web`, `bun nx lint web`, `bun nx typecheck web`

### Phase 3 — Web: the five components + consumer rewires
`PhoneInput` (shared "looks unnormalised" rule — **no country auto-prefix**), `MoneyAmount`
(minor units, never floats), `PermissionGate` (mirrors `StaffRole.Satisfies`, labelled a UI hint
not a boundary), `ConsentBadge` (+ adapter/screen wiring), `Countdown` (over `useTickingNow`).
- Automated: `bun nx test web`, `bun nx lint web`, `bun nx typecheck web`

### Phase 4 — Web: availability
`useAvailability` + `toAvailableSlots`; **advisory** guidance in the create drawer (empty ⇒ say
no slots are declared, never "unavailable"); owner-gated slot declaration in Settings so the read
has a source.
- Automated: `bun nx test web`, `bun nx lint web`, `bun nx typecheck web`

### Phase 5 — Docs reconciliation
Items 1–4 with the `CLOSED 2026-08-06` inline style; add C6 for `modifications`.

### Phase 6 — Review, regression diff, cache-free verification

## What we're NOT doing

Auth / web-origin `TenantContext` (§12 gap 1). Deposits (gap 2). Menu artifact upload (gap 5).
A resources/staff model for the schedule grid (gap 9 — availability slots carry `resource_id`,
but `ScheduledBookingView` still carries none, so the grid cannot map bookings to lanes).
`extract_failed` (blocked on D4). The `/api/availability/candidates` agent-lane gap.
Adding `mode` to `AvailabilityResult` (spec 07's contract to change, not the console's).

## Cross-cutting requirements

Tenant scope on every new read (Inv-13, keys rooted at tenant). No new side-effecting path
without idempotency (Inv-2/3). Owner-gated writes keep `X-Actor`/`X-Actor-Role`/`X-Reason-Code`
with **nothing preselected**. No new `.cs` file without UTF-8 BOM. One nx command at a time.

---

# Part 6 — Residual review findings, 8 items (2026-08-07)

# Plan — close the 8 residual review findings

**Research/baseline:** [`../research/2026-08-04_frontend-api-wiring.md`](../research/2026-08-04_frontend-api-wiring.md)
**Branch:** `feat/ui-wire` · **Lanes:** api (Application, Infrastructure, Domain, migration) + web
**Delivery-sequencing position:** hardening of Phases 3–5 work already shipped; no new vertical.

---

## How this maps onto Cordia

| Need | Existing Cordia artifact it reuses | Consequence |
|---|---|---|
| Recover an ambiguous waitlist notification | `notifications.correlation_id` (dead nullable column, migration `20260630123648`) + the ambient correlation already carrying `enrollment.EnrollmentId` | No DDL, no new table, no new identity. The column exists and the value already flows — it is only dropped by the raw INSERT. |
| Resolve a waitlist recipient without an operation id | New overload on the existing `INotificationRecipientResolver`, reusing `IWaitlistEnrollmentRepository.GetAsync` | Keeps one resolver as the single front door; no parallel resolution path. |
| Schedule rows carry the real reference | `BookingReadRepository.BaseRows()`'s LEFT JOIN + `FirstNonEmpty(row, payload)` fallback | The sibling read model already solved this; the console read model copies the proven shape rather than inventing one. |
| Client/server phone agreement | `PhoneMatching.MaxLength` (64) is the authoritative bound | The server bound wins; the client mirrors it, exactly as canon.v1 pins the three hash implementations. |
| Fingerprint repair | New forward-only EF migration with `migrationBuilder.Sql`, per the `20260628023022_OpenWorkItemUniquePerOperation` precedent | The immutability guard forbids editing the applied migration; a new one is the sanctioned route. |
| Draft cache coherence after approve | `queryKeys.settings.catalogDraft(id)`, already invalidated by `useEditCatalogItems` | Reuses the established key; no new cache concept. |

**Governing directive:** integrate with existing mechanisms; never add a parallel implementation.

## Spec ownership & frozen decisions

| Contract | Owner | Status here | Constraints |
|---|---|---|---|
| `notification_id` / `RecipientRef` derivation | 10 | **referenced — must not change** | Inv-9: altering derivation re-keys every notification and breaks dedup |
| `Notification.OperationId` nullability | 10 (ratified "null for non-operation messages") | **referenced — send side stays as-is** | The send path is already correct |
| `notifications.correlation_id` | 12 (DDL exists) | **referenced** — populate only | No migration |
| `guest_contacts` | Owner ruling 2026-08-06 (not in D17's 24 tables) | **owned** — data backfill only | Inv-13 tenant scope; spec 14 PII |
| `ScheduledBookingView` | console read model | **owned** — value only | Shape already nullable in `v1.json`; no regeneration |
| `PhoneMatching` | 08 | **owned** (server bound authoritative) | Inv-2/3 — payload immutability |
| `operation_status` / `NormalizedOperationStatus` | 06 | **referenced — read only** | D1/D2 |

**Frozen decisions in play:** D1/D2 (status sets — read only), D7 (status endpoint), D17 (table
catalog — `guest_contacts` sits outside it under the owner ruling).
**Invariants in play:** Inv-2/3 (payload+hash immutability), Inv-9 (notification dedup),
Inv-13 (tenant scope), Inv-16 (only authoritative outcomes).

## Package notes

**No new NuGet / npm / uv dependencies.** The backfill uses `pgcrypto`, a stock PostgreSQL
extension, enabled with `CREATE EXTENSION IF NOT EXISTS`.

---

## Phase 1 — Waitlist notification recovery (P1)

The only item that loses real customer-visible truth: a guest who *was* notified is permanently
recorded `InvalidRecipient`.

**Changes**
1. `NotificationRepository.TryInsertAsync` — add `correlation_id`, `causation_id` to the raw INSERT
   column list, sourced explicitly from the command (the interceptor cannot fire for raw SQL).
2. `SendNotificationCommandHandler` — pass `command.CorrelationId` / `CausationId` onto the entity.
3. `INotificationRecipientResolver` — add `ResolveByEnrollmentAsync(tenantId, enrollmentId, ct)`,
   reusing the existing enrollment→destination tail of `TryResolveFromWaitlistAsync` (extract the
   shared tail; do not duplicate the channel-derivation logic).
4. `ReconcileNotificationCommandHandler` (~line 174) and `RetryNotificationCommandHandler`
   (~line 123) — when `OperationId is null`, fall back to `CorrelationId` → enrollment resolution.
   Fails closed: a correlation id that is not an enrollment resolves null and keeps today's
   behaviour.

**TDD (RED→GREEN).** Write first: reconcile and retry of a `Sending` waitlist notification with
`OperationId == null` must resolve a destination and **not** settle `InvalidRecipient`.

**Automated Verification**
- [x] `bun nx test api` — **1331 passed, 0 failed**
- [x] `bun nx test-integration api` — **545 passed, 1 skipped, 0 failed**
- [x] `bun nx lint api` — clean (pre-existing NU1903 `Microsoft.OpenApi` advisory only)
- [x] `bun nx build api` — Build succeeded
- [x] The 4 new tests confirmed executing: `--filter FullyQualifiedName~WaitlistRow_NullOperation`
      → 4 passed

**Manual Verification**
- [ ] A waitlist offer whose send goes ambiguous reconciles to `Accepted`/`Delivered`, not
      `InvalidRecipient`.
- [ ] **Regression check (Inv-9):** `notification_id` values are unchanged for identical inputs —
      no duplicate guest notification.
- [ ] **Regression check:** non-waitlist notifications with a real `OperationId` reconcile exactly
      as before.

### Discoveries and Notable Information

1. **The defect was in two handlers, not one.** `RetryNotificationCommandHandler.cs:123-125` had the
   structurally identical `n.OperationId is null` gate. The original review only named Reconcile.
   Both are fixed and both now have coverage; a shared private `ResolveDestinationAsync` in each
   handler keeps the fallback identical.

2. **`RecipientRef` could not be the fix surface**, despite looking like the obvious candidate and
   despite spec 10's entity sketch describing it as an "opaque handle → minimized destination". The
   implementation made it a **one-way, truncated** hash —
   `"recip_" + SHA256($"{tenantId}|{value}")[..16]` — derived from the *operation id*. Its own doc
   warns that changing the derivation re-keys every `notification_id` and would notify guests twice
   (Inv-9). Test `WaitlistRow_NullOperation_ResolvesDestinationFromEnrollmentCorrelation` asserts
   the row's persisted ref (`recip-test`) still reaches the adapter unchanged, pinning that.

3. **`correlation_id`/`causation_id` were dead columns on every notification ever written** — not
   just waitlist ones. `TryInsertAsync` is raw `ExecuteSqlInterpolatedAsync`, which bypasses
   `SaveChangesAsync`, so `CorrelationStampingInterceptor` (a `ChangeTracker` walker) never fired
   despite the entity implementing `ICorrelatable` and its doc claiming the interceptor stamps it.
   Fixing this restores the spec-13 §4.1 correlation chain for the whole notifications table as a
   side benefit.

4. **Keyed on the enrollment, not the offer.** `TryResolveFromWaitlistAsync` used the offer only as
   a route to the enrollment. One enrollment can have many offers, so enrollment → offer is
   ambiguous; resolving straight from the enrollment is strictly more robust. Both paths now share
   one `TryResolveFromEnrollmentAsync`, so send and recover cannot disagree about where a guest is
   reachable.

5. **Send-path behaviour is unchanged and deliberately so.** Spec 10 ratifies `OperationId` as
   "null for non-operation messages", so writing NULL was correct all along — only the readers were
   wrong. No DDL, no FK change, no identity change.

---

## Phase 2 — Phone normalisation parity on the immutable path (P1)

The mint path is already safe (server 400s). The exposure is the **legacy inline payload**, where
an over-64-char value enters a hashed, immutable structure and becomes permanently unmatchable.

**Changes**
1. `apps/web/src/features/bookings/buildSubmission.ts` — `normalisePhone` returns `undefined` when
   the trimmed raw length exceeds **64**, matching `PhoneMatching.MaxLength` exactly: same
   threshold, same raw-length basis, same wholesale-reject semantics (**not** truncation).
2. Surface it at capture rather than silently dropping — `PhoneInput`'s existing warning path gets
   an overlong case so the operator is told before submitting.
3. Add the boundary vector on **both** sides so the bound cannot drift: a `PhoneMatchingTests` case
   at 64/65 raw chars, and a `normalisePhone` unit test with the same two vectors.

**Automated Verification**
- [x] `bun nx test web` — **596 passed** (4 new parity tests confirmed via `-t "parity with PhoneMatching"`)
- [x] `bun nx test api` — **1334 passed** (1331 → 1334; 3 new boundary tests)
- [x] `bun nx lint web` — 0 warnings, 0 errors

**Manual Verification**
- [ ] Pasting an overlong blob into the phone field warns at capture instead of producing an
      unfindable booking.
- [ ] **Regression check:** a normal `+61 400 111 222` still normalises identically on both sides
      and remains findable via Settings → *Find a booking*.

### Discoveries and Notable Information

1. **The capture warning needed no code change.** `looksUnnormalisedPhone` (`PhoneInput.tsx:77-80`)
   is implemented as `!/^\+[1-9]\d{6,14}$/.test(normalisePhone(raw) ?? "")` — it delegates to
   `normalisePhone`. Adding the ceiling there made the operator-facing warning cover the
   over-length case automatically. This is the payoff of the earlier consolidation to "one rule,
   one definition of usable"; a test pins it so a future refactor cannot silently decouple them.

2. **The exposure was narrower than the report implied, and worse where it existed.** The *mint*
   path (`POST /api/guest-contacts`) was already safe — `GuestContactsController` rejects an
   unnormalisable phone with a 400. The live defect was only the **legacy inline fallback** in
   `toCanonicalPayload` (`buildSubmission.ts:161-162`), which has no server-side gate at write time
   because the value sits inside an opaque `customer` object. There it was unrecoverable, not just
   wrong: the payload is hashed and immutable, so the number could never be tidied afterwards
   (Inv-2/3). Test `keeps an over-length phone out of the immutable payload entirely` targets that
   exact path.

3. **The bound is measured on raw length, before punctuation is stripped** — it is a `stackalloc`
   stack-safety bound on the server, not a tidiness rule. A number with an unremarkable digit count
   but heavy separator padding is refused. Both sides check in that order;
   `TheCeilingIsMeasuredOnRawLengthNotDigitCount` pins it so nobody "fixes" one side into checking
   digit count instead.

4. **Rejection, not truncation, on both sides.** A truncated phone is a different phone; silently
   dialling it is worse than refusing. Both test suites assert the null/undefined result explicitly
   rather than a shortened string.

5. **Knock-on to the LOOKUP path — deliberate, and warned.** `normalisePhone` has three callers:
   `toCanonicalPayload` (the bug), `looksUnnormalisedPhone` (the warning), and `lookup.ts:47`
   (`normalisePhone(value) ?? value`). For an over-64-char search term the lookup now sends the RAW
   value, which the server also refuses to normalise, so it finds nothing — whereas previously the
   client pre-normalised it and could match. This is a genuine, if very narrow, behaviour change
   (>64 raw characters means deliberate padding or a paste accident).
   **Accepted on purpose**: the alternative is a client that quietly works around a server bound,
   re-creating exactly the two-definitions-of-usable drift this codebase consolidated away. The
   operator is not left guessing — `BookingLookupPanel.tsx:53` keys its advisory off the same
   `looksUnnormalisedPhone`, which now flags over-length input, so the screen says the number will
   not match before the search returns empty. Flagged here so it is a recorded decision rather than
   an unnoticed side effect.

---

## Phase 3 — Console schedule booking reference (P2)

**Changes (as implemented — deviates from the planned approach, see Discoveries)**
1. `BuildCustomerResolverAsync` — the existing single `db.Bookings` query now also selects
   `BookingReference` and returns a `BookingIdentity(CustomerRef, BookingReference)` record.
2. `ToScheduledBooking` — resolves `FirstNonEmpty(identity.BookingReference, payload.BookingReference)`,
   the same precedence `BookingReadRepository.ToProjection` applies.

**No `api-contract` regeneration** — the field is already nullable in `v1.json`.

**Automated Verification**
- [x] `bun nx test-integration api` — **547 passed, 1 skipped, 0 failed** (was 545/546)
- [x] `bun nx test api` — **1334 passed**
- [x] `bun nx lint api` — clean
- [x] `bun nx build api` — Build succeeded

**Manual Verification**
- [ ] Finalized schedule rows show the booking reference; rows previously rendering `"Guest"` now
      show it.
- [ ] **Regression check:** in-flight operations with no booking row still appear — covered
      automatically by `Schedule_StillProjectsAnInFlightOperationThatHasNoBookingRow`.

### Discoveries and Notable Information

1. **The planned LEFT JOIN was the WRONG FIX, and the test caught it.** I first implemented exactly
   what the plan specified — mirroring `BookingReadRepository.BaseRows()`'s left join on
   `booking_operations.booking_id` — and the new integration test failed with `Actual: null`.
   The reason: the schedule row's booking id is the **parsed** id, which falls back to the canonical
   payload when the operation row's own `BookingId` is still null (`SeedBookingOperationAsync`
   never sets it, and neither does an operation before finalize). A join on `op.BookingId` misses
   precisely the rows the fix targets. The reference has to be keyed on the **same id the row
   displays**, which is only known after parsing — i.e. in C#, not in SQL.

2. **The right shape was already in the file.** `BuildCustomerResolverAsync` builds a dictionary
   from `db.Bookings` keyed by the parsed booking ids for exactly this reason. Extending that one
   query to carry the reference costs nothing extra (no second query, no join added to the shared
   `ScanAsync` used by the customers/funnel/analytics projections) and is keyed correctly by
   construction.

3. **Deliberately kept the payload fallback** even though a current-schema payload can never carry
   `booking_reference` (`BookingRequest` is `additionalProperties: false`). Historical rows may, and
   `BookingReadRepository.ToProjection` applies the identical `FirstNonEmpty(row, payload)`
   precedence — matching it is what keeps the two projections from disagreeing.

4. **One unrelated flake observed.** `RetryAndFailureTests.Transient_FirstCreate_DurableRetry_ThenSuccess`
   failed once in a full run (`Expected: Finalizing, Actual: Booked`) but passes in isolation and
   did not recur on the next full run. Order-dependent/environmental, unrelated to this phase —
   consistent with the known test-fixture-litter issue in this suite. Not treated as a regression;
   flagged for Stage 6.

---

## Phase 4 — Menu approve invalidates the draft cache (P2)

**Changes**
`useCatalogLifecycleMutation` (`mutations.ts:51-61`) — invalidate
`queryKeys.settings.catalogDraft(variables.catalogId)` alongside `catalogList()` in `onSettled`.
`onSettled`, not `onSuccess`, so a lost 202 also recovers.

**Automated Verification**
- [x] `bun nx test web` — **599 passed** (596 → 599)
- [x] `bun nx lint web` — 0 warnings, 0 errors
- [x] `bun nx typecheck web` — clean

**Manual Verification**
- [ ] With the draft editor open, approving moves the stepper off `in_review` and retires the save
      controls without a page reload.

### Discoveries and Notable Information

1. **Fixed at the shared hook, not just approve.** The defect lives in
   `useCatalogLifecycleMutation`, so `useSendCatalogForReview` had it too — any lifecycle
   transition left a mounted `CatalogDraftEditor` stale, not only approve. One edit covers both.

2. **`onSettled` with `variables`** rather than `onSuccess`: react-query passes the mutation
   variables as the third argument, which is where `catalogId` comes from. Settling on both
   outcomes matters here for the same reason the existing list invalidation does — a 409 means the
   catalog is *not* where this session thought it was, which is exactly when the stale draft most
   needs replacing.

---

## Phase 5 — `isBooked` honours its documented contract (P2)

Not reachable through the live API (the server keeps `status`/`terminal` in sync), but the code
contradicts its own header comment and is one stale cache entry from claiming a booking that is not
terminal.

**Changes**
`useOperationStatus.ts` — `isBooked(s)` requires `s?.status === "booked" && s?.terminal === true`.
Add the untested disagreement vector `{status:"booked", terminal:false}` → `false`.

Single production caller (`CreateBookingDrawer.tsx:227`); `terminal ∧ ¬booked` already renders an
error state at line 489, so no new UI branch is needed.

**Automated Verification**
- [x] `bun nx test web` — **599 passed**
- [x] `bun nx lint web` / `bun nx typecheck web` — clean

**Manual Verification**
- [ ] A normal create still reaches "Booking confirmed" and appends its schedule row.

### Discoveries and Notable Information

1. **Genuine RED→GREEN.** `isBooked({status:"booked", terminal:false})` returned `true` before and
   `false` after — the one vector the existing suite never exercised. Added alongside it:
   `{status:"rejected", terminal:true}` → `false`, pinning that `terminal` alone is not success
   (rejected/expired/failed are all terminal).

2. **No UI branch needed.** `CreateBookingDrawer.tsx:489` already renders an error state for
   `terminal && !booked`, so a `booked`-but-not-terminal poll now falls through to "still working"
   rather than to a dead end. The drawer needed no change at all.

---

## Phase 6 — Lock in the MaskedField TTL (P2, won't-fix + test)

**Recommendation: keep the 20s TTL, do not revert to blur.** The blur approach was evaluated and
rejected with per-browser evidence (Safari never fires `focusout` on a button click → indefinite
exposure; Chrome/Firefox re-mask before a copy gesture completes). The component protects
conversation-trace content, not PAN. The genuine defect is that **no test locks the TTL in**.

**Changes**
Add a fake-timer test asserting `revealed` flips back after `REVEAL_TTL_MS`, and that the timer is
cleared on unmount. Record the deviation in the component doc so it is not re-reported.

**Automated Verification**
- [x] `bun nx test web` — **599 passed** (3 new MaskedField tests, confirmed executing via `-t`)
- [x] `bun nx lint web` / `bun nx typecheck web` — clean

**Manual Verification**
- [ ] Reveal a trace value; it re-masks ~20s later without interaction.

### Discoveries and Notable Information

1. **Three tests, not one.** Added the TTL auto-remask (fake timers), the unmount-cleanup guard
   (a dropped cleanup would fire `setRevealed` into a dead component), and the `tail={0}` clamp —
   which was already fixed in code but pinned by nothing, so a future refactor could have
   reintroduced the `slice(-0)` full-value leak silently.

2. **`REVEAL_TTL_MS` is now exported** so the test asserts against the real constant rather than a
   hard-coded 20000 that could drift from it.

3. **Item 6 folded in as agreed** — `adapters.ts` comment corrected to the audited scope: the
   operator-collapse fallback fires only for operations with no booking row yet, because
   `FinalizeBookingCommandHandler`'s `?? providerReference` means `Booking.CustomerId` is never
   blank once a booking exists. The note now also records *why* the real fix is deferred (no origin
   metadata on `CustomerSummary`; adding one changes the OpenAPI surface). Docs only — no behaviour
   or test change.

---

## Phase 7 — Guest-contact fingerprint backfill (P3, defensive)

Unreachable outside a local dev DB (both migrations shipped in `970ca4c`; the app cannot insert
against the earlier schema). Shipped as cheap, idempotent insurance and to make the schema
self-consistent.

**Changes**
One **new** forward-only migration (never an edit to `20260806042423_...` — the immutability guard
blocks that) whose `Up()` runs:
- `CREATE EXTENSION IF NOT EXISTS pgcrypto;`
- `UPDATE guest_contacts SET phone_fingerprint = encode(digest(tenant_id || chr(31) || phone_e164, 'sha256'),'hex') WHERE phone_fingerprint IS NULL AND phone_e164 IS NOT NULL;`
- the `email_fingerprint` equivalent.

Reproduces `GuestContactRepository.Hash` exactly: unkeyed SHA-256 over `tenant_id \x1F value`, hex
lowercase, over the already-normalised stored column. `Down()` is a no-op (repairing data has no
meaningful inverse) — stated explicitly, mirroring the `extract_failed` `Down()` precedent.

**Automated Verification**
- [x] `bun nx test-integration api` — **549 passed, 1 skipped, 0 failed** (547 → 549)
- [x] `bun nx test api` — **1334 passed**
- [x] `bun nx lint api` — clean (after the line-ending fix below)
- [x] `bun nx build api` — Build succeeded

**Manual Verification**
- [x] **Regression check (Inv-13):** covered automatically by
      `TheBackfillKeepsFingerprintsTenantScoped` — same phone in two tenants backfills to two
      different fingerprints, and tenant A's lookup returns only tenant A's contact.
- [x] Fingerprints computed by SQL match `GuestContactRepository` — asserted, not eyeballed, by
      `TheFingerprintBackfillReproducesTheRepositoryHashExactly`.

### Discoveries and Notable Information

1. **No `pgcrypto` needed.** PostgreSQL has had a built-in `sha256()` since v11 and this deployment
   runs **pg16** (`docker-compose.yml`: `pgvector/pgvector:pg16`), so the migration uses
   `encode(sha256(convert_to(... , 'UTF8')), 'hex')` with no extension at all — avoiding both the
   `CREATE EXTENSION` privilege requirement and a dependency the plan had assumed.

2. **The equivalence is tested, not asserted.** `TheFingerprintBackfillReproducesTheRepositoryHashExactly`
   mints a row (C# writes the fingerprints), captures them, NULLs them to reproduce a pre-migration
   row, proves discovery is broken, runs the migration's exact SQL, and requires the **original**
   values back byte-for-byte. Without this, a subtly wrong formula would look like a successful
   repair while writing fingerprints no lookup can ever match.

3. **`Down()` is a deliberate no-op, and nulling out would be actively harmful.** The migration adds
   no schema to remove, and it cannot distinguish rows it filled from rows the application wrote
   correctly — so a "reverting" `Down()` would destroy working lookup data for every contact in the
   table and re-create the exact failure this fixes.

4. **A whitespace guard mirrors `ChannelFingerprint`.** That method returns null rather than hashing
   a blank value, so the SQL adds `btrim(...) <> ''`; without it the backfill could write a
   fingerprint where the application would have written none.

5. **`dotnet ef` fights the repo's line endings — twice.** It rewrote
   `ApplicationDbContextModelSnapshot.cs` entirely in CRLF (a spurious 2079-line diff whose content
   was byte-identical under `--ignore-all-space`); restored from HEAD. It also emitted both new
   migration files in CRLF, which **failed `bun nx lint api` with `ENDOFLINE`** — converted to LF
   with the BOM preserved (the `.editorconfig` requires UTF-8 **with BOM** *and* LF). Worth knowing
   for the next migration added on Windows.

6. **The immutability guard permits this.** `tools/scripts/check_migrations_immutable.sh` blocks
   `--diff-filter=MDR` on existing migration files but always allows an **added** one, and
   explicitly exempts the snapshot. No `SKIP=` needed.

---

## Blast radius

**Intentionally changed**

| Item | Change |
|---|---|
| `NotificationRepository.TryInsertAsync` | +2 columns in the INSERT list |
| `SendNotificationCommandHandler` | passes correlation/causation onto the entity |
| `INotificationRecipientResolver` (+impl) | new enrollment-keyed overload |
| `ReconcileNotificationCommandHandler`, `RetryNotificationCommandHandler` | null-operation fallback |
| `ConsoleReadModel.PayloadRows` / `ToScheduledBooking` | LEFT join + reference fallback |
| `buildSubmission.ts` `normalisePhone`, `PhoneInput` warning | 64-char bound |
| `mutations.ts` `useCatalogLifecycleMutation` | +1 invalidation |
| `useOperationStatus.ts` `isBooked` | +`terminal` requirement |
| New EF migration | data backfill only |

**Must stay byte-for-byte the same**

- `NotificationIdentity.Derive` and `NotificationRecipientResolver.Token` — **Inv-9 tripwire**.
- `Notification.OperationId` on the **send** path (spec-10 compliant as-is).
- `PhoneMatching.Normalise` server semantics — the client moves to meet it, not the reverse.
- `Booking.CustomerId` resolution in `FinalizeBookingCommandHandler`.
- Every `ScheduledBookingView` / `ScheduledBooking` **field shape** (value only changes).
- `GuestContactRepository.Hash` / `ChannelFingerprint` — the SQL mirrors it; neither may drift.
- All generated types — **no regeneration in this plan**.

**Type-generation consumers:** none. No Pydantic change, no OpenAPI shape change.

## What we're NOT doing

- **Not** changing how `notification_id` or `RecipientRef` are derived (Inv-9).
- **Not** making `Notification.OperationId` non-null or adding an FK-free operation column — spec 10
  ratifies the null.
- **Not** adding DDL for items 1–6; only the Phase 7 data migration touches the database.
- **Not** reverting `MaskedField` to blur-remask (Phase 6 rationale).
- **Not** truncating overlong phone numbers — reject, matching the server.
- **Not** regenerating `shared-schemas` or `api-contract`.
- **Not** fixing item 6 (customers guest-vs-operator) in this plan — see the open decision below.

## Cross-cutting requirements

- **Tenant scope (Inv-13):** the enrollment fallback and the backfill are both tenant-keyed; the
  backfill test asserts cross-tenant isolation.
- **Queue safety (Inv-5):** Reconcile/Retry stay inside `InboxDeduplicationBehavior`'s transaction —
  no new `IUnitOfWork`, and the fallback must **settle** a clean terminal state, never throw.
- **Terminal-path discipline (Inv-7/8/14):** the null-operation fallback fails closed to today's
  behaviour rather than inventing an outcome.
- **PII (spec 14):** no contact value is persisted or logged by any change; the resolver keeps the
  `Destination`/`RecipientRef` split.
- **Migrations:** exactly one, in Phase 7, additive/data-only, new file. No earlier phase depends on
  it, so `test-integration` runs normally throughout.
- **Encoding:** any new/edited `.cs` file is UTF-8 **with BOM**.
- **nx:** one command at a time (daemon lock); tee to `.tmp/`, never pipe to `tail`.

## Stage 5 review + Stage 6 validation (2026-08-07)

Review: [`../reviews/2026-08-04_frontend-api-wiring.md`](../reviews/2026-08-04_frontend-api-wiring.md).
**No blockers; 4 🟡 all addressed.** The most important finding reversed part of Phase 2: the
length bound had been applied to the raw input rather than the normalised output, which would have
dropped an ordinary padded number's phone out of an immutable payload — the exact harm the phase
exists to prevent. Fixed, plus the mint path now normalises before posting, which is the half of the
original finding the first implementation had not delivered.

### Final verification

| Check | Result |
|---|---|
| `bun nx build api` | Build succeeded |
| `bun nx test api` | **1335 passed**, 0 failed |
| `bun nx test-integration api` | **549 passed**, 1 skipped, 0 failed |
| `bun nx lint api` | clean |
| `bun nx test web` | **601 passed**, 0 failed |
| `bun nx lint web` · `bun nx typecheck web` | clean |

### Regression diff against the Stage 1 baseline audit

Every audited item, classified and checked:

| Baseline item | Disposition | Verified by |
|---|---|---|
| `isBooked` gate | intentionally changed | new disagreement + `rejected/terminal` vectors |
| Waitlist notification recovery | intentionally changed | 5 new tests across Reconcile + Retry |
| Guest-contact fingerprints | intentionally changed (**data only**) | SQL≡C# equality test + tenant-scope test |
| Console schedule reference | intentionally changed | 2 new `Schedule_*` integration tests |
| Phone normalisation | intentionally changed | 7 vectors, both languages |
| Customers adapter | **comment only** | reviewer confirmed body unchanged; claim verified true |
| Menu approve invalidation | intentionally changed | web suite green |
| MaskedField | **export + tests only** | 3 new tests |

Must-stay-the-same, all confirmed unchanged: `NotificationIdentity.Derive` and
`NotificationRecipientResolver.Token` (Inv-9 tripwires), the send path's `Notification.OperationId`
null, `PhoneMatching.Normalise` server semantics, `Booking.CustomerId` resolution in
`FinalizeBookingCommandHandler`, every `ScheduledBooking`/`ScheduledBookingView` field shape,
`GuestContactRepository.Hash`, and all generated types (**no regeneration performed**).

### Invariant re-check

| Invariant | Status |
|---|---|
| Inv-2/3 payload + hash immutability | **strengthened** — Phase 2 keeps un-normalisable values out of the payload; nothing rewrites one |
| Inv-5 queue safety | **held** — the retry handler (only inbox consumer here) still settles via the result switch, never throws |
| Inv-9 notification dedup | **held** — derivation untouched; a test pins that the row's persisted `RecipientRef` reaches the adapter |
| Inv-13 tenant scope | **held** — resolver re-checks tenant; backfill hashes each row's own `tenant_id`, pinned by a cross-tenant test |
| Inv-16 authoritative outcomes | **strengthened** — Phase 5 requires terminal; Phase 1 fails closed; Phase 3 reports a null reference rather than inventing one |

**No unexplained deviations.** One environmental flake observed and not reproduced
(`RetryAndFailureTests.Transient_FirstCreate_DurableRetry_ThenSuccess`; passes in isolation, absent
from three subsequent full runs).

## Approved decisions (Stage 3 gate, 2026-08-07)

| Decision | Ruling |
|---|---|
| Scope | **All 7 phases.** |
| Item 6 — customers guest-vs-operator | **Leave behaviour unchanged; correct the stale comment.** The audit showed `Booking.CustomerId` falls back to `providerReference`, not `identity_ref`, so only operations with no booking row collapse — the `adapters.ts:33-45` comment overstates it. No origin metadata exists to fix it properly, and adding one would force an OpenAPI change. Folded into **Phase 6** as a docs-only edit. |
| Item 8 — MaskedField | **Keep the 20s TTL; close as won't-fix and add the missing regression test.** Blur was rejected with per-browser evidence and the component protects trace content, not card data. |
| Item 3 — fingerprint backfill | **Ship it** as defensive insurance (Phase 7), despite being unreachable outside a local dev DB. |

### Phase 6 addendum — item 6 comment correction

`apps/web/src/features/customers/adapters.ts` (comment block ~lines 33-45): replace the claim that
"every such booking collapses into one row" with the audited behaviour — the operator-collapse
fallback fires **only** for operations that have not yet materialised a booking row, because
`FinalizeBookingCommandHandler` always populates `Booking.CustomerId`. Docs only; no behaviour
change, no test change.

---

# Part 7 — Capacity lock-order deadlock + waitlist accept limbo (2026-08-07)

# Plan — capacity lock-order deadlock + waitlist accept limbo

**Research/baseline:** [`../research/2026-08-04_frontend-api-wiring.md`](../research/2026-08-04_frontend-api-wiring.md)
**Branch:** `feat/ui-wire` · **Lane:** api only (Domain, Application, Infrastructure)
**No DDL, no migration, no type regeneration.**

## How this maps onto Cordia

| Need | Existing artifact it reuses | Consequence |
|---|---|---|
| Canonical capacity lock order | `ICapacityHoldRepository` + the existing hold-before-slot convention every other method already follows | The convention already exists and is upheld in 4 of 5 methods; this makes the fifth path conform rather than inventing a locking scheme. |
| Lock without mutating | `SELECT … FOR UPDATE` via `FromSqlInterpolated` | The only way to hoist the lock above `FinalizeAgainstOperationAsync` without releasing a hold the `HoldNoLongerValid` branch must leave intact. |
| "Has 06 finished?" | `OperationStateMachine.IsTerminal` — already the authority for `AdvanceBookingOperationCommandHandler` and `EvaluateOperationSlaCommand` | Deletes a duplicated set instead of adding a case to it (single-ownership; spec 06 owns terminal semantics). |
| "Don't release a hold the booking now owns" | `IBookingOperationRepository.GetAsync` on the offer's own `op_wl_*` id | The operation row's existence is already the exact signal that the accept minted; no new state needed. |

## Spec ownership & frozen decisions

| Contract | Owner | Status |
|---|---|---|
| `operation_status` + terminal set | **06** | **referenced — must consult, not copy** |
| Atomic capacity reservation / hold lifecycle | **07** | **owned** |
| Waitlist offer lifecycle | **11** | **owned** |
| `capacity_holds` / `capacity_slots` DDL | **12** | referenced — **no DDL** |

D1 (16 statuses) read-only. Invariants: **Inv-6** (capacity atomic & separate from idempotency),
**Inv-7** (ambiguous ⇒ reconcile), **Inv-8** (no notification before persisted state),
**Inv-16** (only authoritative outcomes).

## Package notes

**No new dependencies.**

---

## Phase 1 — Canonical lock order on the absorb path (the deadlock)

**Root cause:** `AbsorbSameSlotAsync` is the only method taking `capacity_slots` before
`capacity_holds`, and because `FinalizeAgainstOperationAsync` has already locked the slot earlier in
the same transaction, the inversion cannot be repaired inside the method.

**Change**
1. `ICapacityHoldRepository` + `CapacityHoldRepository` — add
   `LockAsync(tenantId, holdId, ct)` issuing `SELECT 1 FROM capacity_holds WHERE tenant_id = … AND
   hold_id = … FOR UPDATE`. Lock only; no state change, so the `HoldNoLongerValid` branch that must
   *not* release the old seat is unaffected.
2. `ModifyBookingCommand.CommitModificationAsync` — when the amendment is same-slot, call
   `holdRepo.LockAsync(previousHold.HoldId)` **immediately before**
   `FinalizeAgainstOperationAsync`. Net order becomes `HOLD(prev) → HOLD(own) → SLOT`, matching
   cancel's `HOLD(prev) → SLOT`. Both transactions now enter through the same row.
3. `ModifyCapacitySettlement.SettleAsync` — identical hoist (same defect, second call site).
4. Reorder `AbsorbSameSlotAsync`'s own statements to holds-before-slot as well. Not sufficient on its
   own, but it removes the standing inversion so the method is safe for any future caller.

**Why not the alternatives.** Reordering cancel would break its documented hard precondition (the
lifecycle transition must precede freeing the seat). Reordering modify's capacity/booking sequence
would break its documented rule that hold validity is decided before anything is settled. A
`40P01` retry wrapper treats the symptom and is explicitly out of scope (below).

**TDD (RED→GREEN):** an integration test running a same-slot modify and a cancel of the same booking
concurrently, asserting neither aborts with `40P01` and the ledger lands consistent.

**Automated Verification**
- [x] `bun nx test api` — **1340 passed** (1335 → 1340)
- [x] `bun nx test-integration api` — **550 passed, 1 skipped, 0 failed**
- [x] `bun nx lint api` — clean · `bun nx build api` — succeeded

**Manual Verification**
- [x] **Regression:** `SameSlotAmendmentTests` all pass — absorb's arithmetic and idempotency are
      untouched; only lock *acquisition* moved, no write reordered.
- [ ] **Inv-6:** confirm under load that no slot goes negative or oversold.

### Discoveries and Notable Information

1. **Reordering `AbsorbSameSlotAsync`'s writes would have broken its contract.** The plan proposed
   flipping its statements to holds-before-slot. That is wrong: the slot UPDATE is written *first
   on purpose*, because a slot that cannot take an increase must leave **everything** untouched
   (`return false` with nothing written). Releasing the previous hold before that check would write
   before the refusal. Resolved by moving the **locks** and leaving the **writes** where they are —
   `LockAsync` is a `SELECT … FOR UPDATE`, so both requirements hold at once.

2. **The fix had to be at the caller, not the method.** By the time absorb runs,
   `FinalizeAgainstOperationAsync` has already taken the slot lock in the same transaction, so no
   internal reordering could help. The load-bearing change is hoisting
   `holdRepo.LockAsync(previousHold.HoldId)` **above** the finalize call in both callers. The locks
   added inside absorb are defence-in-depth for future callers, not the fix.

3. **Hold locks inside absorb are ordered by hold id, not by role.** Locking previous-then-new would
   let two amendments on adjacent operations (`Op2` absorbing `Op1`, `Op3` absorbing `Op2`) take the
   same two rows in opposite orders and deadlock *against each other* — trading one cycle for
   another. Ordinal hold-id ordering is total, so no pair can invert.

4. **Only the same-slot path takes the lock.** A genuine cross-slot reschedule releases via
   `ReleaseFinalizedByOperationAsync`, which already orders hold-before-slot; locking there would
   serialise transactions that never contend. Pinned by
   `CrossSlotReschedule_DoesNotTakeThePreviousOwnerLock`.

5. **The ordering test asserts an ORDER, not a count.** `SameSlotModify_LocksThePreviousOwnersHold…`
   records callbacks and asserts `["lock","finalize","absorb"]`. A `Times.Once` check would pass
   even if the lock landed *after* the finalize — which would restore the exact cycle. Genuine
   RED→GREEN: before the fix there is no `lock` call at all.

---

## Phase 2 — Cancel must not release a hold the booking already owns

**Root cause:** cancel's guard (`GetLiveByEnrollmentAsync` excludes `Accepted`) closes the window
*after* the accept claims the offer, but not between the spec-04 mint and that claim — nor during
the entire deferred auto-accept window, where the offer sits `Offered` with no claim at all.

**Change**
`CancelWaitlistEnrollmentCommandHandler` — before releasing the offer's hold, check whether the
offer's `OperationId` now has a `booking_operations` row. If it does, the accept has minted and the
06 engine owns that hold: resolve the offer but **do not** release capacity, and record why.

This is precise because the operation row's existence is exactly the "has an accept minted?" signal:
`MatchWaitlistOfferCommandHandler` sets `offer.OperationId` at match time, but the
`booking_operations` row appears only when an accept submits through the spec-04 door. A cancel
racing a *not-yet-run* auto-accept correctly still releases — no booking exists.

**Automated Verification**
- [x] `bun nx test api` — **1340 passed**
- [x] `bun nx test-integration api` — **550 passed** (`WaitlistFlowTests` 25/25)
- [x] `bun nx lint api` — clean

**Manual Verification**
- [x] **Regression:** `Cancel_WithLiveOffer_ReleasesHold_AndEnqueuesRematch`,
      `Decline_ReleasesHold_AndEnqueuesRematch`, `Expire_…` all still release and re-match — none
      has a minted operation, so the new guard correctly does not engage.

### Discoveries and Notable Information

1. **The guard distinguishes the two windows precisely.** `offer.OperationId` is set at *match*
   time, but a `booking_operations` row appears only when an accept submits through the spec-04
   door. So a cancel racing a **not-yet-run auto-accept** still releases (correct — no booking
   exists), while a cancel racing an accept that has **already minted** does not. One read
   distinguishes them; no new state was needed.

2. **The enrollment is still cancelled — only the seat is retained.** The guest asked to come off
   the waitlist and that stands; what changes is that the hold now belongs to the booking, whose
   lifecycle 06 owns (it releases on compensation, expiry, or a later cancel). No re-match is
   enqueued either, because the opening is not actually free — enqueuing one would offer a seat
   that is still occupied.

3. **The retained-hold decision is audited** (`waitlist.hold_retained_for_booking`). Without it the
   divergence between "enrollment cancelled" and "hold still held" would be invisible and look like
   the leak this change prevents.

---

## Phase 3 — Reconciler: one terminal authority, and no silent abandonment

**Root cause:** the reconciler hand-copies a narrower terminal set instead of consulting spec 06's
`OperationStateMachine.IsTerminal`, so `CompensationRequired` and `ManualReview` both fall through
to "in-flight"; and on exhausting 3 × 5 min it gives up with only a `LogWarning`.

**Change**
1. Classify "06 has finished" via `OperationStateMachine.IsTerminal` — the single authority — instead
   of `_terminalFailureStates`. Keep `AlternativesRequired` as an explicit **waitlist-specific** stop
   state (06 treats it as a wait state, but for a delegated waitlist booking it will not self-recover),
   named and justified as a deliberate addition rather than a silent copy.
2. Treat `ManualReview` as **stop probing — awaiting a human**. Re-probing cannot help: the driver
   has no dispatch case for it, so it cannot progress without an operator. Emit an audit event
   linking enrollment ↔ operation so the person resolving the review can see the waitlist
   consequence.
3. On attempt exhaustion, emit an audit event rather than only logging, so an abandoned reconcile is
   durably visible. The enrollment stays eligible (not stamped fulfilled) — the honest outcome when
   the booking result is genuinely unknown, and the safe one: stamping fulfilment for a booking that
   later fails would strand the guest, while leaving it eligible risks at worst a second offer.

**TDD (RED→GREEN):** parametrized tests for `CompensationRequired` and `ManualReview` — the two
states with no coverage today.

**Automated Verification**
- [x] `bun nx test api` — **1340 passed** (`ReconcileWaitlistAcceptHandlerTests` 12/12)
- [x] `bun nx test-integration api` — **550 passed**
- [x] `bun nx lint api` — clean

**Manual Verification**
- [x] **Regression:** all four previously-hard-coded states and the `Booked` fulfilment path
      classify exactly as before — `FailedFinal`/`Rejected`/`Expired` now come from
      `IsTerminal`, `AlternativesRequired` from the documented waitlist stop list.
- [x] **Inv-16:** `ManualReview_StopsProbing_AndSettlesWithAnAudit` asserts the enrollment is **not**
      stamped fulfilled on an unknown outcome.

### Discoveries and Notable Information

1. **Deleting the duplicate set was the fix; adding a case would have been the patch.** The old
   `_terminalFailureStates` was a hand-copy of a set spec 06 owns. `IsTerminal` — already the
   authority for `AdvanceBookingOperationCommandHandler` and `EvaluateOperationSlaCommand` — now
   answers "has 06 finished", so the two can no longer drift.

2. **What remains is a *short, documented exception list*, not a second copy.**
   `_waitlistStopStates` holds exactly two entries with stated reasons: `AlternativesRequired`
   (06 calls it a wait state, but nobody will choose an alternative on behalf of a waitlist accept)
   and `ManualReview` (no driver dispatch case exists, so probing cannot change the answer).

3. **`CompensationRequired` is deliberately still treated as in-flight**, contrary to the goal's
   wording. It has legal outgoing edges and the driver dispatches compensation on the *next
   iteration of the same loop*, so it resolves in milliseconds — long before the first 5-minute
   probe. Settling on it would record an outcome for a state the operation is actively leaving.
   `CompensationRequired_IsStillInFlight_AndKeepsProbing` pins that.

4. **`ManualReview` was the real stranding**, and it is reached from exactly the race in Phase 2: a
   released hold → `HoldNoLongerValid` → `CompensationRequired` → (ambiguous provider cancel) →
   `ManualReview`, where it sits until a human intervenes.

5. **The enrollment stays eligible on an unknown outcome, on purpose.** Of the two ways to be wrong,
   leaving a guest eligible costs at worst a duplicate offer; stamping fulfilment for a booking that
   later fails leaves them with nothing.

6. **Abandonment is now audited** (`waitlist.accept_reconcile_abandoned`). Every other outcome of
   this handler wrote an audit row; the one that gives up on a guest wrote nothing but a log line.

---

## Stage 5 review + Stage 6 validation (2026-08-07)

Review: [`../reviews/2026-08-04_frontend-api-wiring.md`](../reviews/2026-08-04_frontend-api-wiring.md).
**No blockers.** One refinement made during review: the Phase 2 retain guard now fires only for a
**non-terminal** operation — retaining for an already-failed one would have left the seat reserved
until offer expiry, strictly worse than the bug.

The review agent hit a session limit mid-read, so the review was completed in the main context, and
the load-bearing premise was settled **empirically**: two integration tests prove `LockAsync` takes
a real row lock (`55P03` for a concurrent writer) and releases it on transaction end.

### Final verification

| Check | Result |
|---|---|
| `bun nx build api` | Build succeeded |
| `bun nx test api` | **1340 passed**, 0 failed |
| `bun nx test-integration api` | **553 passed**, 1 skipped, 0 failed |
| `bun nx lint api` | clean |

### Regression diff against the baseline audit

| Baseline item | Disposition | Verified by |
|---|---|---|
| `AbsorbSameSlotAsync` arithmetic/idempotency | **must stay same** — only locks moved, no write reordered | `SameSlotAmendmentTests` 9/9 |
| Cancel's lifecycle-transition-as-precondition | **must stay same** | untouched; `CancelBookingTests` green |
| Modify's decide-capacity-before-settling order | **must stay same** | untouched; only a lock hoisted above it |
| `ReleaseFinalizedByOperationAsync` hold→slot order | **must stay same** | untouched |
| Cancel release + rematch for a live offer | **must stay same** | `Cancel_WithLiveOffer_…`, `Decline_…`, `Expire_…` all green |
| `OperationStateMachine._terminalStates` | **must stay same — the authority** | consulted, never edited; `CompensationRequired` still non-terminal |
| Reconciler's four prior failure states + `Booked` | **must stay same** | 12/12 `ReconcileWaitlistAcceptHandlerTests` |
| Accept lane record-before-state | **must stay same** | untouched |

### Invariant re-check

| Invariant | Status |
|---|---|
| Inv-5 queue safety | **held** — no new `IUnitOfWork`; terminal outcomes settled, never thrown |
| Inv-6 capacity atomic | **held** — writes unchanged; only lock acquisition moved |
| Inv-7 ambiguous ⇒ reconcile | **held** — `CompensationRequired` still routes to compensation, not settled early |
| Inv-13 tenant scope | **held** — explicit tenant predicate in `LockAsync`'s SQL and an explicit re-check on the minted operation |
| Inv-16 authoritative outcomes | **strengthened** — no enrollment stamped fulfilled on an unknown outcome; abandonment is now audited rather than silent |

**No unexplained deviations.**

## Blast radius

**Intentionally changed**

| File | Change |
|---|---|
| `ICapacityHoldRepository` + `CapacityHoldRepository` | new `LockAsync` |
| `FirstPartyCapacityReservation.AbsorbSameSlotAsync` | statement order only — arithmetic untouched |
| `ModifyBookingCommand.CommitModificationAsync` | hoisted lock acquisition |
| `ModifyCapacitySettlement.SettleAsync` | hoisted lock acquisition |
| `CancelWaitlistEnrollmentCommandHandler` | minted-operation guard before release |
| `ReconcileWaitlistAcceptCommandHandler` | classification via `IsTerminal`; `ManualReview` stop; exhaustion audit |

**Must stay byte-for-byte the same**

- `OperationStateMachine._terminalStates` — **the authority; consulted, never edited.**
  `CompensationRequired` must remain non-terminal (it has legal outgoing edges).
- `AbsorbSameSlotAsync`'s delta arithmetic and idempotency (only statement order moves).
- `FinalizeAgainstOperationAsync`'s `HoldNoLongerValid` semantics and the modify branch that
  deliberately does **not** release the old slot.
- Cancel's lifecycle-transition-as-hard-precondition ordering.
- The accept lane's record-before-state ordering (mint before claim).
- `MaxAttempts`/`Delay` values — Phase 3 changes *classification*, not the cadence.
- All generated types — **no regeneration**.

**Type-generation consumers:** none.

## What we're NOT doing

- **No `40P01`/`40001` retry wrapper in `UnitOfWork`.** Tempting, but it is a symptom treatment that
  would mask future lock-order inversions across every transaction in the system. Fixing the order
  makes the cycle unreachable. Worth revisiting as separate, deliberate resilience work — not folded
  in here.
- **No isolation-level change** (READ COMMITTED stays).
- **No DDL, no migration, no new pg-enum value** (spec 12 owns those).
- **Not making `CompensationRequired` terminal** — it has legal outgoing edges and is actively
  driven; doing so would contradict spec 06 and strand compensation.
- **Not reversing the accept lane to state-before-record** — record-before-state is deliberate and
  self-healing.
- **Not adding a driver dispatch case for `ManualReview`** — it is a business state resolved by an
  operator (T10), by design.

## Cross-cutting requirements

- **Inv-6:** capacity stays atomic; the reordering must not introduce a read-then-write.
- **Inv-7:** ambiguous outcomes still route to reconcile/manual review, never a blind retry.
- **Inv-13:** every new query stays tenant-scoped.
- **Inv-16:** no enrollment stamped fulfilled on an unknown outcome; no claimed outcome the platform
  cannot back.
- **Queue safety (Inv-5):** `CancelWaitlistEnrollmentCommandHandler` and the reconciler run inside
  their existing transactions — no new `IUnitOfWork`, and terminal outcomes are settled, never thrown.
- **Encoding:** new/edited `.cs` files UTF-8 **with BOM**, LF endings.
- **nx:** one command at a time; tee to `.tmp/`.

---

# Part 8 — Global capacity lock order, state ownership, ManualReview (2026-08-07)

# Plan — global capacity lock order, single-source state ownership, ManualReview durability

**Research/baseline:** [`../research/2026-08-04_frontend-api-wiring.md`](../research/2026-08-04_frontend-api-wiring.md)
**Branch:** `feat/ui-wire` · **Lane:** api (Domain, Application, Infrastructure, Api) + web + docs
**Baseline:** 1341 unit / 553 integration passing.

## Approval gate decisions (2026-08-07)

| Question | Decision |
|---|---|
| Edge validation | **Enforce at the choke point** — `TryTransitionStatusAsync` calls `EnsureAllowed`. An illegal edge throws `InvalidStateTransitionException`, which the driver already catches into the poison/DLQ path with a work item (Inv-14). |
| Work-item visibility | **Endpoint + wire into `QueueScreen`** — adds a web lane (Phase 7) and an `api-contract` regeneration. |
| Refactor scope | **All 15 sites.** |

## How this maps onto Cordia

| Need | Existing artifact it reuses | Consequence |
|---|---|---|
| One canonical lock order | `CapacityHoldRepository.LockAsync` (`SELECT … FOR UPDATE`) — already proven to lock by two integration tests | The primitive exists; this generalises it to slots and expresses the order **once**, instead of re-deriving it at each call site. |
| Total order across rows | ascending id, the same device `AbsorbSameSlotAsync:252-259` already uses for holds | Extends a rule the codebase already applies to holds so it also covers slots and multi-row loops. |
| "Is 06 finished / waiting / driveable?" | `OperationStateMachine` — already the authority for the driver and the SLA sweeper | The reconcilers **ask** the machine instead of keeping parallel sets, exactly as `ReconcileWaitlistAcceptCommandHandler` was already converted to do. |
| Durable ManualReview tracking | `ManualReviewEscalator` + `operations_work_items` + `uq_open_work_item_per_operation` | The mechanism is built and correct for operations; this closes the paths that bypass it and adds the missing read side. |
| Atomic work-item insert | `IOperationWorkItemRepository.TryAddOpenAsync` — already written, currently near-dead | Uses the primitive that exists rather than adding one. |
| Operator surface | `GET /api/booking-operations?needs_attention=true` + `QueueScreen` | Extends the existing console front door; no new tooling. |

## Spec ownership & frozen decisions

| Contract | Owner | Status |
|---|---|---|
| Capacity reservation, hold lifecycle, **lock ordering** | **07** | **owned — may define the rule** |
| `capacity_holds` / `capacity_slots` DDL | 12 | referenced — **no DDL** |
| `operation_status` (D1) + terminal semantics | **06** | **referenced — consult, never copy** |
| `NormalizedOperationStatus` (D2) | 06 | referenced — read only |
| `operations_work_items` DDL | 12 | referenced — **no DDL**; new read method only |
| ManualReview semantics, FM#11 work item | **06** | referenced — implementing what it already mandates |
| Pre-order saga §5.3 / §21.4 | **11** | **owned** |

D1/D2 are **read-only** — no status added, removed, or reclassified.
Invariants: **Inv-6, Inv-7, Inv-13, Inv-14, Inv-15, Inv-16**.

## Package notes

**No new dependencies.** The architecture tests are hand-rolled reflection (matching the repo's
zero-arch-test-framework status quo) rather than adding `NetArchTest`.

---

## The rule this plan establishes

> **Cordia Capacity Lock Order (CLO).** Within a single transaction:
> 1. Acquire every `capacity_holds` row lock **before** any `capacity_slots` row lock.
> 2. Acquire multiple `capacity_holds` rows in **ascending `hold_id`** order.
> 3. Acquire multiple `capacity_slots` rows in **ascending `slot_id`** order.
> 4. **Carve-out:** an `INSERT` of a *new* hold row acquires no pre-existing row lock and therefore
>    cannot participate in a wait cycle. `ReserveAsync`'s slot-update-then-hold-insert is compliant.

Clauses 2–4 are new. Clause 1 alone is what the previous pass enforced, and it is **not sufficient**
— it does not prevent the cross-slot slot↔slot cycle (A1).

---

## Phase 1 — Fix the live cross-slot deadlock and give the rule one implementation

**Root cause (A1).** A cross-slot reschedule takes `SLOT(new)` then `SLOT(old)` — ordered by *role*.
Two reschedules moving in opposite directions between the same two slots hold one row each and want
the other. `40P01`. Present in `ModifyBookingCommand:511-518` (automatic lane) and
`ModifyCapacitySettlement:122-123` (manual-recovery lane).

**Change**
1. `ICapacitySlotRepository` + `CapacitySlotRepository` — add `LockAsync(tenantId, slotId, ct)`,
   mirroring the hold primitive exactly.
2. New `ICapacityLockOrder` (Domain) + `CapacityLockOrder` (Infrastructure) —
   `AcquireAsync(tenantId, holdIds, slotIds, ct)` acquires distinct holds ascending, then distinct
   slots ascending. **This is the single implementation of CLO**; call sites state *which rows*, never
   *in what order*.
3. `ModifyBookingCommand.CommitModificationAsync` and `ModifyCapacitySettlement.SettleAsync` — replace
   the same-slot-only `holdRepo.LockAsync` with one `CapacityLockOrder.AcquireAsync` covering the
   previous-owner hold and **both** slots, on **both** branches, before the capacity phase.
4. `FirstPartyCapacityReservation` — hoist all hold locks above the first slot write in
   `ReleaseFinalizedByOperationAsync` and `ReleaseByOperationAsync`, and add `ORDER BY hold_id` there
   and in `CapacityHoldRepository.GetActiveByOperationAsync` (A2).
5. Document the A4 carve-out in `ReserveAsync`.

**Deliberate behaviour change.** `CrossSlotReschedule_DoesNotTakeThePreviousOwnerLock`
(`ModifyBookingTests`) asserts today that cross-slot takes *no* lock, justified as "transactions that
never contend". A1 disproves that premise — they contend on two slot rows. The test is **updated**,
and this is called out here rather than silently changed.

**TDD (RED→GREEN):** Phase 2's `TwoOpposingCrossSlotReschedulesDoNotDeadlock` fails before this
change and passes after.

**Automated Verification**
- [x] `bun nx test api` — **1342 passed** (1341 → 1342)
- [x] `bun nx test-integration api` — **562 passed**, 1 skipped, 0 failed
- [x] `bun nx build api` — succeeded

**Manual Verification**
- [x] **Regression:** `SameSlotAmendmentTests` all green — absorb's arithmetic untouched, only lock
      acquisition moved.
- [ ] **Inv-6:** confirm under load that no slot goes negative or oversold.

### Discoveries and Notable Information

1. **The fix had to move up a level, not sideways.** The previous pass hoisted a *hold* lock. That
   removes the hold↔slot cycle and nothing else. The cross-slot cycle is built from two SLOT rows,
   so no amount of hold ordering reaches it. What actually generalises is **acquire every row the
   transaction will touch, up front, in one canonical order** — which is why the fix is a primitive
   (`ICapacityLockOrder`) rather than another hoisted call.

2. **The acquisition is now unconditional, and that reverses a prior decision.** The earlier pass
   locked only on the same-slot branch, reasoning that cross-slot "would serialise transactions that
   never contend". That premise is false and is now disproved by a test: cross-slot is precisely the
   branch that touches two slot rows, so it is the branch that most needs ordering.
   `CrossSlotReschedule_DoesNotTakeThePreviousOwnerLock` asserted the old premise and has been
   replaced by `CrossSlotReschedule_AcquiresBothSlotsBeforeTouchingEither`.

3. **`ReserveAsync` is compliant, and the rule had to be restated to say why.** Read as "which table
   is touched first" it looks like the exact inversion being forbidden. Stated over **row locks on
   pre-existing rows**, it is compliant: an INSERT acquires nothing another transaction can hold.
   The unique-index conflict paths were traced and neither can close a cycle. This is a precision
   fix to the rule, not an exemption from it — written up in `ReserveAsync` and in spec 07.

4. **The multi-row release loops were latent, not live.** `ReleaseFinalizedByOperationAsync` and
   `ReleaseByOperationAsync` interleave `HOLD → SLOT → HOLD → SLOT` over an unordered list. Reaching
   `n > 1` needs one operation to own two same-status holds at once, which no current flow produces
   (`create` is the only workflow step). Fixed anyway — they are public primitives with no guard and
   no comment saying `n ≤ 1` is required, and the fix is an `ORDER BY` plus one call.

5. **Ordinal comparison is load-bearing.** The sort has to be identical in every process competing
   for the rows. A culture-sensitive comparison is not, so two nodes under different cultures could
   order the same pair oppositely and rebuild the cycle. `StringComparer.Ordinal` throughout.

---

## Phase 2 — Prove it against real PostgreSQL

**Root cause.** Spec 07 §12 requires *"deterministic two-transaction interleaving"* integration tests
for concurrency. Exactly one test in the repo opens two concurrent connections, and **no test asserts
a `40P01`**. The lock order is currently defended by a Moq call-order assertion and prose.

**Change** — new `IntegrationTests/Capacity/CapacityLockOrderTests.cs`, using the two-context pattern
from `SameSlotAmendmentTests:317-349`:

| Test | Proves |
|---|---|
| `OppositeOrderSlotAcquisition_Deadlocks_ProvingTheCycleIsReal` | two transactions taking `S1,S2` vs `S2,S1` abort with **`40P01`** — the cycle A1 describes genuinely exists |
| `AscendingSlotAcquisition_NeverDeadlocks` | the same two transactions taking both slots ascending: one **waits**, both commit, **no `40P01`** |
| `TwoOpposingCrossSlotReschedulesDoNotDeadlock` | the real handler path, end to end — the previous deadlock scenario cannot happen |
| `ConcurrentModifyAndCancel_RespectTheHoldBeforeSlotOrder` | a holder locking `HOLD(prev)` blocks the modify path **before** it reaches the slot (`55P03` under `lock_timeout`), i.e. hold-before-slot holds at *transaction* level |
| `SlotLockAsync_GenuinelyHoldsTheRowAgainstAConcurrentWriter` | the new slot primitive really locks (`55P03`) |
| `LocksReleaseOnCommit` / `LocksReleaseOnRollback` | locks release at transaction end in both directions — the fix cannot trade a deadlock for a stuck row |

**Determinism.** The deadlock test is deterministic by construction: PostgreSQL's deadlock detector
*guarantees* detection and aborts one side. Blocking tests use `SET LOCAL lock_timeout` so they fail
fast instead of hanging the suite — the device the existing lock test already uses.

**Automated Verification**
- [x] `bun nx test-integration api` — new suite **9/9 green**; full suite 562 passed
- [x] `bun nx build api`

### Discoveries and Notable Information

1. **The cross-slot deadlock is confirmed, not theorised.**
   `OppositeOrderSlotAcquisition_Deadlocks_ProvingTheCycleIsReal` genuinely aborts with `40P01`
   against real PostgreSQL. The audit's analysis is now evidence.

2. **Every safety proof is paired with a negative control.** A test showing "the current code does
   not deadlock" is worthless if the scenario could never deadlock anyway. Each safe case therefore
   has a sibling that takes the *same rows* in the *wrong order* and is shown to abort — so the safe
   test is known to be testing something.

3. **The safe tests must NOT use the barrier.** With a correct order the second transaction blocks
   on the first row, so it can never signal "I hold my first row"; waiting for that signal would
   hang the suite instead of proving anything. Deadlock tests use the barrier (it is what makes
   detection deterministic); ordering tests use a bounded `Task.WhenAny` timeout instead.

4. **Hold-before-slot is asserted positively, not inferred.** After the blocked acquisition rolls
   back, a third transaction updates the slot under a short `lock_timeout` and succeeds — which is
   impossible if the blocked transaction had reached the slot first. "It threw at the hold" alone
   would not have proved it never touched the slot.

---

## Phase 3 — One authority per state machine

**Root cause (B1).** Four of five state machines expose no `IsTerminal`, forcing 15 call sites to
hand-roll sets. `OperationStateMachine` itself carries two sources of terminality.

**Change**
1. **Domain — give every machine the queries its callers need.**
   `NotificationStateMachine`, `WaitlistOfferStateMachine`, `CatalogStateMachine`: add
   `TerminalStates` + `IsTerminal` (and `IsInProgress` for catalog).
   `PreorderSagaStateMachine`: add an **explicit** `TerminalStates` + `IsTerminal` + `IsResolved`
   (`ManualReview` counts as resolved-for-reconcile). *Explicit, not derived from dictionary absence
   — its unwired legacy members `HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold` are also
   absent yet are not terminals.*
   `OperationStateMachine`: expose `TerminalStates`; **derive** it from `AllowedTransitions` absence
   so the class has one source; add the semantic queries the reconcilers need —
   `IsAwaitingUser`, `IsDriverDispatchable`, `RequiresOperator`, `NextStates`.
2. **Rewire all 15 sites to ask rather than copy** (full list in the research doc §B1). Notably:
   - `BookingOperationRepository.TerminalStatuses` → materialised from `OperationStateMachine.TerminalStates`
     (an array is still needed for SQL translation, but it is now *derived*, not typed out).
   - `SlaEligibleStatuses` / `UserWaitStatuses` → one partition owned by `OperationStateMachine`,
     both files derive from it.
   - `RetentionPolicy` → **rename** `TerminalStatuses`/`IsTerminal` → `PrunableStatuses`/`IsPrunable`,
     derived as *terminals except `Waitlisted`* with the reason stated. **Behaviour is unchanged** —
     this removes a name collision with a concept spec 06 owns, it does not alter what gets pruned.
   - The two byte-identical `ToLifecycleStatus` switches → **one** shared helper, both call it.
   - `NotificationRepository` → collapse its two representations of the same sets into one.
   - `ReconcilePreorderSagaCommandHandler` → `IsTerminal` + a short documented delta, the shape
     `ReconcileWaitlistAcceptCommandHandler` already uses.
3. **`ListOperationsQuery.NeedsAttention`** stays a product classification but moves next to the
   partition it depends on and is covered by a completeness test.

**Automated Verification**
- [x] `bun nx test api` — all five `*StateMachineTests` still green (1422 total)
- [x] `bun nx test-integration api` (568) · `bun nx lint api` · `bun nx build api`

**Manual Verification**
- [x] **Regression:** retention prunes exactly the same rows — `RetentionPolicyTests` green + `RetentionPrunable_IsTerminalMinusWaitlistedOnly` pins the derivation.
- [x] **Regression:** console and manage-booking lifecycle labels identical — they now call ONE helper, so they cannot differ.

### Discoveries and Notable Information

---

## Phase 4 — Close the ManualReview gaps

**Root cause.** Entry is sound; the gaps are C1 (nothing can enumerate outstanding work items),
C2 (pre-order saga parks with no work item at all), C3 (non-atomic insert), C4 (reconcile exhaustion
logs only).

**Change**
1. **C3** — `ManualReviewEscalator:56-69` and `AdvanceBookingOperationCommandHandler:170-185` switch
   to `TryAddOpenAsync`. A lost race becomes a benign no-op instead of aborting the whole transaction.
2. **C2** — `CompensatePreorderBookingCommandHandler` and `ReconcilePreorderSagaCommandHandler` create
   an open work item (`Reason = "preorder_manual_review"`) when parking the saga, so a parked saga is
   durably tracked, counted in `fulfillment.manual_review.depth`, and carries an SLA deadline.
3. **C4** — `ReconcileWaitlistAcceptCommandHandler:136-139` writes a durable audit event on
   exhaustion **and** ensures a work item exists, instead of `LogWarning` and return.
4. **C1** — `IOperationWorkItemRepository.ListOpenAsync(tenantId, limit, ct)` (tenant-scoped,
   ordered by SLA deadline) + `ListOpenWorkItemsQuery` + `GET /api/booking-operations/work-items`,
   returning `ActionResult<T>` so the generated TS is typed. Then
   `bun nx run api-contract:generate-types`.

**C5 is deliberately not changed.** `EvaluateOperationSlaCommand` already guards on an existing open
work item, so ManualReview cannot accumulate duplicates. Changing SLA classification is a
behaviour change with no defect behind it; it is documented instead.

**Automated Verification**
- [x] `bun nx test api` · `bun nx test-integration api` · `bun nx lint api` · `bun nx build api`
- [x] `bun nx run api-contract:generate-types` — `/api/booking-operations/work-items` present in `v1.json` + generated TS

### Discoveries and Notable Information

---

## Phase 5 — Regression safeguards

**Change** — new `UnitTests/Architecture/` suite (hand-rolled reflection, no new package):
1. `StateOwnershipArchitectureTests` — scans `Cordia.Application` + `Cordia.Infrastructure` for
   static collection fields typed over a state enum and fails on any that is neither derived from its
   state machine nor in an **explicitly justified allow-list**. This is the guard that stops a
   sixteenth copy appearing.
2. `WorkflowDefinitionTests` —
   - `IsTerminal(s) == !AllowedTransitions.ContainsKey(s)` for all 16 statuses;
   - the driver's dispatch set exactly equals `IsDriverDispatchable`;
   - the SLA/user-wait/terminal partition covers all 16 exactly once;
   - `NeedsAttention` ⊆ the classified set;
   - every state machine's terminal set has no outgoing edges.
   These are the tests that make "a workflow change cannot silently make reconciler behaviour
   incorrect" true rather than aspirational.
3. `CapacityLockOrderTests` (unit) — asserts `ICapacityLockOrder` emits ascending-ordered acquisition
   for any input permutation, and that no Application handler calls a repository `LockAsync` directly
   (reflection over method bodies is impractical; enforced instead by making the repository
   `LockAsync` methods internal to the ordered primitive's assembly where feasible, else by an
   explicit allow-list test).
4. **B2 — edge validation at the choke point:** `TryTransitionStatusAsync` calls
   `OperationStateMachine.EnsureAllowed(from, to)`. Nine handlers currently write literal transitions
   with no validation; a removed edge would diverge silently. **This is the highest-risk change in the
   plan** — see the approval gate.

**Automated Verification**
- [x] `bun nx test api` (1422) · `bun nx lint api` · `bun nx build api`

### Discoveries and Notable Information

---

## Phase 6 — Documentation

**Change**
1. **`docs/specs/07-capacity-and-holds.md` §14** — add the normative CLO rule (all four clauses),
   *why* holds lock before slots, and a worked table of **incorrect transaction patterns**: the
   original hold↔slot inversion, the cross-slot slot↔slot cycle, the unordered multi-row loop, and
   the insert carve-out that looks like a violation but is not.
2. **`docs/dotnet-api-patterns.md`** — a transaction/locking conventions section pointing at spec 07
   as the owner, plus the general rule (no new `IUnitOfWork` inside a consumer; no deadlock retry
   wrappers; total order on multi-row acquisition).
3. **`docs/specs/06-booking-workflow-and-fulfillment.md`** — state that ManualReview entry MUST create
   a work item (already FM#11) and that outstanding items MUST be enumerable; note the single-authority
   rule for terminal/wait classification.
4. **`docs/specs/11-waitlist-menu-preorder.md`** — pre-order saga ManualReview creates a work item.

**Automated Verification**
- [x] Full suite: `bun nx test api` 1422, `bun nx test-integration api` 568, `bun nx lint api`, `bun nx build api` — all green

### Discoveries and Notable Information

---

---

## Phase 7 — Surface work items in the console queue (web)

**Change** — `apps/web`: extend `features/operations/api.ts` with the typed
`GET /api/booking-operations/work-items` call (via `paths[...]` from the regenerated
`@cordia/api-contract`), join by `operation_id` in `features/operations/adapters.ts`, and render
reason / SLA deadline / age on the existing `QueueScreen` rows — flagging items past their SLA
deadline. No new route, no new design-system component.

**Automated Verification**
- [x] `bun nx test web` (609) · `bun nx lint web` (0 warnings) · `bun nx build web`

### Discoveries and Notable Information

---

## Blast radius

**Intentionally changed**

| File | Change |
|---|---|
| `ICapacitySlotRepository` / `CapacitySlotRepository` | new `LockAsync` |
| `ICapacityLockOrder` / `CapacityLockOrder` (new) | the single CLO implementation |
| `FirstPartyCapacityReservation` | hoisted + ordered hold locks in the two loops; A4 comment |
| `CapacityHoldRepository` | `ORDER BY hold_id` on `GetActiveByOperationAsync` |
| `ModifyBookingCommand` / `ModifyCapacitySettlement` | ordered acquisition on **both** branches |
| 5 state machines (Domain) | additive query methods; `OperationStateMachine` derives its terminal set |
| 15 duplication sites (§B1) | derive instead of copy |
| `RetentionPolicy` | **rename only**, behaviour identical |
| `ManualReviewEscalator`, `AdvanceBookingOperationCommandHandler` | atomic work-item insert |
| `CompensatePreorderBookingCommandHandler`, `ReconcilePreorderSagaCommandHandler` | create a work item |
| `ReconcileWaitlistAcceptCommandHandler` | durable record on exhaustion |
| `IOperationWorkItemRepository` + new query + controller action | enumeration surface |
| `BookingOperationRepository.TryTransitionStatusAsync` | edge validation (Phase 5, gated) |
| `ModifyBookingTests.CrossSlotReschedule_DoesNotTakeThePreviousOwnerLock` | **premise disproved — updated** |
| 4 docs | the written rules |

**Must stay byte-for-byte the same**

- `AbsorbSameSlotAsync`'s delta arithmetic, idempotency, and its "slot refuses ⇒ nothing written"
  contract — only lock *acquisition* moves, never a write.
- `FinalizeAgainstOperationAsync`'s `HoldNoLongerValid` semantics and hold→slot order.
- Cancel's lifecycle-transition-as-hard-precondition ordering.
- `ReserveAsync`'s conditional-write predicate and idempotency pre-check (Inv-6).
- The **set membership** of every state classification — Phase 3 changes where a set is *defined*,
  never which states are in it. `RetentionPolicy` keeps excluding `Waitlisted`.
- `OperationStateMachine.AllowedTransitions` edge set and D1's 16 values.
- `notification_id` / `RecipientRef` derivation (Inv-9) — untouched.
- `MaxAttempts` / `Delay` cadences.
- All generated types except the one `api-contract` regeneration in Phase 4.

**Type-generation consumers:** `api-contract` only, Phase 4, additive path. No Pydantic contract, so
**no `shared-schemas` regeneration**.

## What we're NOT doing

- **No `40P01` / `40001` retry wrapper and no isolation-level change.** Explicitly out of scope per
  the goal: the order is fixed so the cycle is unreachable, rather than masking inversions system-wide.
- **No DDL, no migration, no new pg-enum value** — spec 12 owns those, and nothing here needs one.
- **Not making `CompensationRequired` terminal** — it has legal outgoing edges and is actively driven.
- **Not adding a driver dispatch case for `ManualReview`** — it is resolved by an operator by design
  (Inv-15).
- **Not changing which states any classification contains** — only where they are defined.
- **No new web screens.** Phase 7 extends the existing `QueueScreen` rows only — no new route, no new
  design-system component.
- **Not touching `RecipientRef`/`notification_id` derivation.**

## Cross-cutting requirements

- **Inv-6:** capacity stays atomic; no read-then-write introduced; only lock acquisition moves.
- **Inv-7:** ambiguous outcomes still reconcile before any repeat create.
- **Inv-13:** every new query and every raw-SQL lock carries an explicit `tenant_id` predicate and is
  fully parameterised; the new endpoint is tenant-scoped with a cross-tenant test.
- **Inv-14/15:** DLQ never replaces business state; ManualReview exits only via typed commands.
- **Inv-16:** no outcome claimed that the platform cannot back.
- **Queue safety (Inv-5):** no handler opens a new `IUnitOfWork`; terminal outcomes are settled, never
  thrown.
- **Encoding:** new/edited `.cs` files UTF-8 **with BOM**, LF endings.
- **nx:** one command at a time; tee to `.tmp/`, never pipe to `tail`.

---

## Stage 5 review + Stage 6 validation (2026-08-07)

### Final verification

| Check | Result |
|---|---|
| `bun nx lint api` | **clean** |
| `bun nx build api` | **Build succeeded**, 0 errors |
| `bun nx test api` | **1422 passed**, 0 failed (baseline 1341 → **+81**) |
| `bun nx test-integration api` | **568 passed**, 1 skipped, 0 failed (baseline 553 → **+15**) |
| `bun nx typecheck web` | clean |
| `bun nx lint web` | 0 warnings, 0 errors |
| `bun nx test web` | **609 passed** (baseline 601 → **+8**) |
| `bun nx build web` | succeeded |
| `bun nx run api-contract:generate-types` | regenerated; `/api/booking-operations/work-items` present |

### Regression diff against the Stage 1 baseline audit

| Baseline item | Disposition | Verified by |
|---|---|---|
| `AbsorbSameSlotAsync` arithmetic + idempotency | **must stay same** — only lock acquisition moved | `SameSlotAmendmentTests` green |
| `FinalizeAgainstOperationAsync` hold→slot order and `HoldNoLongerValid` semantics | **must stay same** | untouched; `FinalizeAgainstHoldTests` green |
| `ReserveAsync` conditional-write predicate (Inv-6) | **must stay same** | untouched — comment only; `ReserveCapacity*` green |
| Cancel's lifecycle-transition-as-precondition | **must stay same** | untouched; `CancelBookingTests` green |
| Cross-slot reschedule takes **no** lock | **intentionally changed** — the premise was disproved | `CrossSlotReschedule_AcquiresBothSlotsBeforeTouchingEither` |
| `OperationStateMachine` edge set / D1's 16 values | **must stay same** | `OperationStateMachineTests` + `WorkflowDefinitionTests` green |
| Membership of every state classification | **must stay same** — only where they are DEFINED moved | `WorkflowDefinitionTests`; `RetentionPolicyTests` green |
| Retention prunes exactly the same rows | **must stay same** (rename only) | `RetentionPrunable_IsTerminalMinusWaitlistedOnly` |
| Console vs manage-booking lifecycle labels | **must stay same** | one shared helper — they can no longer differ |
| `ManualReviewEscalator` transition + outbox + audit | **must stay same**; insert made atomic | `ManualRecoveryTests` incl. `DoubleEscalate_YieldsOneOpenWorkItem` |
| `MaxAttempts`/`Delay` cadences | **must stay same** | untouched |
| `notification_id` / `RecipientRef` derivation (Inv-9) | **must stay same** | untouched |
| SLA classification of `ManualReview` | **must stay same** (C5 not changed) | `SlaEvaluatorTests` green |

### Invariant re-check

| Invariant | Status |
|---|---|
| Inv-5 queue safety | **held** — no new `IUnitOfWork`; a lost work-item race is now a no-op instead of a transaction abort, which *strengthens* it |
| Inv-6 capacity atomic | **held** — no write reordered; only lock acquisition moved and hoisted |
| Inv-7 ambiguous ⇒ reconcile | **held** — `CompensationRequired` still routes to compensation |
| Inv-13 tenant scope | **held** — new lock SQL and the new endpoint carry explicit tenant predicates; cross-tenant test added |
| Inv-14 DLQ ≠ business state | **held** — poison path still commits business state before dead-lettering; its work-item insert can no longer abort that transaction |
| Inv-15 typed human retries | **held** — no new automatic exit from `ManualReview` |
| Inv-16 authoritative outcomes | **strengthened** — parked pre-orders and abandoned reconciles are now durably tracked and enumerable |

**No unexplained deviations.**

### Scope note

One item beyond the plan's blast radius: `IOperationWorkItemRepository.AddAsync` was **removed**
after every caller moved to `TryAddOpenAsync`, leaving it dead. Left in place it is a loaded footgun
— the exact unguarded insert whose 23505 aborts the ambient transaction — sitting on the interface
for the next escalation path to find. Removing it is traceable to Phase 5's stated purpose
(prevent the regression, not just fix the instances) and is recorded here rather than made silently.
