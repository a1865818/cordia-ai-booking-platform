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

---

## Update — 2026-08-09 terminal-path preconditions (`e8dc43e`, `a1dd572`)

A follow-up review of the branch returned eight findings — six in the manual-recovery and
capacity terminal paths, two in the console. Three of the six were one defect wearing three hats,
so they were fixed together rather than patched individually.

### The load-bearing finding

**Resolving a ManualReview treated its preconditions as optional side effects.** A cancel reached
its lifecycle transition only when the booking was already `CancellationPending`, and then
discarded the result. Both gaps committed the same lie: outside that state the block was skipped
entirely, and a zero-row result fell through — so the operation recorded `Booked`, the outbox
announced the cancellation had succeeded, and the booking stayed **live**. A guest told their
booking was cancelled, sitting in a slot the venue still has reserved (Inv-7/Inv-8).

### What changed

| Area | Fix |
|---|---|
| Cancel resolved Booked | The lifecycle transition is a hard precondition, matching `CancelBookingCommandHandler` — missing booking or zero rows refuses. Already-`Cancelled` is idempotent success, and the seat release runs unconditionally as the sibling handler does (a zero-row no-op when already free) |
| Modify resolved Booked | Refuses **before** any capacity or outbox write when the booking is not amendable. Stated as an allow-list of the two settleable lifecycle states, so `CancellationPending` — a cancellation in flight — refuses too, and a value added later fails closed |
| Pending-change clear | Version-guarded and its result is a precondition: a lost race used to leave the booking at `ModificationPending` permanently while the outbox reported success |
| `NotTracked` ≠ finalized | No first-party hold was ever created, so there is no destination seat — the same position as `HoldNoLongerValid`. A cross-slot payload still derives a non-null target key, so the previous owner's finalized hold was released anyway and the booking committed Booked with capacity on **neither** slot. Fixed in **both** lanes; the automatic one is the common path |
| Failed-mutation revert | The pending state is now matched to the operation that parked it. Reverting on either state meant a failed Modify whose booking had moved to `CancellationPending` was pushed back to Confirmed, silently undoing an unrelated in-flight cancellation. Fixed in `BookingPendingLifecycle`, so the four terminal-failure routes sharing it are fixed too |
| Console "today" | `isToday` came from `ConsoleWeek.todayIndex`, frozen at week-build time — a console open across local midnight kept **yesterday** marked as today, now-line and "remaining" tile included. `todayIndexAt` asks the screen's existing 30s clock; DST-safe by comparing calendar days. The create drawer's default day read the same stale index |
| Refused reschedule | The no-confirmed-version branch returned without reverting the optimistic move, leaving the booking in a slot the platform had just declined |
| Partially-answered queue | `loadFailures` omitted the work-items query, so a failure there rendered as a fully loaded queue that had quietly dropped every escalation's reason, SLA deadline and assignee |

### Gates

| Lane | Result |
|---|---|
| api | 1442 unit · lint · build |
| web | 615 tests · lint 0/0 · typecheck |

langgraph is untouched by this pass. Integration suites were not re-run (they need the Postgres
fixture); the pre-commit hook ran the .NET unit suite and formatting on every commit.

### Reviewer notes

- **One prescription was deliberately not followed.** The report asked that the modify path require
  `ModificationPending` before reverting. It must not: `ModifyBookingCommand` treats that state as
  optional precisely because a reschedule accepted through the spec-04 door never parks the booking
  there, so enforcing it would refuse every console reschedule this lane exists to recover. The
  automatic lane's actual rule — refuse on a booking that is not amendable — is used instead.
- **`/code-review high` then found four defects in these fixes, all since closed:** the `NotTracked`
  guard was applied to the manual lane only, while that class's own comment says the two lanes must
  not drift; a fall-through comment claimed a state enumeration was exhaustive when
  `CancellationPending` was unhandled; an already-cancelled path dropped a seat release the sibling
  handler runs unconditionally; and a React effect dependency widened to `week` would have wiped a
  half-typed booking form on any week navigation.
- **No new migration, no contract change**, so nothing regenerates.

---

## Update — 2026-08-09 PII minimisation + console truthfulness (`b7d876a`…`4837748`)

A security-focused pass over the branch returned **one HIGH finding**, independently confirming what
an earlier `/code-review high` had raised and I had deferred as "needs a product decision". It is now
closed rather than deferred. Alongside it, ten console defects and three places where this document
had stopped describing the code.

### The load-bearing finding — `customer_ref` was not opaque

`customer_ref` is documented as an opaque handle. It is not.
`FinalizeBookingCommandHandler` resolves `bookings.customer_id` through
`customer_id → contact_ref → caller_id → phone_e164 → email`, and
`waitlist_enrollments.customer_ref` is the value `NotificationRecipientResolver` hands to the
SMS/email adapter **as the send destination**. The console mints an opaque `contact_ref` before it
hashes a payload — but the **agent lane never does** (`contact_ref` appears once in `cordia_agent`,
as a field declaration nothing populates) and carries `phone_e164` inline.

So every agent-created booking put a raw phone number in that column, and four reads returned it to
any caller holding tenant scope — **no grant, no actor, no reason code, no audit row**:
`GET /api/customers`, `GET /api/Bookings/schedule`, `GET /api/conversation-sessions`,
`GET /api/waitlist/enrollments`. That is a strictly lower bar than the transcript read of the same
data class, which demands a `read_sensitive` grant or operator+reason and audits allow **and** deny.
`?query=+614` worked as a phone-prefix search. `CustomersController`'s own doc comment claimed the
endpoint exposed "no name, phone or email (spec 14 minimisation, Inv-12)".

**Fixed at the read boundary.** `ContactMinimisation.Mask` runs at the point of emission on all four:
a phone leaves as `•••222`, an email as `p•••@example.com`, and anything that is not a deliverable
address passes through untouched. The search matches the masked value, removing the oracle — the
console never used the parameter (`useCustomers("", 50)` is its only call site), so no working
behaviour is lost. Grouping still uses the **raw** key, so per-guest counts stay exact. The un-masked
contact remains behind the grant-gated `GET /api/Bookings/{bookingId}`.

**Still open at the source, and recorded as such:** minting a `contact_ref` in the agent lane so the
column stops being an address at all. This closes the exposure, not its origin.

### What else changed

| Area | Fix |
|---|---|
| Modify vs cancel | The in-transaction guard rejected only `Cancelled`, so a booking parked at `CancellationPending` still booked its amendment and told the guest it moved while another operation was removing it. Now an allow-list of the two settleable states, so a lifecycle value added later fails closed |
| Accepted-operation identity | The create drawer described an accepted operation from the LIVE form, and guarded `onCreated` by the form-derived `attemptKey` — so a catalog republish re-fired it and appended the booking twice. Both now use a snapshot frozen at submit time, guarded by the operation id |
| In-flight submissions | A submission surviving a close-and-reopen applied itself to the new form. A token invalidates it; an abandoned-but-**accepted** operation is reported to the console so it surfaces in the queue rather than inviting a duplicate |
| `composeReason` | The Latin-1 substitution ran before the whitespace collapse, and `[^ -ÿ]` matches CR/LF/TAB — every line break in an audit note became a literal `?`. Had no tests; has six now |
| `canonicalHash` | Trimmed timestamps with `String.trim()` while the server trims by `char.IsWhiteSpace` — the U+0085/U+FEFF divergence the file's own header names. A padded timestamp hashed differently on the two sides and 422'd |
| Six smaller console defects | Unclickable unfinalised bars in Diagram view; `TIME_SLOTS` covering only 09:00–17:30 (blank picker for out-of-hours bookings); the waitlist countdown running on accepted/expired offers; the menu draft editor savable after Approve; a 409 telling operators to blind-retry; demo customers rendering as real after the live list arrived |

### Gates

| Lane | Unit | Integration | Lint | Build |
|---|---|---|---|---|
| api | 1456 | **568** (1 pre-existing skip) | ✅ | ✅ |
| web | 636 | — | 0/0 | ✅ |
| langgraph | 1023 | 51 | ✅ | — |

The integration suites ran for the first time in these passes (a local Postgres was available), which
closes the coverage caveat the previous two updates carried.

### Reviewer notes

- **A test caught a bug in the security fix itself.** `PhoneMatching.Normalise` discards every
  non-digit — it exists to match numbers a human typed — so it "normalised" `gc_9f2c41ab8e004d10` to
  `9241800410` and masked it. Every opaque console label would have become `•••410`. A shape check
  (a letter or underscore means handle, not number) runs before the normaliser.
- **All 568 integration tests passed both before and after the masking change.** Nothing had pinned
  the raw value, which is precisely why the exposure survived this long.
- **This document was wrong in three places** and is corrected: §17 and §18 both asserted that no
  reschedule `VerificationGate` was wired and that this was the right answer. True for a *pre-emptive*
  gate; false absolutely, because the staff role authorizes the write and never the gated booking-details
  read that precedes it. §8 now records that the grant travels on **both** calls.

---

## Update — 2026-08-09 concurrency, honesty and the draft editor (`1c9ef50`…`caa2ab6`)

Three review rounds, eight commits. Two findings turned out to be a **defect class rather than a
defect**, and in both the fix as proposed would not have worked.

### `GetOrAddAsync` — one bug, eight handlers, and a remedy that had to be the other one

A reviewer flagged that `ModifyBookingCommand.CreateOrLoadStepAsync` does an unlocked read then an
insert, so two concurrent deliveries both insert and one hits `uq_workflow_step_per_type`. Correct
— but the suggested remedy (catch the violation, re-read) **cannot work here**: PostgreSQL aborts
the WHOLE ambient transaction on a constraint violation, so by the time 23505 surfaces the
re-read *and the commit* fail with `current transaction is aborted`. This repo had already hit
that and documents it in three places; the working primitive is `ON CONFLICT DO NOTHING`.

It also was not one call site. The identical read-then-add appears in **eight** handlers, and in
`Validate` and `ReserveCapacity` it is worse than in `Modify`: the step insert shares its
transaction with the operation's own status transition, so a routine redelivery **rolled the
operation back** instead of advancing it. `AddAsync` is gone from `IWorkflowStepRepository` — an
unguarded insert on that interface is this bug offered to the next caller.

A conflict whose winner cannot then be read now **throws** rather than returning the candidate.
That was a review finding on my own fix, and the reasoning is the point: returning a
`workflow_step_id` that exists nowhere means the finalize lane calls the provider, its
version-guarded transition affects zero rows and reads as an ordinary lost race, and the retry
scheduler schedules against a phantom step — a booking created at the provider with the operation
stranded in `Finalizing`. The read-then-add it replaced failed loudly there.

### Security: a partially-applied grant strip fell back to the raw payload

`StripAuthorizationMaterial` removes the spec-08 credential before the payload goes to a
third-party booking provider. It read `confirmation.identity` with `GetValue<string>()`, which
**throws on a non-string node**, and the throw landed in a catch that returned the ORIGINAL
payload — after `grant_id` had already been removed from the copy. A payload whose `identity` was
a number, object, array, bool or null therefore shipped the live verification grant to the
provider, where it lands in that provider's request logs. The one input shape the method did not
expect defeated it completely rather than degrading it. Parsing is now the only step that can
throw, and it happens before anything is removed.

### Console honesty

| Area | Fix |
|---|---|
| Scan ceiling | `ScanAsync` reported `truncated` whenever the loop hit `MaxScan`, without asking whether anything was left. A tenant with exactly 20,000 rows had been read in **full** and was still told its grid might be missing bookings, and its customer search might be missing a guest who does not exist. "Possibly incomplete" is the one answer an operator cannot act on |
| Catalog list | A failed `GET /api/menu/catalogs` was indistinguishable from a tenant with no catalogs — the operator was told there was nothing to approve when the truth was that we could not find out, with no retry. Loading and error states now render alongside the session-confirmed rows |
| Merge order | Session-tracked rows seeded the map first and kept their positions, so a catalog created seconds ago on another machine rendered **below** rows from an old session. The endpoint's order is the only ordering signal there is; it is now the spine of the merge |
| 403 headings | `ErrorState` said "Verification needed" for **every** 403, but `toApiError` already distinguishes a verification requirement from a role refusal — and the role refusal is the console's most common 403 by far. Those operators were being pointed at a flow that cannot fix their problem |
| Wiring guide | Four corrected claims: a "nothing is unbuilt" headline contradicted by its own gap table, a "changes no behaviour" line covering a monitor that settles catalogs terminally, five lowercase `/api/bookings`, and a stray `+` breaking a paragraph into a list item |

### The catalog draft editor: a race, and a field that was never there

**Save and Approve could run at once.** `in_review` carries both `canApprove` and `canEdit`, so
they sit on the same row with separate pending flags. An owner could stage a price correction,
click Save, and click Approve before it returned; if Approve won, the catalog left `in_review`,
the editor unmounted, the edit was refused and **its error went with the unmounted component** —
the operator watched the row turn Approved with no reason to doubt the corrections landed. Items
cannot change after approval, so that is not recoverable.

The race runs **both** ways — Save was disabled only by its own `edit.isPending`, so it stayed
clickable during an Approve — so this is a two-way handshake. Three further doors each reopened
it on their own: the failure panel's "Try again" (guarded on entry but not on the stored retry),
the editor's own `Close` (unmounting mid-save let Approve go live while the POST was on the
wire), and `GhostButton`, which **declared `disabled` in its props and silently ignored it**.

**The description field did not exist.** `CatalogItemEdit` has carried `description` and
`pending` has staged it since the editor was written — there was simply no input, so the branch
was unreachable and a wrong extracted description could be neither seen nor corrected before it
was frozen into the catalog. Adding it surfaced five more:

- `null` and `''` both mean "no value" but were compared as different, so touching an empty field
  staged a write that changes nothing — for `description` **and** `name`;
- a description CLEAR and a description EDIT rendered byte-identical text in the diff, so the
  approval surface could not tell a destructive removal from a correction;
- `before`/`after` were computed unconditionally, so a description-only edit struck through the
  old price beside an identical new one — asserting a price change nobody made;
- clearing a NAME produced no explanation and no row heading: the most destructive edit was
  described the least;
- an item whose `item_id` is null was staged into the batch, where one unmatched edit makes
  `ApplyItemEditsAsync` throw and roll back **every other correction in the save**.

### Gates

| Lane | Unit | Integration | Lint | Typecheck | Build |
|---|---|---|---|---|---|
| api | **1462** | **573** (1 pre-existing skip) | ✅ | — | ✅ |
| web | **676** | — | 0/0 | ✅ | ✅ |
| langgraph | **1023** | 51 | ✅ | — | — |

### Reviewer notes

- **Two proposed fixes were wrong for this codebase, and the commits say why** — the catch-23505
  approach (Postgres aborts the transaction) and `return page.Count < PageSize` (`page` is scoped
  inside the loop). Both are commented so the next person does not retry them.
- **A test file was exercising a real `fetch` that jsdom rejects.** `MenuCatalogPanel`'s discovery
  read was never mocked, so the panel rendered its error path throughout and the assertions passed
  only because the empty state did not distinguish "failed" from "empty" — the same defect being
  fixed in the component, mirrored in its tests.
- **`ErrorState` had no tests at all** despite being the failure surface for sixteen call sites,
  which is how the 403 heading survived. It has fifteen now.
- Each new test was checked to **fail against the old code**. The first null-vs-empty test did
  not — it used the fixture's `description: ""`, which the old comparison already handled — so it
  was rewritten against a `null`, which is what the generated contract actually permits.


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
