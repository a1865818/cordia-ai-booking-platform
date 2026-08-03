/**
 * Adapts the derived customer projection to the `Customer` shape `CustomersScreen` renders.
 *
 * The API deliberately exposes very little (spec 14 minimisation): an opaque
 * `customer_ref`, a booking count, first/last activity and the last service. It has no
 * phone, no no-show count, no lifetime spend and no tags — those are mock-only fields.
 *
 * Rather than zero-filling them (a "$0 lifetime" or "0 no-shows" reads as a fact, not as
 * "unknown"), they are left empty and `CustomersScreen` suppresses those stats when it is
 * showing live rows.
 */

import type { Customer } from "../../data/console";
import { SERVICES } from "../../data/console";
import type { CustomerSummary } from "./api";

/** Coarse "last seen" phrasing matching the mock's `lastVisit` strings. */
export function lastSeenLabel(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "";
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "";

  const days = Math.floor((now - then) / 86_400_000);
  if (days <= 0) return "Today";
  if (days === 1) return "Yesterday";
  if (days < 7) return `${days} days ago`;
  if (days < 14) return "Last week";
  if (days < 60) return `${Math.floor(days / 7)} weeks ago`;
  return `${Math.floor(days / 30)} months ago`;
}

export function toCustomer(summary: CustomerSummary, now = Date.now()): Customer {
  const serviceId = summary.last_service_id ?? "";
  return {
    id: summary.customer_ref ?? "",
    // The reference IS the identity the API exposes; no name is available.
    name: summary.customer_ref ?? "Guest",
    phone: "",
    channel: "web",
    visits: summary.booking_count ?? 0,
    // Not modelled server-side — suppressed in the UI rather than shown as zero.
    noShows: 0,
    lastVisit: lastSeenLabel(summary.last_seen_at, now),
    spend: 0,
    tags: [],
    fav: SERVICES[serviceId]?.name ?? serviceId,
    note: "",
  };
}

export function toCustomers(
  summaries: readonly CustomerSummary[] | null | undefined,
  now = Date.now()
): Customer[] {
  return (summaries ?? []).map((s) => toCustomer(s, now));
}
