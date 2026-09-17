# PR Title: fix: close the remaining Wave 3–5 security and correctness backlog

## Change description

This is the rest of the roadmap backlog in one PR: 15 planned phases, one commit each, security first, plus fixes from an xhigh code review. Every guard was tested by breaking it on purpose: its own test had to fail and the matching negative test had to keep passing.

Research changed several fixes before any code was written, because the remedy filed in the issue would have broken something (details in `thoughts/shared/research/2026-08-31_waves-3-5-remaining.md` and the plan's Discoveries).

### Security and authorization (#72, #80, #71, #73, #74)

- **#72: operator actions now require a proven identity.** `OperatorActionBehavior` checked the *role* in `X-Actor-Role` but never whether the identity behind it was verified. So `X-Actor: anyone` + `X-Actor-Role: owner` authorized all ~32 operator actions in every environment. It now requires a verified operator session unless `Authorization:AllowAssertedStaffRole` is on (on in Development and the test host, off elsewhere), and it writes the DENY audit row either way.
- **#72: console reads that expose PII or money** now require a signed-in operator through `OperatorGuard`: customers, deposits, payouts, reporting, schedule, operations list, work items, conversations, waitlist enrollments and the new notification reads. `IOperatorActionRequest` wasn't reused for these because it requires `X-Reason-Code` and would 403 every console screen. Mutations are left to the behaviour, so their refusals stay audited.
- **#80: tenant scope is stamped once.** `TenantContext.SetTenant` tolerates the same tenant again (dead-letter replay does exactly that) and refuses a different or blank one. The public setter is gone.
- **#71: verification codes are six-digit CSPRNG values**, compared in constant time. A challenge locks after five wrong codes (new `failed_attempts` column). The **committed (public) pepper** is refused anywhere but Development, so a real pepper must be configured there or the host refuses to start: the old check only matched the literal string "Production", so Staging used the committed pepper. Nothing delivers codes yet, so Development and the test host return the code in `development_code`, and the console's verification box shows it.
- **#73: the API refuses to start outside Development** while any credential committed to the repo is in effect: DB password, signing keys, webhook secret or object-store key. It names the setting, never the value. Deleting the values instead would crash the host and break the byte-for-byte shared signing key with `apps/langgraph/.env.example`.
- **#74: delivery receipts are HMAC-signed** over timestamp, tenant and raw body, within a ±5 minute window. The raw body is verified by a resource filter that runs before model binding, so the JSON contract is unchanged.

### Correctness (#105, #102, #108, #110, #106)

- **#105:** reconciliation lookups respect an open provider circuit and use their own circuit key. An exhausted budget still escalates to manual review.
- **#102:** outbox leases are renewed for the current row and every row waiting behind it in the lane. A row another publisher has reclaimed is skipped. A failed renewal skips only that row.
- **#108:** the dispatch step and cancel-before-create take the same per-operation `pg_advisory_xact_lock`, which closes the race Wave 2 only narrowed. A version bump was rejected because it would silently fail 49 unrelated version-checked transitions.
- **#110:** the console scan pages by keyset, `(updated_at, operation_id) < (last row)`, over a new index. On a 20,001-row tenant a page is an index seek of about 2 ms, against 80 ms plus a 20k-row sort for the old deep `OFFSET` page.
- **#106:** new `tenant_capacity_primitives` table; the resolver is now async and scoped. The first-party provider is always the first-party rung, enforced by a check constraint. Also fixes the stale spec-07 line about slot provisioning.

### Console and visibility (#83, #75)

- **#83:** `GET /api/Bookings/{id}/notifications` and `GET /api/booking-operations/{id}/notifications` return stored delivery state, without recipient or provider identifiers. The booking drawer renders them as recorded: no rows reads "No messages recorded", never "sent". The console still has no way to post a receipt.
- **#75:** one startup line lists every stubbed port and what it means (a warning outside Development). Setting a `Stubs:<Port>=false` flag for a port with no real implementation refuses to start. A drift test compares the list with what `AddInfrastructure` actually registers.

### Code review (xhigh, Sonnet subagents)

**Round 1:** 15 findings, each checked against the code: 11 fixed, 4 accepted with reasons. The main fixes:
- the webhook signature now covers the raw tenant header, which it must because outside Development the tenant header shim is off and the resolved tenant is empty;
- a reconcile lookup that throws no longer leaves the circuit stuck closed;
- five more console reads are guarded;
- a failed lease renewal can no longer abort the lane.

**Round 2** (PR review): 20 findings plus 2 restatements, all fixed after verification. Highlights:
- every guarded console read now declares its 403, and a test pins the contract;
- a signed receipt must name its tenant, a blank tenant claim is a 401 (not a 500), and a whitespace actor id is not a session;
- outbox renewal is a bounded heartbeat — a fast lane writes nothing — and a failed queued-row renewal no longer skips the current row;
- the lookup circuit ignores our own cancellation but still counts provider timeouts;
- the web client clears its query cache at sign-out and sign-in, so a second operator on the same tab never sees the first operator's cached reads;
- a `booking_operations (tenant_id, booking_id)` index backs the notification drawer read.

Both review tables are at the end of the plan doc.

### ⚠️ Deployment and contract changes

| Change | Action required |
|---|---|
| Webhook `X-Webhook-Secret` removed | Providers must send `X-Webhook-Timestamp` + `X-Webhook-Signature: sha256=<hex HMAC of "{ts}.{tenant_byte_len}:{tenant}." + body>` |
| Committed credentials refused outside Development | Set real DB password, signing keys, webhook secret, object-store key |
| Pepper required outside Development | Set `Verification:CodePepper` (or `CORDIA_VERIFICATION_CODE_PEPPER`) |
| Operator reads/actions need a verified session outside Development | Console must be signed in |
| Verification codes are random | Real step-up verification can't be completed in a deployment until a delivery channel exists |
| 4 additive migrations | `VerificationAttemptLockout`, `TenantCapacityPrimitives`, `ConsoleScanKeysetIndex`, `BookingOperationsBookingIdIndex`. The index migrations are plain `CREATE INDEX`, accepted because Cordia isn't deployed |
| OpenAPI additions | 2 notification GET routes, `development_code` on `POST /api/verifications`, 413 and a required signed `X-Tenant-Id` on the webhook, 403 on every operator-guarded console read |

### Not in this PR

- **#84** (menu artifact upload): blocked. Its table has no spec-12 owner, and the object store only works on one machine.
- Follow-ups: operator provisioning (#113) and mandatory login (#114) are filed; still to file: a verification code delivery channel; real implementations of the stubbed ports; the missing `GET /api/customers/context` that the agent calls; the stale `MenuController` comment about the 08/14 decision.

### Verification

- `bun nx test api`: 1938 passed
- `bun nx test-integration api`: 780 passed, 1 skipped (already skipped before this PR)
- `bun nx lint api` clean · `bun nx lint web` clean · `bun nx typecheck web` clean · `bun nx build web` passes
- Web tests: 730 passed. Run with `npx bun@latest test --parallel`, because local Bun 1.3.14 is below the suite's required 1.4.0; CI uses the latest Bun.
- Local dev database migrated to the latest migration, `BookingOperationsBookingIdIndex`.

Closes #71
Closes #72
Closes #73
Closes #74
Closes #75
Closes #80
Closes #83
Closes #102
Closes #105
Closes #106
Closes #108
Closes #110

## Type of change

- [x] Bug fix (fixes an issue)
- [x] New feature (adds functionality)

## Checklists

### Development

- [x] Unit tests written and passing
- [x] Relevant documentation updated (spec 07; research, plan and per-phase discoveries under `thoughts/shared/`)

### Security

- [x] Security impact of change has been considered: threat-modelled against Inv-1/11/12/13, and every guard tested by breaking it on purpose
- [x] Code follows company security practices and guidelines: no secret values logged; committed credentials refused outside Development

### Code review

- [x] Pull request has a descriptive title and context useful to a reviewer
- [ ] Changes have been reviewed by at least one other contributor (automated xhigh review done and addressed; human review pending)
- [x] Pull request linked to task tracker where applicable
