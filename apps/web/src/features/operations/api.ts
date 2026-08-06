import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type ListOperationsResponse =
  paths["/api/booking-operations"]["get"]["responses"][200]["content"]["application/json"];

export type OperationSummary = NonNullable<ListOperationsResponse["operations"]>[number];

export interface OperationFilters {
  /** Snake_case values from the ratified 16-value set (D1). An unknown value is a 400. */
  statuses?: string[];
  /** Shorthand for the states waiting on a person or no longer progressing on their own. */
  needsAttention?: boolean;
  limit?: number;
}

export function listOperations(filters: OperationFilters = {}, signal?: AbortSignal) {
  const params = new URLSearchParams();
  for (const status of filters.statuses ?? []) params.append("status", status);
  if (filters.needsAttention) params.set("needs_attention", "true");
  if (filters.limit !== undefined) params.set("limit", String(filters.limit));

  const query = params.toString();
  return fetchApi<ListOperationsResponse>(
    query ? `/api/booking-operations?${query}` : "/api/booking-operations",
    { signal }
  );
}

// ---------------------------------------------------------------------------
// Operator follow-ups (docs/frontend-api-wiring.md §10).
//
// None of these carries an idempotency key — repeats are guarded by the server's own
// status checks (a wrong-status call is a 409, cancel is an idempotent flag) — so none may
// ever be retried automatically. All four return anonymous 202 bodies the generated
// contract cannot type; `statusFromResponse` reads the echoed status without trusting it.
// ---------------------------------------------------------------------------

/**
 * Asks the platform to stop a non-terminal operation at its next safe checkpoint.
 *
 * Deliberately no actor headers: the endpoint reads none. This *requests* cancellation
 * (202 `cancellation_pending`) — it does not cancel a booked operation, and callers must
 * not report it as done (Inv-16).
 */
export function requestOperationCancellation(operationId: string) {
  return fetchApi<unknown>(
    `/api/booking-operations/${encodeURIComponent(operationId)}/cancel`,
    { method: "POST" }
  );
}

/**
 * Sends a `manual_review` operation back to the platform to re-check (→ `reconciling`).
 *
 * The operator spine is mandatory and fail-closed server-side: `X-Actor`, `X-Actor-Role`
 * and `X-Reason-Code` all travel, and the reason is the operator's own statement — never
 * defaulted here. `{}` body: the server mints the replay command id.
 */
export function resumeOperation(operationId: string, reasonCode: string) {
  return fetchApi<unknown>(
    `/api/booking-operations/${encodeURIComponent(operationId)}/resume`,
    { method: "POST", actor: true, reasonCode, body: JSON.stringify({}) }
  );
}

export interface ResolveOperationInput {
  /** The only two legal exits from manual review (spec 06 §11). */
  resolution: "booked" | "failed_final";
  /** The provider's booking reference — required by the server when resolving as booked. */
  providerRef?: string;
}

/**
 * Resolves a `manual_review` operation to one of its two legal exits.
 *
 * `booked` requires the provider's reference (the server rejects it otherwise — the
 * booking id the platform records IS this reference, so it can only come from the
 * operator who checked with the provider).
 */
export function resolveOperation(
  operationId: string,
  input: ResolveOperationInput,
  reasonCode: string
) {
  const body: Record<string, string> = { resolution: input.resolution };
  if (input.providerRef) body.provider_ref = input.providerRef;

  return fetchApi<unknown>(
    `/api/booking-operations/${encodeURIComponent(operationId)}/resolve`,
    { method: "POST", actor: true, reasonCode, body: JSON.stringify(body) }
  );
}

/**
 * Answers an `alternatives_required` operation for the guest: `waitlist` enrols them,
 * `decline` closes the operation as not booked. No operator headers — the endpoint is the
 * same one the guest-facing lane uses and reads none.
 */
export function respondToAlternatives(operationId: string, response: "waitlist" | "decline") {
  return fetchApi<unknown>(
    `/api/booking-operations/${encodeURIComponent(operationId)}/alternatives`,
    { method: "POST", body: JSON.stringify({ response }) }
  );
}

/**
 * The `status` a follow-up's 202 body echoed, if it did — the body is anonymous, so this
 * is runtime-checked rather than assumed. `null` means "the server did not say".
 */
export function statusFromResponse(body: unknown): string | null {
  if (typeof body !== "object" || body === null) return null;
  const status = (body as Record<string, unknown>).status;
  return typeof status === "string" && status.length > 0 ? status : null;
}
