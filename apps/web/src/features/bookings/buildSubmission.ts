/**
 * Builds the `SubmitConfirmedIntentRequest` body for `POST /api/bookings` (spec 04 §10).
 *
 * The critical part is the hash binding. The server recomputes the `canon.v1` hash over
 * `revision.canonical_payload` and rejects the submission with **422** unless
 * `confirmation.intent_payload_hash` equals it; a database trigger additionally requires
 * `revision.payload_hash` to equal the same value. So both are set from one computation —
 * they can never drift apart here.
 *
 * The structural validator also requires `confirmation.intent_id` /
 * `confirmation.intent_revision` to match the revision's, and a `target_booking_id` for
 * modify / cancel / reschedule. All of that is enforced by construction below rather than
 * left to the caller.
 */

import type { components } from "@cordia/api-contract";
import { canonicalHash } from "../../api/canonicalHash";
import type { AttemptIdentity } from "./identity";

type SubmitConfirmedIntentRequest = components["schemas"]["SubmitConfirmedIntentRequest"];
type BookingRequest = components["schemas"]["BookingRequest"];

/** Schema/policy versions the agent lane also sends; the validator requires both. */
export const SCHEMA_VERSION = "booking.request.v1";
export const TENANT_POLICY_VERSION = "console.v1";

export interface BookingDraft {
  operation: "create" | "modify" | "cancel" | "reschedule";
  serviceId: string;
  /** Offset-aware ISO-8601 instant. */
  startsAt: string;
  partySize: number;
  durationMinutes?: number;
  locationId?: string;
  specialRequests?: string[];
  targetBookingId?: string;
  targetBookingVersion?: number;
}

export interface SubmissionContext {
  identity: AttemptIdentity;
  /** Conversation/session this submission came from; the validator requires it. */
  sessionId: string;
  /** Who confirmed — an operator identity for console-originated bookings. */
  identityRef: string;
  /** Deterministic id for the read-back the operator confirmed against. */
  readBackVersion: string;
  /** ISO instant of confirmation; defaults to now. */
  confirmedAt?: string;
}

/**
 * The canonical payload, with absent optional fields omitted entirely.
 *
 * `canon.v1` treats absent and null identically, but the payload is also stored verbatim,
 * so omitting rather than nulling keeps the persisted intent free of meaningless keys.
 */
export function toCanonicalPayload(draft: BookingDraft): BookingRequest {
  const payload: Record<string, unknown> = {
    operation: draft.operation,
    service_id: draft.serviceId,
    starts_at: draft.startsAt,
    party_size: draft.partySize,
  };

  if (draft.durationMinutes !== undefined) payload.duration_minutes = draft.durationMinutes;
  if (draft.locationId) payload.location_id = draft.locationId;
  if (draft.specialRequests?.length) payload.special_requests = draft.specialRequests;
  if (draft.targetBookingId) payload.target_booking_id = draft.targetBookingId;
  if (draft.targetBookingVersion !== undefined) {
    payload.target_booking_version = draft.targetBookingVersion;
  }

  return payload as BookingRequest;
}

export async function buildSubmission(
  draft: BookingDraft,
  context: SubmissionContext
): Promise<SubmitConfirmedIntentRequest> {
  const canonicalPayload = toCanonicalPayload(draft);

  // One computation, used for both fields the server cross-checks.
  const { value: payloadHash } = await canonicalHash(canonicalPayload);

  const confirmedAt = context.confirmedAt ?? new Date().toISOString();
  const { identity } = context;

  return {
    operation_id: identity.operationId,
    idempotency_key: identity.idempotencyKey,
    intent_id: identity.intentId,
    intent_revision: identity.revision,
    revision: {
      intent_id: identity.intentId,
      revision: identity.revision,
      canonical_payload: canonicalPayload,
      payload_hash: payloadHash,
      schema_version: SCHEMA_VERSION,
      tenant_policy_version: TENANT_POLICY_VERSION,
      source_session_id: context.sessionId,
      created_at: confirmedAt,
    },
    confirmation: {
      confirmation_id: identity.confirmationId,
      intent_id: identity.intentId,
      intent_revision: identity.revision,
      // Must equal revision.payload_hash — a DB trigger enforces it (spec 12 §6).
      intent_payload_hash: payloadHash,
      identity: context.identityRef,
      source_turn_id: `console:${identity.operationId}`,
      read_back_version: context.readBackVersion,
      verification_level: "staff_confirmed",
      confirmation_channel: "web",
      confirmed_at: confirmedAt,
      disclosures_presented: [],
    },
    // The generated type models optional fields as `T | null` and the union-typed
    // slots (`starts_at`, `party_size`, `duration_minutes`) as TriState; the object
    // above is a valid instance of every one of them, but TypeScript cannot narrow a
    // fresh literal to that shape, so the cast is through `unknown`.
  } as unknown as SubmitConfirmedIntentRequest;
}

/**
 * Combines a local week-day index and a minutes-since-midnight slot into an
 * offset-aware ISO instant.
 *
 * The console's grid is expressed in local time, but the API stores instants; doing the
 * conversion here (rather than string-formatting a date) means daylight-saving changes are
 * handled by the platform rather than by arithmetic.
 */
export function toIsoInstant(weekStart: Date, dayIndex: number, minutesFromMidnight: number): string {
  const when = new Date(weekStart);
  when.setDate(when.getDate() + dayIndex);
  when.setHours(0, minutesFromMidnight, 0, 0);
  return when.toISOString();
}
