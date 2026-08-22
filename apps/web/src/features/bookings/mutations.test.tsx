/**
 * Regression tests for the write mutations' failure handling.
 *
 * These cover the cases that are easy to get subtly wrong and impossible to notice by hand:
 * an optimistic rollback that reaches beyond its own row, and a cancel that reports an
 * outcome the server never gave (Inv-16).
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "../../test/vitest-compat";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, act, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import {
  useRemoveFromWaitlist, useCancelBooking, useAcceptOffer, useDeclineOffer,
  offerFailureMessage,
} from "./mutations";
import type { WaitlistItem } from "../../data/console";

vi.mock("../waitlist/api", () => ({
  cancelEnrollment: vi.fn(),
  acceptOffer: vi.fn(),
  declineOffer: vi.fn(),
  enrollGuest: vi.fn(),
}));
vi.mock("./api", () => ({
  getBookingDetails: vi.fn(),
  getOperationStatus: vi.fn(),
  recordAttendance: vi.fn(),
}));
vi.mock("./submit", () => ({
  submitCancellation: vi.fn(),
}));

import { acceptOffer, cancelEnrollment, declineOffer } from "../waitlist/api";
import { ApiError } from "../../api/problem";
import { getBookingDetails, getOperationStatus } from "./api";
import { submitCancellation } from "./submit";

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const row = (id: string, name: string): WaitlistItem =>
  ({ id, name, party: 2, service: "gel", window: "any", priority: 1 }) as unknown as WaitlistItem;

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
});
afterEach(() => sessionStorage.clear());

describe("useRemoveFromWaitlist", () => {
  it("restores only its own row when it fails, leaving an overlapping removal removed", async () => {
    // The bug this pins needs the two removals to OVERLAP: rolling back to the whole
    // pre-removal snapshot resurrects a row that a different, already-succeeded removal
    // legitimately took out — telling the operator a guest is still waiting after the server
    // confirmed they were removed.
    let rows = [row("a", "Ada"), row("b", "Bo"), row("c", "Cy")];
    const applyRows = (update: React.SetStateAction<WaitlistItem[]>) => {
      rows = typeof update === "function" ? update(rows) : update;
    };

    let rejectA!: (error: unknown) => void;
    let resolveB!: () => void;
    vi.mocked(cancelEnrollment)
      .mockImplementationOnce(() => new Promise((_, reject) => { rejectA = reject; }) as never)
      .mockImplementationOnce(() => new Promise<void>((resolve) => { resolveB = resolve; }) as never);

    const { result } = renderHook(() => useRemoveFromWaitlist(applyRows), { wrapper });

    // A is removed first (snapshot [a,b,c]) and is still in flight...
    let pendingA!: Promise<unknown>;
    await act(async () => {
      pendingA = result.current.mutateAsync({ enrollmentId: "a", current: rows }).catch(() => {});
    });
    await waitFor(() => expect(rows.map(r => r.id)).toEqual(["b", "c"]));

    // ...when B is removed too.
    let pendingB!: Promise<unknown>;
    await act(async () => {
      pendingB = result.current.mutateAsync({ enrollmentId: "b", current: rows }).catch(() => {});
    });
    await waitFor(() => expect(rows.map(r => r.id)).toEqual(["c"]));

    // B succeeds; A then fails.
    await act(async () => { resolveB(); await pendingB; });
    await act(async () => { rejectA(new Error("nope")); await pendingA; });

    // A comes back at its original position; B stays gone.
    expect(rows.map(r => r.id)).toEqual(["a", "c"]);
  });

  it("puts the row back where it was, not at the end", async () => {
    let rows = [row("a", "Ada"), row("b", "Bo"), row("c", "Cy")];
    const applyRows = (update: React.SetStateAction<WaitlistItem[]>) => {
      rows = typeof update === "function" ? update(rows) : update;
    };

    const { result } = renderHook(() => useRemoveFromWaitlist(applyRows), { wrapper });
    vi.mocked(cancelEnrollment).mockRejectedValueOnce(new Error("nope"));

    await act(async () => {
      await result.current.mutateAsync({ enrollmentId: "b", current: rows }).catch(() => {});
    });

    expect(rows.map(r => r.id)).toEqual(["a", "b", "c"]);
  });
});

describe("useCancelBooking", () => {
  beforeEach(() => {
    vi.mocked(getBookingDetails).mockResolvedValue({ booking: { version: 3 } } as never);
  });

  it("reports cancelled only when the server settled it as booked", async () => {
    vi.mocked(submitCancellation).mockResolvedValue({ operation_id: "op_1" } as never);
    vi.mocked(getOperationStatus).mockResolvedValue({ status: "booked", terminal: true } as never);

    const { result } = renderHook(() => useCancelBooking(), { wrapper });
    let outcome: Awaited<ReturnType<typeof result.current.mutateAsync>> | undefined;
    await act(async () => {
      outcome = await result.current.mutateAsync({ bookingId: "bk_1", grantId: "grant_1" });
    });

    expect(outcome).toMatchObject({ settled: true, cancelled: true });
  });

  it("does not claim a cancellation the server declined", async () => {
    vi.mocked(submitCancellation).mockResolvedValue({ operation_id: "op_1" } as never);
    vi.mocked(getOperationStatus).mockResolvedValue({ status: "rejected", terminal: true } as never);

    const { result } = renderHook(() => useCancelBooking(), { wrapper });
    let outcome: Awaited<ReturnType<typeof result.current.mutateAsync>> | undefined;
    await act(async () => {
      outcome = await result.current.mutateAsync({ bookingId: "bk_1", grantId: "grant_1" });
    });

    // Settled, but a settled *refusal* — the caller must not dismiss the booking.
    expect(outcome).toMatchObject({ settled: true, cancelled: false });
  });

  it("claims nothing when the server accepts but returns no operation to track", async () => {
    vi.mocked(submitCancellation).mockResolvedValue({} as never);

    const { result } = renderHook(() => useCancelBooking(), { wrapper });
    let outcome: Awaited<ReturnType<typeof result.current.mutateAsync>> | undefined;
    await act(async () => {
      outcome = await result.current.mutateAsync({ bookingId: "bk_1", grantId: "grant_1" });
    });

    expect(outcome).toMatchObject({ status: null, settled: false, cancelled: false });
    expect(getOperationStatus).not.toHaveBeenCalled();
  });

  it("carries the grant to the server — the endpoint rejects an empty one structurally", async () => {
    // `grant_id` is NotEmpty-validated on the API, so an absent grant is a 400 the operator
    // cannot resolve, not a 403 they can. The grant must be obtained BEFORE the first submit.
    vi.mocked(submitCancellation).mockResolvedValue({ operation_id: "op_1" } as never);
    vi.mocked(getOperationStatus).mockResolvedValue({ status: "booked", terminal: true } as never);

    const { result } = renderHook(() => useCancelBooking(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ bookingId: "bk_1", grantId: "grant_abc" });
    });

    expect(vi.mocked(submitCancellation).mock.calls[0][0]).toMatchObject({
      bookingId: "bk_1",
      grantId: "grant_abc",
    });
  });

  it("carries the grant on the version READ too, not only on the write", async () => {
    // `GET /api/Bookings/{id}` answers `requires_verification` as a typed 403 for a gated
    // booking. The read ran without the grant, so an operator could complete step-up and
    // still have the cancellation fail — on the version lookup, before anything was submitted.
    vi.mocked(submitCancellation).mockResolvedValue({ operation_id: "op_1" } as never);
    vi.mocked(getOperationStatus).mockResolvedValue({ status: "booked", terminal: true } as never);

    const { result } = renderHook(() => useCancelBooking(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ bookingId: "bk_1", grantId: "grant_abc" });
    });

    expect(vi.mocked(getBookingDetails)).toHaveBeenCalledWith("bk_1", "grant_abc");
  });

  it("refuses a booking with no confirmed version rather than guessing one", async () => {
    vi.mocked(getBookingDetails).mockResolvedValue({ booking: {} } as never);

    const { result } = renderHook(() => useCancelBooking(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ bookingId: "bk_1", grantId: "grant_1" }).catch(() => {});
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(submitCancellation).not.toHaveBeenCalled();
  });
});

describe("waitlist offer decisions", () => {
  it("sends the offer id to the server and reports success only then", async () => {
    vi.mocked(acceptOffer).mockResolvedValue({ status: "accepted" });

    const { result } = renderHook(() => useAcceptOffer(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ offerId: "off_1", name: "Ada" });
    });

    expect(acceptOffer).toHaveBeenCalledWith("off_1");
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it("declines through the decline endpoint, not the accept one", async () => {
    vi.mocked(declineOffer).mockResolvedValue({ status: "declined" });

    const { result } = renderHook(() => useDeclineOffer(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ offerId: "off_2", name: "Bo" });
    });

    expect(declineOffer).toHaveBeenCalledWith("off_2");
    expect(acceptOffer).not.toHaveBeenCalled();
  });

  it("surfaces a failure rather than claiming the offer was answered", async () => {
    // Deliberately NOT optimistic: accepting finalizes a capacity hold and confirms a
    // booking server-side, so the row must not move until the server has decided (Inv-16).
    vi.mocked(acceptOffer).mockRejectedValue(new ApiError(409, "gone"));

    const { result } = renderHook(() => useAcceptOffer(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ offerId: "off_1", name: "Ada" }).catch(() => {});
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

describe("offerFailureMessage", () => {
  it("explains a 409 as the race it is, not as a bug", () => {
    expect(offerFailureMessage(new ApiError(409, "conflict"), "accept"))
      .toBe("That offer is no longer live — the guest answered it or it expired.");
  });

  it("explains a 404 separately", () => {
    expect(offerFailureMessage(new ApiError(404, "missing"), "decline"))
      .toBe("That offer no longer exists.");
  });

  it("falls back to the error's own message", () => {
    expect(offerFailureMessage(new Error("network down"), "accept")).toBe("network down");
  });
});
