/**
 * Adapts waitlist enrollments to the `WaitlistItem` shape `WaitlistScreen` renders.
 */

import type { WaitlistItem } from "../../data/console";
import { SERVICES } from "../../data/console";
import { remainingLabel } from "../../components/ui/Countdown";
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

/**
 * "expires in 7 min" / "expired" — the only offer timing the projection supports.
 *
 * Shares `isOfferActionable`'s threshold exactly: expired means `expiry <= now`, nothing else.
 * Rounding to the nearest minute made the two disagree — an offer 25 seconds from expiry was
 * still actionable but its label already read "expired", so the row showed a live Accept button
 * beside the word expired. Floors throughout, so the label can understate the time remaining
 * but never overstate it.
 */
export function offerExpiryLabel(expiresAt: string | null | undefined, now = Date.now()): string {
  // Delegates to the shared rule so the label the row renders and the label this returns can
  // never drift apart — that drift is exactly what once put "expired" beside a live Accept
  // button. The threshold and the flooring both live in `remainingLabel` now.
  return remainingLabel(expiresAt, now);
}

export function toWaitlistItem(enrollment: WaitlistEnrollmentSummary): WaitlistItem {
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
    // The RAW offer fields. Actionability and the countdown are deliberately NOT decided here:
    // an adapter runs once per fetch, so freezing them meant a row kept a live Accept button
    // and a stale "expires in 3 min" for as long as the list went un-refetched — the very 409
    // the actionability guard exists to prevent. The screen recomputes both against a ticking
    // clock instead.
    offerId: enrollment.latest_offer_id ?? undefined,
    offerStatus: enrollment.latest_offer_status ?? undefined,
    offerExpiresAt: enrollment.latest_offer_expires_at ?? undefined,
    serviceId: serviceId || undefined,
    partySize: enrollment.party_size ?? undefined,
    acceptableFrom: enrollment.acceptable_from ?? undefined,
  };
}

export function toWaitlistItems(
  enrollments: readonly WaitlistEnrollmentSummary[] | null | undefined
): WaitlistItem[] {
  return (enrollments ?? []).map(toWaitlistItem);
}
