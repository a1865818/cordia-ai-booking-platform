import { describe, expect, it } from "../../test/vitest-compat";
import { UNASSIGNED_RESOURCE_ID } from "./resources";
import type { ScheduledBooking } from "./api";
import { startOfWeek, toBooking, toBookingStatus, toBookings, toDayAndStart, weekWindow } from "./adapters";

const GEL = { gel: { name: "Gel manicure", dur: 45 } };

function row(overrides: Partial<ScheduledBooking> = {}): ScheduledBooking {
  return {
    booking_id: "bk_1",
    operation_id: "op_1",
    booking_reference: "CORD-1",
    starts_at: "2026-08-05T09:30:00.000Z",
    party_size: 4,
    duration_minutes: 45,
    service_id: "gel",
    customer_ref: "cust-1",
    lifecycle_status: "confirmed",
    operation_status: "booked",
    updated_at: "2026-08-01T00:00:00.000Z",
    ...overrides,
  } as ScheduledBooking;
}

describe("startOfWeek", () => {
  it("returns the Monday of the containing week at local midnight", () => {
    // 2026-08-05 is a Wednesday.
    const monday = startOfWeek(new Date(2026, 7, 5, 14, 30));
    expect(monday.getFullYear()).toBe(2026);
    expect(monday.getMonth()).toBe(7);
    expect(monday.getDate()).toBe(3);
    expect(monday.getHours()).toBe(0);
    expect(monday.getMinutes()).toBe(0);
  });

  it("treats Sunday as the last day of the week, not the first", () => {
    // 2026-08-09 is a Sunday; its week starts Monday 2026-08-03.
    const monday = startOfWeek(new Date(2026, 7, 9, 12, 0));
    expect(monday.getDate()).toBe(3);
  });

  it("handles Monday itself without shifting a week back", () => {
    const monday = startOfWeek(new Date(2026, 7, 3, 8, 0));
    expect(monday.getDate()).toBe(3);
  });
});

describe("weekWindow", () => {
  it("spans exactly seven days from the week's Monday", () => {
    const { from, to } = weekWindow(new Date(2026, 7, 5));
    const span = new Date(to).getTime() - new Date(from).getTime();
    expect(span).toBe(7 * 24 * 60 * 60 * 1000);
  });
});

describe("toDayAndStart", () => {
  it("maps a local instant to a day index and minutes since midnight", () => {
    const weekStart = startOfWeek(new Date(2026, 7, 5));
    const { day, start } = toDayAndStart(new Date(2026, 7, 5, 9, 30), weekStart);
    expect(day).toBe(2); // Monday = 0, so Wednesday = 2
    expect(start).toBe(9 * 60 + 30);
  });
});

describe("toBookingStatus", () => {
  it("only reports confirmed for a booked operation", () => {
    expect(toBookingStatus("confirmed", "booked")).toBe("confirmed");
    expect(toBookingStatus("confirmed", "accepted")).toBe("confirming");
    expect(toBookingStatus("confirmed", "in_progress")).toBe("confirming");
  });

  it("maps terminal failures and cancellations", () => {
    expect(toBookingStatus("cancelled", "booked")).toBe("cancelled");
    expect(toBookingStatus("confirmed", "rejected")).toBe("failed");
    expect(toBookingStatus("confirmed", "expired")).toBe("failed");
    expect(toBookingStatus("confirmed", "failed_final")).toBe("failed");
  });

  it("maps recovery and attention states without claiming success", () => {
    expect(toBookingStatus("confirmed", "reconciling")).toBe("retrying");
    expect(toBookingStatus("confirmed", "compensation_required")).toBe("retrying");
    expect(toBookingStatus("confirmed", "manual_review")).toBe("pending");
    expect(toBookingStatus("confirmed", "alternatives_required")).toBe("pending");
    expect(toBookingStatus("confirmed", "waitlisted")).toBe("waitlisted");
  });
});

describe("toDayAndStart across a DST transition", () => {
  it("counts calendar days, so a 23- or 25-hour day cannot shift later columns", () => {
    // Pick a week containing a DST change in whichever zone the test host uses by walking
    // a whole year; if the host is UTC there is none and the assertion is trivially true.
    const weekStart = startOfWeek(new Date(2026, 2, 2)); // early March
    for (let offset = 0; offset < 370; offset += 1) {
      const probe = new Date(weekStart);
      probe.setDate(probe.getDate() + offset);
      const probeWeek = startOfWeek(probe);
      for (let day = 0; day < 7; day += 1) {
        const target = new Date(probeWeek);
        target.setDate(target.getDate() + day);
        target.setHours(12, 0, 0, 0);
        expect(toDayAndStart(target, probeWeek).day).toBe(day);
      }
    }
  });

  it("returns a negative index for a date before the displayed week", () => {
    const weekStart = startOfWeek(new Date(2026, 7, 5));
    const before = new Date(weekStart);
    before.setDate(before.getDate() - 3);
    expect(toDayAndStart(before, weekStart).day).toBe(-3);
  });
});

describe("toBooking", () => {
  const weekStart = startOfWeek(new Date(2026, 7, 5));

  it("maps the API row onto the grid shape", () => {
    const booking = toBooking(row(), { weekStart, serviceNames: GEL });
    expect(booking).not.toBeNull();
    expect(booking!.id).toBe("bk_1");
    expect(booking!.service).toBe("gel");
    expect(booking!.serviceName).toBe("Gel manicure");
    expect(booking!.dur).toBe(45);
    expect(booking!.name).toBe("cust-1");
    expect(booking!.status).toBe("confirmed");
    expect(booking!.staff).toBe(UNASSIGNED_RESOURCE_ID);
    expect(booking!.channel).toBeNull();
    expect(booking!.transcript).toBeNull();
  });

  it("falls back to the service catalogue duration when the API omits one", () => {
    const booking = toBooking(row({ duration_minutes: null }), { weekStart, serviceNames: GEL });
    expect(booking!.dur).toBe(45);
  });

  it("shows the raw service id rather than inventing a name for an unknown service", () => {
    const booking = toBooking(row({ service_id: "mystery" }), { weekStart });
    expect(booking!.serviceName).toBe("mystery");
    expect(booking!.dep).toBe(0);
  });

  it("falls back to the booking reference when there is no customer ref", () => {
    const booking = toBooking(row({ customer_ref: null }), { weekStart });
    expect(booking!.name).toBe("CORD-1");
  });

  it("drops a row that cannot be placed on the grid", () => {
    expect(toBooking(row({ starts_at: null }), { weekStart })).toBeNull();
    expect(toBooking(row({ starts_at: "not-a-date" }), { weekStart })).toBeNull();
  });

  it("does not claim a provenance the API never records", () => {
    const booking = toBooking(row(), { weekStart });
    expect(booking!.prov).toBe("unknown");
  });

  it("maps attributed_source onto console provenance", () => {
    expect(toBooking(row({ attributed_source: "agent" }), { weekStart })!.prov).toBe("ai");
    expect(toBooking(row({ attributed_source: "operator" }), { weekStart })!.prov).toBe("you");
    expect(toBooking(row({ attributed_source: "something-else" }), { weekStart })!.prov).toBe("unknown");
  });

  it("places the booking on the named resource lane", () => {
    const booking = toBooking(row({ resource_id: "staff_alex" }), { weekStart });
    expect(booking!.staff).toBe("staff_alex");
  });

  it("maps a present channel and leaves it null when the row has none", () => {
    expect(toBooking(row({ channel: "voice" }), { weekStart })!.channel).toBe("phone");
    expect(toBooking(row({ channel: "messaging" }), { weekStart })!.channel).toBe("sms");
    expect(toBooking(row({ channel: null }), { weekStart })!.channel).toBeNull();
  });

  it("converts a deposit hold from AUD minor units and treats a missing hold as 0", () => {
    expect(toBooking(row({ deposit_amount_minor: 2500 }), { weekStart })!.dep).toBe(25);
    expect(toBooking(row({ deposit_amount_minor: null }), { weekStart })!.dep).toBe(0);
  });

  it("carries the source session as the transcript handle", () => {
    expect(toBooking(row({ session_id: "sess_9" }), { weekStart })!.transcript).toBe("sess_9");
  });

  it("drops a booking outside the displayed week rather than filing it under a wrong day", () => {
    // The grid only renders days 0-6; an unclamped index would silently land elsewhere.
    expect(toBooking(row({ starts_at: "2026-09-05T09:30:00.000Z" }), { weekStart })).toBeNull();
    expect(toBooking(row({ starts_at: "2026-07-01T09:30:00.000Z" }), { weekStart })).toBeNull();
  });
});

describe("toBookings", () => {
  it("filters out unplaceable rows instead of throwing", () => {
    const weekStart = startOfWeek(new Date(2026, 7, 5));
    const bookings = toBookings([row(), row({ starts_at: null })], { weekStart });
    expect(bookings).toHaveLength(1);
  });

  it("tolerates a null or missing list", () => {
    expect(toBookings(null)).toEqual([]);
    expect(toBookings(undefined)).toEqual([]);
  });
});
