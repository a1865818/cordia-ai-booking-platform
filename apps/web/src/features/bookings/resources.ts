/**
 * The lanes the schedule grid draws bookings on.
 *
 * **The platform models no resource.** `BookingRequest` has no field for a staff member, room
 * or table; `ScheduledBookingView` returns none; there is no resources endpoint in the
 * contract. The grid nevertheless rendered four invented people (`STAFF` in `data/console.ts`
 * — Linh Pham, Trang Vo, Mai Dang, Tony Bui), stacked every live booking into lane one, printed
 * "<service> · Linh" beside real bookings, and offered a "With" picker whose value was
 * discarded on submit.
 *
 * So the grid now draws **one unnamed lane**, following the same ruling the rest of the console
 * already follows for things the API does not record — `prov: "unknown"`, `channel: null`,
 * `dep: 0`, the deleted `MOCK_WAIT`: omit rather than fabricate.
 *
 * This is a prop rather than a constant precisely so it can stop being one lane. When a
 * resources endpoint exists, pass the real lanes and every consumer below works unchanged.
 */

export interface GridResource {
  id: string;
  /**
   * Display name, or `""` when the lane stands for "every booking" rather than for a person.
   * Consumers must render a name only when there is one — an empty label is the honest
   * rendering of a dimension the platform does not track.
   */
  name: string;
  /** Secondary line under the name, e.g. a role. Empty for the unassigned lane. */
  role?: string;
}

/**
 * The lane every booking lands on while no resource dimension exists.
 *
 * The id is kept as `s1` — the value `adapters.ts` has always written into `Booking.staff` —
 * so no persisted or in-flight row changes meaning. It addresses nothing on the server either
 * way; it is a grid coordinate.
 */
export const UNASSIGNED_RESOURCE_ID = "s1";

/** The single-lane resource list: what the grid shows until resources are modelled. */
export const UNASSIGNED_RESOURCES: readonly GridResource[] = [
  { id: UNASSIGNED_RESOURCE_ID, name: "", role: "" },
];
