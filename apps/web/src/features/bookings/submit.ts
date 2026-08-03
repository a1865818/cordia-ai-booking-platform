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

/** Durably accepts a confirmed create intent. Returns the operation to poll. */
export function submitBooking(request: SubmitConfirmedIntentRequest) {
  return fetchApi<OperationAccepted>("/api/Bookings", {
    method: "POST",
    body: JSON.stringify(request),
  });
}

/**
 * Pre-flight for a change: surfaces `requires_confirmation` / `requires_payment` / fees
 * *before* the operator commits (N5 — prevent the error rather than report it).
 */
export function validateChange(
  bookingId: string,
  input: { expectedBookingVersion: number; requestedFields: unknown; grantId?: string }
) {
  return fetchApi<ValidateChangeResponse>(
    `/api/Bookings/${encodeURIComponent(bookingId)}/changes:validate`,
    {
      method: "POST",
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
          verification_level: "staff_confirmed",
          confirmation_channel: "web",
          confirmed_at: confirmedAt,
          disclosures_presented: [],
        },
      }),
    }
  );
}

export { SCHEMA_VERSION, TENANT_POLICY_VERSION };

/**
 * Whether the platform can actually fulfil a reschedule.
 *
 * **Verified false today.** The acceptance endpoint accepts `operation: "reschedule"` (202),
 * but the fulfilment pipeline is create-only: nothing in
 * `Handlers/BookingFulfillment/**` branches on `OperationTypeEnum`, and
 * `revision.canonical_payload.target_booking_id` is never read by any handler
 * (`FinalizeBookingCommandHandler` calls `adapter.CreateBookingAsync` unconditionally).
 * A submitted reschedule therefore creates a **second, disconnected booking** rather than
 * moving the existing one.
 *
 * Submitting under those conditions would put a duplicate booking in the venue's system, so
 * the console does not submit. Everything else — payload construction, canonical hashing,
 * idempotency identity, polling — is built and tested; flip this constant when the
 * fulfilment engine handles operation types (spec 06/08) and the flow activates.
 */
export const RESCHEDULE_FULFILMENT_SUPPORTED = false;
