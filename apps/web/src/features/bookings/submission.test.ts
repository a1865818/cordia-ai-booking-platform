import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { canonicalHash } from "../../api/canonicalHash";
import {
  buildSubmission, customerFromRef, toCanonicalPayload, toIsoInstant,
  normalisePhone, MAX_PHONE_RAW_LENGTH,
} from "./buildSubmission";
import { looksUnnormalisedPhone } from "../../components/ui/PhoneInput";
import { getOrCreateAttempt, resetAttempt, supersedeAttempt } from "./identity";
import { nextInterval, normalizedStatusLabel, isBooked, isTerminal, MAX_INTERVAL_MS } from "./useOperationStatus";
import { isRetryableWithNewAttempt, writeFailureMessage } from "./mutations";
import { ApiError, ApiIdempotencyConflictError, ApiVerificationRequiredError } from "../../api/problem";

const context = {
  identity: {
    intentId: "int_1",
    operationId: "op_1",
    confirmationId: "cnf_1",
    idempotencyKey: "idem_1",
    revision: 1,
    confirmedAt: "2026-08-01T10:00:00.000Z",
  },
  sessionId: "sess_1",
  identityRef: "operator@venue",
  readBackVersion: "rb_1",
  confirmedAt: "2026-08-01T10:00:00.000Z",
};

const draft = {
  operation: "create" as const,
  serviceId: "gel",
  startsAt: "2026-08-05T09:30:00.000Z",
  partySize: 4,
  durationMinutes: 45,
};

describe("toCanonicalPayload", () => {
  it("omits absent optional fields rather than nulling them", () => {
    const payload = toCanonicalPayload(draft) as Record<string, unknown>;
    expect(Object.keys(payload).sort()).toEqual([
      "duration_minutes",
      "operation",
      "party_size",
      "service_id",
      "starts_at",
    ]);
    expect("location_id" in payload).toBe(false);
    expect("target_booking_id" in payload).toBe(false);
    expect("resource_id" in payload).toBe(false);
  });

  it("includes resource_id only when it is a real staff id, not the unassigned sentinel", () => {
    expect(
      (toCanonicalPayload({ ...draft, resourceId: "staff_alex" }) as Record<string, unknown>).resource_id
    ).toBe("staff_alex");
    expect(
      "resource_id" in (toCanonicalPayload({ ...draft, resourceId: "s1" }) as Record<string, unknown>)
    ).toBe(false);
  });

  it("includes the modify target when present", () => {
    const payload = toCanonicalPayload({
      ...draft,
      operation: "modify",
      targetBookingId: "bk_1",
      targetBookingVersion: 3,
    }) as Record<string, unknown>;
    expect(payload.target_booking_id).toBe("bk_1");
    expect(payload.target_booking_version).toBe(3);
  });

  // The payload is hashed and immutable, so a phone the SERVER cannot normalise must never be
  // written into it: every server-side PhoneMatching.Normalise would read it back as null and the
  // booking would be permanently unfindable by phone and un-notifiable, with no repair possible
  // (rewriting the payload invalidates payload_hash — Inv-2/3).
  it("keeps a phone the server could never re-normalise out of the immutable payload", () => {
    const tooManyDigits = `+${"4".repeat(70)}`; // normalises to 71 chars > MAX_PHONE_RAW_LENGTH
    const payload = toCanonicalPayload({
      ...draft,
      customer: { phoneE164: tooManyDigits },
    }) as Record<string, unknown>;

    expect("customer" in payload).toBe(false);
  });

  // The case that matters most, and the one an earlier attempt at this fix broke: a perfectly
  // ordinary number padded past 64 RAW characters. Dropping it would leave a booking with no
  // contact at all in an immutable payload — worse than the bug being fixed.
  it("keeps an ordinary number that is merely padded past the raw ceiling", () => {
    const padded = `+61 400 111 222${" ".repeat(60)}`;
    expect(padded.length).toBeGreaterThan(MAX_PHONE_RAW_LENGTH);

    const payload = toCanonicalPayload({
      ...draft,
      customer: { phoneE164: padded },
    }) as Record<string, unknown>;

    expect(payload.customer).toEqual({ phone_e164: "+61400111222" });
  });
});

describe("normalisePhone — parity with PhoneMatching.Normalise (apps/api)", () => {
  // The bound is the SERVER's (PhoneMatching.MaxLength = 64), applied to the NORMALISED output —
  // that is the value written into the payload and the one the server must be able to re-normalise.
  // These vectors are the parity pin: if either side moves its threshold, one of them fails.
  it("accepts a value at exactly the 64-char ceiling", () => {
    const atLimit = `+${"4".repeat(63)}`; // 64 chars normalised
    expect(atLimit).toHaveLength(MAX_PHONE_RAW_LENGTH);
    expect(normalisePhone(atLimit)).toBe(atLimit);
  });

  it("rejects a value one character over the ceiling, rather than truncating it", () => {
    const overLimit = `+${"4".repeat(64)}`; // 65 chars normalised
    expect(normalisePhone(overLimit)).toBeUndefined();
  });

  it("measures the ceiling AFTER stripping formatting, not on the raw input", () => {
    // 12 digits wrapped in enough separators to exceed 64 raw characters. The server's own bound
    // never sees this string — it sees the normalised output — so refusing it here would lose a
    // usable phone for no reason.
    const padded = `+61 - 400 - 111 - 222${" ".repeat(50)}`;
    expect(padded.length).toBeGreaterThan(MAX_PHONE_RAW_LENGTH);
    expect(normalisePhone(padded)).toBe("+61400111222");
  });

  it("still strips formatting inside the ceiling", () => {
    expect(normalisePhone("+61 400 111 222")).toBe("+61400111222");
    expect(normalisePhone("(+61) 400-111-222")).toBe("+61400111222");
  });

  it("warns at capture for a number that normalises too long", () => {
    // looksUnnormalisedPhone delegates to normalisePhone, so the operator-facing warning covers
    // this for free — one rule, one definition of "usable".
    expect(looksUnnormalisedPhone(`+${"4".repeat(64)}`)).toBe(true);
    // ...and does NOT nag about a merely padded, perfectly good number.
    expect(looksUnnormalisedPhone(`+61 400 111 222${" ".repeat(60)}`)).toBe(false);
  });
});

describe("buildSubmission", () => {
  it("binds the confirmation hash to the revision hash", async () => {
    const request = (await buildSubmission(draft, context)) as Record<string, any>;

    // The server rejects the submission with 422 unless these are equal, and a DB trigger
    // enforces the same thing — so they must come from one computation.
    expect(request.confirmation.intent_payload_hash).toBe(request.revision.payload_hash);
  });

  it("computes the same hash the shared canon.v1 implementation does", async () => {
    const request = (await buildSubmission(draft, context)) as Record<string, any>;
    const expected = await canonicalHash(toCanonicalPayload(draft));

    expect(request.revision.payload_hash).toBe(expected.value);
    expect(request.revision.payload_hash).toHaveLength(64);
  });

  it("binds the confirmation to the same intent and revision the validator checks", async () => {
    const request = (await buildSubmission(draft, context)) as Record<string, any>;

    expect(request.confirmation.intent_id).toBe(request.revision.intent_id);
    expect(request.confirmation.intent_revision).toBe(request.revision.revision);
    expect(request.intent_id).toBe("int_1");
  });

  it("carries every field the structural validator requires", async () => {
    const request = (await buildSubmission(draft, context)) as Record<string, any>;

    for (const field of ["operation_id", "idempotency_key", "revision", "confirmation"]) {
      expect(request[field]).toBeTruthy();
    }
    for (const field of ["intent_id", "schema_version", "tenant_policy_version", "payload_hash", "source_session_id"]) {
      expect(request.revision[field]).toBeTruthy();
    }
    for (const field of ["confirmation_id", "intent_payload_hash", "identity", "source_turn_id", "read_back_version", "verification_level"]) {
      expect(request.confirmation[field]).toBeTruthy();
    }
  });

  it("produces a different hash when a material field changes", async () => {
    const a = (await buildSubmission(draft, context)) as Record<string, any>;
    const b = (await buildSubmission({ ...draft, partySize: 6 }, context)) as Record<string, any>;
    expect(a.revision.payload_hash).not.toBe(b.revision.payload_hash);
  });
});

describe("toIsoInstant", () => {
  it("places the slot on the right local day and time", () => {
    const weekStart = new Date(2026, 7, 3); // Monday
    const iso = toIsoInstant(weekStart, 2, 9 * 60 + 30); // Wednesday 09:30 local
    const parsed = new Date(iso);
    expect(parsed.getDate()).toBe(5);
    expect(parsed.getHours()).toBe(9);
    expect(parsed.getMinutes()).toBe(30);
  });
});

describe("attempt identity", () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it("returns the same identity across retries — the key is never re-minted", () => {
    const first = getOrCreateAttempt("create:1");
    const second = getOrCreateAttempt("create:1");
    expect(second).toEqual(first);
    expect(second.idempotencyKey).toBe(first.idempotencyKey);
    expect(second.operationId).toBe(first.operationId);
  });

  it("mints a fresh identity for a different attempt", () => {
    const a = getOrCreateAttempt("create:1");
    const b = getOrCreateAttempt("create:2");
    expect(b.idempotencyKey).not.toBe(a.idempotencyKey);
  });

  it("mints a fresh identity after the attempt is reset", () => {
    const first = getOrCreateAttempt("create:1");
    resetAttempt("create:1");
    const second = getOrCreateAttempt("create:1");
    expect(second.idempotencyKey).not.toBe(first.idempotencyKey);
  });

  it("supersedes with a new key and operation but the same intent (Inv-3/Inv-10)", () => {
    const first = getOrCreateAttempt("create:1");
    const next = supersedeAttempt("create:1");

    expect(next.intentId).toBe(first.intentId);
    expect(next.revision).toBe(first.revision + 1);
    // A key must never be bound to two different canonical payloads.
    expect(next.idempotencyKey).not.toBe(first.idempotencyKey);
    expect(next.operationId).not.toBe(first.operationId);
  });
});

describe("operation polling", () => {
  it("backs off but never past the ceiling", () => {
    expect(nextInterval(0)).toBe(1000);
    expect(nextInterval(10_000)).toBe(2000);
    expect(nextInterval(120_000)).toBe(MAX_INTERVAL_MS);
  });

  it("only reports booked when the server says booked", () => {
    expect(isBooked({ status: "booked", terminal: true } as never)).toBe(true);
    expect(isBooked({ status: "accepted", terminal: false } as never)).toBe(false);
    expect(isBooked(undefined)).toBe(false);
    // The disagreement case, previously untested and previously TRUE: a booked status that is not
    // yet terminal must not be announced as a confirmed booking (Inv-16).
    expect(isBooked({ status: "booked", terminal: false } as never)).toBe(false);
    // `terminal` alone is not success either — rejected/expired/failed are all terminal.
    expect(isBooked({ status: "rejected", terminal: true } as never)).toBe(false);
  });

  it("treats terminal strictly as the server's flag", () => {
    expect(isTerminal({ terminal: true } as never)).toBe(true);
    expect(isTerminal({ terminal: false } as never)).toBe(false);
    expect(isTerminal(undefined)).toBe(false);
  });

  it("phrases a cancellation as cancelled, not confirmed", () => {
    // A successful cancel terminates at `booked` — D1 has no cancelled operation status, so
    // the BOOKING carries the cancellation. Echoing the create wording would tell an operator
    // their cancellation was "Confirmed", i.e. the opposite of what happened.
    expect(normalizedStatusLabel("booked", "cancel")).toBe("Cancelled");
    expect(normalizedStatusLabel("booked", "create")).toBe("Confirmed");
    expect(normalizedStatusLabel("in_progress", "cancel")).toBe("Cancelling");
    expect(normalizedStatusLabel("failed", "cancel")).toBe("Couldn't cancel");
  });

  it("never borrows create wording for a cancellation", () => {
    // D2's 9-value status union is shared by every operation type, so a cancel *can* carry
    // `alternatives_required`/`waitlisted`. The create copy ("That time isn't available",
    // "Added to the waitlist") would read as the opposite of what the operator asked for.
    for (const status of ["alternatives_required", "waitlisted"]) {
      expect(normalizedStatusLabel(status, "cancel"))
        .not.toBe(normalizedStatusLabel(status, "create"));
    }
    // Nothing a cancel can report should ever read as a confirmation.
    for (const status of ["accepted", "in_progress", "awaiting_user_action", "alternatives_required",
                          "booked", "waitlisted", "rejected", "expired", "failed"]) {
      expect(normalizedStatusLabel(status, "cancel")).not.toMatch(/^Confirmed$/);
    }
  });

  it("renders an unrecognised status as words rather than a raw enum", () => {
    expect(normalizedStatusLabel("some_new_status", "cancel")).toBe("some new status");
  });

  it("renders operator language for every normalized status (D2)", () => {
    const normalized = [
      "accepted", "in_progress", "awaiting_user_action", "alternatives_required",
      "booked", "waitlisted", "rejected", "expired", "failed",
    ];
    for (const status of normalized) {
      const label = normalizedStatusLabel(status);
      expect(label).not.toBe(status);
      expect(label).not.toContain("_");
    }
    // "accepted" must never read as "confirmed" — the distinction is the whole point.
    expect(normalizedStatusLabel("accepted")).not.toMatch(/confirmed/i);
    expect(normalizedStatusLabel("booked")).toBe("Confirmed");
  });
});

describe("write failure handling", () => {
  it("treats a stale version and an idempotency conflict as needing a fresh attempt", () => {
    expect(isRetryableWithNewAttempt(new ApiError(409, "stale"))).toBe(true);
    expect(isRetryableWithNewAttempt(
      new ApiIdempotencyConflictError(409, "bound", { existing_operation_id: "op_1" }))).toBe(true);
  });

  it("does not treat an ordinary failure as a spent attempt", () => {
    expect(isRetryableWithNewAttempt(new ApiError(500, "boom"))).toBe(false);
    expect(isRetryableWithNewAttempt(new TypeError("offline"))).toBe(false);
  });

  it("gives verification-required its own actionable message rather than a raw 403", () => {
    const message = writeFailureMessage(
      new ApiVerificationRequiredError(403, "needs verification", { status: "requires_verification" }),
      "cancel this booking"
    );
    expect(message).toMatch(/verified/i);
    expect(message).not.toMatch(/403/);
  });

  it("tells the operator a conflicted key cannot be replayed", () => {
    const message = writeFailureMessage(
      new ApiIdempotencyConflictError(409, "bound", {}), "cancel this booking");
    expect(message).toMatch(/start a new one/i);
  });

  it("falls back to the error's own message", () => {
    expect(writeFailureMessage(new Error("disk on fire"), "cancel this booking")).toBe("disk on fire");
  });
});

describe("who the confirmation identifies (spec 04 / spec 14)", () => {
  it("leaves the canonical hash untouched whoever confirmed", async () => {
    // `identity` lives on the CONFIRMATION, not on the canonical payload. Changing the console
    // from sending the guest's typed name to sending the operator must therefore not move the
    // hash by a single byte — a moved hash is a 422 (ConfirmationHashMismatchException) and a
    // broken idempotency replay (Inv-2/3).
    const asGuestName = await buildSubmission(draft, { ...context, identityRef: "Sarah Nguyen" });
    const asOperator = await buildSubmission(draft, { ...context, identityRef: "console-operator" });

    expect(asOperator.revision.payload_hash).toBe(asGuestName.revision.payload_hash);
    expect(asOperator.confirmation.intent_payload_hash).toBe(asOperator.revision.payload_hash);
  });

  it("carries no guest NAME in the canonical payload, even when a guest is attached", async () => {
    // The payload now has a `customer` slot, but the name deliberately does not go in it: the
    // revision is immutable, replayed, and outside the retention sweep, so a name written here
    // could never be erased. A phone is the minimum that makes the booking reachable.
    const request = await buildSubmission(
      { ...draft, customer: { phoneE164: "+61400111222", customerId: "cust-7" } },
      { ...context, identityRef: "console-operator" }
    );
    const payload = request.revision.canonical_payload as Record<string, unknown>;

    expect(JSON.stringify(payload)).not.toContain("Sarah");
    expect(JSON.stringify(payload)).not.toContain("display_name");
  });

  it("omits `customer` entirely when no guest was captured", async () => {
    // Backward compatibility: a guest-less booking must hash exactly as it did before the field
    // existed. canon.v1 drops null and {} alike, but omitting keeps the stored payload clean too.
    const request = await buildSubmission(draft, { ...context, identityRef: "console-operator" });
    const payload = request.revision.canonical_payload as Record<string, unknown>;

    expect(Object.keys(payload)).not.toContain("customer");
  });

  it("leaves the hash identical to the pre-guest payload when no guest is captured", async () => {
    // Pins the property the golden corpus asserts (guest_absent_matches_minimal_create): adding
    // an optional field must not move any existing payload's hash, or every stored idempotency
    // record would stop replaying (Inv-2/3).
    const withoutGuest = await buildSubmission(draft, { ...context, identityRef: "op" });
    const withEmptyGuest = await buildSubmission(
      { ...draft, customer: {} },
      { ...context, identityRef: "op" }
    );

    expect(withEmptyGuest.revision.payload_hash).toBe(withoutGuest.revision.payload_hash);
  });

  it("changes the hash once a guest IS attached", async () => {
    // The converse: the guest is genuinely material, so attaching one forces a new revision.
    const withoutGuest = await buildSubmission(draft, { ...context, identityRef: "op" });
    const withGuest = await buildSubmission(
      { ...draft, customer: { phoneE164: "+61400111222" } },
      { ...context, identityRef: "op" }
    );

    expect(withGuest.revision.payload_hash).not.toBe(withoutGuest.revision.payload_hash);
    // Both fields the server cross-checks still come from one computation.
    expect(withGuest.confirmation.intent_payload_hash).toBe(withGuest.revision.payload_hash);
  });

  it("normalises the phone BEFORE hashing, so a formatted number is still findable", async () => {
    // The payload is immutable once written, so this is the only place a canonical form can be
    // established — and the server's PhoneMatching.Normalise must agree with it.
    const request = await buildSubmission(
      { ...draft, customer: { phoneE164: "  +61 400 111 222  " } },
      { ...context, identityRef: "op" }
    );
    const payload = request.revision.canonical_payload as Record<string, unknown>;
    expect(payload.customer).toEqual({ phone_e164: "+61400111222" });
  });

  it("does NOT guess a country code — that needs a tenant country the platform lacks", async () => {
    const request = await buildSubmission(
      { ...draft, customer: { phoneE164: "0400 111 222" } },
      { ...context, identityRef: "op" }
    );
    const payload = request.revision.canonical_payload as Record<string, unknown>;
    // Formatting stripped, country untouched. Guessing +61 would attach this booking to a
    // stranger's number in any country whose national format collides.
    expect(payload.customer).toEqual({ phone_e164: "0400111222" });
  });

  it("omits a phone that carries no digits at all", async () => {
    const request = await buildSubmission(
      { ...draft, customer: { phoneE164: "  ( ) - " } },
      { ...context, identityRef: "op" }
    );
    const payload = request.revision.canonical_payload as Record<string, unknown>;
    expect(Object.keys(payload)).not.toContain("customer");
  });

  it("puts the phone where the server actually looks for it", async () => {
    // CanonicalBookingPayload.PhoneNames is ["phone_e164","phoneE164","phone"] and it descends
    // into nested objects — so the key name here is load-bearing, not cosmetic.
    const request = await buildSubmission(
      { ...draft, customer: { phoneE164: "+61400111222" } },
      { ...context, identityRef: "op" }
    );
    const payload = request.revision.canonical_payload as Record<string, unknown>;

    expect(payload.customer).toEqual({ phone_e164: "+61400111222" });
  });

  it("puts exactly what the caller passed into confirmation.identity", async () => {
    const request = await buildSubmission(draft, { ...context, identityRef: "console-operator" });
    expect(request.confirmation.identity).toBe("console-operator");
  });

  describe("customerFromRef — carrying a guest onto a follow-up submission", () => {
    // A reschedule writes a NEW canonical payload, and NotificationRecipientResolver reads the
    // destination from THAT operation's revision — not from the booking it targets. So a
    // follow-up that drops the guest emits a confirmation with nowhere to send, silently.

    it("routes a gc_ handle to contact_ref so the server dereferences it", () => {
      const payload = toCanonicalPayload({
        ...draft,
        operation: "reschedule",
        customer: customerFromRef("gc_2f8ac1"),
      });

      expect(payload.customer).toEqual({ contact_ref: "gc_2f8ac1" });
    });

    it("routes a legacy inline phone to phone_e164, never to contact_ref", () => {
      // Putting a phone in contact_ref would send the resolver looking up a handle that does not
      // exist, and the notification would settle undeliverable — worse than the omission it was
      // meant to fix.
      const payload = toCanonicalPayload({
        ...draft,
        operation: "reschedule",
        customer: customerFromRef("+61400111222"),
      });

      expect(payload.customer).toEqual({ phone_e164: "+61400111222" });
    });

    it("carries an opaque operator handle as customer_id without claiming it is a destination", () => {
      const payload = toCanonicalPayload({
        ...draft,
        operation: "reschedule",
        customer: customerFromRef("console-operator"),
      });

      expect(payload.customer).toEqual({ customer_id: "console-operator" });
    });

    it("omits the customer node entirely when the booking records no ref", () => {
      for (const ref of [null, undefined, "", "   "]) {
        const payload = toCanonicalPayload({
          ...draft,
          operation: "reschedule",
          customer: customerFromRef(ref),
        });
        expect(Object.keys(payload)).not.toContain("customer");
      }
    });
  });
});
