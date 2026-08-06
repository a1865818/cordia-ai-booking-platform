/**
 * Identity-first booking lookup — `GET /api/bookings` (spec 08 §9.4.1).
 *
 * A guest rings back and gives a phone number, a booking reference, or an email. This is the
 * only way to get from that to their booking, and until now nothing in the console called it.
 *
 * **What it returns is deliberately minimised.** `ManageBookingCandidate` carries a reference,
 * the venue name, a date, an approximate time, a party size and a lifecycle status — and nothing
 * else. It is disambiguation material a caller can confirm, not a customer record, and the
 * console must not present it as one.
 *
 * **Discovery is not authorization.** Finding a booking says nothing about whether the caller
 * may change it: every mutation still needs its own spec-08 verification grant. A candidate list
 * is the start of that conversation, not the end of it.
 */

import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";
import { normalisePhone } from "./buildSubmission";

export type FindBookingsResponse =
  paths["/api/Bookings"]["get"]["responses"][200]["content"]["application/json"];

export type BookingCandidate = NonNullable<FindBookingsResponse["candidates"]>[number];

/** Which identifier the operator is searching by. The server needs at least one. */
export type LookupKind = "phone" | "reference" | "email";

export interface LookupInput {
  kind: LookupKind;
  value: string;
}

/**
 * Looks a booking up by one identifier.
 *
 * The phone is normalised with the SAME function the submission path uses, so a search typed
 * with spaces finds a booking stored without them. That matters because the stored value lives
 * in an immutable, hashed payload and can never be tidied after the fact — the server normalises
 * both sides too, and the two implementations have to agree.
 */
export function findBookings(input: LookupInput, signal?: AbortSignal) {
  const params = new URLSearchParams();
  const value = input.value.trim();

  if (input.kind === "phone") {
    params.set("phone_e164", normalisePhone(value) ?? value);
  } else if (input.kind === "reference") {
    params.set("booking_reference", value);
  } else {
    params.set("email", value);
  }

  return fetchApi<FindBookingsResponse>(`/api/Bookings?${params.toString()}`, { signal });
}

/**
 * The server's own word for how the search went.
 *
 * `none` is a real answer, not a failure: this venue has no booking matching that identifier.
 * `multiple` matters because the operator must disambiguate rather than assume the first row.
 */
export function lookupStatus(response: FindBookingsResponse | undefined): string | null {
  const status = (response as { status?: unknown } | undefined)?.status;
  return typeof status === "string" ? status : null;
}

/**
 * Operator-facing explanation for an empty result.
 *
 * Empty has two very different causes here and conflating them wastes an operator's time:
 * the booking may not exist, OR the venue may simply have no name set — in which case the
 * platform withholds every candidate by design (docs §13 B2), and no phone number will ever
 * return anything until that is fixed.
 */
export const EMPTY_LOOKUP_HINT =
  "No booking matches that. If nothing is ever found here, check that a venue name is set in "
  + "Settings — the platform withholds candidates entirely until one is.";
