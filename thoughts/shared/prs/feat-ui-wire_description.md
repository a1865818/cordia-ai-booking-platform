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
