/**
 * Adapts operation summaries to the `QueueItem` shape `QueueScreen` already renders.
 *
 * `statusCopy` is the single place raw domain enums become operator language (N2): the
 * board must never show `compensation_required` to a venue owner.
 */

import type { Channel, QueueItem } from "../../data/console";
import type { OperationSummary } from "./api";

/** Plain-language label for each of the 16 ratified `operation_status` values (D1). */
export const OPERATION_STATUS_COPY: Record<string, string> = {
  accepted: "Received — starting",
  validating: "Checking the details",
  awaiting_user_action: "Waiting on the guest",
  reserving_capacity: "Holding a slot",
  held: "Slot held",
  awaiting_payment: "Waiting on deposit",
  finalizing: "Confirming with the venue",
  booked: "Confirmed",
  alternatives_required: "No slot — needs a new time",
  reconciling: "Checking what happened",
  compensation_required: "Needs attention — refund pending",
  manual_review: "Needs your review",
  rejected: "Declined",
  expired: "Expired",
  waitlisted: "On the waitlist",
  failed_final: "Couldn't complete",
};

export function operationStatusLabel(status: string | null | undefined): string {
  if (!status) return "Unknown";
  // `split/join` rather than `replaceAll`: the app targets ES2020.
  return OPERATION_STATUS_COPY[status] ?? status.split("_").join(" ");
}

/**
 * Maps an operation to a queue row. The API models no channel on an operation, so
 * `channel` is set to `web` rather than guessed per row — the accurate per-channel view
 * lives on the Conversations screen.
 */
export function toQueueItem(operation: OperationSummary): QueueItem {
  return {
    id: operation.operation_id ?? "",
    name: operation.booking_id ?? operation.intent_id ?? operation.operation_id ?? "",
    service: operation.operation_type ?? "create",
    slot: operation.updated_at ? new Date(operation.updated_at).toLocaleString() : "",
    reason: operation.status_reason ?? operationStatusLabel(operation.status),
    // The raw status, not the label: a dismissal is keyed to it, so it must change exactly when
    // the platform moves the operation (e.g. manual_review -> compensation_required).
    status: operation.status ?? undefined,
    // Attempt count is not projected on the summary; the operation detail owns that.
    retries: 0,
    channel: "web" as Channel,
    transcript: null,
  };
}

export function toQueueItems(operations: readonly OperationSummary[] | null | undefined): QueueItem[] {
  return (operations ?? []).map(toQueueItem);
}
