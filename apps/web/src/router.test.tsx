import { describe, expect, it } from "vitest";
import { router } from "./router";

describe("router", () => {
  it("has a root route", () => {
    expect(router.routes).toHaveLength(1);
    expect(router.routes[0].path).toBe("/");
  });

  it("has an index route and a wildcard fallback", () => {
    const children = router.routes[0].children ?? [];
    expect(children.some((r) => r.index)).toBe(true);
    expect(children.some((r) => r.path === "*")).toBe(true);
  });
});
