import { afterEach, describe, expect, it } from "vitest";
import { queryKeys } from "./queryKeys";

describe("queryKeys", () => {
  afterEach(() => {
    window.APP_CONFIG = undefined;
  });

  it("roots every key at the tenant id so a tenant switch cannot reuse cached rows", () => {
    window.APP_CONFIG = { TENANT_ID: "tenant-a" };
    const a = queryKeys.bookings.schedule("2026-08-01", "2026-08-08");

    window.APP_CONFIG = { TENANT_ID: "tenant-b" };
    const b = queryKeys.bookings.schedule("2026-08-01", "2026-08-08");

    expect(a).not.toEqual(b);
    expect(a[1]).toBe("tenant-a");
    expect(b[1]).toBe("tenant-b");
  });

  it("is stable for the same inputs", () => {
    expect(queryKeys.operations.status("op_1")).toEqual(queryKeys.operations.status("op_1"));
  });

  it("normalises filter order so equivalent filters share one cache entry", () => {
    const a = queryKeys.operations.list({ status: "failed", limit: 50 });
    const b = queryKeys.operations.list({ limit: 50, status: "failed" });
    expect(a).toEqual(b);
  });

  it("ignores undefined and empty filter values rather than widening the key", () => {
    const withUndefined = queryKeys.operations.list({ status: "failed", needsAttention: undefined });
    const without = queryKeys.operations.list({ status: "failed" });
    expect(withUndefined).toEqual(without);
  });

  it("distinguishes different filters", () => {
    expect(queryKeys.operations.list({ status: "failed" })).not.toEqual(
      queryKeys.operations.list({ status: "booked" })
    );
  });

  it("nests under a domain prefix so a domain can be invalidated wholesale", () => {
    const all = queryKeys.bookings.all();
    const detail = queryKeys.bookings.detail("bk_1");
    expect(detail.slice(0, all.length)).toEqual([...all]);
  });
});
