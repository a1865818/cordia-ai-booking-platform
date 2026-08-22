import { describe, expect, it } from "../test/vitest-compat";
import { ApiError } from "./client";
import { MAX_RETRIES, createQueryClient, shouldRetry } from "./queryClient";

describe("shouldRetry", () => {
  it("retries transport failures that never produced a response", () => {
    expect(shouldRetry(0, new TypeError("Failed to fetch"))).toBe(true);
  });

  it("retries 5xx", () => {
    expect(shouldRetry(0, new ApiError(500, "boom"))).toBe(true);
    expect(shouldRetry(0, new ApiError(503, "unavailable"))).toBe(true);
  });

  it.each([400, 401, 403, 404, 409, 422])("does not retry %i", (status) => {
    expect(shouldRetry(0, new ApiError(status, "no"))).toBe(false);
  });

  it("retries the two 4xx that are genuinely transient", () => {
    expect(shouldRetry(0, new ApiError(408, "timeout"))).toBe(true);
    expect(shouldRetry(0, new ApiError(429, "slow down"))).toBe(true);
  });

  it("stops once the attempt budget is spent", () => {
    expect(shouldRetry(MAX_RETRIES - 1, new ApiError(500, "boom"))).toBe(true);
    expect(shouldRetry(MAX_RETRIES, new ApiError(500, "boom"))).toBe(false);
  });
});

describe("createQueryClient", () => {
  it("never auto-retries mutations, because a write would be re-sent", () => {
    const defaults = createQueryClient().getDefaultOptions();
    expect(defaults.mutations?.retry).toBe(false);
  });

  it("uses the shared retry policy for queries", () => {
    const defaults = createQueryClient().getDefaultOptions();
    expect(defaults.queries?.retry).toBe(shouldRetry);
  });
});
