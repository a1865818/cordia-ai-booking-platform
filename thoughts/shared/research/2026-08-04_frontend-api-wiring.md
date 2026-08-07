---
date: 2026-08-07
researcher: Claude (spec-loop Stage 1 — six passes)
git_commit: 343f220 (passes 1–2) · 77b4fd8 (pass 3) · e037c32 (passes 4–6)
branch: feat/ui-wire
repository: Cordia
topic: "Wiring apps/web to apps/api — consolidated baseline research"
tags: [research, baseline-audit, apps/web, apps/api, api-contract, react-query, canonical-hash, truthfulness, capacity, lock-ordering, state-ownership, manual-review, spec-04, spec-06, spec-07, spec-08, spec-11, spec-14, spec-15]
status: complete
consolidates:
  - 2026-07-27 frontend-api-wiring baseline
  - 2026-08-03 frontend-wiring gap closure baseline
  - 2026-08-04 console truthfulness baseline
  - 2026-08-07 residual review findings baseline (Part 4)
  - 2026-08-07 capacity deadlock + waitlist limbo baseline (Part 5)
  - 2026-08-07 capacity lock order + state ownership baseline (Part 6)
---

# Research — Wiring the Cordia Web Console to the Backend API

This is the single research record for the `feat/ui-wire` work. It merges the three baseline
audits taken before each implementation pass:

| Pass | Date | Question | Baseline labels |
|---|---|---|---|
| **1** | 2026-07-27 | What must change to replace the mock data layer with real API calls? | `1·B1`–`1·B9` |
| **2** | 2026-08-03 | What does the console still assert that the API does not record? | `2·B1`–`2·B10` |
| **3** | 2026-08-04 | Same question, for the shell, the drawers and the ids written into durable bookings | `3·B1`–`3·B9` |

Each pass's "Existing Feature Behavior" section is the **regression baseline** its Stage 6 diff
was checked against. They are kept separate rather than merged, because each states what the code
did *at that moment* — pass 2's baseline is partly pass 1's output.

The implementation record is `../plans/2026-08-04_frontend-api-wiring.md`; the review record is
`../reviews/2026-08-04_frontend-api-wiring.md`.

> **Note on `docs/frontend-api-wiring.md` references.** Section numbers cited below (§11 rules, §12
> gap-closure pass, §13 truthfulness pass) describe that guide **as it stood during this work**. Those
> sections have since been removed and the guide is back to its pre-implementation §1–§10 form, so the
> rules and verdicts quoted here are reproduced in full rather than left as pointers.

---

# Part 1 — Pass 1 baseline (2026-07-27, `343f220`)

**Companion:** `docs/ui-ux-blueprint.md` (Rev 3),
`thoughts/shared/research/2026-07-10_00-20-27_backend-ui-audit.md`

## Research question

What must change to replace `apps/web`'s mock data with real calls to `apps/api`, what is
genuinely wireable today, and what is the regression surface?

## Summary

- The web console is **100% mock-backed**. `apps/web/src/api/client.ts` exists but has **zero call
  sites**; every screen reads constants from `apps/web/src/data/console.ts`.
- The API is live and tenant-scoped: **17 controllers / 40 actions**. Tenant comes from a signed
  `X-Tenant-Context` JWS (spec 15) with an `X-Tenant-Id` **dev shim** that only applies when the
  signed path did not already set a tenant. `TenantGuard.RequireTenant` is fail-closed → HTTP 400.
- The **generated api-contract TS was stale** (generated 2026-07-11; `openapi/v1.json` committed
  2026-07-27 in `6884035` added `/api/channels/*`). Regenerated during this stage from the
  committed `v1.json`; it now has 39 paths / 78 schemas.
- The single biggest hidden cost is **`canon.v1`**: `POST /api/Bookings` requires the *client* to
  supply `revision.payload_hash` **and** `confirmation.intent_payload_hash`, and the server
  recomputes the canonical hash and rejects a mismatch with **422**
  (`ConfirmationHashMismatchException`). A booking cannot be created from the web app without a
  faithful TypeScript port of the `canon.v1` algorithm.
- The doc's per-screen verdicts hold: *action* surfaces (booking create/modify/cancel, waitlist
  actions, verification, menu/catalog) are wireable now; most *list* screens (Today, Queue,
  Conversations list, Customers, Deposits, Campaigns, most of Analytics) have **no endpoint** and
  stay on mock data.

## apps/web — current shape

| Concern | State | Evidence |
|---|---|---|
| Routes | `/` → HomePage, `/dashboard` → ConsolePage, `*` → NotFoundPage | `router.tsx:12-25` |
| Console | One route, 10 internal "screens" switched by `useState<Screen>` | `ConsolePage.tsx:34-37,70` |
| Server state | `useState(() => [...MOCK])` seeded from module constants | `ConsolePage.tsx:132-143` |
| "Loading" | fake `setTimeout(…, 540)` on screen change | `ConsolePage.tsx:188-190` |
| Mutations | `setTimeout` + local `setState` (create 1000 ms, cancel 850 ms, queue 900 ms) | `CreateBookingDrawer.tsx:75-86`, `ConsolePage.tsx:199-236` |
| Fetch layer | `fetchApi<T>` + `ApiError` + `isApiError` — **no call sites** | `api/client.ts:1-43` |
| Base URL | `window.APP_CONFIG?.API_URL`, else relative `/api` via Vite proxy | `client.ts:24-30`, `vite.config.ts:19-27` |
| Generated types | `@cordia/shared-schemas`, `@cordia/api-contract` are deps but **never imported** | `package.json:16-17` |
| Data-fetching lib | none (no react-query / SWR) | `package.json` |
| Styling | Tailwind v4 utilities + inline `style` for computed values; one stylesheet with an `@theme` token block | `tailwind.css:1-79` |
| Tests | **one** file, `src/router.test.tsx` (15 lines). No component/hook/api tests, no MSW, no `vi.mock` anywhere | `src/router.test.tsx` |
| Lint | `oxlint src/ --deny-warnings`, plugins react/typescript/unicorn | `project.json`, `.oxlintrc.json` |
| Typecheck | `tsc --noEmit`, `strict: true`, `moduleResolution: bundler` | `tsconfig.app.json` |

`apps/web/src/data/console.ts` (583 lines) is the whole "backend": hand-rolled types (`Booking`,
`Thread`, `Customer`, `Deposit`, `Campaign`, `QueueItem`, `WaitlistItem`, `Service`,
`TenantConfig`, `AnalyticsData`) + full literal datasets + formatters (`fmtT`, `fmtRange`,
`fmtDur`, `money`, `statusMeta`, `chMeta`, `idemKey`). It is imported by 18 files, including
`components/ui/` primitives (`StatusTags`, `Chip`, `Avatar`, `CommandPalette`) — so it is **not**
cleanly removable; the wiring must adapt API DTOs *into* these shapes, not delete them.

## apps/api — HTTP surface actually available

39 OpenAPI paths. Relevant to this work:

| Verb | Path | Response type | Wireable |
|---|---|---|---|
| GET | `/api/reporting/funnel?from&to` | `BookingFunnelView` | ✅ the one clean analytics read |
| GET | `/api/consent-policies?channel` | `ConsentPolicyView` | ✅ |
| GET | `/api/catalog/items?query&category&max_results` | anon list (`IActionResult` → `content?: never`) | ⚠️ untyped body |
| GET | `/api/catalog/items/{itemId}` | anon (`IActionResult`) | ⚠️ untyped body |
| GET | `/api/Bookings?phone_e164&booking_reference&email` | `FindManageableBookingsResponse` | ✅ identity lookup only |
| GET | `/api/Bookings/{bookingId}` | `GetBookingDetailsResponse` (200/403/404 all same body) | ✅ |
| POST | `/api/Bookings` | `OperationAcceptedResponse` 202 / `IdempotencyConflict` 409 / `ProblemDetails` 422 | ✅ needs canon.v1 |
| POST | `/api/Bookings/{id}/changes:validate` | `ValidateBookingChangeResponse` | ✅ |
| POST | `/api/Bookings/{id}/modifications` | `SubmitBookingModificationResponse` 202/409/422 | ✅ needs canon.v1 |
| POST | `/api/Bookings/{id}/cancellations` | `SubmitBookingCancellationResponse` 202/409 | ✅ |
| GET | `/api/booking-operations/{operationId}` | `OperationStatusView` | ✅ poll target |
| POST | `/api/verifications`, GET `/{id}`, POST `/{id}/verify` | `RequestVerificationResponse` / `CheckVerificationResponse` | ✅ |
| POST | `/api/waitlist/enrollments`, DELETE `/{id}`, POST `/offers/{id}/{accept\|decline}` | anon (`IActionResult`) | ⚠️ untyped body; `X-Actor` required |
| GET | `/api/conversation-sessions/{sessionId}/transcript` | `TranscriptView` | ✅ may need grant + `X-Actor*` |

**No endpoint exists** for: tenant-wide booking list, operations-by-status list, sessions list,
waitlist enrollment list, customers, deposits, campaigns, or richer analytics aggregates. Those
screens have no wireable read.

## Headers and auth

There is **no `[Authorize]` anywhere**. Access is gated by header shims:

- `X-Tenant-Id` — dev shim, `TenantResolutionMiddleware.cs:21,29,33`, applied **only if**
  `TenantContextVerificationMiddleware` (signed `X-Tenant-Context` JWS) did not already set the
  tenant (`:28` guard). Missing → `TenantGuard` throws `TenantScopeMissingException` → 400.
- `X-Correlation-Id` — `CorrelationMiddleware.cs:22,34-38,44`; echoed or minted as `corr_{guid}`;
  must match `^[A-Za-z0-9_-]{1,64}$`.
- `X-Actor` / `X-Actor-Role` / `X-Reason-Code` — required on booking-operation `resume`/`resolve`,
  all Menu actions, Preorder actions, DLQ replay, transcript read, model-trace read, and
  (`X-Actor` only) every Waitlist action.
- `X-Grant-Id` (or `?grant_id=`) — `BookingsController.cs:50,56`,
  `ConversationSessionsController.cs:102`.
- CORS: `Cors:AllowedOrigins` = `["http://localhost:4200"]` in `appsettings.Development.json:14-16`;
  **empty** in `appsettings.json:24-26`.

## Error contract (what the client must parse)

`GlobalExceptionHandler.cs` maps ~29 typed exceptions. Load-bearing for the UI:

| Exception | Status | UI consequence |
|---|---|---|
| `ValidationException` (FluentValidation) | 400 `ValidationProblemDetails` (`errors` map) | field-level messages |
| `ConfirmationHashMismatchException` | 422 | canon.v1 drift — a hard bug, not user error |
| `IdempotencyConflictException` | 409 with **custom snake_case `IdempotencyConflict` body**, not ProblemDetails | must not be parsed as ProblemDetails |
| `TenantScopeMissingException` | 400 | missing tenant header |
| `TenantContextVerificationException` | 401 | signed-context failure |
| `VerificationRequiredException` | 403 | mount the verification/OTP flow |
| `AuthorizationFailedException` | 403 | "no access" state |
| `StaleBookingVersionException` | 409 | reload + retry with fresh version |
| `OperationConflictException`, `InvalidStateTransitionException` | 409 | reload |
| `ConsentRequiredException` | 409 | consent gate |

Note also: `GET /api/Bookings/{id}` returns **200/403/404 with the same `GetBookingDetailsResponse`
body** (`status: ok | requires_verification | not_found`) — so a 403 there is a *typed business
response*, not a ProblemDetails error, and the client must read the body rather than treat it as a
bare failure.

## canon.v1 — the load-bearing constraint

`POST /api/Bookings` acceptance flow (`SubmitConfirmedIntentCommandHandler.cs:53-67`):

1. Server serializes `revision.canonical_payload` with the generated `Converter.Settings`.
2. `ICanonicalRequestHasher.Hash(node)` computes `H` (`CanonicalRequestHasher.cs`).
3. If `confirmation.intent_payload_hash != H.Value` → **422**.
4. `revision.payload_hash` is stored bare and a **DB trigger** asserts
   `intent_confirmations.intent_payload_hash = booking_intent_revisions.payload_hash`
   (handler comments at `:208-209`, `:225-226`) — so both client-supplied hashes must equal `H`.

`SubmitConfirmedIntentValidator.cs` additionally requires (all 400 if missing): `operation_id`,
`idempotency_key`, `revision.{intent_id, schema_version, tenant_policy_version, canonical_payload,
payload_hash, source_session_id}`, `confirmation.{confirmation_id, intent_payload_hash, identity,
source_turn_id, read_back_version, verification_level}`, plus `confirmation.intent_id ==
revision.intent_id` and `confirmation.intent_revision == revision.revision`, plus
`target_booking_id` for modify/cancel/reschedule.

**canon.v1 algorithm** (`CanonicalRequestHasher.cs:11-37`, version label `canon.v1`): object — drop
null / empty-object / empty-array values, NFC keys, sort keys ordinal; string — NFC, trim, collapse
internal whitespace runs to one space, preserve case, except ISO-8601-with-offset which is parsed to
UTC and re-emitted `yyyy-MM-ddTHH:mm:ss.fffZ` with **truncation** (floor) to ms; number — integers
normalized via BigInteger (`5e2` = `500` = `500.0`), fractions via shortest round-trip; array —
canonicalize elements then sort (by `item_id`, then `modifier_id`, then full canonical JSON); encode
compact UTF-8 JSON → SHA-256 → lowercase hex. Null/empty payload hashes `{}`.

**The Python side is a stub** and does *not* match:
`apps/langgraph/cordia_agent/revisions/manager.py:28-41` is
`sha256(json.dumps(model_dump(mode="json"), sort_keys=True, separators=(",",":")))`, explicitly
labelled "deterministic stand-in". So there is no existing correct non-C# implementation to copy —
the TS port must be written against the C# source and pinned to golden vectors produced by the real
C# hasher.

## Existing feature behavior — pass-1 baseline

### 1·B1 · `apps/web/src/api/client.ts` (shared code, 0 consumers today)

- `ApiError(status, message)` with `name = "ApiError"`; `isApiError()` type guard.
- `resolveUrl`: `window.APP_CONFIG?.API_URL` (trailing slash stripped) else the path verbatim.
- `fetchApi<T>`: sends `Accept: application/json` merged with `init.headers`, **then spreads `init`
  after `headers`** — meaning `init.headers` currently *overwrites* the merged object (a latent bug:
  `{ headers: {...}, ...init }` puts `init.headers` last).
- Non-2xx → `ApiError(status, "Request to {path} failed: {status}")`; no body parsing.
- Always `response.json()` — a 204 would throw.
- **Blast radius: none today (no callers).** Changing it cannot regress a screen.

### 1·B2 · ConsolePage state machine and screen contracts

- Owns *all* console state: `screen`, `loading`, `selDay`, `convOpen`/`convView`/`takenOver`,
  `autoBook`/`waitlist`, `customerFocus`, `services`/`tenantCfg`, `bookings`/`queue`/`busy`,
  `drawer`, `cancelBusy`, toasts, `cmdOpen`, `sidebarCollapsed`, `scrolled`.
- `useDevice()` → `'phone' | 'desktop'` at the 768 px breakpoint; sidebar auto-collapses ≤1100 px.
- `go(next)` sets screen and starts the fake 540 ms skeleton; resets conversations to list view on
  phone.
- Screens are **presentational**: they receive data + callbacks as props and never fetch. Any wiring
  must either keep that contract (fetch in ConsolePage / a hook, pass down) or change each screen's
  props — the latter is a wider blast radius.
- ⌘K opens the command palette; timers (`ltRef`, `btRef`, `qtRef`) are cleared on unmount.
- Sign-out navigates to `import.meta.env.BASE_URL`.

### 1·B3 · Mock mutation flows (to be replaced; behavior preserved or bettered)

- **Create** (`CreateBookingDrawer.tsx:75-86`): `phase: 'form' → 'confirming' → 'done'`; 1000 ms fake
  latency; on done calls `onCreated(Omit<Booking,'id'>)`; ConsolePage appends with
  `id = 'new' + Date.now()`. Drawer resets all fields on `open`. Guard: no services → an explicit
  empty state and **no footer button**.
- **Cancel** (`ConsolePage.tsx:199-217`): 850 ms; sets status `cancelled`; toast with a 5 s **Undo**
  that restores the previous status; `cancelBusy` prevents re-entry.
- **Reschedule** (`onRescheduleDone`): patches `{day, start, staff, dur?}` in place. Also reached by
  **drag** on TodayScreen (`onDragReschedule`).
- **Queue finish/dismiss**: 900 ms / immediate, both remove the row; dismiss offers Undo.
- **Waitlist** `toggleWaitlistAuto` / `removeFromWaitlist` (with Undo toast).
- All are optimistic and never fail. Real endpoints introduce failure and async (202 + poll)
  semantics that the current UI has no vocabulary for.

### 1·B4 · Analytics screen

`AnalyticsScreen.tsx` reads `ANALYTICS_BY_RANGE[range]` for `'7d' | '30d' | '90d'` and renders KPI
grid, per-day bars, channel split, top services, agent-vs-human split.
`GET /api/reporting/funnel` returns only `{tenantId, from, to, accepted, confirmed, ambiguous,
failedTerminal, notified, notifyPending}` — it covers **none** of `perDay`, `channels`,
`topServices`, `handled`. Only a *funnel* sub-view is wireable; the rest must stay mock and be
labelled as such.

### 1·B5 · Settings screen

`SettingsScreen(services, tenantCfg, onUpdateServices, onUpdateTenant, device)` — all local state, no
persistence. `GET /api/consent-policies` and `GET /api/catalog/items` are additive read-only
surfaces; hours/guardrails/knowledge have no endpoint.

### 1·B6 · Type-generation consumers

- `libs/api-contract/generated/typescript/index.ts` — consumed by web only. **Was stale**;
  regenerated from the committed `libs/api-contract/openapi/v1.json` (39 paths, 78 schemas).
- `libs/shared-schemas/generated/typescript/{index,zod}.ts` — present (2026-07-11).
  `libs/shared-schemas/schemas/` **does not exist** in this checkout, so `shared-schemas:generate-types`
  cannot be re-run without first running `langgraph:generate-schemas` (uv/Python). No Pydantic
  contract is being changed by this work, so the existing generated output stands.
- Neither package is imported by any `apps/web` source file today.

### 1·B7 · Existing automated coverage that must stay green

- `apps/web`: `bun nx test web` → the single `router.test.tsx`; `bun nx lint web` (oxlint
  `--deny-warnings`); `bun nx typecheck web`; `bun nx build web`.
- `apps/api`: `bun nx test api` (Category=Unit) — includes
  `UnitTests/Idempotency/CanonicalRequestHasherTests.cs` (34 property-based assertions, no pinned hex
  constants) and `SubmitConfirmedIntentValidatorTests.cs`. `bun nx test-integration api` (Alba)
  includes `IntegrationTests/Persistence/ConfirmationHashBindingTests.cs`.
- No API source change is required for Phases 0–4 of the doc's plan, so the .NET suites are a pure
  regression check unless golden-vector fixtures are added.

### 1·B8 · Release-blocking invariants touched by this work

| Inv | Statement | How the current code upholds it | Risk from wiring |
|---|---|---|---|
| 1 | No side effect from transcript text alone | Web has no submission path at all | New write paths must go through `POST /api/bookings` only |
| 2 | Stable `operation_id` + idempotency key | n/a (no client) | Key must be **minted once per attempt and reused across retries**, never inside a retry loop |
| 3 | Key bound to one canonical request hash | Server-side (`SubmitConfirmedIntentCommandHandler.cs:78-88`) | A client that re-mints the payload but reuses the key produces a 409 the UI must surface |
| 4 | Acceptance + operation + outbox atomic | Server-side | untouched |
| 10 | Material change ⇒ new revision + confirmation | Server-side + validator binding rules | The client must bump `revision` and re-confirm on material edits |
| 11 | Mutations require server-verified authorization | `grant_id` on modify/cancel | The UI must implement the verification flow rather than omit `grant_id` |
| 12 | Card data never in agent/transcript/queue/logs | No payment UI exists | Must stay true — **no card fields in any new form** |
| 13 | Tenant scope before data access | `TenantGuard` fail-closed | The client must send a tenant on every request; a wrong/absent tenant must fail visibly, not silently |
| 16 | Agent states only authoritative outcomes | n/a | The UI must render **server** operation status, not an optimistic guess |

### 1·B9 · Registry position of this work

`docs/specs/README.md` lists **"apps/web operations console UI"** under *Known deferred items
(tracked, not yet owned by a spec)*. Consequently:

- This work **owns no shared contract**. Every contract it touches (`SubmitConfirmedIntentRequest`,
  `IntentRevision`, `Confirmation`, `OperationStatusView`, canon.v1, `VerificationGrant`) is
  **referenced** and must match its owner byte-for-byte: 04 (submission, canonical hash, idempotency
  disposition), 06 (`NormalizedOperationStatus`), 08 (verification grant / manage-booking), 07
  (`AvailabilityMode`), 15 (`TenantContext`).
- Adding **new API endpoints** would create API surface no spec owns — a scope decision for the
  approval gate, not something to do silently.

## Pass-1 open questions (answered at the Stage-3 gate)

1. **New list endpoints** — in or out of this run? *(Answer: in — all six.)*
2. **Verification-grant UI** — build the OTP flow, or wire modify/cancel without `grant_id` and
   surface the 403? *(Answer: build it.)*
3. **`X-Actor` identity** — waitlist/menu/operator writes require an actor and there is no auth, so
   the value must come from config. What is acceptable for dev? *(Answer: `VITE_DEV_ACTOR`.)*

---

# Part 2 — Pass 2 baseline (2026-08-03, gap closure)

**Goal:** bring `apps/web` + the console-facing slice of `apps/api` into full conformance with
`docs/frontend-api-wiring.md`, closing the fourteen gaps (G1–G14) catalogued after pass 1.

**Method note:** the gap catalogue was produced earlier in the same session by direct file reads and
by running every lane's test/lint/build target. No research sub-agents were re-spawned — the primary
evidence was firsthand. What follows adds the contract-level detail the plan needed.

## Summary

Fourteen gaps. Three are **not** frontend-wiring work and are excluded by the wiring doc's own words
or by spec ownership: G1 (modify/reschedule fulfilment — "spec 06/08 work, not frontend wiring"),
the G3 contract half (no ratified way to attach a guest identity to a staff-made booking), and the
deposits/campaigns screens (no spec owns them). Everything else is closable inside `apps/web` plus
two small, additive `apps/api` changes.

The single largest live defect is **G2**: the console fetches the real current week and labels it
with a hard-coded mock week (`9–15 June`). Live bookings therefore carry fabricated dates on the
grid, in the detail drawer, in the reschedule drawer and in the create drawer's day picker.

## Identity model (settles G3)

- `BookingRequest` (`apps/langgraph/cordia_agent/state.py:75-123`) has **no** customer identity or
  contact field, by design: `operation, service_id, location_id, starts_at, party_size,
  duration_minutes, special_requests, preorder, target_booking_id, target_booking_version`.
- `Confirmation.identity` (`apps/langgraph/cordia_agent/models/intent.py:56-58`) is documented as
  *"Verified identity, or 'anonymous_session' sentinel."* — it is a **verified identity reference**,
  not a free-text name.
- The console currently sends the guest's typed name there (`CreateBookingDrawer.tsx:145`
  `identityRef: name.trim()`), which the server surfaces as `customer_ref` in `/api/customers`. Both
  cancel and reschedule already send `getActor()` instead, so the create path is also **internally
  inconsistent**.
- ⇒ The web-side fix is unambiguous: send the operator identity, and stop presenting a name the
  platform does not transport. Attaching a *guest* to a staff-made booking needs a spec 04/08/12
  decision (new payload field or an identity-registration call) and is out of scope.

## Spec-registry position

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
except the api-contract TS if a controller signature changes.

**Type-generation blast radius:** none. `libs/shared-schemas/**` and `libs/api-contract/**` stay as
generated today (44 paths / 46 operations, verified current). The `IActionResult → ActionResult<T>`
follow-up *would* regenerate api-contract — deliberately **out of scope**.

## Existing feature behavior — pass-2 baseline

### 2·B1 · Console read surface (`features/hooks.ts`, `pages/ConsolePage.tsx`)

Five `useQuery` hooks fire on mount regardless of active screen: schedule, operations
(`needsAttention: true, limit 100`), sessions (50), waitlist enrollments (`activeOnly, 100`),
customers (`"", 50`). Each feeds an adapter → the mock-shaped component types. `loadFailures`
(`ConsolePage.tsx:612-618`) covers exactly these five and renders one `ErrorState` naming them. Demo
fallback happens **only** when `query.data === undefined` (never on an empty array). `truncated`
renders a `PartialBadge` on Today and Customers.
**Upholds:** the "empty is a real answer" rule; Inv-16 for these five. **Must stay the same:** all of it.

### 2·B2 · Calendar dates (`data/console.ts:275`, `TodayScreen.tsx:922-944`)

`WEEK` is a frozen literal (`doms 9..15`, `month 'June'`, `today 3`). It supplies every date label in
TodayScreen, the ConsolePage header subtitle (`:596-602`), BookingDetailDrawer (`:62`),
RescheduleDrawer (`:131`), and `DAY_OPTS` used by CreateBookingDrawer (`console.ts:593`).
`weekWindow()` (`features/bookings/adapters.ts:32`) independently computes the **real** current
Monday→Monday window; `toDayAndStart` maps live `starts_at` to a 0-based weekday index.
**Currently violates** the "nothing asserted that the API does not record" rule. **Intentionally changing.**

### 2·B3 · Queue (`QueueScreen.tsx`, `features/operations/adapters.ts`)

Adapter maps `OperationSummary` → `QueueItem` with `retries: 0` and `channel: "web"` hard-coded, and
`name` falling back to `booking_id ?? intent_id ?? operation_id`. The screen renders a
`waitingTime(id, retries)` string that is `MOCK_WAIT[id] ?? retries*9+8` minutes, a `chMeta(channel)`
badge, `"{retries} retries"`, and a primary button labelled *Finish booking* / *Re-booking* wired to
`finishQueue`, which is a **local dismissal** keyed by `dismissalKey = id|status` so an escalation
resurfaces the row.
**Upholds:** the dismissal-key design (keep it). **Violates:** fabricated wait/retries/channel and a
button label promising a server action. **Intentionally changing** (the fictions and the label); the
dismissal semantics **must stay**.

### 2·B4 · Conversations (`features/conversations/*`, `ConsolePage.tsx:653-705`)

Session list is live; `toThread` sets `status: "pending"` and `prov: "ai"` for every session because
`ConversationSessionSummary` carries neither. Transcript is fetched only for live session ids and
only after the operator picks a `SensitiveReadPurpose`; the query has `staleTime: Infinity`,
`refetchOnWindowFocus/Reconnect: false` (deliberate — one audit row per deliberate read).
`liveMessages` falls back to the mock `MESSAGES` map when `transcriptQuery.data` is undefined, and
`ConversationsScreen.tsx:212` renders `messages[active.id] || []` ⇒ a failed/403 transcript read shows
an **empty thread with no error**. `transcriptQuery` is absent from `loadFailures`. `getTranscript`
accepts a `grantId` nothing passes. `takeOver` is local state.
**Must stay the same:** the purpose gate, the no-refetch policy, the live-session-only rule.
**Intentionally changing:** the error path, the fabricated `status`/`prov`.

### 2·B5 · Waitlist (`features/waitlist/*`, `WaitlistScreen.tsx`)

`listEnrollments` is live and includes `latest_offer_status` / `latest_offer_expires_at`.
`cancelEnrollment` is real, audited via `actor: true`, and optimistic with rollback
(`useRemoveFromWaitlist` — snapshot → rollback → `onSettled` refetch), covered by 2 vitest cases.
`acceptOffer` / `declineOffer` exist and are **called from nowhere**. There is no `enroll` function.
The master auto-book toggle and the per-guest Auto/Manual chip mutate local state only, although
`auto_book_authorized` is a **consent grant** (spec 11 §20 / Inv-1). "Book now" opens an empty create
drawer.
**Must stay the same:** removal semantics + its tests. **Intentionally changing:** offer actions
become real; the consent toggles stop lying.

### 2·B6 · Booking writes (`features/bookings/*`)

- **Create:** `buildSubmission` builds the canonical payload, hashes once with `canon.v1`, sets both
  `revision.payload_hash` and `confirmation.intent_payload_hash` from it. Attempt identity is minted
  per drawer-open and reused across retries (`getOrCreateAttempt`). Submit → 202 →
  `useOperationStatus` polls; the drawer only reports success on a `booked` status, guarded by
  `announcedRef`. `identityRef` is the **typed guest name** (the defect); `verification_level`
  hard-coded `staff_confirmed`.
- **Cancel:** `useCancelBooking` runs read-version → submit → poll inside one `mutationFn`.
  `getBookingDetails(bookingId)` is called **without** the grant that is in scope. `reason_code`
  hard-coded `operator_request`. Reports `cancelled` only on a settled `booked` status.
- **Reschedule:** fully built (payload, hash, identity, version from a real read, polling, in-flight
  guard) but gated off by `RESCHEDULE_FULFILMENT_SUPPORTED = false`; `onRescheduleDone` refuses and
  does not move the row.

**Must stay the same:** hash binding, attempt-identity reuse, `retry: false` on mutations, the
terminal-status wording split (`normalizedStatusLabel(status, 'cancel')`), the reschedule gate, all
221 vitest cases. **Intentionally changing:** `identityRef` on create; the grant on the cancel pre-read.

### 2·B7 · Verification (`features/verification/*`)

`VerificationGate` (request → code → grant, in-memory only) is mounted **only** for cancel
(`ConsolePage.tsx:843-853`), obtained *before* the first submit because `grant_id` is
`NotEmpty`-validated server-side. A 403 carrying `code: "verification_required"` re-opens the gate
(`isApiVerificationRequiredError`). `getBookingDetails` throws on the typed 403 and no caller catches it.
**Must stay the same:** gate-before-submit ordering, in-memory-only grant, matching on `code` not
`title`. **Intentionally changing:** the gate becomes reachable from gated reads.

### 2·B8 · Settings (`SettingsScreen.tsx`)

Consent policy (`/api/consent-policies?channel=web`) and catalog items (`/api/catalog/items`) are live
with their own pending/error/empty states. Services, hours and deposit policy are demo **and badged**.
Business setup, agent guardrails and the knowledge base are demo and **unbadged**, with controls that
appear to save.
**Must stay the same:** the two live panels. **Intentionally changing:** badges on the three unbadged panels.

### 2·B9 · Tenancy / actor (`api/config.ts`, `Program.cs:169-181`)

`fetchApi` sends `X-Tenant-Id` (from `window.APP_CONFIG.TENANT_ID` → `VITE_DEV_TENANT_ID` → `"demo"`)
and a minted `X-Correlation-Id` on every request; `X-Actor`/`X-Actor-Role` on `{actor: true}`;
`X-Grant-Id` on `{grantId}`; `Content-Type` only for string bodies. Server pipeline is Correlation →
**TenantContextVerification** → **TenantResolution (shim)**; the shim is gated to never override a
verified signed context (`TenantResolutionMiddleware.cs:28`) but is registered with **no environment
gate**, so where no signed context exists any client can assert a tenant.
**Must stay the same:** header set, shim subordination to the signed context, `TenantGuard`
fail-closed. **Intentionally changing:** the shim becomes non-production.

### 2·B10 · Test coverage that must stay green

`bun nx test web` **221**; `bun nx test api` **1170**; `bun nx test langgraph` **1007**;
`bun nx test-integration api` **413 passed / 1 failed / 2 skipped** — the failure
(`RetentionSweepTests.Sweep_PrunesRecord_AndEmitsAuditEvent_WhenTerminalOpAndPastDeadline`, line 88)
**passes 6/6 in isolation**: at the time diagnosed as shared-database/parallel-collection
contamination. *(That diagnosis was wrong — see the plan's Phase 8 discoveries: it is a PAN-detector
false positive on random hex ids.)*

## Pass-2 open questions for Stage 3

1. **G1** (modify/reschedule fulfilment) — in or out? The wiring doc calls it "spec 06/08 work, not
   frontend wiring". *(Answer: out.)*
2. **G3 contract half** — a guest identity on staff-made bookings needs a spec 04/08/12 decision.
   *(Answer: web-side honesty now, contract decision deferred.)*
3. **G4** (commit/push/PR) — the user must confirm they want commits made.

---

# Part 3 — Pass 3 baseline (2026-08-04, console truthfulness)

**Base commit:** `77b4fd8` · **Lanes:** web (primary), api (read-only inspection), langgraph
(inspection only — out of scope) · **Type-generation boundary crossed:** NO ·
**Security-sensitive:** YES (canonical booking payload contents; spec-14 customer minimisation)

## Goal

`docs/frontend-api-wiring.md` §11 states two rules the console holds itself to:

> - **A successful-but-empty response is a real answer.** Screens fall back to demo rows only while a
>   list has *never* loaded, never to fill in an empty result.
> - **Nothing is asserted that the API does not record.**

and one badging rule:

> Everything still on demo data carries a visible **Demo data** badge, so a plausible number cannot be
> mistaken for the venue's own.

Passes 1–2 enforced these across the ten **screens**. The follow-up audit (R1–R15) found they do not
hold for the **shell**, the **More** tab, the **create drawer's form body**, the **resource
dimension**, or the **service ids the console writes into durable bookings**.

## Registry position

| Contract / mechanism | This change's position | Constraint |
|---|---|---|
| `Confirmation` (`identity`, `verification_level`) | **referencer** — spec 04 owns it | Must send a value from the owner's vocabulary; may not extend it |
| `BookingRequest.service_id` (canonical payload) | **referencer** — spec 04 owns the payload; spec 07/11 own the catalog | May change *which* id the console sends; may not add a field |
| `/api/customers` projection | **referencer** — spec 12 owns tables, spec 14 owns minimisation | Client-side presentation only; no DDL, no new endpoint |
| Console read models (`ConsoleReadModel`) | **inspection only** | Not modified |
| `DemoBadge` / `PartialBadge` | web-local convention | Reuse, do not replace |

**Frozen decisions in play:** D5 (`verification_action`), D7 (durable acceptance front door), D17
(ratified table set). **Invariants:** Inv-1, Inv-3, Inv-12, Inv-16.

**Type-generation blast radius: none.** Deliberately — every alternative that *would* have crossed
the boundary (adding `staff_confirmed` to the verification vocabulary, adding a resource field to
`BookingRequest`, adding a tenant-profile endpoint) is a spec decision, recorded as out-of-scope below.

## Existing feature behavior — pass-3 baseline

### 3·B1 · Console shell identity (Sidebar / Header / MoreScreen)

**Today:** three independent hard-coded literals, not read from any constant or endpoint.

- `console/Sidebar.tsx:117` avatar `"MC"`, `:121` `"Maya Chen"`, `:122` `"Bella Nails · Owner"`.
- `console/Header.tsx:70` `WorkspaceBtn` renders `"Bella Nails"` with a `"B"` avatar and a chevron
  implying a workspace switcher that does nothing.
- `screens/MoreScreen.tsx:50,52,53` the same three strings again.
- `data/console.ts:297-302` `TENANT` carries `name: 'Bella Nails'` — **and is not what those three
  components read**; they duplicate the literal.

`ConsolePage.tsx:237` seeds `tenantCfg` from `TENANT`, and `:806` passes `tenantCfg.resourceLabel`
(*"Technician"*) into the **live** `TodayScreen`. `SettingsScreen` renders and edits `tenantCfg`
locally under a *"no tenant-profile endpoint"* badge.

**Truth available:** `api/config.ts` exposes `getTenantId()` (sent as `X-Tenant-Id` on every request)
and `getActor()` / `getActorRole()` (sent as `X-Actor` on staff writes). These are what the platform
actually attributes the session to. No endpoint returns a venue display name or an operator profile;
no `[Authorize]` exists anywhere in `apps/api/src`.

**Invariant upheld today:** none. **Coverage:** none — no test renders `Sidebar`, `Header` or `MoreScreen`.

### 3·B2 · MoreScreen row sub-labels and nav badges

**Today (`MoreScreen.tsx:11-16`):** every row's sub-label is a literal or derived from a mock.

| Row | Sub-label source | Live equivalent available? |
|---|---|---|
| Waitlist | `${WAITLIST.length} waiting · auto-book ${autoText}` — mock array length | **Yes** — `ConsolePage` holds `waitlist` from `/api/waitlist/enrollments` |
| Deposits | `${money(PAYOUT.held)} held · next payout …` — mock money | No — spec 09 deferred |
| Campaigns | `'1 awaiting your approval'`, `badge: 1` | No — no spec owns campaigns |
| Analytics | `'Bookings, no-shows, AI handled'` | Static copy, no claim |
| Settings | `'Business, services, hours, agent'` | Static copy, no claim |

`ConsolePage.tsx:422` separately hard-codes `const campCount = 1`, threaded to `Sidebar` (`:980`) and
painted as an amber badge on the desktop Campaigns nav item (`Sidebar.tsx:100`). The literal `1`
therefore exists in two unrelated files. `MoreScreen` imports `DemoBadge` **nowhere**. On phone this
is the primary navigation surface.

### 3·B3 · CreateBookingDrawer deposit panel

**Today (`CreateBookingDrawer.tsx:377-385`):** the form body renders `{money(service.dep)} deposit`,
sub-copy *"Mirrors your deposit policy"*, and an **"Auto"** chip. `service.dep` comes from the
hand-written `SERVICES` map (`data/console.ts:315-321`).

The **confirmation** panel in the same file (`:268-271`) deliberately omits the deposit, with a
comment: *"the deposit here comes from the hand-written mock catalog — the platform defers payments
(spec 09) and never requested or collected anything. Telling the operator a hold is pending would be a
fabricated fact about money."*

So the file already contains the correct ruling; it was applied to the post-submit panel only.

### 3·B4 · Resource / staff dimension

**Today:** fabricated end to end, and the fabrication is load-bearing in the grid's layout maths.

- `data/console.ts:325-330` — `STAFF`: four invented people with `role` and `skills`.
- `TodayScreen.tsx` imports `STAFF` directly (`:2`) and uses it for: `yToStaffIdx` clamp (`:40`),
  day-view lookup (`:197`), `gridH = STAFF.length * ROW_H` (`:247`), `staffY` map (`:250`), drag lane
  resolution (`:305`), the label column (`:449`), row stripes (`:485`), and two more booking-card
  lookups (`:666`, `:816`). `hasConflict` (`:29-36`) keys collision detection on `b.staff`.
- `features/bookings/adapters.ts:133` — **every live booking is assigned `STAFF[0]`**, so a real day
  stacks in lane one beside three empty invented lanes.
- `CreateBookingDrawer.tsx:123` `const eligible = STAFF`, rendered as a **"With"** select (`:346-348`);
  the chosen `staff` reaches only the optimistic row (`:137`) and is never submitted.
- `RescheduleDrawer.tsx:85` `STAFF.filter(s => s.skills.includes(b.service))` — a second picker.
- `ConsolePage.tsx:567,600-684` — `onRescheduleDone` receives `patch.staff` from a lane drag and
  `submitReschedule` **discards it**: `buildSubmission` has no staff field.

**Server truth:** `BookingRequest` models no resource. `ScheduledBookingView` returns none. There is
no resources endpoint in the 44-path contract. **Coverage:** `adapters.test.ts` pins
`staff: STAFF[0].id`; nothing tests the grid.

### 3·B5 · Service catalogue used for submission

**Today:** two disconnected service concepts on screen at once.

- `ConsolePage.tsx:236` — `services` state seeds from `SERVICE_LIST` (derived from the mock `SERVICES`
  map: `gel`, `sns`, `acrylic`, `pedi`, `lash`).
- `SettingsScreen.tsx:429` — `ServicesManagement` edits that local array, badged *"no services
  endpoint — changes are not saved"* (`:427`).
- `SettingsScreen.tsx:340,388-420` — a **live** `useCatalogItems()` list from `GET /api/catalog/items`,
  rendered read-only under *"Menu catalog"*.
- `CreateBookingDrawer.tsx:344` — the Service select is fed from the **mock** `services` prop, and
  `:175` submits `serviceId: svc` — a mock key — into `buildSubmission`, which places it in
  `revision.canonical_payload.service_id`, which is hashed by `canon.v1` and persisted as the durable
  intent.

So every console-created booking carries a `service_id` that exists in no catalog on the server. The
Settings badge is accurate about persistence and silent about this.

`SERVICES` is *also* used to **name** live rows in three adapters (`bookings/adapters.ts:122`,
`customers/adapters.ts:46`, `waitlist/adapters.ts:74`) and `AnalyticsScreen.tsx:225` — each falls back
to the raw id when the lookup misses, which is honest and must stay.

### 3·B6 · `/api/customers` after the identity fix

**Today:** `CreateBookingDrawer.tsx:192` sends `identityRef: getActor()` — a constant, default
`"console-operator"`. `submit.ts` does the same for cancel and reschedule.

`ConsoleReadModel.BuildCustomerResolverAsync` (`ConsoleReadModel.cs:451-500`) resolves a customer as
`bookings.customer_id` when a booking row exists, else the confirmation's `identity_ref`. Console
bookings have no `customer_id` (there is no guest), so **every console-created booking projects under
the operator's id**, collapsing into one `CustomerSummary` whose `booking_count` is the number of
staff-made bookings. `customers/adapters.ts:37-38` maps `customer_ref` to both `id` and `name`, so
`CustomersScreen` renders the operator as a guest named `console-operator`.

**Inv-12 holds** (no PII in the ref — that was the point of the fix). **Inv-16 is violated:** the list
asserts the operator is a customer.

### 3·B7 · `verification_level` is silently coerced 🔴 NEW — not in either audit

**Today:** the console sends `verification_level: "staff_confirmed"` (`buildSubmission.ts:113`,
`submit.ts:106`).

`SubmitConfirmedIntentValidator.cs:73-75` enforces **`NotEmpty()` only**. The handler then calls
`ConfirmationMappings.MapVerificationLevel` (`ConfirmationMappings.cs:34-47`), whose ratified
vocabulary is `unverified | channel_verified | identity_verified | high_assurance` and whose default
arm is `_ => VerificationLevel.Unverified`.

**So `"staff_confirmed"` is stored as `unverified`.** The console asserts an assurance level the
platform discards — the same class of defect as the queue's invented wait time, on a write.

`ConfirmationMappings.IsKnownVerificationLevel` exists precisely to prevent this and is applied by the
**waitlist** edge validator, not by the booking submit validator. Its own XML doc says unknown values
would otherwise be *"silently coerced to Unverified … long after the customer enrolled."*

**The langgraph lane has the same defect**, differently spelled: `await_confirmation.py:161` sends
`state.verification_level or "none"`, and ~20 test sites use `"none"`, `"sms"`, `"session"` — none
ratified. Tightening the server validator would break the langgraph lane and is a **spec-04 vocabulary
decision**, not a wiring fix.

### 3·B8 · Small assertions

- `conversations/adapters.ts:22-24` — `toChannel` maps API `email` **and** `web` both to mock `web`, so
  an email conversation renders a *Website* channel badge. The mock `Channel` union has no `email`
  member and `chMeta` has no entry for one.
- `data/console.ts:240` — `money = n => 'A$' + n`; `CatalogItem` carries a `currency` field the console
  ignores. Reaches MoreScreen (3·B2), CreateBookingDrawer (3·B3), DepositsScreen (badged),
  CustomersScreen `spend` (suppressed on live rows). **After 3·B2 and 3·B3 are fixed it only reaches
  badged demo surfaces**, so no change is required — recorded so the diff can prove it.
- `AnalyticsScreen.tsx:149` — accents a bar via `i === WEEK.today` when the series is the **mock** one
  (badged). Last live-file reference to the frozen week literal.
- `data/console.ts:624-627` — `DAY_OPTS`, built from `WEEK`, is imported by nothing (dead since the
  pass-2 work replaced it with `dayOptions(week)`).

### 3·B9 · Existing automated coverage that must stay green

| Suite | Count | Relevance |
|---|---|---|
| `bun nx test web` | 302 / 19 files | `adapters.test.ts` pins `staff: STAFF[0].id` (3·B4) and `SERVICES.gel` naming (3·B5); `submission.test.ts` pins canonical payload + hash; `mutations.test.tsx` pins cancel/offer flows; `WaitlistScreen`/`ConversationsScreen`/`CommandPalette` component tests pin badge behaviour |
| `bun nx test api` | 1176 | `ConsoleReadModel` resolver tests, `SubmitConfirmedIntent` handler/validator tests |
| `bun nx test-integration api` | 419 (+2 skipped) | End-to-end submit/cancel; `verification_level` persisted by `SubmitConfirmedIntentCommandHandler` |
| `bun nx test langgraph` | ~1007 | Untouched — but ~20 sites assert `verification_level="none"`, which is why 3·B7's server half is out of scope |

**No test renders `ConsolePage`.** This is the reason every one of 3·B1–3·B6 survived two review passes.

---

# Established patterns to follow (web)

- **Component tests** (`WaitlistScreen.test.tsx`, `ConversationsScreen.test.tsx`,
  `CommandPalette.test.tsx`): `render()` directly, no providers, a `renderScreen(props = {})` helper
  spreading `Partial<Parameters<typeof C>[0]>` over defaults, `screen.getBy*`/`queryBy*` +
  `fireEvent`. Each `it` carries a comment naming the invariant it pins.
- **Hook/page tests needing react-query** (`mutations.test.tsx`): module-level
  `vi.mock("../feature/api", () => ({ fn: vi.fn() }))` **above** the import, a fresh
  `QueryClient({ retry: false })` per render via `QueryClientProvider`, `renderHook`/`render` + `act` +
  `waitFor`. **No MSW in this repo** — do not introduce it.
- **Transport tests** (`client.test.ts`): `vi.stubGlobal("fetch", spy)` / `vi.unstubAllGlobals()`.
- **Badges assert as text**: `screen.getByText("Demo data")` (or `getAllByText` when several render),
  `screen.getByTitle(/demo data/i)` for the reason.
- Test env: `jsdom`, `globals: true` but tests still import from `"vitest"` explicitly,
  `setupFiles: ./src/test-setup.ts` (jest-dom + a `scrollIntoView` stub).
- **Omit, don't fabricate** is the house ruling for anything the API does not model — see
  `prov: "unknown"`, `channel: null`, `dep: 0`, `lifecycle_status: null`, and the deleted `MOCK_WAIT`.
  Where the surface must remain, render it **read-only with an explanatory `title`** (the waitlist
  consent chip) rather than as a live-looking control.

# Out of scope across all three passes (spec decisions — recorded, not fixed)

| Ref | Item | Owner |
|---|---|---|
| G1 / R1 | Modify/reschedule fulfilment | spec 06/08 |
| G3 / R9 | Guest identity on a staff-made booking (`BookingRequest` has no customer field) | spec 04/08/12 |
| R10 | Whether resources/staff are modelled at all | spec 06/12 |
| R11 | `SubmitModificationInput` carries no changed data | spec 08 |
| R12 | PAN-detector false positive on hex ids | spec 14 |
| R13 | Cancellation `reason_code` vocabulary | spec 14 |
| R14 | `confirmation.verification_level` hard-coded (blocked on auth) | spec 04/14 |
| **R15** | **`verification_level` vocabulary drift across all three lanes** — web sent `staff_confirmed`, langgraph sends `none`/`sms`/`session`, none ratified; the submit validator does not apply `IsKnownVerificationLevel`, so every one is stored as `unverified` | **spec 04** |

The **client half** of R15 *was* in scope and is done: the console no longer claims a level the
platform discards. Tightening the server validator and fixing the langgraph lane are not.

# Code references

- `apps/web/src/api/client.ts:1-43` — the fetch wrapper extended in pass 1
- `apps/web/src/pages/ConsolePage.tsx:132-143,199-236` — mock state + mock mutations (pass-1 state)
- `apps/web/src/components/platform/drawers/CreateBookingDrawer.tsx:75-86` — mock create
- `apps/api/src/Cordia.Api/Controllers/BookingsController.cs:152-171` — acceptance endpoint
- `apps/api/src/Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/SubmitConfirmedIntentCommandHandler.cs:53-67` — hash check
- `apps/api/src/Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/SubmitConfirmedIntentValidator.cs:14-91` — required fields
- `apps/api/src/Cordia.Infrastructure/Services/CanonicalRequestHasher.cs:11-427` — canon.v1
- `apps/api/src/Cordia.Api/Middleware/TenantResolutionMiddleware.cs:21-33` — `X-Tenant-Id` shim
- `apps/api/src/Cordia.Api/Errors/GlobalExceptionHandler.cs:33-234` — error → status map
- `apps/langgraph/cordia_agent/revisions/manager.py:28-41` — the **non-conforming** Python stub (pass-1 state)


---

<!-- Consolidated from 2026-08-05_three-wiring-gaps.md (2026-08-06): the file below was merged into this record verbatim. -->

---
date: 2026-08-05
researcher: Claude (spec-loop Stage 1)
git_commit: 77b4fd8 (+ staged, uncommitted pass-1..4 work on feat/ui-wire)
branch: feat/ui-wire
repository: Cordia
topic: "Closing the three remaining frontend-wiring gaps: waitlist enrolment, operator follow-ups, menu catalog management"
tags: [research, baseline-audit, apps/web, waitlist, booking-operations, menu-catalogs, spec-11, spec-06, spec-08]
status: complete
supplements: 2026-08-04_frontend-api-wiring.md (the consolidated three-pass baseline)
---

# Research — the three remaining wiring gaps

Scope: `apps/web` ONLY. The backend (`apps/api`, `apps/langgraph`, `libs/`, `docs/specs/`) is
read-only for this pass. A gap that needs an API change is recorded as a **backend gap**, not
worked around.

The three gaps, per `docs/frontend-api-wiring.md` (§9 Phase 3, §10, §4):

1. **Waitlist enrolment** — `POST /api/waitlist/enrollments` exists; only cancel/accept/decline
   are wired. No console surface enrols a guest.
2. **Operator follow-ups** — `POST /api/booking-operations/{id}/{cancel|resume|resolve|alternatives}`
   exist; none is called. The Queue's "Clear from queue" is a local acknowledgement only.
3. **Menu catalog management** — `POST /api/menu/catalogs` + `/{id}/{review|edit|approve|publish}`
   exist; Settings only READS `/api/catalog/items`.

## Registry position (docs/specs/README.md)

- The **apps/web operations console UI** is a *known deferred item* — it owns **no shared
  contract**. Everything here is a **referencer**.
- **Spec 11** owns the waitlist enrollment/offer lifecycle (§20: silent auto-book prohibited —
  `auto_book_authorized` is an explicit guest consent, never assumed) and the menu ingestion
  pipeline (§21).
- **D4** freezes `catalog_status = uploaded, scanning, quarantined, extracting, draft_ready,
  in_review, approved, published, superseded`.
- **D1/D2** freeze the 16-value `operation_status` and the 9-value normalized set; **spec 06**
  owns the operation state machine, **spec 08** the manage-booking invariants.
- Invariants in play: Inv-1 (no side effect from transcript text — all writes go through the
  durable endpoints), Inv-2/3 (stable operation identity + key↔hash binding, where an endpoint
  takes a key), Inv-11 (server-verified authorization), Inv-13 (tenant scope on every request),
  Inv-16 (assert only authoritative outcomes).

## Existing Feature Behavior (Baseline Audit)

### B1 · Waitlist screen + feature (`features/waitlist/*`, `WaitlistScreen.tsx`, `ConsolePage.tsx:184-240,937-952`)

- **Reads:** `useWaitlistEnrollments(true, 100)` → `GET /api/waitlist/enrollments?active_only=true&limit=100`.
  `toWaitlistItem` maps `customer_ref → name`, `service_id` (labelled via the legacy `SERVICES`
  map with raw-id fallback), acceptable window → `want`, `auto_book_authorized → auto`
  (read-only consent chip on live rows), raw `latest_offer_*` fields.
- **Writes wired today:** `cancelEnrollment` (DELETE, `actor: true`, optimistic with
  single-row rollback — `useRemoveFromWaitlist`), `acceptOffer` / `declineOffer`
  (POST, `actor: true`, non-optimistic, shared-busy disable, ticking-clock actionability).
- **No `enroll` function exists** in `waitlist/api.ts`; no drawer or form collects an enrolment.
  "Book now" (`bookFromWaitlist`) opens the create-booking drawer prefilled from the enrolment.
- **Empty/demo semantics:** list seeds empty; `loaded={waitlistQuery.data !== undefined}`;
  the page-level `loadFailures` banner names a failed waitlist load; no demo fallback rows
  (the demo fallback pattern exists only for conversations/customers surfaces).
- **Must stay the same:** removal optimistic semantics + tests; offer actionability rules
  (`isOfferActionable`, floors-only expiry label); read-only consent chip; `loaded` gating.

### B2 · Queue screen + operations feature (`features/operations/*`, `QueueScreen.tsx`, `ConsolePage.tsx:381-407,713-753`)

- **Reads:** `useOperations({needsAttention: true, limit: 100})` → `GET /api/booking-operations`.
  Adapter carries raw `status` (all 16 D1 values possible), `status_reason`, `updated_at` age,
  operation type copy, booking-id row handle. No retries/channel (deliberately absent).
- **Writes wired today: none.** "Clear from queue" (`finishQueue`) and "Dismiss" (`dismissQueue`)
  are LOCAL: they add `dismissalKey = id|status` to `dismissedQueueIds` so a refetch cannot
  resurrect the row *unless the status changes* (escalation resurfaces). Toast says "Cleared
  from your queue"; the button's `title` says "The platform keeps working on the operation."
- The follow-up endpoints (`cancel|resume|resolve|alternatives`) are **called from nowhere** in
  `apps/web` (verified: no reference to those paths outside `getOperationStatus`).
- **Must stay the same:** the dismissal-key semantics (id|status), the local acknowledgement
  as a *distinct* affordance, the neutral StatusPill, `loaded` gating, `busy` prop contract.

### B3 · Settings screen (`SettingsScreen.tsx`, `features/settings/api.ts`)

- **Reads:** `useConsentPolicy('web')` (live) and `useCatalogItems()` (live; Zod-parsed because
  the server action returns `IActionResult` → generated body type is `never`). Catalog panel
  renders up to 8 items, has pending/error/empty states; empty state says "Upload and publish a
  menu to make items bookable."
- **Writes wired today: none.** No call to `POST /api/menu/catalogs` or its transitions exists
  anywhere in `apps/web`. Business setup / services / hours / deposit / guardrails / knowledge
  are all local state behind `DemoBadge`s.
- The create drawer's bookable services come from the same catalog query
  (`toBookableServices`) — so **publishing a catalog changes what bookings can be made against**;
  any write surface must invalidate `queryKeys.settings.catalogItems` and the drawer list.
- **Must stay the same:** the two live read panels and their states; the badge wording on the
  demo panels; `toBookableServices` filtering rules.

### B4 · Shared write/transport machinery (must be reused, not duplicated)

- `fetchApi` (client.ts): tenant + correlation on every request; `actor: true` → `X-Actor` +
  `X-Actor-Role`; `reasonCode` → `X-Reason-Code`; `grantId` → `X-Grant-Id`; string body →
  `Content-Type: application/json`; non-2xx → typed `ApiError` subclasses (`problem.ts`).
- `AttemptIdentity` (`bookings/identity.ts`): idempotency identity minted once per attempt key,
  sessionStorage-backed, reset only on terminal outcome; `isRetryableWithNewAttempt` frees a
  spent key on 409.
- Mutations: TanStack `useMutation`, `retry: false` inherited; `onSettled` invalidates by
  domain via `queryKeys`; optimistic only where a 409 is impossible-by-design (removal);
  failure surfaced by named message functions + toast, or inline `ErrorState` with retry.
- Test idioms: no MSW; `vi.mock` the feature api module; fresh `QueryClient` per render;
  component tests render directly with a `renderScreen(props)` helper; badges asserted as text.
- `ConsolePage.test.tsx` exists (page-level render coverage was made a deliverable in round 6).

### B5 · Existing automated coverage that must stay green

`bun nx test web` (~302 tests / 19 files at HEAD; more in working tree), `bun nx lint web`
(oxlint --deny-warnings), `bun nx typecheck web`, `bun nx build web` (typechecks test files).
Key pinned behaviours: `mutations.test.tsx` (cancel/offer flows), `offers.test.ts`
(actionability), `prefill.test.ts`, `adapters.test.ts`, `ConsolePage.test.tsx`,
`services.test.ts` (bookable services), `identity.test.ts` (session identity).

## Server contracts for the three endpoint families

Audited read-only from `apps/api` source + the generated `libs/api-contract` TS.

### A · `POST /api/waitlist/enrollments` (WaitlistController.cs:74-101)

- **Headers:** `X-Tenant-Id` mandatory. `X-Actor` **optional** — a blank header defaults the
  recorded actor to the literal `"customer"` (`ResolveActor`). `X-Actor-Role` is never read on
  this family. (The console still sends `actor: true` — audited attribution is the house rule
  on every waitlist write.)
- **Required body** (sole enforcement = `EnrollWaitlistValidator`): `customer_ref` (NotEmpty,
  no format rule), `location_id` (NotEmpty), `service_id` (NotEmpty), `notification_channel`
  (NotEmpty, free string), `party_size` (>0), `offer_lifetime_seconds` (>0),
  `acceptable_to > acceptable_from`.
- **Optional:** `verification_level` (must be ratified vocab *if present*), `identity_ref`
  (defaults to `customer_ref`), `deposit_terms` (unvalidated), `auto_book` — if present,
  `consent_evidence_id` NotEmpty (spec 11 §20: recorded consent), plus constraint rules.
- **Idempotency: none.** Re-POSTing the same payload creates a second enrollment (`wle_{guid}`).
- **Response:** `201` + `Location`, anonymous body `{enrollment_id, status:"enrolled"}` —
  untyped in the generated contract (IActionResult).
- The **list view** (`GET /api/waitlist/enrollments`, wired) returns `location_id` per row, so
  locations the platform has actually recorded are readable.
- Generated-type trap: `EnrollWaitlistRequest.auto_book`/`deposit_terms` are generated as
  **non-optional** object types though they are `?`-optional in C# and at runtime.

### B · `POST /api/booking-operations/{id}/…` (BookingOperationsController.cs)

| Action | Headers | Body | Legal from | Effect |
|---|---|---|---|---|
| `/cancel` | none | none | any **non-terminal** internal status (terminal = booked, rejected, expired, waitlisted, failed_final → 409) | idempotent 202 `{operation_id, cancellation_pending:true}` — a *request*, observed at the next checkpoint; does not cancel a booked op |
| `/resume` | `X-Actor` + `X-Actor-Role` (role ≥ operator; owner satisfies) + `X-Reason-Code`, all mandatory (fail-closed 403, audited) | optional `{replay_command_id?}` (server mints) | **`manual_review` only** (else 409) | → `reconciling`; 202 `{operation_id, replay_command_id}` |
| `/resolve` | same as resume | **required** `{resolution: "booked"\|"failed_final", provider_ref?, replay_command_id?}`; any other resolution → 400; `provider_ref` **required when booked** (400) | **`manual_review` only** (else 409) | → `booked` (BookingId = provider_ref) or `failed_final`; 202 `{operation_id, status}` |
| `/alternatives` | none | required `{response: "waitlist"\|"decline"}` else 400 | **`alternatives_required` only** (else 409); a pre-order op 409s on `waitlist` regardless | → `waitlisted` / `failed_final`; 202 `{operation_id, status}` |

- **No idempotency keys**; `replay_command_id` is a replay/audit distinguisher, not dedup.
- `GET /api/booking-operations/{id}` returns the **normalized 9-value** projection —
  `manual_review` is invisible there (collapses to `in_progress`). Only the console **list**
  (`GET /api/booking-operations`) exposes the internal 16-value `status`, which is what the
  queue rows already carry — so per-row eligibility is decidable client-side.
- `needs_attention` = `awaiting_user_action, alternatives_required, reconciling,
  compensation_required, manual_review, failed_final` (ListOperationsQuery.cs:45-53). Of
  these, `failed_final` is terminal → cancel must not be offered on it.

### C · `POST /api/menu/catalogs` + transitions (MenuController.cs)

- **All five actions** (upload, review, edit, approve, publish): `X-Actor` + `X-Actor-Role` +
  `X-Reason-Code` mandatory, fail-closed 403; **RequiredRole = `owner` exactly** —
  `operator` does NOT satisfy (StaffRole: owner ⇒ providedRole == owner).
- **State machine** (D4, CatalogStateMachine.cs): `uploaded → scanning → {extracting,
  quarantined}`; `extracting → draft_ready → in_review → approved → published → superseded`.
  `quarantined`/`superseded` terminal. **No `extract_failed` state** — a failed extraction
  leaves the row in `extracting` forever (DLQ-only visibility).
- **Upload:** body `{source_artifact_ref (required), version_label?}` — a *reference* to an
  already-stored artifact; **no file-upload endpoint exists**. 202 `{catalog_id,
  status:"uploaded"}`, then async ingest.
- **review** (`draft_ready → in_review`, optional `edits`), **edit** (`in_review` only;
  `item_id` must be the namespaced `{catalogId}:{rawId}` form), **approve** (`in_review →
  approved`), **publish** (`approved → published`; force-supersedes the previously published
  catalog in the same txn). Wrong source status → 409; unknown catalog → 404.
- **No GET for catalogs anywhere.** `GET /api/catalog/items` is published-only. The console
  cannot observe `scanning/extracting/draft_ready` progress, cannot list existing catalogs,
  and cannot read draft item ids (which `edit` requires).
- **No idempotency**; repeats are guarded only by the state machine (re-publish → 409).

### Backend gaps identified (record in the doc; do NOT work around)

1. **No catalog status/list read** (`GET /api/menu/catalogs[/{id}]` missing) — the console
   cannot observe the async `uploaded→scanning→extracting→draft_ready` progress, cannot
   discover catalogs it did not itself create, and cannot tell `quarantined` from
   "still extracting".
2. **No draft-catalog items read** — `edit` requires namespaced draft `item_id`s that no
   endpoint returns (`/api/catalog/items` is published-only), so the edit action cannot be
   honestly offered.
3. **No menu artifact upload endpoint** — `source_artifact_ref` must reference an artifact
   stored out-of-band (the object-storage lifecycle is a known deferred item).
4. **No idempotency key on waitlist enrolment** — a re-POST after an ambiguous network
   failure can create a duplicate enrollment; the console must never auto-retry and should
   refetch before manual retry.
5. **No locations read endpoint** — the enrolment's required `location_id` has no picker
   source; the operator must supply it (existing enrollments' `location_id`s are the only
   platform-recorded values visible).

### The standing rules this pass is judged on (from the goal)

- Empty success is a real answer; demo rows only while a list has NEVER loaded.
- Assert nothing the API does not record.
- Demo surfaces wear the badge.
- Writes carry a stable idempotency key where the endpoint takes one; never silently retried.
- Failures are named, with retry — never calm emptiness.
- Do not offer a button whose call is certain to fail for the row's current status.


---

<!-- Consolidated from 2026-08-05_21-07-32_console-booking-guest-identity.md (2026-08-06): the file below was merged into this record verbatim. -->

---
date: 2026-08-05T21:07:32
topic: Giving a console-made booking a real guest identity
branch: feat/ui-wire
base_commit: 77b4fd8
status: complete
tags: [spec-04, spec-10, spec-12, spec-14, canon.v1, Inv-12, guest-identity]
---

# Research: a real guest identity on a console-made booking

## Question

A console-made booking carries no guest. Consequently it has no notification
destination (spec 10) and `GET /api/Bookings?phone_e164=` cannot find it. Where
should the guest live, and what does the repo already ratify?

## Summary of findings

Five findings, in descending order of how much they change the design.

### F1 — Spec 04 already ratifies BOTH fields, on the hashed body

`docs/specs/04-submission-and-idempotency.md:97-98`, inside the `IntentRevisionPayload`
sketch (§4.2 — "The canonical, hashed body"):

```python
customer_ref: str | None = None          # opaque; NEVER PAN/CVV (Inv-12)
notification_destination: str | None = None
```

Spec 04 **owns** the submission contract and the canonical request hash. It has
already decided that a customer reference and a notification destination live
*inside* the canonical, hashed payload. Neither field has ever been implemented:
`notification_destination` appears nowhere in the repo outside that one spec line.

This means the change is not inventing a contract — it is implementing two
already-ratified spec-04 fields that the implementation skipped.

### F2 — Inv-12's scope is card data, not contact details

`docs/specs/14-security-and-privacy.md:234` states Inv-12 as *"card/sensitive data
never in agent context, transcript, queue, or logs"* — concretely PAN/CVV,
government identifiers, provider credentials, and secrets (`:39`). A phone number
is not in Inv-12's enumerated scope.

Spec 04's own annotation on `customer_ref` is `# opaque; NEVER PAN/CVV (Inv-12)` —
the Inv-12 constraint attached to this exact field is "not a card number", not
"not contact details". And `notification_destination` is by definition a phone or
an email, sitting in the same hashed body.

So the premise "PII in the hash violates Inv-12" does not hold as stated. There is
still a real privacy cost — see F5 — but it is a retention cost, not Inv-12.

### F3 — The backend already reads the guest out of the canonical payload

Both acceptance-criteria paths are already built, and both already do a
**recursive, nested-object-aware** descent through the canonical payload:

| Consumer | File | Reads |
|---|---|---|
| Phone lookup | `Cordia.Infrastructure/Repositories/CanonicalBookingPayload.cs:47` + `BookingReadRepository.cs:150` | `phone_e164` / `phoneE164` / `phone`, plus `contact_phone`, `caller_id`, `email`, `contact_email` |
| Booking aggregate | `Handlers/BookingFulfillment/Finalize/FinalizeBookingCommandHandler.cs:795-812` | `customer_id`, `caller_id`, `phone_e164`, `contact_phone`, `email`, `contact_email` → `Booking.CustomerId` |

`CanonicalBookingPayload.TryFindProperty` (`:139-172`) descends into nested objects
and arrays. So a nested `customer.phone_e164` would already be found by both
readers with **zero reader changes**.

`BookingReadRepository.MatchesAny` (`:166`) is **exact, case-insensitive string
equality** — no E.164 normalisation. The stored value must equal the query
parameter byte-for-byte (modulo case).

### F4 — The existing phone-lookup tests are vacuous

`IntegrationTests/ManageBooking/ManageBookingReadEndpointTests.cs:108-117`
seeds a canonical payload by hand:

```csharp
var payload = JsonSerializer.Serialize(new {
    booking_id = bookingId,            // a create payload NEVER carries this
    booking_reference = bookingReference,
    business_name = "Serenity Spa",    // not a BookingRequest field
    booking_date = "2026-07-10",       // not a BookingRequest field
    approx_time = "19:00",             // not a BookingRequest field
    party_size = 2,
    customer = new { phone_e164 = phoneE164 },  // BookingRequest has no customer
});
```

Not one of `booking_id`, `business_name`, `booking_date`, `approx_time`, or
`customer` is a field the real submission path can produce. `BookingReadRepositoryTests.cs:81,140`
does the same. This is precisely the failure mode the task brief warned about
(a fixture baking `booking_id` into a create payload).

**Consequence:** `GET /api/Bookings?phone_e164=` is green in CI and non-functional
in production — for the **agent lane too**, not just the console. The gap is wider
than "console bookings lack identity". Any fix must add a test that drives the
**real** `POST /api/bookings` path and then queries by phone.

### F5 — The canonical payload is immutable and outside the retention sweep

`Cordia.Infrastructure/Services/Retention/RetentionSweepHandler.cs:126-148`
anonymises exactly one thing: `ConversationSession.CustomerRef = null` past the
window. It never touches `booking_intent_revisions.canonical_payload`.

`booking_intent_revisions` is immutable by design (spec 12) and is replayed. So
anything written into the canonical payload is **permanent** and immune to the
retention sweep. That is the concrete, real cost of putting a raw phone there —
a retention/erasure gap, not an Inv-12 breach.

### F6 — There is no notification destination for ANY booking today

`Cordia.Infrastructure/Providers/NotificationRecipientResolver.cs` is a **stub**:

```csharp
var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}|{operationId}"));
var recipientRef = "recip_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
return new ResolvedRecipient(recipientRef, NotificationChannel.Sms, "en");
```

It derives an opaque handle from the tenant and operation ids and never consults
the booking, the guest, or `tenant_channel_endpoints`. Every booking — agent-made
or console-made — already gets a non-null but entirely fictional recipient.

Spec 10 §13 lists **"Real recipient store lookup, PII minimization, and channel
preferences"** as an explicit **open decision**. Replacing this stub is spec 10's
call, not this change's.

## How the pieces actually connect

```
console CreateBookingDrawer
  └─> buildSubmission.ts  toCanonicalPayload()  ── builds BookingRequest JSON
        └─> canonicalHash(payload)              ── canon.v1 in TS
              └─> POST /api/bookings { revision.canonical_payload, payload_hash,
                                       confirmation.intent_payload_hash }
                    └─> SubmitConfirmedIntentCommandHandler
                          :58  SerializeToNode(revision.CanonicalPayload)  ← DESERIALIZED C# OBJECT
                          :59  hasher.Hash(...)
                          :62  if (confirmation.IntentPayloadHash != H.Value) throw  → 422
                          :207 CanonicalPayload = JsonSerializer.Serialize(...)      → jsonb
```

**The load-bearing detail is line 58.** The server hashes the *re-serialized C#
object*, not the raw request JSON. The generated `BookingRequest`
(`SharedTypes.cs:1538`) has **no catch-all for unknown properties**. So any field
the client sends that is not on the generated type is silently dropped before
hashing, the recomputed hash differs, and the submission is rejected 422.

⇒ Adding the field to the **Pydantic source and regenerating** is not a
stylistic preference here; it is the only mechanism that can work.

## Existing Feature Behavior (Baseline Audit)

Regression baseline for Stage 6.

### B1 — `POST /api/bookings` acceptance (spec 04, Inv-2/3/4)
- Hashes `revision.canonical_payload` with canon.v1; rejects 422 on confirmation-hash
  mismatch; version-prefixed `requestHashStored` = `canon.v1:<hex>`.
- Creator election via `ElectCreatorAsync` (INSERT … ON CONFLICT DO NOTHING) inside one
  UoW transaction; same key + same hash → 202 replay from snapshot; same key + different
  hash → 409.
- Writes intent (idempotent) + revision + confirmation + operation(`accepted`) + one
  outbox message + one audit event, atomically.
- Pre-order routing: `CanonicalPayload.Preorder is { Lines.Count: > 0 }` → `preorder.commands.start.v1`,
  else `booking.command.requested.v1`.
- **Must stay byte-for-byte identical** for any payload that does not use a new field.
- Coverage: `UnitTests/BookingOperations/SubmitConfirmedIntent*`, `IntegrationTests/Acceptance/AcceptanceEndpointTests.cs`.

### B2 — canon.v1 hashing, three implementations
- C# `Cordia.Infrastructure/Services/CanonicalRequestHasher.cs` is authoritative.
- TS `apps/web/src/api/canonicalHash.ts`; Python `cordia_agent/revisions/canonical_hash.py`.
- Shared corpus `tools/config/canon-v1-test-vectors.json`, consumed by all three
  (`CanonV1GoldenVectorTests.cs`, `canonicalHash.test.ts`, `test_canonical_hash.py`).
- Object canonicalization **omits null, `{}` and `[]`** (`CanonicalRequestHasher.cs:91-102`).
  ⇒ an optional field left absent/null does not change any existing hash, and every
  existing corpus vector stays valid.
- **Must stay unchanged**; the corpus may only **grow**.
- Known trap (memory: canon-v1 cross-language corpus): `tools/config/*.json` was once
  missing from nx cache inputs, so stale passes masked corpus growth. Verify inputs.

### B3 — `GET /api/Bookings?phone_e164=|booking_reference=|email=` (spec 08)
- `BookingsController.cs:31` → `FindManageableBookingsQuery`; validator requires at least
  one hint; handler → `BookingReadRepository`.
- `MatchesHints` compares each hint by exact case-insensitive equality against fields
  pulled from the canonical payload by recursive descent.
- Tenant-scoped; cross-tenant read returns 404 (`ManageBookingReadEndpointTests:90-95`).
- **Currently vacuous — see F4.** Behaviour with a real payload is *undefined by test*.
- Coverage: `IntegrationTests/ManageBooking/ManageBookingReadEndpointTests.cs`,
  `IntegrationTests/Persistence/BookingReadRepositoryTests.cs`.

### B4 — Notification send path (spec 10, Inv-9)
- `SendNotificationCommandHandler` resolves recipient → template → identity
  (`NotificationIdentity`) → dedup on `(tenant_id, notification_id)`.
- `recipient is null` → settle `Pending → InvalidRecipient` with `no_destination`,
  emit `notification.invalid_recipient` audit, bump `NotificationInvalidRecipientTotal`.
  The stub never returns null, so this branch is currently unreachable in practice.
- Consent gate checked against `recipientRef` before send.
- **Must stay unchanged** — replacing the resolver stub is spec 10's open decision.
- Coverage: `UnitTests/Notifications/*`, `IntegrationTests/Messaging/Notification*`.

### B5 — Booking aggregate construction (spec 06)
- `FinalizeBookingCommandHandler.BuildBookingAggregate` sets `Booking.CustomerId` from the
  first of `customer_id|caller_id|phone_e164|contact_phone|email|contact_email` found by
  recursive descent, falling back to `providerReference`.
- Today a console booking always falls back to `providerReference`.
- **Intentionally changes** if a guest field lands in the payload — `CustomerId` becomes
  the guest handle rather than the provider reference.

### B6 — Console create-booking submission (web)
- `buildSubmission.ts:58` `toCanonicalPayload` emits `operation, service_id, starts_at,
  party_size` plus optional `duration_minutes, location_id, special_requests,
  target_booking_id, target_booking_version`. Absent optionals are **omitted, not nulled**.
- `confirmation.identity` = `context.identityRef` = `getActor()` (an operator id) after
  commit `882042c`; `verification_level` hard-coded `"unverified"` — truthful for a
  staff-made booking.
- Coverage: `CreateBookingDrawer.test.tsx`, `features/bookings/submission.test.ts`
  (asserts `customer_ref` is **excluded** from the payload — this assertion will need
  to change deliberately), `buildSubmission` tests.

### B7 — Retention / privacy (spec 14, Inv-12)
- `AuditEventSanitizationInterceptor` scope is `ChangeTracker.Entries<AuditEvent>()` —
  **only** `AuditEvent` rows. `BookingIntentRevision` never passes a sanitiser.
  ⇒ Trap 2 from the brief does **not** fire; to be proven by test, not by this reading.
- `RetentionSweepHandler` anonymises `ConversationSession.CustomerRef` only.
- `SensitiveDataMask.SanitizeJson` exempts control-plane ids (`*_id` and an allowlist of
  system-minted refs); `customer_ref` is explicitly **not** exempt.
- **Must stay unchanged.**

### B8 — Type-generation blast radius
Changing `BookingRequest` in `cordia_agent/state.py` regenerates:
- `libs/shared-schemas/schemas/models/BookingRequest.json` + `shared/PydanticSharedDefs.json`
- `libs/shared-schemas/generated/csharp/SharedTypes.cs` → consumed by
  `SubmitConfirmedIntentCommandHandler`, `SubmitConfirmedIntentValidator`,
  `ConsoleReadModel`, `WaitlistAcceptSubmission`, `AcceptWaitlistOfferCommandHandler`,
  `ReserveCapacityCommandHandler`, `FinalizeBookingCommandHandler`
- `libs/shared-schemas/generated/typescript/{index.ts,zod.ts}`
- `libs/api-contract/openapi/v1.json` → `libs/api-contract/generated/typescript/index.ts`
  → `buildSubmission.ts`, `submit.ts`, `services.ts`, `adapters.ts`
Also embeds into `BookingDraft.request`, `CanonicalBookingDraft.request`,
`IntentRevision.canonical_payload`.

### B9 — Spec registry position
| Contract | Owner | This change |
|---|---|---|
| `IntentRevisionPayload` / canonical payload field set | **spec 04** | implements two ratified-but-missing fields → **owned**, may change |
| canon.v1 algorithm + corpus | **spec 04** | corpus **grows only**; algorithm untouched |
| Notification recipient resolution | **spec 10** (open decision) | **referenced — must not change** |
| `audit_events` write path, redaction, retention | **spec 14** | **referenced — must not change** |
| All DDL / pg-enums | **spec 12** | no new table/column/enum needed |
| Manage-booking discovery | **spec 08** | reader unchanged; only its test stops being vacuous |

## Open question for the user (Stage 3 gate)

Where the guest lives, and — given F6 — whether "has a notification destination"
is even reachable without reopening spec 10's open decision.

## Code references

- `apps/langgraph/cordia_agent/state.py:75` — `BookingRequest` (no customer field)
- `apps/langgraph/cordia_agent/state.py:162` — `CustomerRef` (exists, has `phone_e164`)
- `apps/langgraph/cordia_agent/state.py:198,218` — `customer` on the *draft*, not the request
- `docs/specs/04-submission-and-idempotency.md:97-98` — the ratified fields
- `apps/api/.../SubmitConfirmedIntentCommandHandler.cs:58,62,207`
- `apps/api/.../CanonicalBookingPayload.cs:47,139`
- `apps/api/.../BookingReadRepository.cs:150,166`
- `apps/api/.../FinalizeBookingCommandHandler.cs:795`
- `apps/api/.../NotificationRecipientResolver.cs:25`
- `apps/api/.../RetentionSweepHandler.cs:148`
- `apps/api/.../AuditEventSanitizationInterceptor.cs:47`
- `apps/web/src/features/bookings/buildSubmission.ts:58`


---

<!-- Consolidated from 2026-08-06_02-57-45_console-reconciliation-and-components.md (2026-08-06): the file below was merged into this record verbatim. -->

---
date: 2026-08-06T02:57:45+09:30
branch: feat/ui-wire
base_commit: 77b4fd8
topic: Console wiring reconciliation — doc drift, waitlist enrolment idempotency, availability, and five endpoint-backed components
tags: [apps/web, apps/api, spec-04, spec-07, spec-11, spec-14, Inv-2, Inv-3, Inv-6, Inv-16, docs-drift]
status: complete
---

# Console reconciliation — research & baseline audit

Scope: `apps/web` (components + availability/locations/consent wiring), `apps/api`
(waitlist enrolment idempotency: a correctness fix plus the ability to accept a caller key),
and `docs/frontend-api-wiring.md` (reconciliation). No Pydantic contract changes; the .NET
OpenAPI surface changes once (additive, nullable) so `api-contract` types regenerate.

## Summary

The console's *code* is at the bar `docs/frontend-api-wiring.md` sets. The doc is behind the
code, and a batch of backend work landed on 2026-08-06 with no research/plan doc and no console
half. Separately there is one real correctness defect (a replayed idempotency record can return
a **cancelled** enrolment as if it were live) and one genuine cross-lane defect found in passing
(the agent lane calls an endpoint that does not exist).

## Existing Feature Behavior (Baseline Audit)

This section is the regression baseline. Every item below must either be listed as
intentionally changing in the plan's Blast radius, or verified byte-for-byte unchanged at Stage 6.

### A. Waitlist enrolment (spec 11 referencing spec 04's idempotency mechanism)

**Web — `features/waitlist/api.ts:26-131`.** `enrollGuest(input)` POSTs eight material fields
(`customer_ref, service_id, location_id, party_size, notification_channel,
offer_lifetime_seconds, acceptable_from, acceptable_to`) with `actor: true`. It deliberately
never sends `auto_book` (needs recorded consent evidence, spec 11 §20), `deposit_terms` (no
payment surface), or `verification_level` (the console cannot verify anyone). It sends **no
idempotency key**. Its doc comment asserts *"The endpoint takes no idempotency key — a re-POST
creates a second enrollment"* — **stale**, see below.

**Web — `features/bookings/mutations.ts:304-335`.** `useEnrollGuest` is not optimistic,
inherits `retry: false` from the shared client, and refetches `queryKeys.waitlist.all()` on both
outcomes. Its comment is **already correct** about server-side derivation.

**Web — `EnrollWaitlistDrawer.tsx:316-336`.** Failure copy is **already correct**
("Retrying is safe — an identical enrolment will not be added twice").

**API — `WaitlistController.cs:96-124`.** `EnrollWaitlistRequest` has **no** `idempotency_key`
field; the action reads no such header; it constructs `EnrollWaitlistCommand` with **14
positional arguments**, never reaching the record's 15th (`IdempotencyKey`). So every HTTP
enrolment takes the auto-derived path. Only tests calling the handler directly supply a key.

**API — `EnrollWaitlistCommandHandler.cs:43-94`.** Derives `wle_auto_{hash}` from six material
fields (`customer_ref, service_id, location_id, party_size, acceptable_from, acceptable_to` —
notably **not** `notification_channel`, `offer_lifetime_seconds`, deposit, auto-book, identity,
verification level). Derived keys get a **1-hour** window; explicit keys **365 days**. On a
same-hash hit it returns `existing.OperationId` **without checking whether that enrolment is
still live**. Creator election is `ON CONFLICT DO NOTHING`; the lost-election path re-reads the
winner and returns the winner's id.

**API — `CancelWaitlistEnrollmentCommandHandler.cs`.** Version-guarded, terminal, idempotent.
Stamps `WaitlistEnrollment.CancelledAt` (a nullable timestamp — there is no bool and no status
enum; the wire projects it to `cancelled: bool`). Releases any live offer, rolls back its hold,
and enqueues a re-match. **It does not touch the idempotency record.**

**Invariants upheld today:** Inv-2/3 (stable key, one key ↔ one hash — `ReusingAKeyForDifferentDetailsIsAConflict`),
Inv-6 (cancel is terminal and version-guarded). **Inv-16 is NOT upheld on the replay path** —
see Defect 1.

**Coverage that must stay green:** `WaitlistFlowTests.cs:533-594` (three integration tests:
identical-enrol dedup, deliberately-different allowed, key-reuse conflict);
`features/waitlist/enroll.test.tsx` (165 lines); `EnrollWaitlistDrawer.test.tsx` (176 lines).

### B. Waitlist locations (docs §12 gap 7)

`GET /api/waitlist/locations` exists (`WaitlistController.cs:45-56`) and **is already consumed**
— `ConsolePage.tsx:890-897` queries it with a 5-minute `staleTime` and falls back to scraping the
enrollments list only while loading. `knownLocations` feeds a `<datalist>` on a free-text input
(`EnrollWaitlistDrawer.tsx:238-256`).

Two stale claims and one missing behaviour:
- `EnrollWaitlistDrawer.tsx:240` hint says *"There is no locations endpoint yet"* — false.
- `ConsolePage.tsx:887-888` comment says *"no locations endpoint exists"* — false.
- The "**unrecognised location warns rather than blocks**" behaviour that both
  `listWaitlistLocations`'s own doc comment and `docs/frontend-api-wiring.md` §17 describe
  **exists in no component**. A typo is still silently accepted.

### C. Availability / capacity (spec 07, docs §12 gap 10)

**Landed 2026-08-06, no console half.** `AvailabilityController.cs` exposes
`GET /api/availability?from&to` (200/400, tenant-scoped, *not* owner-gated) and
`POST /api/availability/slots` (200/400/403, **owner-only**, audited, idempotent by derived slot
key). `AvailableSlot` = `{slot_id, resource_id, starts_at, total_capacity, free_capacity}`;
`free_capacity` is `total − held − booked` floored at 0. Validator: `to > from`, window ≤ 62 days.
Both paths are already in the committed `libs/api-contract/openapi/v1.json` (lines 2304, 2371),
and the generated TS is present locally.

**The console books blind today.** `CreateBookingDrawer.tsx:507` renders a static
`TIME_SLOTS` grid (`data/console.ts:603-607`, 09:00–17:30 every 30 min) with no server call; the
day picker is derived locally. Nothing constrains time by service or capacity.

**Load-bearing constraint (spec 07).** `capacity_slots` is only populated by the new POST, which
**no console surface calls**. So for every existing tenant `GET /api/availability` returns `[]`.
Spec 07 §169 classifies a non-atomic, no-hold read as **`informational`** — allowed wording
*"That time appears available."* The `AvailabilityResult` DTO carries **no `mode` field**, though
spec 07 §44 states every availability result carries one.

### D. The five components

| Component | Real duplication found today | Verdict |
|---|---|---|
| **PhoneInput** | `normalisePhone` is correctly centralised (`buildSubmission.ts:120-130`, 3 importers), but the "does this look wrong?" heuristic is written **twice with different rules** — `!startsWith("+")` (`BookingLookupPanel.tsx:49-50`) vs `/^\+[1-9]\d{6,14}$/` (`CreateBookingDrawer.tsx:218-220`) | Real drift to close |
| **MoneyAmount** | Same `price_minor/100 → 2dp → currency` computation twice: `CatalogDraftEditor.tsx:292-296` (with null guard) and `SettingsScreen.tsx:425-428` (**without**) | Real duplication |
| **PermissionGate** | `session.role.trim() === 'owner'` copy-pasted verbatim **including its comment** in `VenueNamePanel.tsx:25-27` and `MenuCatalogPanel.tsx:80-83`, each with its own bespoke refusal block | Real duplication |
| **ConsentBadge** | **None — no consumer exists.** `consent_state`/`recording_allowed` are on the contract but `conversations/adapters.ts:47-63` never reads them and `ConversationsScreen.tsx` never mentions consent | New wiring of an available field, not an extraction |
| **Countdown** | `useTickingNow` already shared. `offerExpiryLabel` (`waitlist/adapters.ts:56-67`) is the one genuine countdown label; `TodayScreen`'s two uses are pixel/count math, not countdowns | One real consumer |

**Role taxonomy is ratified and is NOT exact-match.** `StaffRole.cs` (gate decision GD-1,
2026-07-11) defines `owner` and `operator` with **owner ⊇ operator**, fail-closed on
unknown/blank. Both current web sites gate *owner-required* actions, where exact match happens to
be correct — but a `PermissionGate` must implement the real matrix or it will wrongly refuse an
owner an operator-required action.

**`components/ui/` conventions:** named `export function`, local non-exported `interface XProps`
with per-prop JSDoc explaining *why*, Tailwind for structure + inline `style` for palette hex,
state→visual `Record` lookup tables rather than ternaries, `aria-*` + `sr-only` for anything
conveyed by colour, and tests in the shared `designSystem.test.tsx` (which pins *rules*, not
markup).

### E. Doc drift in `docs/frontend-api-wiring.md`

| § | Claim | Reality |
|---|---|---|
| §5, §11 | Reschedule "gated off, `RESCHEDULE_FULFILMENT_SUPPORTED = false`" | `submit.ts:137` is `true`; `AdvanceBookingOperationCommandHandler.cs:104-107` routes Modify/Reschedule to `ModifyBookingCommandHandler` |
| §10 | Endpoint reference | Missing `GET /api/menu/catalogs`, `GET /api/waitlist/locations`, `GET /api/availability`, `POST /api/availability/slots` |
| §12 | Gaps 3, 6, 7 open | Closed by §17; gap 10 closed 2026-08-06 |
| §13 | "every endpoint in §10 is in exactly one of the three groups" | False — `POST /api/Bookings/{id}/modifications`, `POST /api/model-traces`, `GET /api/Health`, `GET /api/catalog/items/{itemId}` are in none. Counts stale |

## Defects found

**Defect 1 (correctness, Inv-16) — a replay can return a cancelled enrolment.**
`EnrollWaitlistCommandHandler` replays on key+hash without a liveness check. Flow: enrol → remove
→ re-enrol with identical details. The derived key collides, the handler returns the **cancelled**
enrolment's id and writes nothing; the console toasts success and the `active_only` list shows
nothing. The 1-hour derived window narrows this but does **not** close it — the console's own
remove-then-re-add happens in minutes. Reachable through the console's normal flow.

**Defect 2 (cross-lane, pre-existing, out of scope) — the agent lane calls a route that does not
exist.** `foreground.py:148` POSTs `/api/availability/candidates`; no controller implements it
(the new endpoint is `GET /api/availability`, a different shape). Every test covering it uses a
fake API (`test_tool_binding.py`, `test_foreground_tools_against_fake_api.py`,
`test_check_candidates.py`), so the gap is invisible to CI. Recorded, not fixed — inventing that
contract is spec 03/07 work.

## Registry position & frozen decisions

| Contract / mechanism | Owner | This work is |
|---|---|---|
| Idempotency disposition (same-key/same-hash, conflict, election) | **04** | **referencer** — must not change the disposition for live operations |
| `AvailabilityMode` enum + agent wording | **07** | **referencer** — console must use `informational` wording |
| Atomic capacity reservation + hold deadline | **07** | untouched |
| `waitlist_enrollments` DDL/EF mapping | **12** | untouched — **no DDL in this work** |
| `audit_events` write path | **14** | untouched |
| Staff role taxonomy (`owner`/`operator`, GD-1) | **14**/08 | **referencer** — mirror `StaffRole.Satisfies` |

D1–D17 in play: none are altered. The relevant invariants are **Inv-2, Inv-3** (idempotency
identity), **Inv-6** (capacity separate from idempotency), **Inv-13** (tenant scope), and
**Inv-16** (only authoritative outcomes) — Defect 1 is an Inv-16 breach.

## Type-generation blast radius

Adding `idempotency_key` to `EnrollWaitlistRequest` changes the .NET OpenAPI surface ⇒
`bun nx run api-contract:generate-types` must run, and `libs/api-contract/openapi/v1.json` is
regenerated. **No Pydantic contract changes**, so `shared-schemas` is untouched. The field is
additive and nullable, so no existing caller breaks.

---

# Part 4 — Residual review findings baseline (2026-08-07, 8 items)


# Residual review findings — research & baseline audit

## Purpose

A large review batch was closed by commit `374f52b` ("close the post-PR hardening review
findings") plus an uncommitted, API-only working-tree pass (51 files). Re-verification found
**eight items still open**. This document is the baseline audit for closing them, and is the
regression baseline the Stage 6 diff runs against.

## Verification method

Four parallel read-only audits (web lane; api infrastructure; api application + langgraph;
schedule/phone), plus direct reads of the fulfilment handlers in the main context. Every
"fixed" claim below was confirmed against **actual code**, not against an explanatory comment —
several files in this repo carry comments describing a fix in the past tense.

## What was already closed (not re-opened here)

Confirmed fixed by direct read: modify/reschedule capacity settlement (previous owner resolved
before the `Booked` transition; `HoldNoLongerValid` and `NotFound` both route to `Reconciling`;
the illegal `Finalizing→Rejected` edge replaced with `CompensationRequired` behind
`OperationStateMachine.EnsureAllowed`; target booking id written back via `TrySetBookingIdAsync`),
the cancel duplicate-notification race (`cancelledHere` now derives from the lifecycle transition
this transaction actually performed), grant single-use consumption gated on the creator election,
assurance level derived from the effective `X-Grant-Id`, the staff-role branch gated behind
`AssertedActorOptions`, the audit allow-envelopes (waitlist accept/decline/cancel, venue name,
capacity slot), `ON CONFLICT DO NOTHING` slot insert, monotonic guest-contact clock, retention
sweeper tenant enumeration + erasure audit, menu monitor single-transaction settle with CAS
gating, recipient-channel derivation from address shape, the reconcile resume guard, the 403
`detail` identity leak, and the langgraph guest-identity projection (fixed in `read_back.py` /
`identify_intent.py`, **not** `state.py` as originally reported).

---

## Existing Feature Behavior (Baseline Audit)

### 1. Operation-status success gate — `apps/web/src/features/bookings/useOperationStatus.ts`

**Today.** `isTerminal(s)` → `s?.terminal === true` (line 26-28). `isBooked(s)` →
`s?.status === "booked"` (line 31-33), with **no** `terminal` requirement. The file header
(lines 5-7) states the hook decides success "only when the server reports `terminal` with a
`booked` status" — the code does not enforce its own stated contract.

**Consumers.** Exactly one production caller: `CreateBookingDrawer.tsx:227`
(`const booked = isBooked(poll.status)`). On true it sets `phase='done'`, calls `onCreated(...)`
appending an optimistic row to the schedule board (client state, not a query invalidation).
`isTerminal` has no caller outside the hook itself.

**Server contract.** `GetOperationStatusQuery.cs:58-77` computes `terminal` from a fixed set that
**always includes `Booked`**, and sets `Status`/`Terminal` on the same object. The two can never
disagree over the wire today.

**Severity re-rating.** Not exploitable against the live API. It is a defence-in-depth gap against
stale react-query cache entries, mocks, and test doubles, plus a code-vs-documented-contract
mismatch in three places (`useOperationStatus.ts:5-7`, `CreateBookingDrawer.tsx:509`).

**Invariant.** Inv-16 (agent/UI states only authoritative outcomes).

**Coverage.** `submission.test.ts:159-169` asserts `{booked,terminal:true}` → true and
`{accepted,terminal:false}` → false. The disagreement case `{booked, terminal:false}` is
**untested**.

### 2. Waitlist notification recovery — Reconcile **and** Retry

**Today.** `SendNotificationCommandHandler.cs:91` writes `OperationId = op is null ? null :
operationId`. For waitlist offer/expiry sends (`RequiresOperation=false`, registered at
`ApplicationServiceCollectionExtensions.cs:262-269`) there is no `booking_operations` row yet, so
the column is NULL — deliberately, to skip the composite FK
(`NotificationConfiguration.cs:28-32`, `ON DELETE RESTRICT`; a NULL column skips enforcement).

**This is spec-compliant.** Spec 10's ratified entity sketch declares `OperationId` as
"null for non-operation messages". The send side is correct.

**The defect is in the readers.** Both gate recipient resolution on the wrong column:
- `ReconcileNotificationCommandHandler.cs:174-176`
- `RetryNotificationCommandHandler.cs:123-125` (**same bug, missed by the original report**)

Both evaluate to `null` for every waitlist row, producing
`NotificationSendOutcome.InvalidRecipient` with `FailureCategory: "no_destination"`, settled via
`MarkFailedAsync` into `NotificationState.InvalidRecipient` — **terminal, unrecoverable**, and
emitting no reconcile audit. A guest who *was* successfully notified is permanently recorded as
unreachable.

**Why `RecipientRef` cannot be the fix surface.** It is not the spec's "opaque handle" — the
implementation made it a **one-way, truncated hash**:
`Token(tenantId, value) = "recip_" + SHA256($"{tenantId}|{value}")[..16]`
(`NotificationRecipientResolver.cs:276-279`). It is derived from the *operation id*, and its own
doc warns that changing the derivation would change every `notification_id` and break Inv-9 dedup.
Off-limits.

**The available no-DDL surface.** `notifications.correlation_id` and `causation_id` already exist
as nullable `text` (migration `20260630123648_AddCorrelationToChainTables.cs:55-67`) and are
**dead columns**: `NotificationRepository.TryInsertAsync` is a raw `ExecuteSqlInterpolatedAsync`
whose column list omits them, which also bypasses `CorrelationStampingInterceptor` (it only scans
`ChangeTracker`). Meanwhile the enrollment id **is already flowing** — `MatchWaitlistOfferCommandHandler.cs:162`
and `ExpireWaitlistOfferCommandHandler.cs:78` set the outbox `CorrelationId = enrollment.EnrollmentId`,
and `CorrelationEnrichmentBehavior.cs:49-54` seeds ambient correlation for every
`IInboxConsumerRequest`. It is simply dropped at insert.

**Resolver shape.** `NotificationRecipientResolver.TryResolveFromWaitlistAsync` (lines 79-102)
accepts only an `op_wl_*` id, looks up `WaitlistOffer` by `OperationId`
(`WaitlistOfferRepository.GetByOperationIdAsync`), then chains to the enrollment. There is no
enrollment-id entry point today.

**`op_wl_*` map.** Minted at `MatchWaitlistOfferCommandHandler.cs:78`; the durable
`op_wl_*` → `EnrollmentId` mapping lives on `WaitlistOffer.OperationId`
(`WaitlistOffer.cs:21`, nullable, no FK).

**Invariants.** Inv-8 (booking persisted before notification — unaffected), Inv-9 (independent
dedup — must not be disturbed), Inv-16.

**Coverage.** `SendNotificationTests.cs:531-558` covers the *send* path with
`RequiresOperation=false`. `ReconcileNotificationTests.cs`, `RetryNotificationTests.cs` and
`NotificationReconcileTests.cs` contain **zero** `Waitlist`/`op_wl` matches. Entirely untested.

### 3. Guest-contact fingerprint backfill

**Today.** `GuestContactRepository.GetOrCreateAsync` is the **only** insert path and always
populates all three fingerprints (lines 65-67, 86-97). Discovery by phone/email goes solely
through `FindRefsByPhoneAsync`/`FindRefsByEmailAsync`, which match on
`PhoneFingerprint`/`EmailFingerprint` — a NULL fingerprint can never match (NULL ≠ NULL).

**Fingerprint algorithm.** Plain, **unkeyed** SHA-256 over `string.Join('\x1F', fields)`, hex
lowercase; the "salt" is the tenant id folded into the plaintext (lines 205-234). No config, no
key ring, no per-process state — **deterministic and reproducible in SQL**. The values hashed are
the already-normalised `phone_e164`/`email` column contents, so no re-implementation of
`PhoneMatching.Normalise` / `ContactAddress.NormaliseEmail` is needed for a backfill.

**Migration history.** `20260806010659_GuestContactStore` created the table with **no** fingerprint
columns. `20260806042423_GuestContactRetentionClockAndCatalogStatusClock` added
`phone_fingerprint`/`email_fingerprint` nullable, plus their indexes, with **no `UPDATE`**.

**Severity re-rating — this is the important finding.** Both migrations shipped in the **same
commit** (`970ca4c`), on `feat/ui-wire`, never merged to `main`. At that commit the application
always writes fingerprints, and against the earlier schema the INSERT would fail outright (columns
absent). NULL-fingerprint rows are therefore reachable **only** in a developer's local database
left at an intermediate migration. Zero rows in CI or production. This is **defensive hygiene, not
a P1 data-loss bug** — a correction to the original report.

**Discovery fallback.** `BookingReadRepository.MatchesHints` (lines 199-238) still matches
*legacy-shape* payloads carrying an inline `phone_e164` directly via `PhoneMatching.Matches`, so
pre-`guest_contacts` bookings are unaffected. There is no fallback for the new `contact_ref`-only
shape.

**Migration-immutability guard.** `tools/scripts/check_migrations_immutable.sh`
(`.pre-commit-config.yaml:129-133`) fails on any `--diff-filter=MDR` change to an existing
migration file (snapshot excepted). **Adding a new migration is always allowed**; editing
`20260806042423_...` — including a `Down()`-only edit — would trip the guard. The backfill must be
a **new forward-only migration**.

**Precedent.** `20260628023022_OpenWorkItemUniquePerOperation.cs:21-32` runs a genuine data
`UPDATE` inside `Up()`.

**Invariants.** Inv-13 (tenant scope — the backfill must stay tenant-keyed), spec 14 PII handling.
Note: `guest_contacts` is **not** in spec 12's D17 24-table catalog; it exists under the
2026-08-06 owner ruling (`docs/frontend-api-wiring.md:707`).

**Coverage.** `GuestContactErasureTests.cs` covers minting, normalisation, tenant isolation,
erasure and the monotonic clock. **No test constructs a NULL-fingerprint row.**

### 4. Console schedule loses the booking reference

**Today.** `ConsoleReadModel.PayloadRows()` (lines 512-532) joins only `booking_operations` ↔
`booking_intent_revisions`. `db.Bookings` is queried **only** inside `BuildCustomerResolverAsync`
(lines 473-480) and selects just `{BookingId, CustomerId}`. `ToScheduledBooking` (line 551) fills
the reference from `payload.BookingReference`.

**Why that is structurally always null.** `BookingRequest` is `additionalProperties: false`, so a
current-schema canonical payload can never carry `booking_reference`. As the sibling read model
documents (`BookingReadRepository.cs:106-117`), the reference "is written at finalize and lives
ONLY on the booking row."

**The pattern to mirror.** `BookingReadRepository.BaseRows()` (lines 118-138) does a **LEFT** join
to `db.Bookings` (left, so pre-finalize operations still project) and resolves via
`FirstNonEmpty(row.BookingReference, payload.BookingReference)` (line 158).

**Authoritative column.** `Booking.BookingReference` is `IsRequired()` and unique per tenant
(`BookingConfiguration.cs:16,25-26`); `ProviderReference` is nullable with a partial index and is
effectively an internal duplicate of `BookingId`.

**Contract impact — none.** `ScheduledBooking.BookingReference` (`ConsoleProjections.cs:27`),
`ScheduledBookingView.BookingReference` (`BookingsController.cs:271`) and
`libs/api-contract/openapi/v1.json:1576-1583` are **already** nullable strings. Only the runtime
value is wrong; **no `api-contract` regeneration required**.

**Visible symptom.** `apps/web/src/features/bookings/adapters.ts:142` renders
`row.customer_ref ?? row.booking_reference ?? "Guest"` — with the reference always null, rows
without a resolvable customer render as literal `"Guest"`.

**Coverage.** None of the 7 `Schedule_*` tests in `ConsoleReadEndpointTests.cs` assert
`booking_reference`. `ConsolePage.test.tsx:208` supplies `booking_reference: "REF1"` but never
asserts it renders. Untested on both sides.

### 5. Phone normalisation parity — client vs server

**Server.** `PhoneMatching.Normalise` keeps ASCII digits plus a leading `+` (only if it precedes
the first digit), and **hard-bails to `null`** when `trimmed.Length > MaxLength` (64) — a
`stackalloc` stack-safety bound checked on the raw length, before punctuation is stripped. It is a
wholesale rejection, not a truncation.

**Client.** `normalisePhone` (`buildSubmission.ts:180-190`) agrees byte-for-byte on digit survival
and the leading-`+` rule, but has **no length cap at all**.

**Where it actually bites — a correction to the original report.** The **mint** path is *safe*:
`CreateBookingDrawer.tsx:355` posts raw `phone.trim()` to `POST /api/guest-contacts`, and
`GuestContactsController.cs:76,86-95` rejects an unnormalisable phone with a **400**. The real
exposure is the **legacy inline-payload fallback** at `buildSubmission.ts:161-162`
(`toCanonicalPayload`), where `normalisePhone`'s uncapped output is embedded directly into the
**hashed, immutable** canonical payload with no server-side gate at write time (it sits inside an
opaque `customer` object). `PhoneInput`'s `looksUnnormalisedPhone` only *warns*. An over-64-char
raw value therefore passes the client, enters an immutable payload, and is then **permanently
unmatchable** by every server-side `PhoneMatching.Normalise` caller — and cannot be repaired,
because rewriting the payload would invalidate `payload_hash` and every derived idempotency record
(Inv-2/3).

**Caller behaviour on `null`** — only the mint endpoint rejects; every lookup path silently
returns no match:

| Caller | Behaviour |
|---|---|
| `GuestContactsController.Create:76` | **400 reject** |
| `GuestContactRepository.GetOrCreateAsync:24` | stores null (safe — controller gated) |
| `GuestContactRepository.FindRefsByPhoneAsync:122` | silent `[]` |
| `NotificationRecipientResolver.AsDeliverable:126` | silent not-deliverable |
| `BookingReadRepository.MatchesAnyPhone:225-241` | silent zero candidates |

**Invariants.** Inv-2/Inv-3 (immutable payload + hash binding) — this is a canon.v1-style
cross-language parity concern, not a UX nicety.

**Coverage.** `PhoneMatchingTests.cs` never exercises the 64-char boundary.
`endpointBackedPrimitives.test.tsx:184-210` covers shape warnings only, never an overlong input.
No direct unit test for `normalisePhone`.

### 6. Customers screen — guest vs operator attribution

**Today.** `ConsoleReadModel.BuildCustomerResolverAsync` (lines 459-506) resolves
`Booking.CustomerId` when a booking row exists, else falls back to the intent confirmation's
`IdentityRef` (who confirmed). `adapters.ts:46-48` flags `isOperatorRef` by string-equality against
the local session actor; `CustomersScreen.tsx:265` renders "You · console bookings".

**Severity re-rating.** `FinalizeBookingCommandHandler.cs:864-879` sets `Booking.CustomerId` from a
preference chain (`customer_id`, `contact_ref`, `phone_e164`, …) with a final
`?? providerReference`. `CustomerId` is therefore **never** null once a booking exists, so the
`identityByIntent` operator-collapse fallback fires **only for operations with no booking row yet**
(in-flight or failed). The `adapters.ts` comment ("every such booking collapses into one row")
overstates the current behaviour and may predate the `contact_ref` ranking fix.

**Available metadata.** `CustomerSummary` carries only `customer_ref`, booking count, first/last
activity and `last_service_id`. **No origin/channel field exists** — `adapters.ts:58` hard-codes
`channel: "web"`.

**Coverage.** Not covered for the in-flight attribution case.

### 7. Menu approve does not invalidate the draft cache

**Today.** `useApproveCatalog` routes through `useCatalogLifecycleMutation`
(`mutations.ts:51-61`), which invalidates **only** `queryKeys.settings.catalogList()`.
`useEditCatalogItems` (lines 100-104) is the only hook that invalidates
`queryKeys.settings.catalogDraft(variables.catalogId)` — a **separate** cache entry
(`queryKeys.ts:71` vs `:73`).

**Why it cannot self-correct.** `CatalogDraftEditor.tsx:80-92` sets `staleTime: Infinity`,
`refetchOnWindowFocus: false`, `refetchOnReconnect: false`, `refetchOnMount: false` — deliberately,
because the draft read is an audited `IOperatorActionRequest` and refetching on focus would spam
audit rows. The stepper is driven by `query.data?.status` (line 96, 152-160).

**Consequence.** With the editor mounted, an approve leaves the stepper showing `in_review` and the
save controls live; the next edit 409s. This is the exact failure the file's own header comment
(`mutations.ts:39-50`) describes for the *list* — the fix was applied to `catalogList` only.

**Coverage.** Not covered.

### 8. MaskedField reveal policy — deliberate deviation

**Today.** `REVEAL_TTL_MS = 20_000` with a `setTimeout` auto-remask (lines 48-65). The blur
approach was **explicitly evaluated and rejected** (lines 51-58): Safari does not focus a
`<button>` on click so `focusout` never fired (indefinite exposure); Chrome/Firefox fired too
eagerly, re-masking before a copy selection could be made.

**What it protects.** Two production call sites only. The substantive one is
`ModelTraceViewerPanel.tsx:172-175`, masking **conversation-trace content** — not PAN. Card data
is handled server-side (`SensitiveDataMask`, `AuditEventSanitizationInterceptor`), never through
this component.

**`tail=0` clamp is already fixed** — `maskValue` clamps via
`Math.min(Math.max(Math.trunc(tail), 0), v.length)` (line 111).

**The real gap.** `designSystem.test.tsx:225-260` covers masking, reveal, non-un-redaction and
`aria-pressed`, but **nothing asserts the 20s TTL actually remasks**. The behaviour is unlocked by
tests.

---

## Registry position & type-generation blast radius

| Contract / mechanism | Owner | This work |
|---|---|---|
| `notifications` table + `notification_id` derivation | 10 (contract) / 12 (DDL) | **referencer** — must not alter `RecipientRef`/`notification_id` derivation (Inv-9) |
| `notifications.correlation_id` column | 12 (DDL, already exists) | **referencer** — populate only, no DDL |
| `guest_contacts` | owner ruling 2026-08-06 (not in D17) | data backfill only, new migration |
| `ScheduledBookingView` / `ScheduledBooking` | console read model | value-only change; shape already nullable |
| `PhoneMatching` | 08 (manage-booking matching) | server bound is authoritative; client must agree |
| `operation_status` / `NormalizedOperationStatus` | 06 | **referencer** — read only |

**Type generation: no Pydantic contract changes, no `shared-schemas` regeneration, and no
`api-contract` regeneration** (item 4's field is already nullable in `v1.json`). This keeps the
cross-app blast radius at zero.

## Automated coverage that must stay green

- `bun nx test api` (Category=Unit) — notification, waitlist, manage-booking, phone-matching suites
- `bun nx test-integration api` — `ConsoleReadEndpointTests`, `GuestContactErasureTests`,
  `NotificationSenderTests`, `WaitlistFlowTests`, `BookingReadRepositoryTests`
- `bun nx test web` — `submission.test.ts`, `designSystem.test.tsx`, `ConsolePage.test.tsx`,
  `endpointBackedPrimitives.test.tsx`
- `bun nx lint api` / `bun nx lint web`

## Net severity re-rating vs the original report

| # | Reported | Audited |
|---|---|---|
| 1 | P1 | **P2** — not reachable via the real API; defence-in-depth + contract mismatch |
| 2 | P1 (1 handler) | **P1, wider** — Reconcile *and* Retry; unrecoverable terminal misclassification |
| 3 | P1 | **P3** — unreachable outside a local dev DB; cheap insurance |
| 4 | P2 | **P2 confirmed** — value always null; visible as `"Guest"` rows |
| 5 | P2 | **P1 on the inline path** — poisons an immutable hashed payload (Inv-2/3) |
| 6 | P2 | **P3** — only in-flight operations; no origin metadata exists to fix it properly |
| 7 | P2 | **P2 confirmed** — editor cannot self-correct by design |
| 8 | P2 | **Won't-fix candidate** — deliberate, well-argued; real gap is missing test |

---

# Part 5 — Capacity lock-order deadlock + waitlist accept limbo (2026-08-07)


# Research & baseline audit — capacity deadlock + waitlist accept limbo

## Existing Feature Behavior (Baseline Audit)

### A. Capacity lock ordering

**The convention (upheld everywhere but one place).** Every method in
`FirstPartyCapacityReservation` locks `capacity_holds` **before** `capacity_slots`:

| Method | Order |
|---|---|
| `ReserveAsync` (55-124) | read holds → **UPDATE slot** → **INSERT hold** (inserts don't contend with existing hold rows) |
| `ReleaseHoldAsync` (412-454) | **UPDATE hold** → **UPDATE slot** |
| `FinalizeAgainstOperationAsync` (159-217) | read hold → **UPDATE hold** → **UPDATE slot** |
| `ReleaseFinalizedByOperationAsync` (338-404) | read → **UPDATE hold** → **UPDATE slot** |

**`AbsorbSameSlotAsync` (220-335) is the sole reversal:**
read previousHold → read newHold → **UPDATE slot (253-262)** → **UPDATE previous-owner hold
(275-285)** → **UPDATE new-op hold (298-309)**. Ordered by *role* (previous-owner first), not by
hold id.

**No `SELECT … FOR UPDATE` exists anywhere in this path.** All reads are `AsNoTracking`. Every lock
comes from an `ExecuteUpdateAsync` single-statement UPDATE (`VersionTokenUpdate.UpdateAsync` is one
UPDATE, not read-then-write).

**The deadlock.** For booking `B` created by `Op1` with finalized hold `H1` on slot `S`:

- **TxM** (`ModifyBookingCommand.CommitModificationAsync`, same-slot amendment `Op2`):
  `FinalizeAgainstOperationAsync` (333-334) locks `HOLD(Op2) → SLOT(S)`; then
  `AbsorbSameSlotAsync` (420-424) wants `HOLD(H1)`. **Net: `SLOT(S) → HOLD(H1)`.**
- **TxC** (`CancelBookingCommandHandler.CommitCancellationAsync`, cancel `Op3`):
  `ReleaseFinalizedByOperationAsync(origin=Op1)` (273-277) locks **`HOLD(H1) → SLOT(S)`**.

Circular wait ⇒ `40P01 deadlock_detected`. Both resolve `previousOwner`/`origin` to `Op1` via
`GetBookedOperationForBookingAsync` — the normal case while neither has committed.

**Critically, the inversion is at TRANSACTION level, not inside the method.** By the time
`AbsorbSameSlotAsync` runs, `FinalizeAgainstOperationAsync` has already taken `SLOT(S)` in the same
transaction. Reordering statements inside `AbsorbSameSlotAsync` therefore **cannot** fix it — the
previous owner's hold must be locked before the capacity phase touches the slot at all.

**Duplicated.** `ModifyCapacitySettlement.SettleAsync:72-89` (reached from
`ResolveManualReviewCommandHandler:177-178`) has the identical Finalize-then-Absorb sequence, so the
manual-recovery lane deadlocks the same way. Any fix must cover both call sites.

**No deadlock handling exists.** `UnitOfWork` (12-21) begins a transaction with **no isolation level
argument** (default READ COMMITTED), no retry wrapper, no SQLSTATE catch. Grep for
`40P01`/`40001`/`serialization_failure`/`deadlock_detected` across `apps/api`: **zero matches**. An
aborted side propagates as an unhandled `PostgresException`; recovery is entirely by redelivery.
(`CancelBookingCommandHandler`'s `BookingTransitionAttempts = 3` loop is an *optimistic-concurrency*
re-read inside one transaction — not deadlock retry.)

**Coverage.** `SameSlotAmendmentTests.cs` exercises `AbsorbSameSlotAsync` thoroughly but
**single-threaded**. No test anywhere runs concurrent modify vs cancel, and none asserts lock order.

### B. Waitlist accept stranded past `CompensationRequired`

**The reconciler's classification** — `ReconcileWaitlistAcceptCommandHandler.cs:34-41`:

```csharp
private static readonly HashSet<OperationStatus> _terminalFailureStates =
[ FailedFinal, Rejected, Expired, AlternativesRequired ];
```

Success = `Booked` only (line 55) → stamps `CancelAsync` as fulfilment (63-64). Failure = that
hand-written set (line 70) → audit, leave enrollment eligible. **Everything else falls through to
"in-flight" by exclusion** (78+) and is re-probed.

**This duplicates a set the codebase already owns.** `OperationStateMachine._terminalStates`
(`OperationStateMachine.cs:100-108`) = `{Booked, Rejected, Expired, Waitlisted, FailedFinal}`, and
`IsTerminal` (115-116) is already the authority used by `AdvanceBookingOperationCommandHandler:80`
and `EvaluateOperationSlaCommand:114`. Per `docs/specs/README.md` the `operation_status` enum **and
its terminal semantics are owned by spec 06**; spec 11 §17 says the accept "delegates … to the
spec-06 operation engine and **awaits its terminal**." The reconciler is a *referencer* keeping its
own narrower copy — a single-ownership violation, and the reason two states fall through the gap.

**The race window is narrower than assumed.** `GetLiveByEnrollmentAsync`
(`WaitlistOfferRepository.cs:30-38`) returns only `Pending|Matched|Offered` — **not `Accepted`** — so
once the accept commits its claim, cancel cannot touch the hold. The real windows are:
1. between `mediator.Send(submit)` (`AcceptWaitlistOfferCommandHandler.cs:104`, which mints the
   spec-04 operation) and the `Offered → Accepted` claim (114-118); and
2. the **entire** deferred auto-accept window — `MatchWaitlistOfferCommandHandler:118-134` leaves the
   offer `Offered` until the queued `AutoAcceptWaitlistOfferCommand` runs.

**How it reaches `CompensationRequired`.** The 06 engine reuses the waitlist hold via
`ReserveAsync`'s idempotency pre-check (`FirstPartyCapacityReservation.cs:57-64`), conditional on the
hold still being `Active`. If cancel released it *after* `ReservingCapacity → Held` but before
finalize, `FinalizeAgainstOperationAsync` returns `HoldNoLongerValid` and
`FinalizeBookingCommandHandler` routes `Finalizing → CompensationRequired` (143-146 and 505-508).
**In both branches the provider `CreateBookingAsync` has already succeeded**, so compensation must
cancel a real external booking.

**`CompensationRequired` is correctly NOT terminal**, and usually resolves in **milliseconds**:
`FinalizeBookingCommandHandler` returns `Continue`, so `AdvanceBookingOperationCommandHandler`'s
same synchronous loop immediately dispatches `CompensateOperationCommand`, which reaches
`FailedFinal` (130-133 / 238-241). The reconciler's 5-minute first probe therefore *usually* sees
`FailedFinal` and classifies correctly — it "gets away with" the missing case.

**The genuinely permanent case is `ManualReview`.** When the provider cancel comes back
Ambiguous/Transient/PermanentError/AuthError, `CompensateOperationCommandHandler:198-207` escalates
`CompensationRequired → ManualReview` and returns `Wait`. `AdvanceBookingOperationCommandHandler`'s
dispatch switch (83-117) has **no case for `ManualReview`** — it falls to `_ => null` and the loop
stops. The operation sits there until a human resolves it. `ManualReview` is in **neither**
the reconciler's failure set **nor** `OperationStateMachine._terminalStates`, so the reconciler
re-probes, exhausts `MaxAttempts = 3` × `Delay = 5 min` (`WaitlistAcceptReconcileScheduler.cs:26,29`)
≈ 15 minutes, then **gives up silently**: a `logger.LogWarning` and `return Unit.Value` — no
`CancelledAt`, no audit event, no work item, no escalation.

So the enrollment is left neither fulfilled nor explicitly re-matched, with no durable record that
the waitlist side was abandoned, while the operation may sit in `ManualReview` indefinitely.

**Coverage.** `ReconcileWaitlistAcceptHandlerTests.cs` covers the four states in the hand-written
set, `Booked`, and one in-flight exemplar (`ReservingCapacity`). **No test uses
`CompensationRequired` or `ManualReview`.** `WaitlistFlowTests` tests cancel/decline/expire release
against a still-`Offered` offer with **no concurrent in-flight accept**.

## Registry position & blast radius

| Contract | Owner | This work |
|---|---|---|
| `operation_status` + terminal semantics | **06** | **referencer** — must consult `OperationStateMachine.IsTerminal`, not copy it |
| Atomic capacity reservation + hold deadline | **07** | **owned** — lock ordering is an implementation concern of the owner |
| Waitlist offer lifecycle | **11** | **owned** |
| `capacity_holds` / `capacity_slots` DDL | **12** | **referenced — no DDL in this work** |

**Type generation: none.** No Pydantic contract, no OpenAPI surface. Zero cross-app blast radius.

Spec 07 §266 states the design intent the absorb path departs from: capacity writes are
"single-statement conditional UPDATE … no row locks held across network."

## Automated coverage that must stay green

- `bun nx test api` — `SameSlotAmendmentTests`, `ReconcileWaitlistAcceptHandlerTests`,
  `CancelBookingTests`, `ModifyBookingTests`, `ReserveCapacity*Tests`
- `bun nx test-integration api` — `WaitlistFlowTests`, `WaitlistAcceptFlowTests`,
  `StateMachineCapstoneTests`, `RetryAndFailureTests`
- `bun nx lint api`

## Severity re-rating vs the goal statement

| Item | As stated | As audited |
|---|---|---|
| Deadlock | liveness, redelivery recovers | **confirmed** — and localized to `AbsorbSameSlotAsync`'s two call sites; not fixable inside the method |
| "reconciler doesn't classify `CompensationRequired` as terminal" | accurate description | **`CompensationRequired` is correctly non-terminal** and self-resolves in ms. The permanent stranding is **`ManualReview`**, plus a **silent** give-up at the cap. Root cause is a duplicated terminal set, not a missing enum case. |
| "cancel releases the hold out from under an in-flight accept" | confirmed | window is narrower than feared (`Accepted` already excluded) but real in two places, incl. the whole deferred auto-accept window |

---

# Part 6 — Global capacity lock order, state ownership, ManualReview (2026-08-07)


# Research & baseline audit — capacity lock order, state ownership, ManualReview

This audit **builds on** the earlier pass
(Part 5 above),
which fixed the modify-vs-cancel `HOLD(prev)`/`SLOT` inversion. That fix is in the working tree and
is correct. This audit widens the sweep to **every** capacity transaction, **every** duplicated state
set, and the **whole** ManualReview lifecycle — and finds work the earlier pass did not cover.

**Baseline at audit time:** `bun nx test api` → **1341 passed, 0 failed**.

---

## Existing Feature Behavior (Baseline Audit)

### A. Capacity: every transaction touching `capacity_holds` / `capacity_slots`

All writes funnel through `FirstPartyCapacityReservation`
(`apps/api/src/Cordia.Infrastructure/Services/FirstPartyCapacityReservation.cs`). There are **no
FKs, no cascades, and no triggers** on either table (migration `20260628034341_CapacitySlotsAndHolds`,
plus `CapacityHoldConfiguration` / `CapacitySlotConfiguration`), so **no other table's write can
implicitly lock a capacity row**. Only two raw-SQL statements touch these tables in production:
`CapacityHoldRepository.LockAsync` (`SELECT … FOR UPDATE`) and `CapacitySlotRepository.TryAddAsync`
(`INSERT … ON CONFLICT DO NOTHING`).

`UnitOfWork.ExecuteInTransactionAsync` (`UnitOfWork.cs:12-21`) opens with **no isolation argument**
→ READ COMMITTED. **No `40P01` / `40001` handling exists anywhere.** Recovery is by broker
redelivery only.

#### Per-method internal order

| Method | Order | Verdict |
|---|---|---|
| `ReserveAsync` (55-124) | HOLD(read, non-locking) → **SLOT(update)** → **HOLD(insert)** | slot-before-hold *textually*, but see **A4** |
| `ReleaseHoldAsync` (437-479) | HOLD(update) → SLOT(update) | canonical |
| `FinalizeAgainstOperationAsync` (159-217) | HOLD(update) → SLOT(update) | canonical |
| `ReleaseByOperationAsync` (139-156) | loop: HOLD(update) → SLOT(update) | see **A2** |
| `ReleaseFinalizedByOperationAsync` (363-429) | loop: HOLD(update) → SLOT(update) | see **A2** |
| `AbsorbSameSlotAsync` (220-360) | HOLD(lock ×2, **ordered by hold id**) → SLOT(update) → HOLD(update) ×2 | fixed by the earlier pass |

#### Per-transaction inventory

| # | Entry point | Ordered capacity ops | Verdict |
|---|---|---|---|
| A | `ReserveCapacityCommandHandler:88-273` | SLOT(update) → HOLD(insert) | safe — **A4** |
| B | `MatchWaitlistOfferCommandHandler:49-152` | SLOT(update) → HOLD(insert) | safe — **A4** |
| C | `FinalizeBookingCommandHandler:125-179, 430-586` | HOLD → SLOT | canonical |
| D | `ModifyBookingCommand.CommitModificationAsync:282-582` **same-slot** | **HOLD(prev, lock)** → HOLD(own) → SLOT → HOLD ×2 | fixed by the earlier pass |
| D′ | same, **cross-slot** | HOLD(own) → **SLOT(new)** → HOLD(prev) → **SLOT(old)** | 🔴 **A1 — live deadlock** |
| E | `ModifyCapacitySettlement.SettleAsync:45-126` | identical to D / D′ | 🔴 **A1** on the cross-slot branch |
| F | `CancelBookingCommandHandler.CommitCancellationAsync:188-324` | HOLD(prev) → SLOT | canonical |
| G | `CompensateOperationCommandHandler:115-182, 221-285` | HOLD → SLOT (loop) | **A2** |
| H | `ResolveManualReviewCommandHandler:51-334` | three branches → F-shape or E-shape | 🔴 **A1** via E |
| I–L | Waitlist cancel/decline/expire, `ReleaseExpiredHoldsCommandHandler` | single HOLD → SLOT | canonical / **A2** |
| M | `CreateCapacitySlotCommandHandler` | SLOT only | N/A |
| N | `AvailabilityQueryHandler` | read-only, non-locking | N/A |

#### 🔴 A1 — Cross-slot reschedule takes two slot rows in **role order, not a total order** (NEW, live)

The earlier pass reasoned only about hold-vs-slot and concluded the cross-slot path was safe because
"`SLOT(new) ≠ SLOT(old)`, so there is no shared row to cycle on". That is true for **one**
transaction against a cancel. It is **false for two cross-slot reschedules running in opposite
directions**, which share *both* rows:

- **TxA** — booking *B* moves `S1 → S2`: `HOLD(ownA)` → **`SLOT(S2)`** → `HOLD(prevA)` → **`SLOT(S1)`**
- **TxB** — booking *C* moves `S2 → S1`: `HOLD(ownB)` → **`SLOT(S1)`** → `HOLD(prevB)` → **`SLOT(S2)`**

TxA holds `SLOT(S2)` and wants `SLOT(S1)`; TxB holds `SLOT(S1)` and wants `SLOT(S2)`. **Circular wait
⇒ `40P01`.** Reachable in ordinary use: two guests swapping between the same two sittings
("move 7pm→8pm" and "move 8pm→7pm") at the same moment. Present in **both** call sites
(`ModifyBookingCommand:511-518` and `ModifyCapacitySettlement:122-123`), i.e. the automatic lane and
the manual-recovery lane, exactly like the original defect.

**Not fixable by the hold-before-slot rule alone** — the rule says nothing about the order of two
slot rows. The rule needs a second clause.

#### 🟡 A2 — Multi-row loops have no total order and interleave hold/slot (latent, structural)

`ReleaseFinalizedByOperationAsync:373-378` and `GetActiveByOperationAsync`
(`CapacityHoldRepository.cs:29-33`) both return `ToListAsync()` with **no `ORDER BY`**. The loop then
runs `HOLD(h₁) → SLOT(s₁) → HOLD(h₂) → SLOT(s₂)`, which is both (a) a hold acquired *after* a slot,
and (b) a hold set acquired in a database-determined order.

**Honest severity: latent, not currently live.** Reaching `n > 1` requires one operation to own two
holds of the same status simultaneously; today `create` is the only workflow step and
`AbsorbSameSlotAsync` collapses to a single finalized hold, so the loops run with `n ≤ 1` in every
flow I traced. It is a **public primitive whose shape permits the inversion**, with no guard, no
ordering, and no comment saying `n ≤ 1` is required. The fix is two lines and removes the class.

#### ✅ A4 — `ReserveAsync`'s SLOT-then-HOLD-insert is **not** a lock-order violation

Textually it touches `capacity_slots` before `capacity_holds`, so a naive reading of the rule flags
it. It is nonetheless safe, and the reason is worth pinning in the rule itself rather than
rediscovering: **an `INSERT` creates a new row and therefore acquires no lock any other transaction
can already hold.** The only way it can wait is a unique-index conflict on
`uq_active_hold_per_op_step`, and that cannot form a cycle here:

- If a concurrent releaser is uncommitted, the step-1 pre-check (`:61-64`) reads the hold as still
  `Active` under READ COMMITTED and returns `Held(existing)` **without touching the slot**.
- If it has committed, the row is `Released` and no longer in the partial index, so no conflict.
- Two concurrent *reserves* for the same `(op, step)` conflict on the index, but neither ever waits
  on the other's slot lock — a wait, not a cycle (documented at `:100-107`).

So the global rule must be stated over **row locks on pre-existing rows**, not over "which table is
touched first". This is a precision fix to the rule, not a weakening of it.

#### Existing capacity coverage

39 test files reference capacity. **Exactly one test uses two genuinely concurrent connections** —
`SameSlotAmendmentTests.LockAsyncGenuinelyHoldsTheRowAgainstAConcurrentWriter:317-349` (holder takes
`FOR UPDATE`; contender sets `lock_timeout='750ms'` and fails `55P03`). Its sibling
`LockAsyncReleasesTheRowWhenItsTransactionEnds:356-382` is sequential. `grep` for
`Task.WhenAll|Parallel\.|Task\.Run` across `apps/api/test` finds **no matches in any capacity test**.
**No test anywhere asserts a `40P01`**, and no test reproduces any deadlock scenario end-to-end.
Lock ordering is currently asserted only by a Moq call-order unit test
(`ModifyBookingTests.cs:539-567`).

`PostgresFixture` supports the needed concurrency: `CreateContext(tenantId)` builds a **fresh
`DbContextOptionsBuilder` and connection** per call, so N independent overlapping transactions are
available. Tests share one physical DB (`Cordia_API_Test`) and isolate by minting unique tenant ids.

**Spec 07 §12 (line 349) explicitly calls for this:** *"Use Alba integration tests against a real
PostgreSQL for the concurrency/race cases (deterministic two-transaction interleaving)."* The
deterministic-interleaving requirement is currently unmet.

#### Where the rule is written down

**Nowhere.** `grep -i "lock order|deadlock|for update"` across `docs/` returns only
`FOR UPDATE SKIP LOCKED` queue-claim references. `docs/dotnet-api-patterns.md` has **zero** matches.
The sole explanation of the ordering rule is a code comment on `ICapacityHoldRepository.LockAsync`
(`:40-63`). Spec 07 never names `FOR UPDATE`, `40P01`, or lock ordering.

---

### B. Workflow state ownership — 15 duplication sites

Five state machines exist, all in `Cordia.Domain`, all `FrozenDictionary`/`FrozenSet` shaped. **Their
public APIs are not uniform, and that asymmetry is the root cause of most duplication:**

| State machine | Terminal set | Exposes `IsTerminal`? |
|---|---|---|
| `OperationStateMachine` (`Entities/OperationStateMachine.cs`) | Booked, Rejected, Expired, Waitlisted, FailedFinal | ✅ |
| `NotificationStateMachine` | SuppressedDuplicate, InvalidRecipient, Delivered, FailedPermanent | ❌ |
| `WaitlistOfferStateMachine` | Accepted, Declined, Expired, Released | ❌ |
| `PreorderSagaStateMachine` | Completed, FailedFinal | ❌ (has `IsInFlightForwardStep`) |
| `CatalogStateMachine` | Quarantined, Superseded, ExtractFailed | ❌ |

**Four of five force every caller that needs "is this done?" to hand-roll a set.**

#### B1 — Duplicated `OperationStatus` sets

| # | Site | Literal | Means | Risk |
|---|---|---|---|---|
| 1 | `BookingOperationRepository.cs:17-24` | 5 terminals | terminal (SQL-translatable) | hand copy; EF genuinely cannot translate `IsTerminal`, but nothing derives it |
| 2 | `BookingOperationRepository.cs:100-110` | `SlaEligibleStatuses` (8) | non-terminal ∧ ¬user-wait | new state silently excluded from SLA sweeping |
| 3 | `EvaluateOperationSlaCommand.cs:38-43` | `UserWaitStatuses` (3) | waiting on the customer | **complement of #2, in a different file** — a state can end up in both or neither |
| 4 | `RetentionPolicy.cs:27-35` | 4 terminals — **excludes `Waitlisted`** | "safe to prune" | **name collision, not a bug** — see below |
| 5 | `BookingFunnelReadModel.cs:29-34` | Rejected, FailedFinal, Expired | terminal failure | new terminal failure silently missing from the funnel |
| 6 | `NotificationRepository.cs:166-177` | `BookedStatuses`, `FailureStatuses` | terminal outcome → notification type | drift |
| 7 | `NotificationRepository.cs:211-216` | the **same two sets again**, as inline LINQ | ditto | two representations **45 lines apart in one file** |
| 8 | `ListOperationsQuery.cs:45-53` | `NeedsAttention` (6) | "operator must act" | a new needs-attention state is invisible to the console queue |
| 9 | `GetOperationStatusQuery.cs:58-64` | `is Booked or Rejected or Expired or Waitlisted or Failed` | terminal | hand-written 5-way pattern instead of `IsTerminal` |
| 10 | `ConsoleReadModel.cs:613-628` **and** `BookingReadRepository.cs:247-262` | byte-identical `ToLifecycleStatus` switch | status → booking lifecycle label | **hand-mirrored across two files**; the comment at `ConsoleReadModel.cs:609-611` says it exists so the two "never disagree" — with no mechanism enforcing that |
| 11 | `ReconcilePreorderSagaCommandHandler.cs:44-50` | `{FailedFinal, Rejected, Expired, AlternativesRequired}` | 06 has failed | **the exact flat copy the waitlist reconciler already removed**, still present here |
| 12 | `ReconcilePreorderSagaCommandHandler.cs:78-83` | `is Completed or FailedFinal or ManualReview` | saga already resolved | no `IsTerminal` on `PreorderSagaStateMachine` to delegate to |
| 13 | `RecordDeliveryReceiptCommandHandler.cs:40-46` | 4 notification terminals | terminal | no `IsTerminal` on `NotificationStateMachine` |
| 14 | `MenuIngestionMonitor.cs:50-51` | `[Scanning, Extracting]` | in progress | no helper on `CatalogStateMachine` |
| 15 | `AdvanceBookingOperationCommandHandler.cs:83-117` | dispatch `switch`, `_ => null` | which states the driver drives | fails **safe** (new state parks) but nothing asserts the wait set matches the machine |

**#4 is a name collision, not a divergence.** `RetentionPolicy.IsTerminal` means *"safe to physically
prune"*, and excluding `Waitlisted` is correct — a waitlisted operation still has a live enrollment.
The defect is that it borrows the word `Terminal` from a concept spec 06 owns, so it reads as drift
and invites someone to "fix" it. Rename + derive, do not change behaviour.

**Also inside `OperationStateMachine` itself:** `_terminalStates` (`:100-108`) is hand-listed while
the class doc (`:16-17`) says terminals are "states absent from this dictionary". Two sources of
terminality in the authority. (Note: deriving terminal-from-absence is **safe for
`OperationStateMachine`** but **unsafe for `PreorderSagaStateMachine`**, whose unwired legacy members
`HoldingCapacity`/`AuthorizingPayment`/`CompensatingHold` are also absent yet are not terminals.)

#### B2 — Unvalidated literal transitions

`IBookingOperationRepository.TryTransitionStatusAsync` (`BookingOperationRepository.cs:37-65`) does a
version-guarded `UPDATE … WHERE Status = @expected` and **never calls
`OperationStateMachine.EnsureAllowed`**. Whether an edge is legal is therefore up to each caller, and
that is applied inconsistently:

- **Disciplined** (calls `EnsureAllowed` first): `ModifyBookingCommand:618`,
  `CompensatePreorderItemsCommandHandler:60,63`, all catalog handlers, `MenuIngestionMonitor:85`,
  `AcceptWaitlistOfferCommandHandler:70`, `DeclineWaitlistOfferCommandHandler:45`.
- **Not disciplined** (literal `(from, to)` straight to the repo, no validation anywhere in the
  method): `CancelBookingCommandHandler:207`, `FinalizeBookingCommandHandler:170,539`,
  `ReconcileOperationCommandHandler:218`, `CompensateOperationCommandHandler:132,240`,
  `ResolveManualReviewCommandHandler:95-104,210-217`, `ValidateOperationCommandHandler:167`,
  `RespondToAlternativesCommandHandler:68,108`, `ProcessDepositEventCommandHandler:95`.

If an edge is ever **removed** from `AllowedTransitions`, those nine sites keep writing the now-illegal
transition to the database and nothing throws. The state machine and runtime behaviour diverge
silently — which is precisely the failure mode this work exists to prevent.

#### B3 — Test coverage for consistency

Each state machine has an exhaustive self-test (`OperationStateMachineTests.cs` is 232 lines and
already asserts `TerminalStates_HaveNoOutgoingEdgesInAllowedTransitions`). **No test anywhere asserts
cross-consistency** between a derived set and its machine: not #2∪#3 = non-terminal, not #4 vs the
terminal set, not the two `ToLifecycleStatus` copies, not `NeedsAttention`, not the driver's wait set.

**There are no architecture tests in the repo at all** — no `NetArchTest`, no `ArchUnit`, no
reflection-based assembly scanning in any test.

---

### C. ManualReview lifecycle

**The goal statement's premise needs correcting: entry is not a dead end.** Every operation-level
entry funnels through `ManualReviewEscalator.EscalateAsync`
(`ManualReviewEscalator.cs:34-103`), which in **one transaction** does the version-guarded
transition + an `OperationWorkItem{State="open"}` + a `notification.still_confirming.v1` outbox row +
an `operation.manual_review` audit event. Callers: `ReconcileOperationCommandHandler:88,146,296` and
`CompensateOperationCommandHandler:198`. `operations_work_items` carries
`uq_open_work_item_per_operation` — a partial unique index on `(tenant_id, operation_id) WHERE state
= 'open'`. Exits are `POST /api/booking-operations/{id}/resolve` and `/resume`, both
`IOperatorActionRequest` (fail-closed actor/role/reason-code headers), both closing the work item.

The real gaps are elsewhere:

#### 🔴 C1 — No way to enumerate outstanding work items

`IOperationWorkItemRepository` (`:6-51`) exposes `GetOpenByOperationAsync` (needs the id you are
looking for), `CountOpenAsync` and `OldestOpenAgeSecondsAsync` — both **cross-tenant, for metrics
gauges only** (`MetricsGaugeHostedService.cs:67-72`). **There is no list method, no query handler,
and no controller reads the table.** An operator can see the *operation* via
`GET /api/booking-operations?needs_attention=true`, but never the work item's reason, SLA deadline,
assignee, or age. "Outstanding ManualReview items can be identified and acted on" is **not**
satisfied: you can see that an operation is in `manual_review`, but not the tracking record, and
nothing tells you *which* ones have breached SLA.

#### 🔴 C2 — Pre-order saga `ManualReview` creates no work item at all

`PreorderSagaState.ManualReview` is a **separate** state machine from `OperationStatus.ManualReview`.
It is reached from `CompensatePreorderBookingCommandHandler:52`
(`CompensatingBooking → ManualReview`) and `ReconcilePreorderSagaCommandHandler:225-243`
(`Reconciling → ManualReview`). Both write **only** an audit event and an outbox notification via
`PreorderSagaSupport.Audit` — **neither touches `IOperationWorkItemRepository`.** A saga parked
awaiting operator + customer agreement (spec 11 §21.4) has **no durable work-tracking row**, does not
appear in the `fulfillment.manual_review.depth` gauge, and has no SLA deadline. This is the genuine
"silent dead end".

#### 🟡 C3 — `ManualReviewEscalator` uses read-then-add instead of the atomic primitive

`:56-69` does `GetOpenByOperationAsync` then `AddAsync`. `TryAddOpenAsync` — the atomic
`INSERT … ON CONFLICT DO NOTHING` designed for exactly this and used by
`AuditMissingTerminalNotificationCommandHandler:71` — exists and is **otherwise dead code**. Two
concurrent escalations both pass the read, the second violates the unique index, and PostgreSQL
aborts the **whole ambient transaction** (the same trap `CapacitySlotRepository.TryAddAsync:34-61`
documents at length). `AdvanceBookingOperationCommandHandler:170-185` has the identical pattern.

#### 🟡 C4 — Waitlist reconcile exhaustion still only logs

`ReconcileWaitlistAcceptCommandHandler:136-139` — after `MaxAttempts` it emits a `LogWarning` and
returns. The earlier pass added audits to the *settled* branches, but the **exhaustion** branch still
has no durable record. The user's requirement — "reconciliation does not only LogWarning and forget
it" — is unmet here.

#### 🟡 C5 — SLA evaluation cannot tell "waiting on an operator" from "waiting on a provider"

`EvaluateOperationSlaCommand.cs:94` folds `ManualReview` into the generic in-progress default, so an
operation already owned by a human keeps being SLA-evaluated and can have a work item re-created.

#### Existing ManualReview coverage

Good at the operation level: `ManualRecoveryTests.cs` (incl.
`ManualReviewEscalator_DoubleEscalate_YieldsOneOpenWorkItem`), `PoisonDeadLetterTests.cs`,
`SlaEvaluatorTests.cs`, `OperationWorkItemPersistenceTests.cs`,
`ResolveManualReviewCommandHandlerTests.cs`, `ResumeWorkflowStepCommandHandlerTests.cs`.
**Zero coverage** for: pre-order saga ManualReview work tracking (C2), any enumeration surface (C1),
the reconcile-exhaustion durable record (C4).

---

## Registry position & blast radius

| Contract / mechanism | Owner | This work |
|---|---|---|
| Atomic capacity reservation, hold lifecycle, lock ordering | **07** | **owned** — ordering is the owner's implementation concern |
| `capacity_holds` / `capacity_slots` DDL | 12 | **referenced — no DDL** |
| `operation_status` (D1) + terminal semantics | **06** | **referenced — consult, never copy** |
| `NormalizedOperationStatus` (D2) | 06 | referenced — read only |
| `operations_work_items` | 12 (DDL) / 06 (semantics, FM#11) | **referenced — no DDL**; new read method + query only |
| Pre-order saga states (§5.3 / §21.4) | **11** | **owned** |
| Notification / catalog / waitlist-offer state machines | 10 / 11 / 11 | owned — adding a query helper changes no state |

**Frozen decisions:** D1 (16 statuses) and D2 (9 normalized) are **read-only** here — no status is
added, removed, or reclassified.

**Release-blocking invariants in play:** **Inv-6** (capacity atomic), **Inv-7** (ambiguous ⇒
reconcile), **Inv-13** (tenant scope), **Inv-14** (DLQ ≠ business state), **Inv-15** (typed human
retries), **Inv-16** (authoritative outcomes only).

**Type-generation blast radius:** none for the capacity and state-ownership work. **One** surface —
if a work-item listing endpoint is added to `Cordia.Api`, `bun nx run api-contract:generate-types`
must be re-run and `apps/web` may consume it. No Pydantic contract is touched, so
`shared-schemas` is **not** regenerated.

## Automated coverage that must stay green

- `bun nx test api` — **1341 baseline.** `SameSlotAmendmentTests`, `FirstPartyCapacityReservationTests`,
  `ReserveCapacity*`, `ModifyBookingTests`, `CancelBookingTests`, `CompensateOperationTests`,
  `ReconcileWaitlistAcceptHandlerTests`, `Resolve/ResumeManualReview*Tests`, all five
  `*StateMachineTests`, `RetentionPolicy` tests, `NormalizedStatusProjectionTests`
- `bun nx test-integration api` — `ManualRecoveryTests`, `SlaEvaluatorTests`, `WaitlistFlowTests`,
  `PoisonDeadLetterTests`, `ConsoleReadEndpointTests`, `StateMachineCapstoneTests`,
  `OperationWorkItemPersistenceTests`, `CapacitySchemaTests`
- `bun nx lint api`, `bun nx build api`

## Severity re-rating vs the goal statement

| Goal item | As stated | As audited |
|---|---|---|
| "Fix every violation incl. `AbsorbSameSlotAsync`, `ModifyCapacitySettlement`, `FinalizeAgainstOperationAsync`" | implies all three are broken | `AbsorbSameSlotAsync` + same-slot `ModifyCapacitySettlement` were **already fixed**; `FinalizeAgainstOperationAsync` is and always was canonical. **The live defect is elsewhere: the cross-slot slot↔slot cycle (A1), which the earlier pass explicitly reasoned was safe.** |
| "capacity_holds → capacity_slots" as the whole rule | one clause | **insufficient** — needs a total order on multi-row acquisition of *each* table (A1, A2) and an explicit insert carve-out (A4) |
| "Remove duplicated hardcoded state sets" | reconciler-scoped | **15 sites across 5 state machines**; root cause is 4 of 5 machines lacking `IsTerminal`. Plus 9 handlers writing transitions with no edge validation at all (B2) |
| "ManualReview becomes a silent dead end" | entry is the problem | **entry is well-built** (`ManualReviewEscalator` = transition + work item + outbox + audit, atomically). Real gaps: **no enumeration surface (C1)** and **pre-order saga parks with no work item at all (C2)** |
| "reconciliation only LogWarning" | accurate | confirmed at `ReconcileWaitlistAcceptCommandHandler:136-139` (C4) |
