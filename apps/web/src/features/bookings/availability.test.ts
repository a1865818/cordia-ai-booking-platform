/**
 * Availability read + slot declaration (docs §12 gap 10, spec 07).
 *
 * The rules pinned here are all about not overstating what this endpoint knows. It is an
 * `informational` read: no hold, no guarantee, and an empty answer that means "nothing has
 * been declared" rather than "nothing is free".
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "../../test/vitest-compat";
import { declareSlot, getAvailability, toAvailableSlots } from "./availability";

describe("toAvailableSlots", () => {
  const slot = (over: Record<string, unknown> = {}) => ({
    slot_id: "slot_1",
    resource_id: "chair-1",
    starts_at: "2026-08-10T09:00:00Z",
    total_capacity: 4,
    free_capacity: 2,
    ...over,
  });

  it("carries the server's own arithmetic through untouched", () => {
    // `free_capacity` is `total − held − booked` computed by the same expression spec 07's
    // atomic conditional write uses. Recomputing it here would risk showing a number the
    // reservation will not actually test.
    const [s] = toAvailableSlots({ slots: [slot()] });
    expect(s.freeCapacity).toBe(2);
    expect(s.totalCapacity).toBe(4);
  });

  it("drops rows it cannot place in time rather than defaulting them", () => {
    // A slot rendered at an invented time is worse than one not rendered: the operator plans
    // around it. Same rule `toBookableServices` applies to catalog items.
    const slots = toAvailableSlots({
      slots: [slot(), slot({ slot_id: "", }), slot({ starts_at: "not-a-date" }), slot({ starts_at: null })],
    });
    expect(slots).toHaveLength(1);
    expect(slots[0].slotId).toBe("slot_1");
  });

  it("returns an empty list for an empty answer, never a fabricated slot", () => {
    expect(toAvailableSlots({ slots: [] })).toEqual([]);
    expect(toAvailableSlots(null)).toEqual([]);
    expect(toAvailableSlots(undefined)).toEqual([]);
  });

  it("orders by start time so the earliest option reads first", () => {
    const slots = toAvailableSlots({
      slots: [
        slot({ slot_id: "b", starts_at: "2026-08-10T14:00:00Z" }),
        slot({ slot_id: "a", starts_at: "2026-08-10T09:00:00Z" }),
      ],
    });
    expect(slots.map(s => s.slotId)).toEqual(["a", "b"]);
  });

  it("keeps a missing resource as null instead of inventing a lane name", () => {
    // §12 gap 9: there is no resource/staff model. A slot may name one; a booking still cannot.
    const [s] = toAvailableSlots({ slots: [slot({ resource_id: null })] });
    expect(s.resourceId).toBeNull();
  });
});

describe("availability transport", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ slots: [] }), { status: 200 })
    ));
  });
  afterEach(() => vi.unstubAllGlobals());

  it("reads a window without asserting an actor — it is not an operator action", async () => {
    await getAvailability("2026-08-10T00:00:00.000Z", "2026-08-17T00:00:00.000Z");

    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/availability?");
    expect(url).toContain("from=2026-08-10T00%3A00%3A00.000Z");
    expect(new Headers(init?.headers).get("X-Tenant-Id")).toBeTruthy();
  });

  it("declares a slot on the audited operator spine, with the reason the operator gave", async () => {
    // Owner-gated and audited server-side: capacity is a commercial statement. The reason is
    // never defaulted client-side — an audit row that always says the same thing records nothing.
    await declareSlot(
      { serviceId: "cat_1:gel", startsAt: "2026-08-10T09:00:00.000Z", totalCapacity: 3 },
      "opening extra chair"
    );

    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/availability/slots");
    expect(init.method).toBe("POST");

    const headers = new Headers(init.headers);
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBe("opening extra chair");

    expect(JSON.parse(init.body as string)).toEqual({
      resource_id: null,
      service_id: "cat_1:gel",
      location_id: null,
      starts_at: "2026-08-10T09:00:00.000Z",
      total_capacity: 3,
    });
  });
});
