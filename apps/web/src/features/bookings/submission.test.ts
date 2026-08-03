import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { canonicalHash } from "../../api/canonicalHash";
import { buildSubmission, toCanonicalPayload, toIsoInstant } from "./buildSubmission";
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
