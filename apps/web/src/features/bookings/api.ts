import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type BookingScheduleResponse =
  paths["/api/Bookings/schedule"]["get"]["responses"][200]["content"]["application/json"];

export type ScheduledBooking = NonNullable<BookingScheduleResponse["bookings"]>[number] & {
  attributed_source?: string | null;
  attendance?: string | null;
  resource_id?: string | null;
  channel?: string | null;
  session_id?: string | null;
  deposit_amount_minor?: number | null;
};

export type AttendanceValue = "attended" | "no_show";

export interface RecordAttendanceResult {
  booking_id: string;
  attendance: string;
}

export type BookingDetailsResponse =
  paths["/api/Bookings/{bookingId}"]["get"]["responses"][200]["content"]["application/json"];

export type OperationStatusView =
  paths["/api/booking-operations/{operationId}"]["get"]["responses"][200]["content"]["application/json"];

export interface ScheduleWindow {
  from: string;
  to: string;
}

/** Bookings starting inside the window. Tenant-wide — not the identity lookup on `GET /api/Bookings`. */
export function getBookingSchedule(window: ScheduleWindow, signal?: AbortSignal) {
  const query = `from=${encodeURIComponent(window.from)}&to=${encodeURIComponent(window.to)}`;
  return fetchApi<BookingScheduleResponse>(`/api/Bookings/schedule?${query}`, { signal });
}

/**
 * Single booking projection.
 *
 * Note the unusual contract: 200, 403 and 404 all return the **same body** with a `status`
 * of `ok | requires_verification | not_found`, so a 403 here is a typed business response
 * rather than a failure. `fetchApi` throws on 403, so callers that need to distinguish
 * `requires_verification` should catch and inspect — see `features/verification`.
 */
export function getBookingDetails(bookingId: string, grantId?: string, signal?: AbortSignal) {
  return fetchApi<BookingDetailsResponse>(`/api/Bookings/${encodeURIComponent(bookingId)}`, {
    grantId,
    signal,
  });
}

/** Polls a durable operation to its terminal state (D7). */
export function getOperationStatus(operationId: string, signal?: AbortSignal) {
  return fetchApi<OperationStatusView>(
    `/api/booking-operations/${encodeURIComponent(operationId)}`,
    { signal }
  );
}

/** Records guest attendance. Operator-gated; does not change lifecycle status. */
export function recordAttendance(bookingId: string, attendance: AttendanceValue) {
  return fetchApi<RecordAttendanceResult>(
    `/api/Bookings/${encodeURIComponent(bookingId)}/attendance`,
    {
      method: "POST",
      actor: true,
      reasonCode: "attendance_recorded",
      body: JSON.stringify({ attendance }),
    }
  );
}
