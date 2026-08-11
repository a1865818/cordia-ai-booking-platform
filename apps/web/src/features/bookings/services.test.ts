import { describe, expect, it } from "vitest";
import { toBookableServices } from "./services";
import type { CatalogItem } from "../settings/api";

function item(overrides: Partial<CatalogItem> = {}): CatalogItem {
  return { item_id: "itm_gel", name: "Gel manicure", ...overrides } as CatalogItem;
}

describe("toBookableServices", () => {
  it("carries the catalog's own item_id through as the bookable id", () => {
    // This id is submitted verbatim as `revision.canonical_payload.service_id` and hashed by
    // canon.v1. The drawer previously submitted a key from the hand-written `SERVICES` map, so
    // every console booking named a service that existed on no server.
    const [service] = toBookableServices([item({ item_id: "itm_7c1", name: "Pedicure" })]);
    expect(service.id).toBe("itm_7c1");
    expect(service.name).toBe("Pedicure");
  });

  it("reports no duration when the catalog states none", () => {
    // `CatalogItem` has no duration field. Defaulting to 60 would durably record how long the
    // venue is booked for on the strength of a guess.
    expect(toBookableServices([item()])[0].durationMinutes).toBeNull();
  });

  it("uses a duration only when it is a usable positive number", () => {
    const rows = toBookableServices([
      item({ item_id: "a", duration_minutes: 45 } as Partial<CatalogItem>),
      item({ item_id: "b", duration_minutes: 0 } as Partial<CatalogItem>),
      item({ item_id: "c", duration_minutes: -30 } as Partial<CatalogItem>),
      item({ item_id: "d", duration_minutes: "60" } as unknown as Partial<CatalogItem>),
      item({ item_id: "e", duration_minutes: Number.NaN } as Partial<CatalogItem>),
    ]);
    expect(rows.map(r => r.durationMinutes)).toEqual([45, null, null, null, null]);
  });

  it("rounds a fractional duration rather than emitting one", () => {
    const [row] = toBookableServices([item({ duration_minutes: 44.6 } as Partial<CatalogItem>)]);
    expect(row.durationMinutes).toBe(45);
  });

  it("drops items with no id — an unbookable option is worse than a shorter list", () => {
    const rows = toBookableServices([
      item({ item_id: undefined }),
      item({ item_id: "" }),
      item({ item_id: "itm_ok" }),
    ]);
    expect(rows.map(r => r.id)).toEqual(["itm_ok"]);
  });

  it("falls back to the id, never to a placeholder name", () => {
    // The operator sees the handle the platform actually knows, not "Untitled service".
    const rows = toBookableServices([
      item({ item_id: "itm_x", name: undefined }),
      item({ item_id: "itm_y", name: "" }),
    ]);
    expect(rows.map(r => r.name)).toEqual(["itm_x", "itm_y"]);
  });

  it("treats null/undefined as an empty list", () => {
    expect(toBookableServices(null)).toEqual([]);
    expect(toBookableServices(undefined)).toEqual([]);
  });
});
