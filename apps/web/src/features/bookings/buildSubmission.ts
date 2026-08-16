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
import { UNASSIGNED_RESOURCE_ID } from "./resources";

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
  /** Staff resource this booking is assigned to, when the venue has a roster. */
  resourceId?: string;
  targetBookingId?: string;
  targetBookingVersion?: number;
  /**
   * The guest this booking is for (spec 04 §4.2).
   *
   * Without it a console booking has no notification destination and
   * `GET /api/bookings?phone_e164=` cannot find it — the server reads both out of the
   * canonical payload.
   *
   * Deliberately no `displayName`. `CustomerRef` has the field, but this payload lands in
   * `booking_intent_revisions`, which is immutable, replayed, and NOT reached by the retention
   * sweep (that only anonymises `ConversationSession.customer_ref`). A phone is the minimum that
   * makes the booking reachable; a name would be permanent with no operational purpose.
   */
  customer?: {
    /**
     * Opaque handle from `POST /api/guest-contacts`. **Preferred over `phoneE164`**: the contact
     * stays out of the hashed, immutable payload, so it can actually be erased later.
     */
    contactRef?: string;
    /**
     * Legacy inline shape. Kept for callers that have not minted a ref, and for the historical
     * revisions that already carry it — but a new booking submitted this way is permanently
     * un-erasable, which is the problem `contactRef` exists to solve.
     */
    phoneE164?: string;
    customerId?: string;
  };
}

export interface SubmissionContext {
  identity: AttemptIdentity;
  /** Conversation/session this submission came from; the validator requires it. */
  sessionId: string;
  /** Who confirmed — an operator identity for console-originated bookings. */
  identityRef: string;
  /** Deterministic id for the read-back the operator confirmed against. */
  readBackVersion: string;
  /**
   * ISO instant of confirmation. **Required, and taken from `AttemptIdentity.confirmedAt`** —
   * never minted here.
   *
   * It was optional with a `?? new Date().toISOString()` fallback, and both production call sites
   * duly forgot it: every retry then re-stamped the wall clock, so the durable record of WHEN the
   * operator confirmed became the instant of the last attempt rather than of the confirmation
   * (Inv-16). On the ManageBooking doors, which hash the whole request body, the same omission is
   * a hard 409. Making it required means the compiler catches the next caller that forgets, which
   * is the only reliable way to stop a silent, timing-dependent defect recurring.
   */
  confirmedAt: string;
}

/**
 * Routes a booking's existing `customer_ref` back into the right slot of a follow-up submission.
 *
 * A change to an existing booking (reschedule today, modify later) writes a NEW canonical payload,
 * and the notification resolver reads the destination from that operation's own revision — not
 * from the booking it targets. So a follow-up that drops the guest produces a confirmation with
 * nowhere to send, silently.
 *
 * The slot matters as much as the value. `customer_ref` is whatever identified the guest when the
 * booking was made, and the three cases resolve differently server-side: a `gc_` handle is
 * dereferenced against `guest_contacts`, an inline phone is read straight off the payload, and
 * anything else is an opaque customer id that is not a destination at all. Putting a phone in
 * `contact_ref` would send the resolver looking up a handle that does not exist and settle the
 * notification as undeliverable — worse than the omission it was meant to fix.
 */
export function customerFromRef(ref: string | null | undefined): BookingDraft["customer"] {
  const value = ref?.trim();
  if (!value) return undefined;

  // Minted by `POST /api/guest-contacts` as `gc_{guid:N}`; see GuestContactRepository.
  if (value.startsWith("gc_")) return { contactRef: value };

  // Legacy inline shape: bookings made before contact moved out of the payload carry the number
  // itself as the ref. Normalised on the way in by `toCanonicalPayload`.
  if (value.startsWith("+")) return { phoneE164: value };

  // An operator handle or provider reference. Carried so the booking keeps its identity, but it
  // is explicitly NOT claimed as a destination.
  return { customerId: value };
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
  if (draft.resourceId && draft.resourceId !== UNASSIGNED_RESOURCE_ID) {
    payload.resource_id = draft.resourceId;
  }
  if (draft.specialRequests?.length) payload.special_requests = draft.specialRequests;
  if (draft.targetBookingId) payload.target_booking_id = draft.targetBookingId;
  if (draft.targetBookingVersion !== undefined) {
    payload.target_booking_version = draft.targetBookingVersion;
  }

  // Emitted only when it carries something. canon.v1 drops null and `{}` alike, so an empty
  // customer could not change the hash — but omitting it keeps the stored, immutable payload
  // free of a meaningless key, and keeps a guest-less booking hashing exactly as it did before
  // this field existed.
  const customer: Record<string, unknown> = {};
  if (draft.customer?.customerId) customer.customer_id = draft.customer.customerId;
  // Formatting is stripped BEFORE the payload is hashed. The payload is immutable once written,
  // so a number stored as "+61 400 111 222" could never be tidied afterwards without
  // invalidating payload_hash — capture is the only place a canonical form can be established.
  // The server also normalises both sides when matching, so an untidied historical booking is
  // still findable; this just stops new ones being created untidy.
  // Preferred: an opaque handle minted by `POST /api/guest-contacts`. The contact itself stays
  // out of this payload, which is hashed and immutable — anything inline here can never be
  // erased without invalidating `payload_hash` (owner retention ruling 2026-08-06).
  if (draft.customer?.contactRef) {
    customer.contact_ref = draft.customer.contactRef;
  } else {
    // Fallback for a caller that has not minted a ref. Formatting is stripped BEFORE hashing:
    // the payload is immutable once written, so a number stored as "+61 400 111 222" could never
    // be tidied afterwards without invalidating payload_hash.
    const phone = normalisePhone(draft.customer?.phoneE164);
    if (phone) customer.phone_e164 = phone;
  }
  if (Object.keys(customer).length > 0) payload.customer = customer;

  return payload as BookingRequest;
}

/**
 * The length ceiling, mirroring `PhoneMatching.MaxLength` on the server.
 *
 * **Applied to the NORMALISED output, not to the operator's raw input** — and the difference
 * matters in both directions.
 *
 * The server bails to `null` above this length, so the invariant that has to hold is: *the value
 * this function writes into the canonical payload must be one the server can still normalise
 * later*. That value is the output (`'+' + digits`), never the raw input, because
 * `toCanonicalPayload` stores what this returns.
 *
 * Bounding the raw input instead would be strictly worse than having no bound. It is unnecessary —
 * output is never longer than the trimmed raw, so `raw ≤ 64` already implies `output ≤ 64` — and
 * it is actively harmful: `"+61 400 111 222"` padded past 64 characters by a CRM paste is a
 * perfectly good 12-digit number, and refusing it would drop the guest's phone out of an
 * immutable, hashed payload entirely, leaving a booking that can never be notified or found. That
 * is the very outcome this ceiling exists to prevent.
 */
export const MAX_PHONE_RAW_LENGTH = 64;

/**
 * Strips separators from a phone number, keeping digits and a leading `+`.
 *
 * Formatting only, and deliberately so: it does NOT turn `0400111222` into `+61400111222`.
 * That needs the tenant's country, which the platform records nowhere, and guessing it would
 * silently attach one guest's booking to another guest's number in any country whose national
 * format collides. The drawer warns when a number is not already E.164 instead.
 *
 * Mirrors `PhoneMatching.Normalise` on the server — the two must agree or a booking created
 * here would not be found by the lookup there.
 *
 * **Including the length bound, which is why it is not merely cosmetic.** `toCanonicalPayload`
 * embeds this output in the canonical payload, which is HASHED and IMMUTABLE. A value longer than
 * the server's ceiling used to pass here and be written into that payload, after which every
 * server-side `PhoneMatching.Normalise` — booking discovery, notification resolution — read it
 * back as `null`. The booking became permanently unfindable by phone and permanently
 * un-notifiable, and it could not be repaired: rewriting the payload would invalidate
 * `payload_hash` and every idempotency record derived from it (Inv-2/3).
 *
 * The over-long value is dropped rather than truncated — a truncated number is a DIFFERENT number,
 * and silently dialling it is worse than not having one. Note this rejects the VALUE; it does not
 * block submission. The operator is warned at capture via `looksUnnormalisedPhone`, which shares
 * this function so the two can never disagree about what counts as usable.
 */
export function normalisePhone(raw: string | undefined): string | undefined {
  if (!raw) return undefined;
  const trimmed = raw.trim();
  // A '+' counts as the international prefix only before any digit.
  const firstDigit = trimmed.search(/\d/);
  const plusIdx = trimmed.indexOf("+");
  const hasPlus = plusIdx >= 0 && (firstDigit < 0 || plusIdx < firstDigit);
  const digits = trimmed.replace(/\D/g, "");
  if (digits.length === 0) return undefined;

  const normalised = hasPlus ? `+${digits}` : digits;
  // Bounded on the OUTPUT: this is the value that reaches the payload and that the server must be
  // able to re-normalise. Formatting is stripped first, so a heavily punctuated but ordinary
  // number keeps its phone instead of losing it.
  return normalised.length > MAX_PHONE_RAW_LENGTH ? undefined : normalised;
}

export async function buildSubmission(
  draft: BookingDraft,
  context: SubmissionContext
): Promise<SubmitConfirmedIntentRequest> {
  const canonicalPayload = toCanonicalPayload(draft);

  // One computation, used for both fields the server cross-checks.
  const { value: payloadHash } = await canonicalHash(canonicalPayload);

  const { identity, confirmedAt } = context;

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
      // A member of the RATIFIED vocabulary, not a word that reads well.
      //
      // The platform's vocabulary is `unverified | channel_verified | identity_verified |
      // high_assurance` (`ConfirmationMappings.MapVerificationLevel`), and the submit validator
      // only checks the field is non-empty — so `"staff_confirmed"` fell through to
      // `_ => VerificationLevel.Unverified` and was stored as `unverified` anyway. The console
      // was asserting an assurance level the platform discarded.
      //
      // `unverified` is also the truthful answer: a staff-made booking has no verified guest.
      // Whether a staff-confirmed level should exist at all is spec 04's call — see R15.
      verification_level: "unverified",
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
