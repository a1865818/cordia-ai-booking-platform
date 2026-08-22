/**
 * The schedule's visible time window.
 *
 * This exists because the window used to come from `dayBounds()` — the hand-written
 * `SETTINGS.hours` constant that SettingsScreen itself badges "no operating-hours endpoint yet".
 * The grid framed 9am-6pm, so a real 20:30 booking was positioned outside the rendered columns
 * and could not be seen. A schedule that silently omits a booking is how this console produced a
 * double-booking once already, so the property below is the one that matters:
 *
 *   **every booking on the day is inside [start, end].**
 */

import { describe, expect, it } from "../../../test/vitest-compat";
import { dayWindowForTest as dayWindow } from "./TodayScreen";
import type { Booking } from "../../../data/console";

function booking(overrides: Partial<Booking> = {}): Booking {
  return {
    id: "bk_1", isBooking: true, day: 0, staff: "s1",
    start: 600, dur: 60, service: "itm_gel", serviceName: "Gel manicure",
    name: "cust_1", prov: "unknown", status: "confirmed",
    channel: null, dep: 0, transcript: null,
    ...overrides,
  } as Booking;
}

/** The invariant, asserted directly. */
function contains(win: { start: number; end: number }, b: Booking) {
  return win.start <= b.start && win.end >= b.start + b.dur;
}

describe("dayWindow", () => {
  it("falls back to a sane default when the day is empty", () => {
    expect(dayWindow([], 0)).toEqual({ start: 8 * 60, end: 20 * 60 });
  });

  it("contains a booking that starts before the default window", () => {
    // 06:15, i.e. before the old hard-coded 9am open.
    const early = booking({ start: 375, dur: 45 });
    const win = dayWindow([early], 0);
    expect(contains(win, early)).toBe(true);
  });

  it("contains a booking that ends after the default window", () => {
    // 20:30 — the exact case the mock 9am-6pm frame made invisible.
    const late = booking({ start: 20 * 60 + 30, dur: 45 });
    const win = dayWindow([late], 0);
    expect(contains(win, late)).toBe(true);
  });

  it("contains a booking that runs past midnight", () => {
    // 23:20 + 90min ends at 24:50 (1490). The window was clamped at 1440, so the bar was drawn
    // past the last column, gridline and row stripe — into blank background with no time axis.
    const overnight = booking({ start: 23 * 60 + 20, dur: 90 });
    const win = dayWindow([overnight], 0);
    expect(win.end).toBeGreaterThanOrEqual(overnight.start + overnight.dur);
    expect(contains(win, overnight)).toBe(true);
  });

  it("contains a booking at the very start of the day", () => {
    const midnight = booking({ start: 0, dur: 30 });
    const win = dayWindow([midnight], 0);
    expect(win.start).toBe(0);
    expect(contains(win, midnight)).toBe(true);
  });

  it("contains every booking when the day spans both extremes", () => {
    const rows = [
      booking({ id: "a", start: 30, dur: 30 }),
      booking({ id: "b", start: 720, dur: 60 }),
      booking({ id: "c", start: 22 * 60, dur: 120 }),
    ];
    const win = dayWindow(rows, 0);
    for (const b of rows) expect(contains(win, b)).toBe(true);
  });

  it("ignores bookings on other days and cancelled ones", () => {
    const rows = [
      booking({ id: "other", day: 3, start: 5, dur: 15 }),
      booking({ id: "cancelled", start: 5, dur: 15, status: "cancelled" }),
    ];
    // Neither should widen day 0's window away from the default.
    expect(dayWindow(rows, 0)).toEqual({ start: 8 * 60, end: 20 * 60 });
  });

  it("bounds a corrupt duration instead of producing an unusable grid", () => {
    // `dur` comes straight from the API with no upper bound. The window must stay finite.
    const corrupt = booking({ start: 600, dur: 100_000 });
    const win = dayWindow([corrupt], 0);
    expect(win.end).toBeLessThanOrEqual(36 * 60);
    expect(win.end).toBeGreaterThan(win.start);
  });

  it("always yields a positive window that divides into whole 15-minute columns", () => {
    // `cols = (end - start) / 15` sizes the grid; a fractional or non-positive value breaks it.
    for (const rows of [[], [booking({ start: 0, dur: 7 })], [booking({ start: 1400, dur: 91 })]]) {
      const win = dayWindow(rows, 0);
      expect(win.end).toBeGreaterThan(win.start);
      expect((win.end - win.start) % 15).toBe(0);
    }
  });
});
