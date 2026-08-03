import { describe, expect, it } from "vitest";
import { operationStatusLabel, toQueueItems } from "./operations/adapters";
import { toChannel, toMessages, toThreads, timeAgo } from "./conversations/adapters";
import { toCustomers, lastSeenLabel } from "./customers/adapters";
import { toWaitlistItems, wantLabel } from "./waitlist/adapters";
import { parseCatalogItems } from "./settings/api";
import { toBookingStatus, toBooking } from "./bookings/adapters";

describe("operation status copy", () => {
  it("renders plain language for every ratified status (N2)", () => {
    // The 16 values of D1 — an operator must never see a raw enum.
    const ratified = [
      "accepted", "validating", "awaiting_user_action", "reserving_capacity", "held",
      "awaiting_payment", "finalizing", "booked", "alternatives_required", "reconciling",
      "compensation_required", "manual_review", "rejected", "expired", "waitlisted",
      "failed_final",
    ];
    for (const status of ratified) {
      const label = operationStatusLabel(status);
      expect(label).not.toBe(status);
      expect(label).not.toContain("_");
    }
  });

  it("degrades readably for an unknown status rather than showing nothing", () => {
    expect(operationStatusLabel("some_new_state")).toBe("some new state");
    expect(operationStatusLabel(null)).toBe("Unknown");
  });
});

describe("toQueueItems", () => {
  it("prefers the server's durable reason over a generic label", () => {
    const [item] = toQueueItems([
      {
        operation_id: "op_1",
        booking_id: "bk_1",
        intent_id: "int_1",
        intent_revision: 1,
        operation_type: "create",
        status: "manual_review",
        status_reason: "provider returned an ambiguous response",
        is_preorder: false,
        correlation_id: "corr_1",
        created_at: "2026-08-01T00:00:00Z",
        updated_at: "2026-08-01T01:00:00Z",
      },
    ]);
    expect(item.id).toBe("op_1");
    expect(item.reason).toBe("provider returned an ambiguous response");
  });

  it("falls back to the status label when the server gave no reason", () => {
    const [item] = toQueueItems([
      {
        operation_id: "op_2",
        booking_id: null,
        intent_id: "int_2",
        intent_revision: 1,
        operation_type: "cancel",
        status: "compensation_required",
        status_reason: null,
        is_preorder: false,
        correlation_id: null,
        created_at: "2026-08-01T00:00:00Z",
        updated_at: "2026-08-01T01:00:00Z",
      },
    ]);
    expect(item.reason).toBe("Needs attention — refund pending");
  });

  it("carries the raw status so a dismissal can be tied to the state it acknowledged", () => {
    // Keying a dismissal on the id alone hides the row forever, so an operation that escalates
    // after being cleared (manual_review -> compensation_required) never resurfaces.
    const [item] = toQueueItems([
      {
        operation_id: "op_3", booking_id: null, intent_id: "int_3", intent_revision: 1,
        operation_type: "create", status: "manual_review",
        status_reason: "needs a human", is_preorder: false, correlation_id: null,
        created_at: "2026-08-01T00:00:00Z", updated_at: "2026-08-01T01:00:00Z",
      },
    ]);
    expect(item.status).toBe("manual_review");

    const [escalated] = toQueueItems([
      {
        operation_id: "op_3", booking_id: null, intent_id: "int_3", intent_revision: 1,
        operation_type: "create", status: "compensation_required",
        status_reason: "needs a human", is_preorder: false, correlation_id: null,
        created_at: "2026-08-01T00:00:00Z", updated_at: "2026-08-01T02:00:00Z",
      },
    ]);
    // Same id and same reason text — only the status distinguishes them.
    expect(escalated.id).toBe(item.id);
    expect(escalated.reason).toBe(item.reason);
    expect(escalated.status).not.toBe(item.status);
  });

  it("tolerates a null list", () => {
    expect(toQueueItems(null)).toEqual([]);
  });
});

describe("conversation adapters", () => {
  it("maps the API channel vocabulary onto the console's", () => {
    expect(toChannel("voice")).toBe("phone");
    expect(toChannel("messaging")).toBe("sms");
    expect(toChannel("web")).toBe("web");
    expect(toChannel("email")).toBe("web");
    expect(toChannel(null)).toBe("web");
  });

  it("treats a session with no end time as live", () => {
    const [thread] = toThreads([
      { session_id: "s1", channel: "voice", customer_ref: "cust-1", consent_state: null, recording_allowed: null, started_at: "2026-08-01T00:00:00Z", ended_at: null },
    ]);
    expect(thread.live).toBe(true);
    expect(thread.channel).toBe("phone");
    expect(thread.name).toBe("cust-1");
  });

  it("treats an ended session as not live", () => {
    const [thread] = toThreads([
      { session_id: "s2", channel: "web", customer_ref: null, consent_state: null, recording_allowed: null, started_at: "2026-08-01T00:00:00Z", ended_at: "2026-08-01T00:10:00Z" },
    ]);
    expect(thread.live).toBe(false);
    expect(thread.name).toBe("Guest");
  });

  it("orders transcript turns by sequence, not arrival", () => {
    const messages = toMessages({
      session_id: "s1",
      turns: [
        { seq: 2, speaker: "agent", text: "second", ts: "2026-08-01T00:00:01Z", turn_id: "t2" },
        { seq: 1, speaker: "customer", text: "first", ts: "2026-08-01T00:00:00Z", turn_id: "t1" },
      ],
    });
    expect(messages.map((m) => m.text)).toEqual(["first", "second"]);
    expect(messages[0].from).toBe("cust");
    expect(messages[1].from).toBe("agent");
  });

  it("returns no messages for an absent transcript", () => {
    expect(toMessages(null)).toEqual([]);
  });

  it("formats coarse relative times", () => {
    const now = Date.parse("2026-08-01T12:00:00Z");
    expect(timeAgo("2026-08-01T11:59:30Z", now)).toBe("just now");
    expect(timeAgo("2026-08-01T11:30:00Z", now)).toBe("30 min");
    expect(timeAgo("2026-08-01T09:00:00Z", now)).toBe("3 hr");
    expect(timeAgo("2026-07-30T12:00:00Z", now)).toBe("2 d");
    expect(timeAgo(null, now)).toBe("");
  });
});

describe("customer adapters", () => {
  it("maps only the fields the API actually provides", () => {
    const now = Date.parse("2026-08-10T00:00:00Z");
    const [customer] = toCustomers(
      [
        {
          customer_ref: "cust-1",
          booking_count: 3,
          first_seen_at: "2026-06-01T00:00:00Z",
          last_seen_at: "2026-08-09T00:00:00Z",
          last_service_id: "gel",
          last_booking_id: "bk_9",
        },
      ],
      now
    );

    expect(customer.name).toBe("cust-1");
    expect(customer.visits).toBe(3);
    expect(customer.fav).toBe("Gel manicure");
    expect(customer.lastVisit).toBe("Yesterday");
    // Fields the API does not model are left empty, never fabricated.
    expect(customer.phone).toBe("");
    expect(customer.tags).toEqual([]);
    expect(customer.note).toBe("");
  });

  it("formats last-seen labels across the ranges", () => {
    const now = Date.parse("2026-08-10T00:00:00Z");
    expect(lastSeenLabel("2026-08-10T00:00:00Z", now)).toBe("Today");
    expect(lastSeenLabel("2026-08-09T00:00:00Z", now)).toBe("Yesterday");
    expect(lastSeenLabel("2026-08-07T00:00:00Z", now)).toBe("3 days ago");
    expect(lastSeenLabel(null, now)).toBe("");
  });
});

describe("waitlist adapters", () => {
  it("reflects the per-guest auto-book grant rather than assuming it", () => {
    const [granted] = toWaitlistItems([
      { enrollment_id: "w1", customer_ref: "cust-1", service_id: "gel", location_id: "loc", party_size: 2, acceptable_from: "2026-08-01T09:00:00Z", acceptable_to: "2026-08-01T11:00:00Z", auto_book_authorized: true, cancelled: false, created_at: "2026-07-01T00:00:00Z", latest_offer_id: null, latest_offer_status: null, latest_offer_expires_at: null },
    ]);
    expect(granted.auto).toBe(true);
    expect(granted.service).toBe("Gel manicure");

    const [ungranted] = toWaitlistItems([
      { enrollment_id: "w2", customer_ref: "cust-2", service_id: "gel", location_id: "loc", party_size: 2, acceptable_from: "", acceptable_to: "", auto_book_authorized: false, cancelled: false, created_at: "2026-07-01T00:00:00Z", latest_offer_id: null, latest_offer_status: null, latest_offer_expires_at: null },
    ]);
    expect(ungranted.auto).toBe(false);
    expect(ungranted.want).toBe("Any time");
  });

  it("degrades to 'Any time' for an unparseable window", () => {
    expect(wantLabel("nonsense", null)).toBe("Any time");
  });
});

describe("parseCatalogItems", () => {
  it("accepts a bare array", () => {
    expect(parseCatalogItems([{ item_id: "i1", name: "Latte" }])).toHaveLength(1);
  });

  it("accepts an envelope with an items array", () => {
    expect(parseCatalogItems({ items: [{ item_id: "i1" }] })).toHaveLength(1);
  });

  it("returns an empty list for a shape it cannot vouch for, rather than crashing", () => {
    expect(parseCatalogItems(null)).toEqual([]);
    expect(parseCatalogItems("nope")).toEqual([]);
    expect(parseCatalogItems({ unexpected: true })).toEqual([]);
  });
});

describe("in-flight bookings on the schedule", () => {
  const weekStart = new Date(2026, 7, 3); // Monday
  const row = (over: Record<string, unknown>) => ({
    operation_id: "op_1",
    starts_at: new Date(2026, 7, 5, 9, 30).toISOString(),
    service_id: "gel",
    party_size: 4,
    duration_minutes: 45,
    ...over,
  }) as never;

  it("keeps an operation that has no booking id yet — it still occupies the slot", () => {
    // Dropping these told the operator the time was free while a guest already held it.
    const booking = toBooking(row({ booking_id: null, operation_status: "held", lifecycle_status: null }), { weekStart });
    expect(booking).not.toBeNull();
    expect(booking!.id).toBe("op_1");
  });

  it("marks it as not-yet-a-booking so nothing offers to cancel it", () => {
    // `id` is an OPERATION id here; cancel/modify address a booking and would 404.
    const inFlight = toBooking(row({ booking_id: null, operation_status: "held" }), { weekStart });
    expect(inFlight!.isBooking).toBe(false);

    const real = toBooking(row({ booking_id: "bk_1", operation_status: "booked", lifecycle_status: "confirmed" }), { weekStart });
    expect(real!.isBooking).toBe(true);
    expect(real!.id).toBe("bk_1");
  });

  it("is the single flag every action path gates on", () => {
    // Three call sites read it: the drawer's Cancel/Reschedule buttons, TodayScreen's drag
    // handler, and ConsolePage.onRescheduleDone. Each addresses a BOOKING; an operation id
    // would 404, so they must all agree on what "not a booking yet" means.
    const inFlight = toBooking(row({ booking_id: null, operation_status: "held" }), { weekStart });
    expect(inFlight!.isBooking).toBe(false);
    // Strictly false, not merely falsy: the guards test `=== false` so demo rows (undefined)
    // keep working.
    expect(inFlight!.isBooking).not.toBeUndefined();
  });

  it("never reports an unfinalised operation as confirmed", () => {
    // The server now sends lifecycle_status: null for these; claiming "confirmed" would be
    // exactly the fabricated outcome Inv-16 forbids.
    for (const status of ["accepted", "validating", "reserving_capacity", "held", "awaiting_payment", "finalizing"]) {
      const booking = toBooking(row({ booking_id: null, operation_status: status, lifecycle_status: null }), { weekStart });
      expect(booking!.status).not.toBe("confirmed");
    }
  });
});

describe("booking status precedence", () => {
  it("shows a hard-failed booking as failed, not cancelled", () => {
    // The server maps rejected/expired/failed_final onto lifecycle "cancelled" as well, so
    // testing lifecycle first would render a failure as a calm "Cancelled" — the operator
    // reads that as "the guest cancelled" and nobody calls the guest about a booking that
    // was never made.
    expect(toBookingStatus("cancelled", "failed_final")).toBe("failed");
    expect(toBookingStatus("cancelled", "rejected")).toBe("failed");
    expect(toBookingStatus("cancelled", "expired")).toBe("failed");
  });

  it("still shows a genuine cancellation as cancelled", () => {
    // A successful cancel terminates the OPERATION at `booked` (D1 has no cancelled
    // operation status) with the booking carrying the cancellation.
    expect(toBookingStatus("cancelled", "booked")).toBe("cancelled");
  });

  it("never claims confirmed before the server says booked", () => {
    for (const status of ["accepted", "validating", "held", "finalizing", "reconciling"]) {
      expect(toBookingStatus("confirmed", status)).not.toBe("confirmed");
    }
    expect(toBookingStatus("confirmed", "booked")).toBe("confirmed");
  });
});
