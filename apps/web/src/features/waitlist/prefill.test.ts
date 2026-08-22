import { describe, expect, it } from "../../test/vitest-compat";
import { prefillFromWaitlist } from "./prefill";
import { buildWeek } from "../bookings/week";
import type { WaitlistItem } from "../../data/console";

function row(overrides: Partial<WaitlistItem> = {}): WaitlistItem {
  return {
    id: "enr_1",
    name: "cust_9f2a",
    service: "Gel manicure",
    want: "Thu 6 Aug from 10:00",
    auto: false,
    isLive: true,
    serviceId: "gel",
    partySize: 3,
    acceptableFrom: new Date(2026, 7, 6, 10, 0).toISOString(),
    ...overrides,
  };
}

const week = buildWeek(new Date(2026, 7, 3).toISOString(), new Date(2026, 7, 3, 9, 0));

describe("prefillFromWaitlist", () => {
  it("carries what the enrollment already records", () => {
    const prefill = prefillFromWaitlist(row(), week);

    expect(prefill).toMatchObject({
      name: "cust_9f2a",
      serviceId: "gel",
      partySize: 3,
      day: 3, // Thursday 6 August, in the week starting Monday 3 August
      start: 600, // 10:00
    });
  });

  it("omits the day when the guest's window falls outside the displayed week", () => {
    // Carrying a day index from another week would point at the same weekday of the week on
    // screen — a different date entirely. The caller moves the grid first; this is the guard
    // for when it could not.
    const prefill = prefillFromWaitlist(
      row({ acceptableFrom: new Date(2026, 7, 20, 14, 30).toISOString() }),
      week
    );

    expect(prefill.day).toBeUndefined();
    expect(prefill.start).toBe(870); // the time is still useful
  });

  it("survives a missing or unparseable window", () => {
    expect(prefillFromWaitlist(row({ acceptableFrom: undefined }), week)).toEqual({
      name: "cust_9f2a",
      serviceId: "gel",
      partySize: 3,
    });

    const bad = prefillFromWaitlist(row({ acceptableFrom: "not-a-date" }), week);
    expect(bad.day).toBeUndefined();
    expect(bad.start).toBeUndefined();
  });

  it("carries nothing the row does not have", () => {
    const prefill = prefillFromWaitlist(
      row({ serviceId: undefined, partySize: undefined, acceptableFrom: undefined }),
      week
    );

    expect(prefill.serviceId).toBeUndefined();
    expect(prefill.partySize).toBeUndefined();
  });
});
