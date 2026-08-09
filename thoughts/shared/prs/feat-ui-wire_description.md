# PR Title: feat(web,api): wire the operator console to the live API surface

## Change description

> This PR takes the operator console from a static mock to a client of the real API, and
> builds the backend endpoints the console legitimately needed along the way. The map of
> what is wired, what is deliberately not, and why, is `docs/frontend-api-wiring.md` —
> the honest total is **36 endpoints wired, 0 left to wire, 6 never-wire by design**.
>
> **Console (apps/web).** Every screen whose data has an endpoint now reads it through a
> typed `fetchApi` client (tenant + correlation headers, typed error classes) and TanStack
> Query keyed by tenant: schedule, queue + operator follow-ups, conversations + purpose-gated
> transcript, waitlist (list/enrol/remove/offer accept-decline), customers, funnel/overview,
> consent policy, catalog, menu catalog management (D4 one-legal-step flow with draft editor
> and diff), venue name, find-a-booking, bookable capacity, and operator recovery (DLQ replay,
> audited model-trace read, pre-order resolution). Booking create/cancel/reschedule submit a
> confirmed intent with the canon.v1 payload hash and per-attempt idempotency identity
> (reused across retries), then poll the operation to a terminal state. Surfaces with no
> endpoint (deposits, campaigns, parts of settings/analytics) stay on demo data behind a
> visible badge — an empty API answer renders as empty, never as demo rows.
>
> **Backend (apps/api).** New surface the console drives: availability read + owner slot
> declaration (keyed by `CapacitySlotKey.Derive`, the same derivation fulfilment uses, so
> declared capacity is actually enforced); guest contacts out-of-band (`guest_contacts` +
> `POST /api/guest-contacts` — the canonical payload carries an opaque `contact_ref`, so
> erasure is a row delete and the hash chain stays intact); tenant display name (feeds the
> spec-08 phone lookup); menu catalog list with `age_seconds` + draft-scoped items read;
> waitlist locations read; `extract_failed` as the tenth D4 catalog status, settled at the
> point of failure with an owner alert. Reschedule/modify fulfilment now works:
> `ModifyBookingCommandHandler` amends in one provider call (preserving the original
> reservation until the amendment is secured) and clears `ModificationPending`.
>
> **Security fixes found on the way.** (a) `POST /api/bookings` authorized no mutation —
> it now requires a verification grant or staff role for any operation carrying
> `target_booking_id`, checked before the idempotency pre-check; (b) all four waitlist
> mutations were unauthenticated and fabricated a `"customer"` actor into the audit trail —
> they are now operator-gated with deny-audits; (c) the `X-Tenant-Id` dev shim is gated to
> Development (`Tenancy:AllowHeaderShim`), with a startup warning when explicitly enabled;
> (d) waitlist enrolment gained real idempotency over every material field with a
> liveness-checked replay; (e) `verification_level` is validated and derived from the grant
> rather than trusted from the caller.
>
> **Known boundary (deliberate, documented):** the console **cannot authenticate outside
> Development**. There is no web-origin `TenantContext` issuance path yet (§12 gap 1 of the
> wiring doc) — building an operator sign-in that mints a signed context is the prerequisite
> for any real deployment and is intentionally out of scope here. Deposits stay unexposed
> (`NoDepositPolicy`), capacity is unenforced until a venue declares slots (ruled, and now
> measured by `fulfillment.capacity.untracked_total`), and pre-ruling inline guest contacts
> remain un-erasable (measured by `retention.unerasable_contact_total`).
>
> Four EF migrations: `TenantDisplayNames`, `AddExtractFailedCatalogStatus`,
> `GuestContactStore`, `GuestContactRetentionClockAndCatalogStatusClock`.
> History records are consolidated into one plan/research/review file each under
> `thoughts/shared/`.

---

## Update — 2026-08-07 backend hardening (`f7052b0`, `75522ef`)

Three hardening passes landed on the branch after the wiring work. They are one commit because
they interleave in the same files (the capacity service, the modify command, the waitlist
reconciler and their tests); a hunk-level split would have produced intermediate states that were
never built or tested.

### The load-bearing finding

An earlier pass fixed a hold↔slot deadlock and argued **in writing** that the cross-slot path was
safe: *"cancel only ever wants `SLOT(old)`, and by definition of cross-slot `SLOT(new) ≠ SLOT(old)`,
so there is no shared row to cycle on."*

That holds against a **cancel**. It is false against **another reschedule** — one moving `S1 → S2`
and one moving `S2 → S1` share *both* rows and take them in opposite orders. The cycle is built
entirely out of slot rows, so hold-before-slot never applied to it. Two guests swapping between the
same two sittings is enough to trigger it. It is now reproduced as a real-PostgreSQL `40P01` rather
than argued.

### What changed

- **Cordia Capacity Lock Order** (spec 07, normative): holds before slots, each set ascending by id,
  with a documented carve-out for inserts. `ICapacityLockOrder` is the single implementation — call
  sites name the rows they touch, never the order.
- **State ownership**: four of five state machines exposed no `IsTerminal`, which is why fifteen call
  sites hand-rolled their own sets. All fifteen now derive or ask. Every `operation_status` edge is
  validated at the single write path, so the §11 map is load-bearing rather than advisory.
- **ManualReview**: added `GET /api/booking-operations/work-items` and surfaced reason / SLA deadline
  / current status on the console queue. The table had **no reader at all**, so an escalation was
  durable and unreachable at the same time. Pre-order saga parks and abandoned accept-reconciles now
  open work items, and every insert is atomic (a lost read-then-add race raised `23505`, which aborts
  the whole ambient transaction and loses the escalation with it).
- **Guards**: reflection architecture tests block a sixteenth duplicated state set and keep the
  driver's dispatch table in step with the state machine.
- Closes the eight residual review findings (notification recipient recovery on reconcile **and**
  retry, guest-contact fingerprint backfill, console booking reference, phone-normalisation parity,
  menu draft cache invalidation, operation-status success gate).

### Gates

| Lane | Result |
|---|---|
| api | 1422 unit · 568 integration (1 pre-existing skip) · lint · build |
| web | 609 tests · lint 0/0 · typecheck · build |
| langgraph | 1021 unit · 51 integration · lint |

### Reviewer notes

- **One deliberate behaviour change:** `TryTransitionStatusAsync` now throws on an illegal state
  edge. No production path was writing one — enforcing it surfaced a *test* asserting on
  `Validating → Booked`, which is not an edge. It routes to the existing DLQ/work-item path (Inv-14).
- **New migration** `20260807023952_BackfillGuestContactFingerprints` — data-only, forward-only,
  idempotent, tenant-scoped. New file only, so the immutability guard is untouched and the
  `ModelSnapshot` is correctly unchanged.
- **`docs/frontend-api-wiring.md` re-mapped to 2026-08-07.** The new endpoint was wired but recorded
  nowhere, which made §13's "every endpoint is in exactly one of the three groups" false; §10 gains
  the row and §20 restates the totals. Group C was re-verified — no file under `apps/web/src` calls
  any of the six must-never-wire endpoints.
- **12 manual-verification items remain outstanding** across the three passes (human acceptance
  checks: overlong-phone paste warning, draft-editor stepper after approve, `MaskedField` 20s
  re-mask, schedule booking-reference rendering, Inv-6 under load). Automated verification is
  complete.
- **Residual risk:** no test starts two real modify handlers concurrently — that would be
  timing-dependent. The chain is covered deterministically instead: handlers name both slots in one
  acquisition (unit), the primitive sorts any input (unit), and sorted acquisition cannot cycle while
  unsorted provably does (integration, real Postgres).

---

## Update — 2026-08-09 asserted-role gate + review batch (`6b288de`…`88d3281`)

Started as one small hardening — the `X-Tenant-Id` shim logs a loud startup warning when enabled
outside Development and `Authorization:AllowAssertedStaffRole` did not, so nobody could tell it had
been switched on in production. The `/code-review high` pass that followed reviewed the whole branch
and surfaced a defect family worth fixing with it. Five commits, split by lane so each builds and
tests on its own.

### The load-bearing finding

The warning would have been **half true**. `POST /api/Bookings/{id}/changes:validate` accepts the
same grant-or-staff-role door as the mutation it precedes, but its role branch carried no
`AllowAssertedStaffRole` gate. So where a deployment had closed the role branch, the pre-flight
stayed open: a caller could aim it at any booking id in the tenant and read back `stale_version` vs
`ok` — an existence-and-version oracle over the venue's bookings, reached with a tenant header and
nothing else (Inv-11). A pre-flight may be no stricter than the change it de-risks; it must not be
looser either.

### What changed

| Area | Fix |
|---|---|
| Startup posture | `Authorization:AllowAssertedStaffRole` now announces itself exactly as the tenant shim does — info in Development, **`LogWarning` outside it**, info when disabled |
| Pre-flight authz | `ValidateBookingChangeQueryHandler`'s role branch gated on the same flag (+3 unit tests) |
| Modify capacity | A payload with no time/resource dimension derives a **null** slot key — "not tracked", not "a different slot". Read as a move, the settlement released a confirmed booking's finalized hold and its seats went back on sale under a seated guest. Every `/modifications` op hit this, since `SubmitModificationInput` carries no such field |
| Party size | `party_size` is optional and arrives absent / null / TriState — all falling back to `1`, which `AbsorbSameSlotAsync` takes as the authoritative **total**. A party of four amended without restating its size was resized to one; reserve computed a delta of `1 - 4 = -3`. The derivation now reports whether the size was *stated* |
| Manual recovery | A **create** resolved as Booked finalized no hold, so the sweeper freed the slot under a terminal Booked operation |
| Terminal failures | `Modification`/`CancellationPending` had no clearer on any failure route (policy rejection, declined alternatives, both compensation outcomes) — the operation read `failed_final` while the booking row claimed a change was still in flight, permanently |
| Provider disclosure | `grant_id` and a `grant_`-prefixed `confirmation.identity` were shipped verbatim to the booking provider on every modify — a live spec-08 credential in a third party's request logs |
| langgraph | `customer` was missing from `_MATERIAL_FIELDS` despite a comment claiming otherwise, so projecting the guest onto the request never superseded the revision. The guest was dropped from the submitted payload — no notification destination, unfindable by `?phone_e164=` |
| web idempotency | `pollToTerminal` returns a **non-terminal** status on its 60s budget timeout, but cancel and reschedule reset the attempt identity regardless — the ordinary "still working" path let a second click mint a duplicate operation (Inv-2) |
| web audit | The transcript query key omitted `reasonCode` under `staleTime: Infinity`, so a re-opened session prompted for a purpose and then served cache — PII disclosed under a purpose that never reached the server and produced no audit row |
| web truthfulness | `threads.find(…) || threads[0]` rendered a real guest session nothing else knew was open: no transcript fetched, no purpose asked, empty message map — a confident, blank conversation. A conflicting drag was a silent no-op |

### Gates

| Lane | Result |
|---|---|
| api | 1435 unit · lint · build |
| web | 610 tests · lint 0/0 · typecheck |
| langgraph | 1023 unit · lint |

Integration suites were **not** re-run in this pass (they need the Postgres fixture); the pre-commit
hook ran the .NET unit suite and formatting on every commit.

### Reviewer notes

- **Two review findings were rejected on inspection, not fixed.** The claimed waitlist decline/expire
  hold race does not exist — both are fenced by `TryResolveAsync(Offered → …)` with a version guard
  and `Accepted` is terminal, so neither can win after an accept.
- **Three are deliberately left for a product/spec decision, not silently changed:**
  - Marking a waitlist enrollment fulfilled on `ManualReview` and treating an expired derived
    idempotency key as a live replay both **reverse decisions the code documents with an Inv-16
    rationale** — each naive fix trades a double-offer for stranding a guest with nothing.
  - `GET /api/customers` exposes `bookings.customer_id`, which falls back to raw phone/email, as a
    substring search behind only the tenant header. Real, but fixing it changes what a shipped
    endpoint returns.
  - `hasConflict` treats *any* venue overlap as a conflict because every booking maps to one lane
    (gap 9: no resource model). Whether overlaps should block at all is a product question — this
    pass only made the refusal **visible** rather than silently permitting them.
- **No new migration, no contract change**, so nothing regenerates and the type-generation blast
  radius is empty. `CapacityRequest` is a new internal Domain record, not a shared schema.
- **`docs/frontend-api-wiring.md`** records the gate and the pre-flight's matching lock beside the
  reschedule authorization section, so the console-authentication exception stays a single
  documented boundary rather than two.


## Type of change

- [x] Bug fix (fixes an issue)
- [x] New feature (adds functionality)

## Checklists

### Development

- [x] Unit tests written and passing — API 1279 unit + 534 integration (1 known unrelated
      skip), web 587 (+ typecheck, lint, build), langgraph 1019 unit + 51 integration;
      all lints clean
- [x] Relevant documentation updated — `docs/frontend-api-wiring.md` fully re-mapped
      against this surface (per-screen verdicts, endpoint reference, header table,
      never-wire rationale)

### Security

- [x] Security impact of change has been considered — see the security fixes above; the
      transcript read stays purpose-gated and audited on allow **and** deny, guest PII moved
      out of the immutable hashed payload, and the six never-wire endpoints (channel ingress,
      webhook receipt, agent-lane writes) are documented as auth boundaries the console must
      not cross
- [x] Code follows company security practices and guidelines — operator spine
      (`X-Actor`/`X-Actor-Role`/`X-Reason-Code`) on every audited write, fail-closed tenant
      guard, no idempotency-free write is ever auto-retried

### Code review

- [x] Pull request has a descriptive title and context useful to a reviewer
- [ ] Changes have been reviewed by at least one other contributor
- [ ] Pull request linked to task tracker where applicable (no tracker in use)
