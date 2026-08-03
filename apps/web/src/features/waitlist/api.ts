import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type ListWaitlistEnrollmentsResponse =
  paths["/api/waitlist/enrollments"]["get"]["responses"][200]["content"]["application/json"];

export type WaitlistEnrollmentSummary =
  NonNullable<ListWaitlistEnrollmentsResponse["enrollments"]>[number];

export function listEnrollments(
  options: { activeOnly?: boolean; limit?: number; signal?: AbortSignal } = {}
) {
  const params = new URLSearchParams();
  if (options.activeOnly !== undefined) params.set("active_only", String(options.activeOnly));
  if (options.limit !== undefined) params.set("limit", String(options.limit));

  const search = params.toString();
  return fetchApi<ListWaitlistEnrollmentsResponse>(
    search ? `/api/waitlist/enrollments?${search}` : "/api/waitlist/enrollments",
    { signal: options.signal }
  );
}

/**
 * Cancels an enrollment.
 *
 * Every waitlist mutation is audited against `X-Actor`, so the actor header is not
 * optional here — the API records who removed the guest.
 */
export function cancelEnrollment(enrollmentId: string) {
  return fetchApi<unknown>(`/api/waitlist/enrollments/${encodeURIComponent(enrollmentId)}`, {
    method: "DELETE",
    actor: true,
  });
}

export function acceptOffer(offerId: string) {
  return fetchApi<unknown>(`/api/waitlist/offers/${encodeURIComponent(offerId)}/accept`, {
    method: "POST",
    actor: true,
  });
}

export function declineOffer(offerId: string) {
  return fetchApi<unknown>(`/api/waitlist/offers/${encodeURIComponent(offerId)}/decline`, {
    method: "POST",
    actor: true,
  });
}
