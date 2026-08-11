import { fetchApi } from "../../api/client";

/**
 * Mints an opaque handle for a guest's contact details (`POST /api/guest-contacts`).
 *
 * **Why the console calls this before submitting.** The canonical payload is hashed HERE, on the
 * client, and the hash is what binds the confirmation to the intent. So contact can only be kept
 * out of the hash if it never enters the payload this app builds — the ref has to exist first.
 * Letting the server strip it afterwards would make the stored payload stop re-hashing to its own
 * recorded hash, breaking the idempotency chain (Inv-2/3) this whole change exists to preserve.
 *
 * The phone therefore reaches the platform on THIS request and never lands in
 * `booking_intent_revisions.canonical_payload`, which is immutable and replayed. Erasing a guest
 * becomes one row delete instead of an impossibility.
 *
 * Idempotent per tenant + contact, so a regular customer has one erasable row rather than one per
 * booking.
 */
export interface GuestContactInput {
  phoneE164?: string;
  email?: string;
  displayName?: string;
}

export interface GuestContactRef {
  contact_ref: string;
}

export function createGuestContact(input: GuestContactInput, signal?: AbortSignal) {
  return fetchApi<GuestContactRef>("/api/guest-contacts", {
    method: "POST",
    // The mint IS audited server-side (`guest_contact.minted`), and without these the audit row
    // names no one: the controller falls back to `ActorId: "unknown"` and a null reason. The
    // console is this endpoint's only client, so omitting them made 100% of the rows anonymous —
    // defeating the point of auditing the one table that holds erasable guest PII.
    actor: true,
    reasonCode: "guest_contact_capture",
    signal,
    body: JSON.stringify({
      phone_e164: input.phoneE164 ?? null,
      email: input.email ?? null,
      display_name: input.displayName ?? null,
    }),
  });
}
