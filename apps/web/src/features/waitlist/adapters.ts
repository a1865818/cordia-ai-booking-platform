/**
 * Adapts waitlist enrollments to the `WaitlistItem` shape `WaitlistScreen` renders.
 */

import type { WaitlistItem } from "../../data/console";
import { SERVICES } from "../../data/console";
import type { WaitlistEnrollmentSummary } from "./api";

/** "Wants" phrasing from the acceptable window the guest enrolled with. */
export function wantLabel(from: string | null | undefined, to: string | null | undefined): string {
  if (!from) return "Any time";

  const start = new Date(from);
  if (Number.isNaN(start.getTime())) return "Any time";

  const day = start.toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short" });
  const time = start.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });

  if (!to) return `${day} from ${time}`;
  const end = new Date(to);
  if (Number.isNaN(end.getTime())) return `${day} from ${time}`;

  const endTime = end.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
  return `${day} ${time}–${endTime}`;
}

/**
 * Whether the latest offer is one the operator can still answer.
 *
 * `offered` is the only actionable state: `pending`/`matched` have not reached the guest,
 * and `accepted`/`declined`/`expired` are terminal. An expiry in the past is treated as
 * inactionable even if the row still says `offered` — the sweeper may not have run yet, and
 * offering a button that will 409 is worse than offering none.
 */
export function isOfferActionable(
  status: string | null | undefined,
  expiresAt: string | null | undefined,
  now = Date.now()
): boolean {
  if (status !== "offered") return false;
  if (!expiresAt) return true;

  const expiry = new Date(expiresAt).getTime();
  return Number.isNaN(expiry) || expiry > now;
}

/** "expires in 7 min" / "expired" — the only offer timing the projection supports. */
export function offerExpiryLabel(expiresAt: string | null | undefined, now = Date.now()): string {
  if (!expiresAt) return "";
  const expiry = new Date(expiresAt).getTime();
  if (Number.isNaN(expiry)) return "";

  const minutes = Math.round((expiry - now) / 60_000);
  if (minutes <= 0) return "expired";
  if (minutes < 60) return `expires in ${minutes} min`;
  return `expires in ${Math.round(minutes / 60)} hr`;
}

export function toWaitlistItem(
  enrollment: WaitlistEnrollmentSummary,
  now = Date.now()
): WaitlistItem {
  const serviceId = enrollment.service_id ?? "";
  return {
    id: enrollment.enrollment_id ?? "",
    name: enrollment.customer_ref ?? "Guest",
    service: SERVICES[serviceId]?.name ?? serviceId,
    want: wantLabel(enrollment.acceptable_from, enrollment.acceptable_to),
    // Per-guest auto-book reflects the explicit grant recorded at enrollment; silent
    // auto-book is prohibited by default (spec 11 §20, Inv-1), so this is never assumed.
    auto: enrollment.auto_book_authorized ?? false,
    // Marks the row as authoritative, which makes the auto-book state read-only: the console
    // has no endpoint that records a change of consent, and a chip the operator could click
    // into a different state would claim a grant was given or withdrawn when nothing was
    // written at all.
    isLive: true,
    // Only carried when the offer can actually be answered; the screen renders the
    // accept/decline actions off the presence of this id, so a terminal or expired offer
    // simply has none.
    offerId: isOfferActionable(enrollment.latest_offer_status, enrollment.latest_offer_expires_at, now)
      ? enrollment.latest_offer_id ?? undefined
      : undefined,
    offerStatus: enrollment.latest_offer_status ?? undefined,
    offerExpiry: offerExpiryLabel(enrollment.latest_offer_expires_at, now),
    serviceId: serviceId || undefined,
    partySize: enrollment.party_size ?? undefined,
    acceptableFrom: enrollment.acceptable_from ?? undefined,
  };
}

export function toWaitlistItems(
  enrollments: readonly WaitlistEnrollmentSummary[] | null | undefined,
  now = Date.now()
): WaitlistItem[] {
  return (enrollments ?? []).map((e) => toWaitlistItem(e, now));
}
