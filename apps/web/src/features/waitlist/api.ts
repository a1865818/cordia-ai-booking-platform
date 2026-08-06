import type { components, paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

type EnrollWaitlistRequest = components["schemas"]["EnrollWaitlistRequest"];

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
 * What the console collects to enrol a guest — exactly the fields `EnrollWaitlistValidator`
 * requires, every one of them stated by the operator or chosen from something the platform
 * already records. The optional request fields are deliberately never sent:
 *
 * - `auto_book` — a consent grant needing a recorded `consent_evidence_id` (spec 11 §20);
 *   the console has no consent-capture surface, so enrolments from here are offer-only.
 * - `deposit_terms` — spec 09 has shipped no payment surface; terms would be invented money.
 * - `verification_level` — the console cannot verify a guest; absent, not guessed.
 * - `identity_ref` — the server defaults it to `customer_ref`.
 */
export interface EnrollGuestInput {
  /** Recorded verbatim as the enrollment's guest reference (and rendered back as its name). */
  customerRef: string;
  /** A published catalog `item_id` — the same source the create drawer books against. */
  serviceId: string;
  locationId: string;
  partySize: number;
  /** One of the ratified `Channel` wire values (`@cordia/shared-schemas`). */
  notificationChannel: string;
  offerLifetimeSeconds: number;
  /** Offset-aware ISO instants; the server requires `to > from`. */
  acceptableFrom: string;
  acceptableTo: string;
  /**
   * Idempotency key for **this attempt**, minted once by the drawer and reused across every
   * retry of it. See `enrollGuest` for why the console mints one rather than relying on the
   * server's derived fallback.
   */
  idempotencyKey: string;
}

/**
 * Enrols a guest on the waitlist.
 *
 * **The console mints the key, and that is a deliberate upgrade over letting the server derive
 * one.** The server's fallback key is a hash of the enrolment's own material fields, so it
 * cannot tell "the operator retried after an ambiguous failure" apart from "the operator
 * deliberately added a second, genuinely separate entry with the same details" — the two are
 * byte-identical requests. A per-attempt key says *this is the same attempt*, which is the fact
 * that actually distinguishes them: a retry reuses the attempt, a deliberate re-add starts a
 * new one.
 *
 * Retries are still never automatic. `retry: false` is inherited, because when the outcome is
 * unknown the decision to send it again is the operator's, taken after the refetched list has
 * shown what really happened.
 *
 * The 201 body is an anonymous object the generated contract cannot type (`content?: never`),
 * and nothing here needs it: the refetched list is the authoritative answer.
 */
export function enrollGuest(input: EnrollGuestInput) {
  const body = {
    customer_ref: input.customerRef,
    service_id: input.serviceId,
    location_id: input.locationId,
    party_size: input.partySize,
    notification_channel: input.notificationChannel,
    offer_lifetime_seconds: input.offerLifetimeSeconds,
    acceptable_from: input.acceptableFrom,
    acceptable_to: input.acceptableTo,
    idempotency_key: input.idempotencyKey,
    // Cast because the generated type marks `auto_book`/`deposit_terms` as required — an
    // OpenAPI-generation artifact; both are `?`-optional in the C# record and validated only
    // when present. Sending them at all is what would be wrong here.
  } as unknown as EnrollWaitlistRequest;

  return fetchApi<unknown>("/api/waitlist/enrollments", {
    method: "POST",
    body: JSON.stringify(body),
    actor: true,
    reasonCode: "waitlist_enrolment",
  });
}

/**
 * Cancels an enrollment.
 *
 * Every waitlist mutation is an operator action (spec 14 §24.5): the API requires `X-Actor`,
 * `X-Actor-Role` and `X-Reason-Code` together and 403s without them, so these headers are not
 * optional decoration — they are what makes the call succeed, and what the audit records.
 *
 * The reason code is a fixed descriptor of the ACTION, not operator free text. These are routine
 * front-desk operations; prompting for a typed justification on every one would train operators to
 * type nothing meaningful, which is worse for the trail than a constant that is always true.
 */
export function cancelEnrollment(enrollmentId: string) {
  return fetchApi<unknown>(`/api/waitlist/enrollments/${encodeURIComponent(enrollmentId)}`, {
    method: "DELETE",
    actor: true,
    reasonCode: "waitlist_cancellation",
  });
}

export function acceptOffer(offerId: string) {
  return fetchApi<unknown>(`/api/waitlist/offers/${encodeURIComponent(offerId)}/accept`, {
    method: "POST",
    actor: true,
    reasonCode: "waitlist_offer_accepted",
  });
}

export function declineOffer(offerId: string) {
  return fetchApi<unknown>(`/api/waitlist/offers/${encodeURIComponent(offerId)}/decline`, {
    method: "POST",
    actor: true,
    reasonCode: "waitlist_offer_declined",
  });
}

// ---------------------------------------------------------------------------
// Locations in use (docs §12 gap 7).
// ---------------------------------------------------------------------------

export type WaitlistLocationsResponse =
  paths["/api/waitlist/locations"]["get"]["responses"][200]["content"]["application/json"];

/**
 * The location ids this venue has already enrolled against.
 *
 * **A usage list, not a registry** — the platform has no locations table, and this does not
 * pretend to be one. It exists because the matcher compares locations by exact string, so a
 * typo'd `location_id` produces an enrolment that can never be matched and raises no error
 * anywhere. Offering the real values is what prevents that.
 *
 * A new venue legitimately has none, which is why an unrecognised value must warn rather than
 * block: the first enrolment at a venue can never be in this list.
 */
export function listWaitlistLocations(signal?: AbortSignal) {
  return fetchApi<WaitlistLocationsResponse>("/api/waitlist/locations", { signal });
}
