/**
 * The lanes the schedule grid draws bookings on.
 *
 * `GET /api/resources` is the roster. Until that list has loaded — or when it is empty —
 * the grid draws one unnamed lane (`UNASSIGNED_RESOURCES`) so every booking still has a
 * column. Assigned rows use `resource_id` from the schedule projection.
 */

import type { StaffResource, StaffResourceListResponse } from "../resources/api";

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
 * The lane a booking lands on when the schedule named no resource.
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

/** Maps a staff-resource list onto grid lanes. Empty list → the unassigned fallback. */
export function toGridResources(
  api: StaffResourceListResponse | readonly StaffResource[] | null | undefined
): GridResource[] {
  const rows: readonly StaffResource[] = Array.isArray(api)
    ? api
    : api != null && "resources" in api
      ? api.resources
      : [];
  if (rows.length === 0) return [...UNASSIGNED_RESOURCES];
  return rows.map((r) => ({
    id: r.resource_id,
    name: r.name,
    role: r.role ?? "",
  }));
}
