import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type AvailabilityResponse =
  paths["/api/availability"]["get"]["responses"][200]["content"]["application/json"];

export type AvailabilitySlotWire = NonNullable<AvailabilityResponse["slots"]>[number];

export interface AvailableSlot {
  slotId: string;
  /** The resource the slot belongs to, when the venue declared one. */
  resourceId: string | null;
  /** ISO instant the slot starts at. */
  startsAt: string;
  totalCapacity: number;
  /** `total − held − booked`, floored at zero by the server. */
  freeCapacity: number;
}

/**
 * Reads the slots a venue has declared for a window (`GET /api/availability`, docs §12 gap 10).
 *
 * **This is an `informational` read in spec 07's sense, and the console must word it that way.**
 * It is non-atomic and takes no hold, so a slot it reports as free can be taken between the read
 * and the submission — spec 07 §6.5 allows only "that time appears available" for this mode, and
 * only a `held` result closes the race. Nothing here may be phrased as a guarantee, and the
 * booking still learns its real outcome from the operation.
 *
 * The response carries no `mode` field even though spec 07 §44 says every availability result
 * does, so the mode is not read off the wire — it is a property of *this endpoint*, which
 * neither holds nor confirms anything. If a mode is added later, this is the one place that has
 * to start honouring it.
 */
export function getAvailability(from: string, to: string, signal?: AbortSignal) {
  const params = new URLSearchParams({ from, to });
  return fetchApi<AvailabilityResponse>(`/api/availability?${params.toString()}`, { signal });
}

/**
 * Wire slots to the shape the drawer renders.
 *
 * Rows missing a `slot_id` or a parseable `starts_at` are **dropped rather than defaulted**: a
 * slot with an invented time is worse than a slot that is not shown, because the operator would
 * plan around it. Same rule `toBookableServices` applies to catalog items.
 */
export function toAvailableSlots(
  response: AvailabilityResponse | null | undefined
): AvailableSlot[] {
  const slots = response?.slots ?? [];

  return slots
    .filter((s): s is AvailabilitySlotWire & { slot_id: string; starts_at: string } =>
      typeof s.slot_id === "string" && s.slot_id.length > 0 &&
      typeof s.starts_at === "string" && !Number.isNaN(Date.parse(s.starts_at))
    )
    .map(s => ({
      slotId: s.slot_id,
      resourceId: s.resource_id ?? null,
      startsAt: s.starts_at,
      totalCapacity: s.total_capacity,
      freeCapacity: s.free_capacity,
    }))
    .sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt));
}

/**
 * Declares a bookable slot (`POST /api/availability/slots`).
 *
 * Owner-only and audited server-side: capacity is a commercial statement about what the venue
 * will honour, not a display preference. Idempotent by the server's derived slot key, so
 * re-declaring the same slot returns the existing one rather than resetting counts that live
 * bookings depend on — which is why this carries no client-minted key.
 *
 * **On the three dimensions.** The slot key is `{dimension}|{starts_at}`, where the dimension is
 * the first non-empty of resource → service → location, and a booking derives its key the same
 * way from its canonical payload. That payload carries `service_id` and never a `resource_id`,
 * so a slot declared against a resource is keyed on something no booking can produce: it would
 * be stored, listed as available, and never matched or enforced. `resourceId`/`locationId` stay
 * on this signature because the endpoint accepts them, but the console only ever sends a service.
 */
export function declareSlot(
  input: {
    resourceId?: string;
    serviceId?: string;
    locationId?: string;
    startsAt: string;
    totalCapacity: number;
  },
  reasonCode: string
) {
  return fetchApi<unknown>("/api/availability/slots", {
    method: "POST",
    actor: true,
    reasonCode,
    body: JSON.stringify({
      resource_id: input.resourceId || null,
      service_id: input.serviceId || null,
      location_id: input.locationId || null,
      starts_at: input.startsAt,
      total_capacity: input.totalCapacity,
    }),
  });
}
