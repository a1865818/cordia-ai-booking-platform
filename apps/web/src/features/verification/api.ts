/**
 * Step-up verification (spec 08, D5).
 *
 * A mutation on someone else's booking needs a **server-verified** grant, never a client
 * claim (Inv-11): the flow is request a code → the guest enters it → the server issues a
 * `grant_id`, which subsequent calls send as `X-Grant-Id`.
 */

import type { components, paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type RequestVerificationResponse =
  paths["/api/verifications"]["post"]["responses"][200]["content"]["application/json"];

export type CheckVerificationResponse =
  paths["/api/verifications/{verificationId}"]["get"]["responses"][200]["content"]["application/json"];

export type VerificationAction = components["schemas"]["VerificationActionEnum"];
export type VerificationChannel = components["schemas"]["VerificationRequestChannel"];

export interface RequestVerificationInput {
  action: VerificationAction;
  channel: VerificationChannel;
  customerId?: string;
  targetBookingId?: string;
  /** Stable per attempt so a re-render cannot send two codes. */
  idempotencyKey: string;
}

export function requestVerification(input: RequestVerificationInput) {
  return fetchApi<RequestVerificationResponse>("/api/verifications", {
    method: "POST",
    body: JSON.stringify({
      action: input.action,
      channel: input.channel,
      customer_id: input.customerId ?? "",
      target_booking_id: input.targetBookingId ?? "",
      verification_idempotency_key: input.idempotencyKey,
    }),
  });
}

export function checkVerification(verificationId: string, signal?: AbortSignal) {
  return fetchApi<CheckVerificationResponse>(
    `/api/verifications/${encodeURIComponent(verificationId)}`,
    { signal }
  );
}

/** Submits the code the guest read out. A wrong code returns `failed`, not an error. */
export function verifyCode(verificationId: string, code: string) {
  return fetchApi<CheckVerificationResponse>(
    `/api/verifications/${encodeURIComponent(verificationId)}/verify`,
    { method: "POST", body: JSON.stringify({ code }) }
  );
}
