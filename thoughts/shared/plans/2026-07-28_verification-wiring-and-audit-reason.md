# Verification wiring + operator reason codes — Implementation Plan (completed)

**Specs:** 08 (owner of `verification_action` / grant lifecycle), 14 (owner of the `audit_events` write path)
**Invariants:** Inv-11 (server-verified authorization), Inv-12 (PII), Inv-13 (tenant scope), Inv-2 (stable idempotency)
**Gate answers:** reason code → *server allowlist in the API*; verification scope → *cancel + grant-backed details read*

## What these two items actually were

Not polish. The baseline audit found:

1. **The console's cancel could never succeed.** `SubmitBookingCancellationCommandHandler` consumes
   a `cancel` grant unconditionally (correctly — Inv-11) and nothing in the app could obtain one:
   `features/verification/**` was fully built and imported by no page, drawer, or hook. A repo-wide
   grep for `features/verification` returned exactly one hit — a code comment.
2. **The audit trail recorded a purpose the operator never chose**, on the console's only PII read,
   and re-recorded it on every window focus.

## Spec ownership

| Contract | Owner | This work |
|---|---|---|
| `verification_action` (D5, six values) | **08** | **referenced** — uses `cancel`; adds nothing |
| `VerificationGrant` lifecycle + issuance endpoints | **08** | **referenced** — unchanged |
| `audit_events` write path | **14** | **referenced** — write path unchanged |
| `AuditEventEnvelope.reason_code` (Pydantic, `str \| None`) | **14** | **untouched** — no regeneration |
| ProblemDetails error envelope | `apps/api` convention | **owned here** — additive extensions only |

## Baseline (verified, not assumed)

- `POST /api/bookings/{id}/cancellations` → `ConsumeGrantAsync(..., Cancel, ...)` inside the same
  transaction that elects the idempotency record and transitions the booking.
- `grant_id` is `NotEmpty`-validated ⇒ the console's `grant_id: ""` was a **400**, not a 403.
- `GET /api/bookings/{bookingId}` returns the **same** `minimal` projection with or without a grant;
  a grant changes only the status and the audit record.
- `GET /api/conversation-sessions/{id}/transcript` authorizes on **either** a `read_sensitive` grant
  **or** staff role + non-empty reason code. The console uses the staff path, which is correct.
- No reason-code allowlist existed anywhere in C#, Python, or TypeScript.

---

## Phase 1 — Machine-readable verification 403 (api) ✅

`WriteProblemAsync` gained an optional `extensions` argument; `VerificationRequiredException` now
emits `code: "verification_required"`, the D5 `action` (via `EnumWireNames.ToWireName`), and
`target_booking_id`. `AuthorizationFailedException` deliberately does not.

- [x] `bun nx build api` · `bun nx lint api`
- [x] `bun nx test api` (1170 passed)
- [x] `bun nx test-integration api` (405 passed, +3)

## Phase 2 — Classify it in the client (web) ✅

`toApiError` maps a 403 carrying `code === "verification_required"` to
`ApiVerificationRequiredError`, which exposes `action` / `targetBookingId`. The existing typed
`status === "requires_verification"` branch is untouched and still matched first.

- [x] `bun nx test web` · `bun nx typecheck web` · `bun nx lint web`

## Phase 3 — Mount VerificationGate in the cancel flow (web) ✅

Clicking Cancel opens the gate in the booking drawer; `onGranted` submits the cancellation with the
grant. `useCancelBooking` now takes `{ bookingId, grantId }`. A grant that expires between issue and
use returns the Phase-1 403, which re-opens the gate. Closing the drawer clears a pending gate.

- [x] `bun nx test web` — the grant reaches `submitCancellation`
- [x] `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual verification**
- [ ] Cancelling a booking prompts for verification, sends a code, and completes on a valid code

## Phase 4 — Operator purpose on the transcript read (web) ✅

`getTranscript` requires an explicit purpose — the `"operator_review"` default is gone.
`useTranscript(sessionId, reasonCode)` is disabled until one is chosen and sets
`staleTime: Infinity`, `refetchOnWindowFocus: false`, `refetchOnReconnect: false`. The operator picks
from seven labelled purposes before the read happens.

- [x] `bun nx test web` (216 passed) · `bun nx typecheck web` · `bun nx lint web` · `bun nx build web`

**Manual verification**
- [ ] Selecting a conversation does not fetch the transcript until a purpose is chosen
- [ ] Alt-tabbing away and back does not issue a second transcript request

## Phase 5 — Reason-code vocabulary (api) ✅ — narrowed

`SensitiveReadPurpose` (`Cordia.Domain/Enums`): seven ratified values, enforced by FluentValidation
on the transcript read. Scoped deliberately — see discoveries.

- [x] `bun nx test api` — 19 unit tests over the vocabulary and both validators
- [x] `bun nx test-integration api` — the existing `dispute_review` fixture still passes

## Phase 6 — Documentation ✅

`docs/frontend-api-wiring.md` §11: the Verification row now says "Live for cancel" with the reason
the grant is obtained up front, and a new subsection records what the audit turned up.

---

## Discoveries and Notable Information

**The console's cancel was not "unverifiable", it was malformed.** The plan assumed a 403 the client
could recover from. `grant_id` is `NotEmpty`-validated, so `grant_id: ""` is rejected **structurally
as a 400** before the authorization spine runs — there is no 403 to catch. This inverted the design:
the grant must be obtained *before* the first submit. The submit-then-recover flow originally planned
would never have worked. Found by writing the integration test and getting 400 where 403 was expected.

**A rejected cancel leaves nothing behind.** `ElectCreatorAsync`, the lifecycle transition, and
`ConsumeGrantAsync` all run in one transaction, so a refused grant rolls back the idempotency record
too. The attempt identity therefore survives reject → verify → retry even though adding `grant_id`
changes the canonical request hash (Inv-2). Pinned by
`SubmitCancellation_WrongGrant_LeavesTheBookingAndTheKeyUntouched`.

**ProblemDetails extensions need the framework serializer.** `WriteProblemAsync` passed a hand-rolled
`JsonSerializerOptions`, which bypasses ASP.NET's `ProblemDetails` converter and serializes the type
as a plain POCO — burying `code`/`action` under a nested `"extensions"` node no RFC-9457 client looks
in. Dropping the explicit options flattens them. The `ValidationProblemDetails` branch already relied
on this for its `errors`, which is why validation bodies looked right and mine did not.

**One repo, three unrelated things called "reason code".** Sizing the allowlist turned up: the
*audit purpose* on a sensitive read (`X-Reason-Code` — the item raised); the *operational narrative*
on an operator action (`provider_outage_recovered` on DLQ replay); and the *business reason* a guest
changed a booking (`customer_request`, `complaint`, sent by both langgraph and web). A flat allowlist
over `IOperatorActionRequest` would have conflated all three. The vocabulary is scoped to the first.

**Model traces are referenced, not categorised.** The first draft applied the allowlist to
`GetModelTraceQuery` too and three integration tests failed on `incident_42` — which is exactly
right: a trace read points at an incident. A closed list would replace a specific pointer with a
vague label, so that surface stays free-form. The asymmetry is pinned by a test.

**`operator_review` is deliberately not ratified.** It was the old hard-coded constant; admitting it
would let the exact behaviour being fixed pass validation.

**Existing cancellation coverage never exercised the console's path.** Every integration test seeded
a `VerificationGrant` row straight into the database, so the no-grant request shape the console
actually sent had no coverage in any lane.

**Vitest does not typecheck.** After `useCancelBooking`'s signature changed, four tests kept passing
while calling it with the old `string` argument — destructuring a string yields `undefined` for
every field and the mocks did not care. `bun nx build web` (which runs `tsc -b` over tests) is what
caught it; `bun nx typecheck web` alone did not.

## What we did NOT do

- Did not wire the gate onto `GET /api/bookings/{bookingId}`. The gate answer selected it, but the
  server returns a byte-identical projection with or without a grant — the operator would see no
  new data, only an extra prompt and an audit row. Recorded as the one deliberate deviation from the
  gate answer; worth revisiting when spec 08 differentiates the projection.
- Did not ratify the vocabulary in the Pydantic contract (spec 14 owns it; that binds all three lanes).
- Did not touch `IOperatorActionRequest.ReasonCode` or the booking `reason_code` business field.
- Did not change the transcript's server-side authorization spine.
