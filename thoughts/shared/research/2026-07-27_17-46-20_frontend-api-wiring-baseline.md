---
date: 2026-07-27T17:46:20+10:00
researcher: Claude (spec-loop Stage 1)
git_commit: 343f220
branch: feat/ui-wire
repository: Cordia
topic: "Wiring apps/web to apps/api — baseline audit (docs/frontend-api-wiring.md)"
tags: [research, codebase, apps/web, apps/api, api-contract, frontend, react-query, canonical-hash]
status: complete
last_updated: 2026-07-27
last_updated_by: Claude
---

# Research: Wiring the Cordia Web Console to the Backend API

**Spec under implementation:** `docs/frontend-api-wiring.md` (Guide, last mapped 2026-07-24)
**Companions:** `docs/ui-ux-blueprint.md` (Rev 3), `thoughts/shared/research/2026-07-10_00-20-27_backend-ui-audit.md`

## Research Question

What must change to replace `apps/web`'s mock data with real calls to `apps/api`, what is
genuinely wireable today, and what is the regression surface?

## Summary

- The web console is **100% mock-backed**. `apps/web/src/api/client.ts` exists but has
  **zero call sites**; every screen reads constants from `apps/web/src/data/console.ts`.
- The API is live and tenant-scoped: **17 controllers / 40 actions**. Tenant comes from a
  signed `X-Tenant-Context` JWS (spec 15) with an `X-Tenant-Id` **dev shim** that only
  applies when the signed path did not already set a tenant. `TenantGuard.RequireTenant`
  is fail-closed → HTTP 400.
- The **generated api-contract TS was stale** (generated 2026-07-11; `openapi/v1.json`
  committed 2026-07-27 in `6884035` added `/api/channels/*`). Regenerated during this
  stage from the committed `v1.json`; it now has 39 paths / 78 schemas.
- The single biggest hidden cost is **`canon.v1`**: `POST /api/Bookings` requires the
  *client* to supply `revision.payload_hash` **and** `confirmation.intent_payload_hash`,
  and the server recomputes the canonical hash and rejects a mismatch with **422**
  (`ConfirmationHashMismatchException`). A booking cannot be created from the web app
  without a faithful TypeScript port of the `canon.v1` algorithm.
- The doc's per-screen verdicts hold: *action* surfaces (booking create/modify/cancel,
  waitlist actions, verification, menu/catalog) are wireable now; most *list* screens
  (Today, Queue, Conversations list, Customers, Deposits, Campaigns, most of Analytics)
  have **no endpoint** and stay on mock data.

## Detailed Findings

### apps/web — current shape

| Concern | State | Evidence |
|---|---|---|
| Routes | `/` → HomePage, `/dashboard` → ConsolePage, `*` → NotFoundPage | `apps/web/src/router.tsx:12-25` |
| Console | One route, 10 internal "screens" switched by `useState<Screen>` | `apps/web/src/pages/ConsolePage.tsx:34-37,70` |
| Server state | `useState(() => [...MOCK])` seeded from module constants | `ConsolePage.tsx:132-143` |
| "Loading" | fake `setTimeout(…, 540)` on screen change | `ConsolePage.tsx:188-190` |
| Mutations | `setTimeout` + local `setState` (create 1000ms, cancel 850ms, queue 900ms) | `CreateBookingDrawer.tsx:75-86`, `ConsolePage.tsx:199-236` |
| Fetch layer | `fetchApi<T>` + `ApiError` + `isApiError` — **no call sites** | `apps/web/src/api/client.ts:1-43` |
| Base URL | `window.APP_CONFIG?.API_URL`, else relative `/api` via Vite proxy | `client.ts:24-30`, `vite.config.ts:19-27` |
| Generated types | `@cordia/shared-schemas`, `@cordia/api-contract` are deps but **never imported** | `apps/web/package.json:16-17` |
| Data-fetching lib | none (no react-query / SWR) | `apps/web/package.json` |
| Styling | Tailwind v4 utility classes + inline `style` for computed values; single stylesheet `src/styles/tailwind.css` with an `@theme` token block | `tailwind.css:1-79` |
| Tests | **one** file, `src/router.test.tsx` (15 lines). No component/hook/api tests, no MSW, no `vi.mock` usage anywhere | `src/router.test.tsx` |
| Lint | `oxlint src/ --deny-warnings`, plugins react/typescript/unicorn | `apps/web/project.json`, `.oxlintrc.json` |
| Typecheck | `tsc --noEmit`, `strict: true`, `moduleResolution: bundler` | `tsconfig.app.json` |

`apps/web/src/data/console.ts` (583 lines) is the whole "backend": hand-rolled types
(`Booking`, `Thread`, `Customer`, `Deposit`, `Campaign`, `QueueItem`, `WaitlistItem`,
`Service`, `TenantConfig`, `AnalyticsData`) + full literal datasets + formatters
(`fmtT`, `fmtRange`, `fmtDur`, `money`, `statusMeta`, `chMeta`, `idemKey`). It is imported
by 18 files, including `components/ui/` primitives (`StatusTags`, `Chip`, `Avatar`,
`CommandPalette`) — so it is **not** cleanly removable; the wiring must adapt API DTOs
*into* these shapes, not delete them.

### apps/api — HTTP surface actually available

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

**No endpoint exists** for: tenant-wide booking list, operations-by-status list, sessions
list, waitlist enrollment list, customers, deposits, campaigns, or richer analytics
aggregates. Those screens have no wireable read.

### Headers and auth

There is **no `[Authorize]` anywhere**. Access is gated by header shims:

- `X-Tenant-Id` — dev shim, `TenantResolutionMiddleware.cs:21,29,33`, applied **only if**
  `TenantContextVerificationMiddleware` (signed `X-Tenant-Context` JWS) did not already
  set the tenant (`TenantResolutionMiddleware.cs:28` guard). Missing → `TenantGuard`
  throws `TenantScopeMissingException` → 400.
- `X-Correlation-Id` — `CorrelationMiddleware.cs:22,34-38,44`; echoed or minted as
  `corr_{guid}`; must match `^[A-Za-z0-9_-]{1,64}$`.
- `X-Actor` / `X-Actor-Role` / `X-Reason-Code` — required on booking-operation
  `resume`/`resolve`, all Menu actions, Preorder actions, DLQ replay, transcript read,
  model-trace read, and (`X-Actor` only) every Waitlist action.
- `X-Grant-Id` (or `?grant_id=`) — `BookingsController.cs:50,56`,
  `ConversationSessionsController.cs:102`.
- CORS: `Cors:AllowedOrigins` = `["http://localhost:4200"]` in
  `appsettings.Development.json:14-16`; **empty** in `appsettings.json:24-26`.

### Error contract (what the client must parse)

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

Note also: `GET /api/Bookings/{id}` returns **200/403/404 with the same
`GetBookingDetailsResponse` body** (`status: ok | requires_verification | not_found`) —
so a 403 there is a *typed business response*, not a ProblemDetails error, and the client
must read the body rather than treat it as a bare failure.

### canon.v1 — the load-bearing constraint

`POST /api/Bookings` acceptance flow (`SubmitConfirmedIntentCommandHandler.cs:53-67`):

1. Server serializes `revision.canonical_payload` with the generated `Converter.Settings`.
2. `ICanonicalRequestHasher.Hash(node)` computes `H` (`CanonicalRequestHasher.cs`).
3. If `confirmation.intent_payload_hash != H.Value` → **422**.
4. `revision.payload_hash` is stored bare and a **DB trigger** asserts
   `intent_confirmations.intent_payload_hash = booking_intent_revisions.payload_hash`
   (handler comments at :208-209, :225-226) — so both client-supplied hashes must equal `H`.

`SubmitConfirmedIntentValidator.cs` additionally requires (all 400 if missing):
`operation_id`, `idempotency_key`, `revision.{intent_id, schema_version,
tenant_policy_version, canonical_payload, payload_hash, source_session_id}`,
`confirmation.{confirmation_id, intent_payload_hash, identity, source_turn_id,
read_back_version, verification_level}`, plus `confirmation.intent_id ==
revision.intent_id` and `confirmation.intent_revision == revision.revision`, plus
`target_booking_id` for modify/cancel/reschedule.

**canon.v1 algorithm** (`CanonicalRequestHasher.cs:11-37`, version label `canon.v1`):
object — drop null / empty-object / empty-array values, NFC keys, sort keys ordinal;
string — NFC, trim, collapse internal whitespace runs to one space, preserve case, except
ISO-8601-with-offset which is parsed to UTC and re-emitted `yyyy-MM-ddTHH:mm:ss.fffZ` with
**truncation** (floor) to ms; number — integers normalized via BigInteger (`5e2` = `500` =
`500.0`), fractions via shortest round-trip; array — canonicalize elements then sort (by
`item_id`, then `modifier_id`, then full canonical JSON); encode compact UTF-8 JSON →
SHA-256 → lowercase hex. Null/empty payload hashes `{}`.

**The Python side is a stub** and does *not* match:
`apps/langgraph/cordia_agent/revisions/manager.py:28-41` is
`sha256(json.dumps(model_dump(mode="json"), sort_keys=True, separators=(",",":")))`,
explicitly labelled "deterministic stand-in". So there is no existing correct non-C#
implementation to copy — the TS port must be written against the C# source and pinned to
golden vectors produced by the real C# hasher.

## Existing Feature Behavior (Baseline Audit)

Every item below is the **regression baseline** for Stage 6.

### B1 — `apps/web/src/api/client.ts` (shared code, 0 consumers today)

- `ApiError(status, message)` with `name = "ApiError"`; `isApiError()` type guard.
- `resolveUrl`: `window.APP_CONFIG?.API_URL` (trailing slash stripped) else the path
  verbatim.
- `fetchApi<T>`: sends `Accept: application/json` merged with `init.headers`, **then
  spreads `init` after `headers`** — meaning `init.headers` currently *overwrites* the
  merged object (a latent bug: `{ headers: {...}, ...init }` puts `init.headers` last).
- Non-2xx → `ApiError(status, "Request to {path} failed: {status}")`; no body parsing.
- Always `response.json()` — a 204 would throw.
- **Blast radius: none today (no callers).** Changing it cannot regress a screen.

### B2 — ConsolePage state machine and screen contracts

- Owns *all* console state: `screen`, `loading`, `selDay`, `convOpen`/`convView`/
  `takenOver`, `autoBook`/`waitlist`, `customerFocus`, `services`/`tenantCfg`,
  `bookings`/`queue`/`busy`, `drawer`, `cancelBusy`, toasts, `cmdOpen`, `sidebarCollapsed`,
  `scrolled`.
- `useDevice()` → `'phone' | 'desktop'` at the 768px breakpoint; sidebar auto-collapses
  ≤1100px.
- `go(next)` sets screen and starts the fake 540ms skeleton; resets conversations to list
  view on phone.
- Screens are **presentational**: they receive data + callbacks as props and never fetch.
  Any wiring must either keep that contract (fetch in ConsolePage / a hook, pass down) or
  change each screen's props — the latter is a wider blast radius.
- ⌘K opens the command palette; timers (`ltRef`, `btRef`, `qtRef`) are cleared on unmount.
- Sign-out navigates to `import.meta.env.BASE_URL`.

### B3 — Mock mutation flows (to be replaced, behavior must be preserved or bettered)

- **Create** (`CreateBookingDrawer.tsx:75-86`): `phase: 'form' → 'confirming' → 'done'`;
  1000ms fake latency; on done calls `onCreated(Omit<Booking,'id'>)`; ConsolePage appends
  with `id = 'new' + Date.now()`. Drawer resets all fields on `open`. Guard: no services →
  an explicit empty state and **no footer button**.
- **Cancel** (`ConsolePage.tsx:199-217`): 850ms; sets status `cancelled`; toast with a 5s
  **Undo** that restores the previous status; `cancelBusy` prevents re-entry.
- **Reschedule** (`onRescheduleDone`): patches `{day, start, staff, dur?}` in place. Also
  reached by **drag** on TodayScreen (`onDragReschedule`).
- **Queue finish/dismiss**: 900ms / immediate, both remove the row; dismiss offers Undo.
- **Waitlist** `toggleWaitlistAuto` / `removeFromWaitlist` (with Undo toast).
- All are optimistic and never fail. Real endpoints introduce failure and async
  (202 + poll) semantics that the current UI has no vocabulary for.

### B4 — Analytics screen

- `AnalyticsScreen.tsx` reads `ANALYTICS_BY_RANGE[range]` for `'7d' | '30d' | '90d'` and
  renders KPI grid, per-day bars, channel split, top services, agent-vs-human split.
- `GET /api/reporting/funnel` returns only
  `{tenantId, from, to, accepted, confirmed, ambiguous, failedTerminal, notified, notifyPending}` —
  it covers **none** of `perDay`, `channels`, `topServices`, `handled`. Only a *funnel*
  sub-view is wireable; the rest must stay mock and be labelled as such.

### B5 — Settings screen

- `SettingsScreen(services, tenantCfg, onUpdateServices, onUpdateTenant, device)` — all
  local state, no persistence. `GET /api/consent-policies` and `GET /api/catalog/items`
  are additive read-only surfaces; hours/guardrails/knowledge have no endpoint.

### B6 — Type-generation consumers

- `libs/api-contract/generated/typescript/index.ts` — consumed by web only. **Was stale**;
  regenerated from the committed `libs/api-contract/openapi/v1.json` (39 paths, 78 schemas).
- `libs/shared-schemas/generated/typescript/{index,zod}.ts` — present (2026-07-11).
  `libs/shared-schemas/schemas/` **does not exist** in this checkout, so
  `shared-schemas:generate-types` cannot be re-run without first running
  `langgraph:generate-schemas` (uv/Python). No Pydantic contract is being changed by this
  work, so the existing generated output stands.
- Neither package is imported by any `apps/web` source file today.

### B7 — Existing automated coverage that must stay green

- `apps/web`: `bun nx test web` → the single `router.test.tsx`; `bun nx lint web` (oxlint
  `--deny-warnings`); `bun nx typecheck web`; `bun nx build web`.
- `apps/api`: `bun nx test api` (Category=Unit) — includes
  `UnitTests/Idempotency/CanonicalRequestHasherTests.cs` (34 property-based assertions, no
  pinned hex constants) and `SubmitConfirmedIntentValidatorTests.cs`.
  `bun nx test-integration api` (Alba) includes
  `IntegrationTests/Persistence/ConfirmationHashBindingTests.cs`.
- No API source change is required for Phases 0–4 of the doc's plan, so the .NET suites are
  a pure regression check unless golden-vector fixtures are added.

### B8 — Release-blocking invariants touched by this work

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

### B9 — Registry position of this work

`docs/specs/README.md` lists **"apps/web operations console UI"** under *Known deferred
items (tracked, not yet owned by a spec)*. Consequently:

- This work **owns no shared contract**. Every contract it touches (`SubmitConfirmedIntentRequest`,
  `IntentRevision`, `Confirmation`, `OperationStatusView`, canon.v1, `VerificationGrant`)
  is **referenced** and must match its owner byte-for-byte: 04 (submission, canonical hash,
  idempotency disposition), 06 (`NormalizedOperationStatus`), 08 (verification grant /
  manage-booking), 07 (`AvailabilityMode`), 15 (`TenantContext`).
- Adding **new API endpoints** (the doc's Phase 4) would create API surface no spec owns —
  a scope decision for the approval gate, not something to do silently.

## Code References

- `apps/web/src/api/client.ts:1-43` — the fetch wrapper to extend
- `apps/web/src/pages/ConsolePage.tsx:132-143,199-236` — mock state + mock mutations
- `apps/web/src/components/platform/drawers/CreateBookingDrawer.tsx:75-86` — mock create
- `apps/web/src/components/platform/screens/AnalyticsScreen.tsx:19` — mock analytics source
- `apps/api/src/Cordia.Api/Controllers/BookingsController.cs:152-171` — acceptance endpoint
- `apps/api/src/Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/SubmitConfirmedIntentCommandHandler.cs:53-67` — hash check
- `apps/api/src/Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/SubmitConfirmedIntentValidator.cs:14-91` — required fields
- `apps/api/src/Cordia.Infrastructure/Services/CanonicalRequestHasher.cs:11-427` — canon.v1
- `apps/api/src/Cordia.Api/Middleware/TenantResolutionMiddleware.cs:21-33` — `X-Tenant-Id` shim
- `apps/api/src/Cordia.Api/Errors/GlobalExceptionHandler.cs:33-234` — error → status map
- `apps/langgraph/cordia_agent/revisions/manager.py:28-41` — the **non-conforming** Python stub

## Open Questions for the approval gate

1. **Phase 4 (new list endpoints)** — in or out of this run? It adds API surface that no
   spec owns.
2. **Verification-grant UI** — the doc's Phase 2 requires a new OTP flow. Build it, or wire
   modify/cancel without `grant_id` and surface the 403?
3. **`X-Actor` identity** — waitlist/menu/operator writes require an actor. There is no
   auth, so the value must come from config. What is acceptable for dev?
