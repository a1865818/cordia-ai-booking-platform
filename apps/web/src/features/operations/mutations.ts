/**
 * Operator follow-ups on booking operations, as TanStack mutations.
 *
 * All four inherit `retry: false`: none of these endpoints takes an idempotency key, so a
 * write is never re-sent behind the operator's back. None is optimistic either — every one
 * of them can legitimately 409 (the platform may move the operation between render and
 * click), so the row is left alone until the server answers and the board is refetched on
 * both outcomes (Inv-16). The queue's local "clear/dismiss" is deliberately NOT here: it is
 * an acknowledgement in the operator's own view, not a server action.
 */

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../../api/queryKeys";
import { isApiError } from "../../api/problem";
import {
  requestOperationCancellation,
  resolveOperation,
  respondToAlternatives,
  resumeOperation,
  type ResolveOperationInput,
} from "./api";
import { replayDeadLetterMessage, type ReplayDeadLetterInput } from "./deadLetters";
import {
  cancelPreorder,
  convertPreorderToStandalone,
  type CancelPreorderInput,
  type ConvertPreorderToStandaloneInput,
} from "./preorderRecovery";

/** Refetches after any follow-up: the operation moved (or refused to), either way the board is stale. */
function useInvalidateOperations(alsoBookings = false) {
  const queryClient = useQueryClient();
  return () => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.operations.all() });
    if (alsoBookings) {
      void queryClient.invalidateQueries({ queryKey: queryKeys.bookings.all() });
    }
  };
}

export interface CancelOperationVariables {
  operationId: string;
}

export function useRequestOperationCancellation() {
  const invalidate = useInvalidateOperations();
  return useMutation({
    mutationFn: ({ operationId }: CancelOperationVariables) =>
      requestOperationCancellation(operationId),
    onSettled: invalidate,
  });
}

export interface ResumeOperationVariables {
  operationId: string;
  /** The operator's own reason — recorded in the audit trail, never defaulted. */
  reasonCode: string;
}

export function useResumeOperation() {
  const invalidate = useInvalidateOperations();
  return useMutation({
    mutationFn: ({ operationId, reasonCode }: ResumeOperationVariables) =>
      resumeOperation(operationId, reasonCode),
    onSettled: invalidate,
  });
}

export interface ResolveOperationVariables extends ResolveOperationInput {
  operationId: string;
  reasonCode: string;
}

export function useResolveOperation() {
  // Resolving as booked records a booking id — the schedule is stale afterwards too.
  const invalidate = useInvalidateOperations(true);
  return useMutation({
    mutationFn: ({ operationId, reasonCode, resolution, providerRef }: ResolveOperationVariables) =>
      resolveOperation(operationId, { resolution, providerRef }, reasonCode),
    onSettled: invalidate,
  });
}

export interface AlternativesVariables {
  operationId: string;
  response: "waitlist" | "decline";
}

export function useRespondToAlternatives() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ operationId, response }: AlternativesVariables) =>
      respondToAlternatives(operationId, response),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.operations.all() });
      // A waitlist answer enrols the guest, so the waitlist board is stale as well.
      void queryClient.invalidateQueries({ queryKey: queryKeys.waitlist.all() });
    },
  });
}

// ---------------------------------------------------------------------------
// Operator recovery tools (DLQ replay · pre-order resolution).
//
// Same rules as the follow-ups above: no idempotency key on any of them, so `retry: false`
// is inherited and nothing is ever re-sent behind the operator's back; none is optimistic,
// because each can legitimately 409 when the platform moved first.
// ---------------------------------------------------------------------------

export interface ReplayDeadLetterVariables extends ReplayDeadLetterInput {}

/**
 * Replays a quarantined message.
 *
 * Invalidates operations because a readmitted message may advance one — but the caller must
 * still read `decision` from the result: `AckedWithoutReadmit` means nothing was dispatched,
 * and reporting it as "replayed" would assert something that did not happen (Inv-16).
 */
export function useReplayDeadLetterMessage() {
  const invalidate = useInvalidateOperations();
  return useMutation({
    mutationFn: (variables: ReplayDeadLetterVariables) => replayDeadLetterMessage(variables),
    onSettled: invalidate,
  });
}

export interface ConvertPreorderVariables extends ConvertPreorderToStandaloneInput {}

/** Keeps the pre-order alive without the booking — requires the customer's grant (§21.4). */
export function useConvertPreorderToStandalone() {
  // Converting detaches the pre-order from the booking, so the schedule is stale too.
  const invalidate = useInvalidateOperations(true);
  return useMutation({
    mutationFn: (variables: ConvertPreorderVariables) => convertPreorderToStandalone(variables),
    onSettled: invalidate,
  });
}

export interface CancelPreorderVariables extends CancelPreorderInput {}

/** The "or cancel" exit from a parked pre-order. No grant — nothing is being kept. */
export function useCancelPreorder() {
  const invalidate = useInvalidateOperations(true);
  return useMutation({
    mutationFn: (variables: CancelPreorderVariables) => cancelPreorder(variables),
    onSettled: invalidate,
  });
}

/** Operator-facing message for a failed follow-up. */
export function operationActionFailureMessage(error: unknown, action: string): string {
  // A 409 is not a bug here: the platform moved the operation between render and click —
  // the guard exists server-side precisely for this race. The refetch shows where it went.
  if (isApiError(error) && error.status === 409) {
    return "This operation moved on while you were looking — the board has been refreshed.";
  }
  if (isApiError(error) && error.status === 404) {
    return "This operation no longer exists.";
  }
  if (isApiError(error) && error.status === 403) {
    return "The platform refused this action for your operator identity — it was not performed.";
  }
  return error instanceof Error && error.message ? error.message : `Could not ${action}`;
}
