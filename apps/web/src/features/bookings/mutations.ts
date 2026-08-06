/**
 * The console's write flows as TanStack mutations (`docs/frontend-api-wiring.md` §8).
 *
 * Booking writes are `202 Accepted` + poll-to-terminal, not simple POSTs, so the whole
 * submit→poll sequence lives inside `mutationFn`: the mutation is only "settled" once the
 * server has reached a terminal state. That keeps `isPending` honest — it stays true for as
 * long as the operation is genuinely in flight — and puts cache invalidation in exactly one
 * place (`onSettled`) instead of at every call site.
 *
 * Mutations inherit `retry: false` from the shared `QueryClient`: a write must never be
 * silently re-sent behind the operator's back.
 */

import { useEffect, useRef, type Dispatch, type SetStateAction } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../../api/queryKeys";
import { getActor } from "../../api/config";
import {
  isApiBookingConflictError,
  isApiError,
  isApiIdempotencyConflictError,
  isApiVerificationRequiredError,
} from "../../api/problem";
import { getBookingDetails, getOperationStatus, type OperationStatusView } from "./api";
import { getOrCreateAttempt, resetAttempt } from "./identity";
import { submitCancellation } from "./submit";
import { POLL_TIMEOUT_MS, nextInterval } from "./useOperationStatus";
import {
  acceptOffer, cancelEnrollment, declineOffer, enrollGuest, type EnrollGuestInput,
} from "../waitlist/api";
import type { WaitlistItem } from "../../data/console";

/**
 * Polls an operation until the server calls it terminal, or the budget runs out.
 * Returns the last status seen so callers report the truth rather than a guess (Inv-16).
 */
export async function pollToTerminal(
  operationId: string,
  signal?: AbortSignal
): Promise<OperationStatusView> {
  const startedAt = Date.now();

  for (;;) {
    const status = await getOperationStatus(operationId, signal);
    if (status.terminal) return status;

    const elapsed = Date.now() - startedAt;
    if (elapsed > POLL_TIMEOUT_MS) return status;

    // Abort has to clear the pending timer, not just skip the next fetch — otherwise the
    // loop keeps firing for the rest of the 60s budget after the operator navigates away.
    // The listener is removed every iteration: `once` only fires-and-forgets, so without this
    // a 60s poll leaves ~25 dead listeners on a signal that outlives the poll.
    await new Promise<void>((resolve, reject) => {
      let onAbort: (() => void) | undefined;
      const timer = setTimeout(() => {
        if (onAbort) signal?.removeEventListener("abort", onAbort);
        resolve();
      }, nextInterval(elapsed));

      onAbort = () => {
        clearTimeout(timer);
        reject(signal!.reason instanceof Error ? signal!.reason : new Error("Polling aborted"));
      };
      signal?.addEventListener("abort", onAbort, { once: true });
    });
  }
}

/**
 * True when the failure means this attempt is spent and a **new** operation identity is
 * required — a stale version (the booking moved) or an idempotency conflict (the key is
 * already bound to a different payload, so replaying it can never succeed).
 */
export function isRetryableWithNewAttempt(error: unknown): boolean {
  // An already-cancelled booking is the one 409 a fresh attempt cannot fix: minting a new key
  // just produces another operation that 409s the same way, forever.
  if (isApiBookingConflictError(error) && error.isTerminal) return false;
  return isApiIdempotencyConflictError(error) || (isApiError(error) && error.status === 409);
}

export function writeFailureMessage(error: unknown, action: string): string {
  if (isApiVerificationRequiredError(error)) {
    return "This booking needs the guest verified first — open it and verify to continue.";
  }
  if (isApiIdempotencyConflictError(error)) {
    return "That request was already submitted with different details — start a new one.";
  }
  if (isApiBookingConflictError(error)) return error.message;
  if (isApiError(error) && error.status === 409) {
    return "This booking changed while you were looking — reloading";
  }
  return error instanceof Error ? error.message : `Could not ${action}`;
}

// ---------------------------------------------------------------------------
// Cancel
// ---------------------------------------------------------------------------

export interface CancelBookingVariables {
  bookingId: string;
  /**
   * A server-issued `cancel` grant (spec 08, D5). **Required** — `grant_id` is `NotEmpty`-validated
   * on the endpoint, so submitting without one is a 400 the operator cannot act on, not a 403 they
   * can resolve. The grant is therefore obtained BEFORE the first submit, never recovered from a
   * rejection.
   */
  grantId: string;
}

export interface CancelBookingResult {
  status: OperationStatusView | null;
  /** True when the server reached a terminal state within the polling budget. */
  settled: boolean;
  /**
   * True only when the server reported the cancellation terminal **and** successful. A cancel
   * can settle as `rejected`/`failed`, which is a settled *refusal* — callers must not treat
   * `settled` alone as "it worked" (Inv-16).
   */
  cancelled: boolean;
}

/**
 * Cancels a booking: read the authoritative version → submit → poll to terminal.
 *
 * The whole sequence is the mutation, so `isPending` covers the real duration and the caller
 * cannot accidentally report success before the platform has decided (Inv-16).
 */
export function useCancelBooking() {
  const queryClient = useQueryClient();
  const abortRef = useRef<AbortController | null>(null);

  // Stop polling when the screen goes away; the server keeps working either way.
  useEffect(() => () => abortRef.current?.abort(), []);

  return useMutation({
    mutationFn: async ({ bookingId, grantId }: CancelBookingVariables): Promise<CancelBookingResult> => {
      const attemptKey = `cancel:${bookingId}`;

      // The grant travels on the READ as well as the write. `GET /api/Bookings/{id}` answers
      // `requires_verification` as a typed 403 for a gated booking, so omitting it here meant
      // the operator completed step-up and the cancellation still failed — on the version
      // lookup, before the cancellation was ever submitted.
      const details = await getBookingDetails(bookingId, grantId);
      const version = details.booking?.version;
      if (version === null || version === undefined) {
        throw new Error(
          "This booking has no confirmed record yet, so it cannot be cancelled. Try again once it is confirmed."
        );
      }

      const identity = getOrCreateAttempt(attemptKey);
      const accepted = await submitCancellation({
        bookingId,
        expectedBookingVersion: version,
        identity,
        identityRef: getActor(),
        readBackVersion: `cancel|${bookingId}|${version}`,
        reasonCode: "operator_request",
        grantId,
      });

      const operationId = accepted.operation_id;
      if (!operationId) {
        // Accepted, but with nothing to track. We have no terminal answer, so we claim none —
        // reporting this as settled would put a "Cancelling" label on a finished mutation.
        return { status: null, settled: false, cancelled: false };
      }

      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;

      let status: OperationStatusView;
      try {
        status = await pollToTerminal(operationId, controller.signal);
      } catch (error) {
        // Abandoned, not failed: keep the attempt identity so a retry replays the same key
        // against the same payload (Inv-2) rather than minting a second operation.
        if (controller.signal.aborted) return { status: null, settled: false, cancelled: false };
        throw error;
      }

      resetAttempt(attemptKey);
      return {
        status,
        settled: status.terminal === true,
        cancelled: status.terminal === true && status.status === "booked",
      };
    },
    onError: (error, { bookingId }) => {
      if (isRetryableWithNewAttempt(error)) resetAttempt(`cancel:${bookingId}`);
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.bookings.all() });
      void queryClient.invalidateQueries({ queryKey: queryKeys.operations.all() });
    },
  });
}

// ---------------------------------------------------------------------------
// Waitlist removal — the textbook optimistic case
// ---------------------------------------------------------------------------

export interface RemoveWaitlistVariables {
  enrollmentId: string;
  /** Current rows, so a failure can put the removed one back exactly where it was. */
  current: WaitlistItem[];
}

/**
 * Removes a guest from the waitlist optimistically, rolling back on failure.
 *
 * `onMutate` snapshots and applies the removal, `onError` restores the snapshot, `onSettled`
 * refetches — so the board stays responsive but never claims a removal that did not happen.
 */
export function useRemoveFromWaitlist(applyRows: Dispatch<SetStateAction<WaitlistItem[]>>) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (variables: RemoveWaitlistVariables) => cancelEnrollment(variables.enrollmentId),
    onMutate: async (variables) => {
      // Stop an in-flight refetch from overwriting the optimistic removal.
      await queryClient.cancelQueries({ queryKey: queryKeys.waitlist.all() });

      const index = variables.current.findIndex((row) => row.id === variables.enrollmentId);
      const removed = index === -1 ? null : variables.current[index];

      applyRows((rows) => rows.filter((row) => row.id !== variables.enrollmentId));
      return { removed, index };
    },
    onError: (_error, variables, context) => {
      // Put back only *this* row, where it was. Restoring the whole pre-removal snapshot
      // would resurrect rows that other, already-succeeded removals legitimately took out,
      // showing a guest as still waiting after the server confirmed they were removed.
      const removed = context?.removed;
      if (!removed) return;
      const index = context?.index ?? 0;

      applyRows((rows) => {
        if (rows.some((row) => row.id === variables.enrollmentId)) return rows;
        const next = rows.slice();
        next.splice(Math.min(index, next.length), 0, removed);
        return next;
      });
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.waitlist.all() });
    },
  });
}

// ---------------------------------------------------------------------------
// Waitlist enrolment
// ---------------------------------------------------------------------------

/**
 * Enrols a guest on the waitlist.
 *
 * Not optimistic: the server is the only party that can accept an enrolment, and only the
 * refetched list can say what was recorded — `onSettled` refetches on both outcomes.
 *
 * `retry: false` is still inherited, but the reason changed. The drawer now mints a key for the
 * attempt and sends it, so an automatic replay could no longer duplicate the guest; it is left
 * off because a retry should be the operator's decision when the outcome is unknown, not the
 * client's.
 */
export function useEnrollGuest() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: EnrollGuestInput) => enrollGuest(input),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.waitlist.all() });
    },
  });
}

/** Operator-facing message for a failed enrolment. */
export function enrollFailureMessage(error: unknown): string {
  return error instanceof Error && error.message
    ? error.message
    : "Could not add this guest to the waitlist";
}

// ---------------------------------------------------------------------------
// Waitlist offers
// ---------------------------------------------------------------------------

export interface OfferDecisionVariables {
  offerId: string;
  /** The enrolled guest, for the toast. */
  name: string;
}

/**
 * Accepts or declines a live waitlist offer.
 *
 * **Not** optimistic, unlike removal. Accepting finalizes a capacity hold and confirms a
 * booking server-side (spec 11 §5.1); declining releases the hold and re-matches the next
 * eligible guest. Both can legitimately 409 — the offer may have expired or been answered by
 * the guest in the meantime — so the row is left alone until the server has actually decided,
 * and the list is refetched either way (Inv-16).
 *
 * Every waitlist action is audited against `X-Actor`, which the API layer attaches.
 */
function useOfferDecision(decide: (offerId: string) => Promise<unknown>) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (variables: OfferDecisionVariables) => decide(variables.offerId),
    onSettled: () => {
      // Both outcomes change the offer's state and can change capacity, so the board and
      // the schedule are both stale afterwards.
      void queryClient.invalidateQueries({ queryKey: queryKeys.waitlist.all() });
      void queryClient.invalidateQueries({ queryKey: queryKeys.bookings.all() });
    },
  });
}

export function useAcceptOffer() {
  return useOfferDecision(acceptOffer);
}

export function useDeclineOffer() {
  return useOfferDecision(declineOffer);
}

/** Operator-facing message for a failed offer decision. */
export function offerFailureMessage(error: unknown, action: "accept" | "decline"): string {
  // A 409 here is not a bug: the offer was answered or expired between render and click.
  if (isApiError(error) && error.status === 409) {
    return "That offer is no longer live — the guest answered it or it expired.";
  }
  if (isApiError(error) && error.status === 404) {
    return "That offer no longer exists.";
  }
  return error instanceof Error ? error.message : `Could not ${action} this offer`;
}
