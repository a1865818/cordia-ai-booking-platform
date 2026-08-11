/**
 * The three booking write flows, all of which are `202 Accepted` + poll (D7) rather than
 * simple POSTs: the API durably accepts an intent and a background worker fulfils it.
 */

import type { components, paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";
import { canonicalHash } from "../../api/canonicalHash";
import type { AttemptIdentity } from "./identity";
import { SCHEMA_VERSION, TENANT_POLICY_VERSION } from "./buildSubmission";

type SubmitConfirmedIntentRequest = components["schemas"]["SubmitConfirmedIntentRequest"];

export type OperationAccepted =
  paths["/api/Bookings"]["post"]["responses"][202]["content"]["application/json"];

export type CancellationResponse =
  paths["/api/Bookings/{bookingId}/cancellations"]["post"]["responses"][202]["content"]["application/json"];

export type ValidateChangeResponse =
  paths["/api/Bookings/{bookingId}/changes:validate"]["post"]["responses"][200]["content"]["application/json"];

/**
 * Durably accepts a confirmed intent. Returns the operation to poll.
 *
 * A **create** needs no authorization. A **mutation of an existing booking** (reschedule /
 * modify / cancel, which carry a `target_booking_id`) does: the server requires either a
 * verification grant or an authenticated staff role, and refuses with 403 otherwise
 * (spec 08 §12, Inv-11). The console is the staff caller, so it sends `X-Actor` +
 * `X-Actor-Role`; the agent lane sends the grant it already holds.
 *
 * The headers ride on every call rather than only on mutations: they are inert for a create,
 * and a conditional here would be one more place to get the condition wrong.
 *
 * `grantId` is the OTHER half of that door, and it is not optional in practice. The asserted-role
 * path is gated on `Authorization:AllowAssertedStaffRole`, which defaults to Development only, so
 * outside Development a mutation that presents no grant is refused however good its actor headers
 * are. Without this parameter a caller that completed step-up had nowhere to put the grant: the
 * console could satisfy the booking READ and then be refused on the WRITE forever, re-prompting
 * for a fresh verification code on every attempt.
 */
export function submitBooking(request: SubmitConfirmedIntentRequest, grantId?: string) {
  return fetchApi<OperationAccepted>("/api/Bookings", {
    method: "POST",
    actor: true,
    grantId,
    body: JSON.stringify(request),
  });
}

/**
 * Pre-flight for a change: surfaces `stale_version` / `capacity_unavailable` / `policy_failed`
 * *before* the operator commits (N5 — prevent the error rather than report it).
 *
 * `actor: true` for the same reason `submitBooking` sends it: the console holds no verification
 * grant, and this endpoint takes a grant OR an authenticated staff role (spec 08 §12). Without
 * the headers the caller that most needs the pre-flight is the one that gets 403'd from running
 * it, and could only discover an invalid change by submitting it.
 */
export function validateChange(
  bookingId: string,
  input: { expectedBookingVersion: number; requestedFields: unknown; grantId?: string }
) {
  return fetchApi<ValidateChangeResponse>(
    `/api/Bookings/${encodeURIComponent(bookingId)}/changes:validate`,
    {
      method: "POST",
      actor: true,
      grantId: input.grantId,
      body: JSON.stringify({
        target_booking_id: bookingId,
        expected_booking_version: input.expectedBookingVersion,
        requested_fields: input.requestedFields,
        grant_id: input.grantId ?? "",
      }),
    }
  );
}

/**
 * NOTE on `POST /api/Bookings/{id}/modifications`: its request body
 * (`SubmitModificationInput`) is `additionalProperties: false` and carries **no field for
 * the changed data** — it confirms a revision that was already submitted. A reschedule
 * therefore goes through the acceptance front door (`POST /api/bookings` with
 * `operation: "reschedule"` and a `target_booking_id`), which is the only path that
 * actually transports the new time. See `SubmitConfirmedIntentValidator`, which requires
 * `target_booking_id` for exactly the modify/cancel/reschedule operations.
 */

export interface CancellationInput {
  bookingId: string;
  expectedBookingVersion: number;
  identity: AttemptIdentity;
  identityRef: string;
  readBackVersion: string;
  reasonCode: string;
  grantId?: string;
}

export async function submitCancellation(input: CancellationInput) {
  // The cancellation's canonical payload is the operation plus its target, so the
  // confirmation is bound to *this* cancellation rather than to any booking change.
  const { value: payloadHash } = await canonicalHash({
    operation: "cancel",
    target_booking_id: input.bookingId,
    reason_code: input.reasonCode,
  });
  // Minted with the attempt, NOT per call: the server hashes the whole request to decide
  // whether a replayed idempotency key carries the same payload, so a fresh timestamp would
  // turn a safe retry into an idempotency conflict (Inv-2).
  const confirmedAt = input.identity.confirmedAt;

  return fetchApi<CancellationResponse>(
    `/api/Bookings/${encodeURIComponent(input.bookingId)}/cancellations`,
    {
      method: "POST",
      grantId: input.grantId,
      body: JSON.stringify({
        operation_id: input.identity.operationId,
        cancellation_idempotency_key: input.identity.idempotencyKey,
        target_booking_id: input.bookingId,
        expected_booking_version: input.expectedBookingVersion,
        reason_code: input.reasonCode,
        grant_id: input.grantId ?? "",
        confirmation: {
          confirmation_id: input.identity.confirmationId,
          intent_id: input.identity.intentId,
          intent_revision: input.identity.revision,
          intent_payload_hash: payloadHash,
          identity: input.identityRef,
          source_turn_id: `console:${input.identity.operationId}`,
          read_back_version: input.readBackVersion,
          // Ratified vocabulary only — see the note in `buildSubmission.ts`. `staff_confirmed`
          // is not a member and was silently stored as `unverified`.
          verification_level: "unverified",
          confirmation_channel: "web",
          confirmed_at: confirmedAt,
          disclosures_presented: [],
        },
      }),
    }
  );
}

export { SCHEMA_VERSION, TENANT_POLICY_VERSION };
