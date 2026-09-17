/**
 * Notification delivery state for the booking drawer (#83, spec 10).
 *
 * The rule this module exists to keep: the console reports what the platform RECORDED, never what
 * it hopes happened. A booking with no notification row shows nothing; `accepted` means a provider
 * took the message, which is not the same as the guest receiving it; and a missing row is never
 * rendered as "sent".
 */

import { useQuery } from "@tanstack/react-query";
import { queryKeys } from "../../api/queryKeys";
import {
  getBookingNotifications,
  getOperationNotifications,
  type NotificationDelivery,
} from "./api";

export type DeliveryTone = "good" | "pending" | "bad" | "muted";

export interface DeliveryLabel {
  text: string;
  tone: DeliveryTone;
}

/**
 * Every server state, spelled out. Unknown states fall through to their raw value rather than to a
 * reassuring default, so a state added on the server can never be displayed as a success.
 */
export function deliveryLabel(state: string | null | undefined): DeliveryLabel {
  switch (state) {
    case "delivered":
      return { text: "Delivered", tone: "good" };
    case "accepted":
      // The provider accepted it. Whether the guest's handset did is not yet known.
      return { text: "Sent, awaiting receipt", tone: "pending" };
    case "pending":
      return { text: "Queued", tone: "pending" };
    case "sending":
      return { text: "Sending", tone: "pending" };
    case "failed_transient":
      return { text: "Retrying", tone: "pending" };
    case "failed_permanent":
      return { text: "Failed", tone: "bad" };
    case "invalid_recipient":
      return { text: "No valid destination", tone: "bad" };
    case "suppressed_duplicate":
      return { text: "Not sent (duplicate)", tone: "muted" };
    default:
      return { text: state ? state.replace(/_/g, " ") : "Unknown", tone: "muted" };
  }
}

const EVENT_LABELS: Record<string, string> = {
  request_accepted: "Request received",
  still_pending: "Still confirming",
  action_required: "Action required",
  booking_confirmed: "Booking confirmation",
  no_capacity_alternatives: "Alternatives offered",
  waitlist_offer: "Waitlist offer",
  waitlist_offer_expired: "Waitlist offer expired",
  preorder_cancelled: "Pre-order cancelled",
  final_failure: "Booking could not be made",
  cancel_or_modify_confirmed: "Change confirmed",
};

export function eventLabel(event: string | null | undefined): string {
  if (!event) return "Message";
  return EVENT_LABELS[event] ?? event.replace(/_/g, " ");
}

/**
 * Loads the delivery rows for the drawer's subject. `isBooking === false` means `id` is an
 * operation id (the request has not become a booking), so the operation route is used.
 */
export function useNotificationDelivery(id: string | null | undefined, isBooking: boolean, enabled: boolean) {
  return useQuery({
    queryKey: isBooking
      ? queryKeys.bookings.notifications(id ?? "")
      : queryKeys.operations.notifications(id ?? ""),
    queryFn: ({ signal }) =>
      isBooking ? getBookingNotifications(id!, signal) : getOperationNotifications(id!, signal),
    enabled: enabled && Boolean(id),
    select: (response): NotificationDelivery[] => response?.notifications ?? [],
  });
}
