/**
 * The drawer's delivery read addresses the right resource and reports only what the server
 * returned (#83).
 */

import { describe, expect, it, vi, beforeEach } from "../../test/vitest-compat";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";

vi.mock("./api", () => ({
  getBookingNotifications: vi.fn(),
  getOperationNotifications: vi.fn(),
}));

import { getBookingNotifications, getOperationNotifications, type NotificationDelivery } from "./api";
import { useNotificationDelivery } from "./notificationDelivery";

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const delivered: NotificationDelivery = {
  notification_id: "notif_1",
  operation_id: "op_1",
  event_type: "booking_confirmed",
  channel: "sms",
  state: "delivered",
  failure_category: null,
  attempt_count: 1,
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:05Z",
};

beforeEach(() => vi.clearAllMocks());

describe("useNotificationDelivery", () => {
  it("reads a booking through the booking route", async () => {
    vi.mocked(getBookingNotifications).mockResolvedValue({ notifications: [delivered] } as never);

    const { result } = renderHook(() => useNotificationDelivery("bk_1", true, true), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(getBookingNotifications).toHaveBeenCalledWith("bk_1", expect.anything());
    expect(getOperationNotifications).not.toHaveBeenCalled();
    expect(result.current.data).toEqual([delivered]);
  });

  it("reads a not-yet-booked request through the operation route", async () => {
    vi.mocked(getOperationNotifications).mockResolvedValue({ notifications: [] } as never);

    const { result } = renderHook(() => useNotificationDelivery("op_9", false, true), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(getOperationNotifications).toHaveBeenCalledWith("op_9", expect.anything());
    expect(getBookingNotifications).not.toHaveBeenCalled();
  });

  it("returns an empty list, not a fabricated row, when nothing was recorded", async () => {
    vi.mocked(getBookingNotifications).mockResolvedValue({ notifications: [] } as never);

    const { result } = renderHook(() => useNotificationDelivery("bk_2", true, true), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual([]);
  });

  it("does not fetch while the drawer is closed", () => {
    renderHook(() => useNotificationDelivery("bk_3", true, false), { wrapper });

    expect(getBookingNotifications).not.toHaveBeenCalled();
  });
});
