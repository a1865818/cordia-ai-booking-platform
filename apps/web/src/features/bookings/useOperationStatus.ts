/**
 * Polls a booking operation to its terminal state (D7, Inv-16).
 *
 * The UI must render what the server says, never an optimistic guess: a submission that
 * returned 202 is *accepted*, not *booked*. This hook is the only thing entitled to
 * decide a booking succeeded, and only when the server reports `terminal` with a
 * `booked` status.
 */

import { useQuery } from "@tanstack/react-query";
import { queryKeys } from "../../api/queryKeys";
import { getOperationStatus, type OperationStatusView } from "./api";

/** Interval floor; backs off as an operation takes longer. */
export const BASE_INTERVAL_MS = 1000;
export const MAX_INTERVAL_MS = 5000;
/** After this long the UI stops polling and tells the operator it is still working. */
export const POLL_TIMEOUT_MS = 60_000;

export function nextInterval(elapsedMs: number): number {
  // Linear-ish backoff: 1s for the first few seconds, easing to 5s.
  const step = Math.floor(elapsedMs / 10_000);
  return Math.min(BASE_INTERVAL_MS * (step + 1), MAX_INTERVAL_MS);
}

export function isTerminal(status: OperationStatusView | undefined): boolean {
  return status?.terminal === true;
}

/** True only when the server itself reports the booking as booked. */
export function isBooked(status: OperationStatusView | undefined): boolean {
  return status?.status === "booked";
}

export interface OperationPollState {
  status: OperationStatusView | undefined;
  isPolling: boolean;
  /** Set when polling stopped without reaching a terminal state. */
  timedOut: boolean;
  error: unknown;
}

export function useOperationStatus(
  operationId: string | null,
  startedAt: number | null
): OperationPollState {
  const query = useQuery({
    queryKey: queryKeys.operations.status(operationId ?? ""),
    queryFn: ({ signal }) => getOperationStatus(operationId!, signal),
    enabled: Boolean(operationId),
    refetchInterval: (q) => {
      const data = q.state.data;
      if (isTerminal(data)) return false;
      if (startedAt !== null && Date.now() - startedAt > POLL_TIMEOUT_MS) return false;
      return nextInterval(startedAt === null ? 0 : Date.now() - startedAt);
    },
    // Polling supersedes the shared stale time — a pending operation is always stale.
    staleTime: 0,
  });

  const elapsed = startedAt === null ? 0 : Date.now() - startedAt;
  const terminal = isTerminal(query.data);

  return {
    status: query.data,
    isPolling: Boolean(operationId) && !terminal && elapsed <= POLL_TIMEOUT_MS && !query.isError,
    timedOut: Boolean(operationId) && !terminal && elapsed > POLL_TIMEOUT_MS,
    error: query.error,
  };
}

/**
 * Operator-facing wording for a normalized operation status (D2 — the 9 cross-service
 * values). Deliberately distinguishes "submitting" from "confirmed" (design §2.6): the
 * console must never tell staff a booking exists before the platform says so.
 */
export const NORMALIZED_STATUS_COPY: Record<string, string> = {
  accepted: "Received — working on it",
  in_progress: "Still confirming",
  awaiting_user_action: "Waiting on the guest",
  alternatives_required: "That time isn't available",
  booked: "Confirmed",
  waitlisted: "Added to the waitlist",
  rejected: "Declined",
  expired: "Expired",
  failed: "Couldn't complete",
};

/**
 * Wording for a **cancellation** operation.
 *
 * D1 defines no `cancelled` operation status, so a successful cancel terminates at `booked`
 * — the generic "this operation succeeded" terminal, with the *booking* carrying the
 * cancellation. Echoing the create wording would tell an operator their cancellation was
 * "Confirmed", which reads as the opposite of what happened.
 */
const CANCEL_STATUS_COPY: Record<string, string> = {
  accepted: "Cancellation received",
  in_progress: "Cancelling",
  awaiting_user_action: "Waiting on the guest",
  booked: "Cancelled",
  rejected: "Cancellation declined",
  expired: "Cancellation expired",
  failed: "Couldn't cancel",
  // D2's status union is shared by every operation type, so a cancel *can* carry these even
  // though they are meaningless for one. Falling back to the create copy would tell an
  // operator cancelling a booking "That time isn't available" — better to send them to the
  // queue than to print nonsense.
  alternatives_required: "Cancellation needs review — check the queue",
  waitlisted: "Cancellation needs review — check the queue",
};

export type OperationKind = "create" | "cancel" | "reschedule";

export function normalizedStatusLabel(
  status: string | null | undefined,
  kind: OperationKind = "create"
): string {
  if (!status) return kind === "cancel" ? "Cancelling" : "Working on it";

  const copy = kind === "cancel" ? CANCEL_STATUS_COPY : NORMALIZED_STATUS_COPY;
  return copy[status] ?? NORMALIZED_STATUS_COPY[status] ?? status.split("_").join(" ");
}
