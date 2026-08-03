/**
 * Adapters from the API's booking shapes to the shapes the existing console components
 * already take (`src/data/console.ts`).
 *
 * Mapping into the mock shapes — rather than rewriting ten screens onto the generated
 * types — keeps the change reviewable and leaves every component's props untouched. The
 * two models genuinely differ:
 *
 *   API  → ISO `starts_at`, `party_size`, opaque `service_id` / `customer_ref`
 *   Mock → `day` (index into the displayed week), `start` (minutes since midnight),
 *          `dur`, `staff`, `serviceName`, `name`
 *
 * Anything the API does not model is filled honestly rather than invented: there is no
 * staff/resource concept server-side, so every booking lands on the first resource lane.
 */

import type { Booking, BookingStatus } from "../../data/console";
import { SERVICES, STAFF } from "../../data/console";
import type { ScheduledBooking } from "./api";

/** Monday 00:00 local time for the week containing `date`. */
export function startOfWeek(date: Date): Date {
  const start = new Date(date);
  start.setHours(0, 0, 0, 0);
  // getDay(): 0 = Sunday. Shift so Monday is index 0, matching WEEK.dows.
  const mondayOffset = (start.getDay() + 6) % 7;
  start.setDate(start.getDate() - mondayOffset);
  return start;
}

/** The seven-day window the schedule grid displays, as an ISO range. */
export function weekWindow(reference = new Date()): { from: string; to: string } {
  const from = startOfWeek(reference);
  const to = new Date(from);
  to.setDate(to.getDate() + 7);
  return { from: from.toISOString(), to: to.toISOString() };
}

/**
 * Day index (0 = Monday) and minutes-since-midnight for a local instant.
 *
 * The day index counts **calendar** days rather than dividing elapsed milliseconds by 24h:
 * a DST transition inside the week makes one local day 23 or 25 hours long, which would
 * shift every subsequent day by one column and silently merge two days' bookings.
 */
export function toDayAndStart(startsAt: Date, weekStart: Date): { day: number; start: number } {
  const cursor = startOfDay(weekStart);
  const target = startOfDay(startsAt);

  let day = 0;
  while (cursor.getTime() < target.getTime() && day < 366) {
    cursor.setDate(cursor.getDate() + 1);
    day += 1;
  }
  if (cursor.getTime() > target.getTime()) {
    // Before the week start: count backwards so the index stays signed and out-of-range
    // rows are detectable rather than silently landing on day 0.
    day = 0;
    const back = startOfDay(weekStart);
    while (back.getTime() > target.getTime() && day > -366) {
      back.setDate(back.getDate() - 1);
      day -= 1;
    }
  }

  return { day, start: startsAt.getHours() * 60 + startsAt.getMinutes() };
}

function startOfDay(date: Date): Date {
  const copy = new Date(date);
  copy.setHours(0, 0, 0, 0);
  return copy;
}

/**
 * Maps the API's `lifecycle_status` and normalized operation status onto the mock
 * `BookingStatus` the components render. Anything not yet booked is shown as in-progress
 * rather than as confirmed — the UI must never claim an outcome the server has not
 * reached (Inv-16).
 */
export function toBookingStatus(lifecycleStatus: string, operationStatus: string): BookingStatus {
  // Failure is tested BEFORE `cancelled`. The server maps rejected/expired/failed_final onto
  // lifecycle "cancelled" too, so checking lifecycle first would render a booking whose
  // fulfilment hard-failed as a calm "Cancelled" — the operator reads that as "the guest
  // cancelled", never investigates, and nobody calls the guest about a booking that was
  // never made.
  if (operationStatus === "rejected" || operationStatus === "expired" || operationStatus === "failed_final")
    return "failed";
  if (lifecycleStatus === "cancelled") return "cancelled";
  if (operationStatus === "booked") return "confirmed";
  if (operationStatus === "waitlisted") return "waitlisted";
  if (operationStatus === "reconciling" || operationStatus === "compensation_required") return "retrying";
  if (operationStatus === "manual_review" || operationStatus === "alternatives_required") return "pending";
  return "confirming";
}

export interface ScheduleAdapterOptions {
  /** Start of the displayed week; defaults to the current week. */
  weekStart?: Date;
}

/**
 * Converts one API schedule row to the mock `Booking` shape.
 * Returns `null` when the row has no start time — it cannot be placed on a grid.
 */
export function toBooking(
  row: ScheduledBooking,
  options: ScheduleAdapterOptions = {}
): Booking | null {
  if (!row.starts_at) return null;

  const weekStart = options.weekStart ?? startOfWeek(new Date());
  const startsAt = new Date(row.starts_at);
  if (Number.isNaN(startsAt.getTime())) return null;

  const { day, start } = toDayAndStart(startsAt, weekStart);
  // The grid only renders days 0-6; a row outside the displayed week has no column, so
  // drop it rather than filing it under the wrong day.
  if (day < 0 || day > 6) return null;

  const serviceKey = row.service_id ?? "";
  const service = SERVICES[serviceKey];

  // An unfinalised operation has no booking row. Falling back to the operation id keeps it on
  // the grid — it still occupies the slot — but the row must not pretend to be a booking.
  const bookingId = row.booking_id ?? null;

  return {
    id: bookingId ?? row.operation_id ?? "",
    isBooking: bookingId !== null,
    day,
    // No staff/resource concept exists server-side, so everything lands on lane one.
    staff: STAFF[0]?.id ?? "s1",
    start,
    dur: row.duration_minutes ?? service?.dur ?? 60,
    service: serviceKey,
    // Fall back to the raw id rather than inventing a display name for an unknown service.
    serviceName: service?.name ?? serviceKey ?? "Booking",
    name: row.customer_ref ?? row.booking_reference ?? "Guest",
    // The schedule projection carries no provenance; claiming "Booked by AI" would
    // fabricate the very fact the API deliberately does not record.
    prov: "unknown",
    status: toBookingStatus(row.lifecycle_status ?? "", row.operation_status ?? ""),
    channel: null,
    // `ScheduledBookingView` carries no deposit field. Reading one from the hand-written mock
    // catalog would assert a payment hold the platform never took.
    dep: 0,
    transcript: null,
  };
}

/** Maps a schedule response to the grid's booking list, dropping rows that cannot be placed. */
export function toBookings(
  rows: readonly ScheduledBooking[] | null | undefined,
  options: ScheduleAdapterOptions = {}
): Booking[] {
  return (rows ?? [])
    .map((row) => toBooking(row, options))
    .filter((booking): booking is Booking => booking !== null);
}
