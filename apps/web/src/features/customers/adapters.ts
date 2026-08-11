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
import { getActor } from "../../api/config";
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

/**
 * True when this row is the console operator, not a guest.
 *
 * `/api/customers` is a projection over booking operations that falls back to the confirmation's
 * `identity_ref` when no booking row carries a `customer_id`
 * (`ConsoleReadModel.BuildCustomerResolverAsync`, spec 04 §8.8). `identity_ref` is whoever
 * confirmed the intent, so for a console-made booking that is the **operator**, and those rows
 * collapse into one whose `booking_count` reads as "how many bookings staff made".
 *
 * **Scope, verified 2026-08-07 — narrower than this note used to claim.** The fallback fires only
 * for operations that have not yet materialised a booking row (in-flight, or terminally failed).
 * Once a booking exists it always carries a `customer_id`: `FinalizeBookingCommandHandler` ranks
 * `customer_id` → `contact_ref` → `phone_e164` → … and ends with `?? providerReference`, so the
 * column is never blank and the `identity_ref` branch is unreachable for finalised bookings. The
 * earlier wording — "every such booking collapses into one row" — predates that ranking and
 * overstated the blast radius.
 *
 * That is a real, correct row about a real thing. It is just not a customer, and the screen must
 * not present it as one: unlabelled, the venue's busiest "customer" is the console itself. This
 * flag is the honest label, not a fix. A true fix needs the projection to expose origin/channel
 * metadata — `CustomerSummary` carries none today — or a ratified way to attach a guest to a
 * staff-made booking (spec 04/08/12). Deliberately deferred: adding an origin field changes the
 * OpenAPI surface, and the mislabelling it would correct is confined to the in-flight case above.
 */
export function isOperatorRef(customerRef: string | null | undefined): boolean {
  return !!customerRef && customerRef === getActor();
}

export function toCustomer(summary: CustomerSummary, now = Date.now()): Customer {
  const serviceId = summary.last_service_id ?? "";
  return {
    isOperator: isOperatorRef(summary.customer_ref),
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
