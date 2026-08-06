/**
 * Durable identity for a booking submission attempt (spec 04 §10.1, Inv-2).
 *
 * The rule that matters: the operation id and idempotency key are minted **once, before
 * the first submission call**, and reused across every retry. Minting inside a retry loop
 * is exactly the failure the invariant exists to prevent — a network timeout on a request
 * the server actually accepted would otherwise create a second booking.
 *
 * Ids are held in `sessionStorage` so a reload mid-attempt resumes the same identity
 * rather than starting a new one. They are opaque and carry no PII.
 */

const STORAGE_PREFIX = "cordia.attempt.";

export interface AttemptIdentity {
  intentId: string;
  operationId: string;
  confirmationId: string;
  idempotencyKey: string;
  /** Revision number for this attempt; bumped when a material field changes (Inv-10). */
  revision: number;
  /**
   * Confirmation timestamp, minted with the attempt and reused on every retry.
   *
   * `SubmitBookingCancellation` and `SubmitBookingModification` hash the WHOLE request —
   * `confirmed_at` included — to decide whether a replayed idempotency key is the same request, so
   * a fresh timestamp per attempt turns the intended safe replay into an idempotency conflict
   * (Inv-2). The acceptance door (`SubmitConfirmedIntent`) hashes only
   * `revision.canonical_payload`, so there the cost is different but not smaller: the stored
   * `confirmed_at`/`created_at` would record the instant of the last retry rather than the
   * confirmation the operator actually gave. One stable value per attempt is right on both.
   */
  confirmedAt: string;
}

function randomId(prefix: string): string {
  const raw =
    typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
      ? crypto.randomUUID().split("-").join("")
      : Math.random().toString(36).slice(2) + Date.now().toString(36);
  return `${prefix}_${raw}`;
}

/**
 * Mints a standalone idempotency key for a write that needs one but has no full
 * {@link AttemptIdentity} behind it (the waitlist enrolment is the only such path today —
 * it carries no intent, revision or confirmation, just a key).
 *
 * Shares `randomId`'s crypto-with-fallback so every key the console mints comes from one
 * implementation rather than each call site rolling its own.
 */
export function mintIdempotencyKey(): string {
  return randomId("idem");
}

function readStored(key: string): AttemptIdentity | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_PREFIX + key);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<AttemptIdentity>;
    if (!parsed.intentId || !parsed.operationId || !parsed.idempotencyKey || !parsed.confirmationId) {
      return null;
    }
    return {
      ...parsed,
      revision: parsed.revision ?? 1,
      confirmedAt: parsed.confirmedAt ?? new Date().toISOString(),
    } as AttemptIdentity;
  } catch {
    // A blocked or full sessionStorage must not stop the operator booking.
    return null;
  }
}

function write(key: string, identity: AttemptIdentity): void {
  try {
    sessionStorage.setItem(STORAGE_PREFIX + key, JSON.stringify(identity));
  } catch {
    /* best effort — the in-memory value is still consistent for this attempt */
  }
}

/**
 * Returns the identity for `key`, minting it on first call and returning the *same* value
 * on every subsequent call until {@link resetAttempt}.
 */
export function getOrCreateAttempt(key: string): AttemptIdentity {
  const existing = readStored(key);
  if (existing) return existing;

  const identity: AttemptIdentity = {
    intentId: randomId("int"),
    operationId: randomId("op"),
    confirmationId: randomId("cnf"),
    idempotencyKey: randomId("idem"),
    revision: 1,
    confirmedAt: new Date().toISOString(),
  };
  write(key, identity);
  return identity;
}

/**
 * Clears the attempt so the next submission is a genuinely new operation.
 *
 * Call this only after a terminal outcome or when the operator abandons the form — never
 * to "retry", which must reuse the same key.
 */
export function resetAttempt(key: string): void {
  try {
    sessionStorage.removeItem(STORAGE_PREFIX + key);
  } catch {
    /* best effort */
  }
}

/**
 * Starts a **new** operation that supersedes the current one, as a materially changed
 * intent requires (spec 04 §10.1, Inv-10). The intent id is preserved and the revision
 * bumped, but the operation id and idempotency key are fresh — the same key must never be
 * bound to two different canonical payloads (Inv-3).
 */
export function supersedeAttempt(key: string): AttemptIdentity {
  const previous = readStored(key);
  const identity: AttemptIdentity = {
    intentId: previous?.intentId ?? randomId("int"),
    operationId: randomId("op"),
    confirmationId: randomId("cnf"),
    idempotencyKey: randomId("idem"),
    revision: (previous?.revision ?? 1) + 1,
    confirmedAt: new Date().toISOString(),
  };
  write(key, identity);
  return identity;
}
