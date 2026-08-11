/**
 * The services a booking can actually be made against.
 *
 * These come from the venue's **live** catalog (`GET /api/catalog/items`), not from
 * `SERVICE_LIST` in `data/console.ts`. That distinction is the whole point of this module:
 * the create drawer used to offer the hand-written mock catalogue and submit its key as
 * `revision.canonical_payload.service_id`, so every console-created booking carried a
 * `service_id` that existed nowhere on the server — hashed by `canon.v1` and persisted as the
 * durable intent. The Settings badge said "changes are not saved", which was true about
 * persistence and silent about that.
 *
 * `SERVICES` keeps its other job — turning a `service_id` on a row the API returned into a
 * readable label, falling back to the raw id when it misses. Labelling an id is not the same
 * as minting one.
 */

import type { CatalogItem } from "../settings/api";

export interface BookableService {
  /** The catalog's own `item_id` — exactly what is submitted as `service_id`. */
  id: string;
  name: string;
  /**
   * Minutes, when the catalog states one.
   *
   * `null` when it does not: `CatalogItem` carries no duration field, so there is usually
   * nothing to state. The draft then omits `duration_minutes` entirely rather than sending a
   * default — an invented duration is a durable claim about how long the venue is booked for.
   */
  durationMinutes: number | null;
}

/** Reads a positive integer duration from a catalog item, if it carries one at all. */
function durationOf(item: CatalogItem): number | null {
  const raw = (item as Record<string, unknown>).duration_minutes;
  return typeof raw === "number" && Number.isFinite(raw) && raw > 0 ? Math.round(raw) : null;
}

/**
 * Maps catalog items to bookable services.
 *
 * Items with no `item_id` are dropped: without one there is nothing to submit, and offering an
 * unbookable option is worse than a shorter list. An item with no name falls back to its id
 * rather than to a placeholder, so the operator sees the handle the platform actually knows.
 */
export function toBookableServices(
  items: readonly CatalogItem[] | null | undefined
): BookableService[] {
  return (items ?? [])
    .filter((item): item is CatalogItem & { item_id: string } =>
      typeof item.item_id === "string" && item.item_id.length > 0
    )
    .map((item) => ({
      id: item.item_id,
      name: item.name && item.name.length > 0 ? item.name : item.item_id,
      durationMinutes: durationOf(item),
    }));
}
