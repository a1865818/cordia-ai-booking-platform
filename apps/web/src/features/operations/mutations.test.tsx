/**
 * Operator follow-up mutations — refetch-on-both-outcomes and failure wording.
 *
 * A follow-up can legitimately 409 (the platform moved the operation between render and
 * click); the board must refetch on failure exactly as on success, and the 409 must read
 * as the race it is, not as a bug.
 */

import { describe, expect, it, vi, beforeEach } from "../../test/vitest-compat";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, act, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";

const __actual_api = { ...(await import("./api")) };
vi.mock("./api", () => ({
  ...__actual_api,
  requestOperationCancellation: vi.fn(),
  resumeOperation: vi.fn(),
  resolveOperation: vi.fn(),
  respondToAlternatives: vi.fn(),
}));

import {
  requestOperationCancellation, resolveOperation, respondToAlternatives, resumeOperation,
} from "./api";
import {
  operationActionFailureMessage, useRequestOperationCancellation, useResolveOperation,
  useRespondToAlternatives, useResumeOperation,
} from "./mutations";
import { ApiError } from "../../api/problem";

function makeWrapper() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const invalidate = vi.spyOn(client, "invalidateQueries");
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return { wrapper, invalidate };
}

function invalidatedDomains(invalidate: { mock: { calls: unknown[][] } }): unknown[] {
  return invalidate.mock.calls.map((call) => {
    const filters = call[0] as { queryKey?: readonly unknown[] } | undefined;
    // Keys are ["cordia", tenant, domain, ...] — index 2 is the domain being refetched.
    return filters?.queryKey?.[2];
  });
}

beforeEach(() => vi.clearAllMocks());

describe("follow-up mutations", () => {
  it("refetches the board after a resume, and passes the operator's reason through", async () => {
    vi.mocked(resumeOperation).mockResolvedValueOnce({ operation_id: "op_1" });
    const { wrapper, invalidate } = makeWrapper();

    const { result } = renderHook(() => useResumeOperation(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ operationId: "op_1", reasonCode: "provider called back" });
    });

    expect(resumeOperation).toHaveBeenCalledWith("op_1", "provider called back");
    expect(invalidatedDomains(invalidate)).toContain("operations");
  });

  it("refetches the board after a FAILED follow-up — the 409 means it moved", async () => {
    vi.mocked(requestOperationCancellation).mockRejectedValueOnce(new ApiError(409, "conflict"));
    const { wrapper, invalidate } = makeWrapper();

    const { result } = renderHook(() => useRequestOperationCancellation(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ operationId: "op_1" }).catch(() => {});
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(invalidatedDomains(invalidate)).toContain("operations");
    // One call, no silent replays.
    expect(requestOperationCancellation).toHaveBeenCalledTimes(1);
  });

  it("a booked resolution also refreshes the schedule — it recorded a booking id", async () => {
    vi.mocked(resolveOperation).mockResolvedValueOnce({ status: "booked" });
    const { wrapper, invalidate } = makeWrapper();

    const { result } = renderHook(() => useResolveOperation(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({
        operationId: "op_1", resolution: "booked", providerRef: "prov_9", reasonCode: "verified",
      });
    });

    expect(resolveOperation).toHaveBeenCalledWith("op_1", { resolution: "booked", providerRef: "prov_9" }, "verified");
    const domains = invalidatedDomains(invalidate);
    expect(domains).toContain("operations");
    expect(domains).toContain("bookings");
  });

  it("a waitlist answer also refreshes the waitlist — it enrolled the guest", async () => {
    vi.mocked(respondToAlternatives).mockResolvedValueOnce({ status: "waitlisted" });
    const { wrapper, invalidate } = makeWrapper();

    const { result } = renderHook(() => useRespondToAlternatives(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ operationId: "op_1", response: "waitlist" });
    });

    expect(respondToAlternatives).toHaveBeenCalledWith("op_1", "waitlist");
    const domains = invalidatedDomains(invalidate);
    expect(domains).toContain("operations");
    expect(domains).toContain("waitlist");
  });
});

describe("operationActionFailureMessage", () => {
  it("names the race, the disappearance, the refusal, and falls back to the server's words", () => {
    expect(operationActionFailureMessage(new ApiError(409, "conflict"), "x"))
      .toBe("This operation moved on while you were looking — the board has been refreshed.");
    expect(operationActionFailureMessage(new ApiError(404, "missing"), "x"))
      .toBe("This operation no longer exists.");
    expect(operationActionFailureMessage(new ApiError(403, "denied"), "x"))
      .toBe("The platform refused this action for your operator identity — it was not performed.");
    expect(operationActionFailureMessage(new Error("Missing provider_ref"), "x"))
      .toBe("Missing provider_ref");
    expect(operationActionFailureMessage("nope", "record this outcome"))
      .toBe("Could not record this outcome");
  });
});
