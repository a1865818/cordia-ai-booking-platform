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
    // The server refuses to waitlist a pre-order (spec 11 §21.4) — the flag decides
    // whether that answer may be offered at all.
    isPreorder: operation.is_preorder === true,
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

/**
 * The five terminal members of the ratified 16-value set (D1). A follow-up on any of them
 * is a certain 409 — the server computes `Terminal` from exactly this set.
 */
export const TERMINAL_OPERATION_STATUSES: ReadonlySet<string> = new Set([
  "booked",
  "rejected",
  "expired",
  "waitlisted",
  "failed_final",
]);

export interface OperationActions {
  /** Cancellation may be requested from any known non-terminal status. */
  canRequestCancellation: boolean;
  /** Resume and resolve are `manual_review`-only (server-guarded; 409 otherwise). */
  canResume: boolean;
  canResolve: boolean;
  /** Waitlist/decline answers are `alternatives_required`-only. */
  canRespondAlternatives: boolean;
  /**
   * The waitlist answer specifically. Narrower than `canRespondAlternatives`: the server
   * unconditionally refuses to route a **pre-order** operation to the waitlist
   * (spec 11 §21.4 — only re-confirm or decline apply), so on a pre-order row that button
   * would be a certain 409 and must not be offered. Decline stays available.
   */
  canWaitlistAlternatives: boolean;
}

const NO_OPERATION_ACTIONS: OperationActions = {
  canRequestCancellation: false,
  canResume: false,
  canResolve: false,
  canRespondAlternatives: false,
  canWaitlistAlternatives: false,
};

/**
 * Which follow-ups the row's CURRENT status permits (docs §10) — so the board never
 * offers a button whose call is certain to fail. Decided from the list's raw 16-value
 * status plus its pre-order flag; the polling endpoint's normalized projection collapses
 * `manual_review` into `in_progress` and cannot drive this. An absent status permits
 * nothing: eligibility is a claim about the operation, and with no status there is nothing
 * to claim it from. The server re-checks every guard regardless — this only removes
 * certain failures.
 */
export function operationActions(
  status: string | null | undefined,
  isPreorder = false
): OperationActions {
  if (typeof status !== "string" || status.length === 0) {
    return NO_OPERATION_ACTIONS;
  }

  const alternatives = status === "alternatives_required";
  return {
    canRequestCancellation: !TERMINAL_OPERATION_STATUSES.has(status),
    canResume: status === "manual_review",
    canResolve: status === "manual_review",
    canRespondAlternatives: alternatives,
    canWaitlistAlternatives: alternatives && !isPreorder,
  };
}
