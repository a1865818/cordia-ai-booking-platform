/**
 * Adapts operation summaries to the `QueueItem` shape `QueueScreen` already renders.
 *
 * `statusCopy` is the single place raw domain enums become operator language (N2): the
 * board must never show `compensation_required` to a venue owner.
 */

import type { QueueItem } from "../../data/console";
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

/** Operator-facing wording for the operation types a queue row can carry. */
const OPERATION_TYPE_COPY: Record<string, string> = {
  create: "New booking",
  modify: "Change",
  reschedule: "Reschedule",
  cancel: "Cancellation",
};

/**
 * A short, stable handle for a row.
 *
 * The queue's `name` column reads as a guest name, and `OperationSummary` carries no guest.
 * A booking reference is the closest thing to a human handle; failing that a truncated
 * operation id is at least honestly an id, rather than a 36-character UUID dressed up as a
 * person.
 */
function rowHandle(operation: OperationSummary): string {
  if (operation.booking_id) return operation.booking_id;
  const id = operation.operation_id ?? operation.intent_id ?? "";
  return id.length > 12 ? `${id.slice(0, 8)}…` : id;
}

/**
 * Maps an operation to a queue row.
 *
 * Everything here comes from the projection. `retries` and `channel` are deliberately
 * **absent**: `OperationSummary` models neither, and the screen previously showed
 * "0 retries", a `web` channel badge and a waiting time computed as `retries * 9 + 8`
 * minutes — three fabrications rendered next to real data with no badge.
 */
export function toQueueItem(operation: OperationSummary): QueueItem {
  return {
    id: operation.operation_id ?? "",
    name: rowHandle(operation),
    service: OPERATION_TYPE_COPY[operation.operation_type ?? ""] ?? operation.operation_type ?? "",
    slot: operation.updated_at ? relativeAge(operation.updated_at) : "",
    reason: operation.status_reason ?? operationStatusLabel(operation.status),
    // The raw status, not the label: a dismissal is keyed to it, so it must change exactly when
    // the platform moves the operation (e.g. manual_review -> compensation_required).
    status: operation.status ?? undefined,
    transcript: null,
  };
}

/**
 * How long ago the platform last touched the operation — the only duration the projection
 * supports. It replaces the invented "waiting" time, which was `MOCK_WAIT[id] ?? retries*9+8`
 * and therefore read "8m waiting" on every live row.
 */
export function relativeAge(iso: string, now: number = Date.now()): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "";

  const minutes = Math.max(0, Math.floor((now - then) / 60_000));
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ${minutes % 60}m ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

export function toQueueItems(operations: readonly OperationSummary[] | null | undefined): QueueItem[] {
  return (operations ?? []).map(toQueueItem);
}
