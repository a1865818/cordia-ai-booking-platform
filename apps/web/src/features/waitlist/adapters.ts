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
  };
}

export function toWaitlistItems(
  enrollments: readonly WaitlistEnrollmentSummary[] | null | undefined
): WaitlistItem[] {
  return (enrollments ?? []).map(toWaitlistItem);
}
