/**
 * API error types, mirroring what `GlobalExceptionHandler` actually returns.
 *
 * The API emits three distinct shapes on failure and they are NOT interchangeable:
 *   - `ProblemDetails`            — the default for every mapped domain exception
 *   - `ValidationProblemDetails`  — 400 from FluentValidation, adds a per-field `errors` map
 *   - `IdempotencyConflict`       — 409 from the acceptance path, **snake_case**, not a
 *                                   ProblemDetails at all (spec 04 §5.1 / Inv-3)
 *
 * `ApiError` lives here rather than in `client.ts` so the subclasses can extend it without
 * a circular import between the two modules; `client.ts` re-exports it.
 */

export interface ProblemDetails {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  instance?: string | null;
}

export interface ValidationProblemDetails extends ProblemDetails {
  errors?: Record<string, string[]> | null;
}

/** 409 body from `POST /api/bookings` — deliberately snake_case, see `WriteIdempotencyConflictAsync`. */
export interface IdempotencyConflict {
  idempotency_key?: string | null;
  existing_operation_id?: string | null;
  message?: string | null;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    /** The parsed ProblemDetails body, when the server sent one. */
    public readonly problem?: ProblemDetails
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export function isApiError(value: unknown): value is ApiError {
  return value instanceof ApiError;
}

/** 400 with per-field messages from the FluentValidation pipeline behaviour. */
export class ApiValidationError extends ApiError {
  constructor(
    status: number,
    message: string,
    public readonly errors: Record<string, string[]>,
    problem?: ProblemDetails
  ) {
    super(status, message, problem);
    this.name = "ApiValidationError";
  }

  /** Flattened `field: message` lines, in declaration order. */
  fieldMessages(): string[] {
    return Object.entries(this.errors).flatMap(([field, messages]) =>
      messages.map((m) => (field ? `${field}: ${m}` : m))
    );
  }
}

/**
 * 403 where the server answered with a typed business status rather than a problem.
 *
 * `GET /api/Bookings/{id}` and the transcript read return **200/403/404 with the same body**
 * carrying `status: ok | requires_verification | not_found`. A 403 there is therefore not a
 * failure to report but an instruction: obtain a spec-08 grant and retry with `X-Grant-Id`.
 * Without this class the body is discarded and the operator sees an opaque 403.
 */
export class ApiVerificationRequiredError extends ApiError {
  constructor(
    status: number,
    message: string,
    /** The typed body, so the caller can read whatever partial projection came with it. */
    public readonly body: Record<string, unknown>
  ) {
    super(status, message);
    this.name = "ApiVerificationRequiredError";
  }

  /**
   * Which D5 verification action to run step-up for, when the server said.
   * Present on the ProblemDetails form; absent on the typed `requires_verification` body.
   */
  get action(): string | undefined {
    return typeof this.body.action === "string" ? this.body.action : undefined;
  }

  /** The booking the grant must be scoped to, when the server said. */
  get targetBookingId(): string | undefined {
    return typeof this.body.target_booking_id === "string" ? this.body.target_booking_id : undefined;
  }
}

export function isApiVerificationRequiredError(value: unknown): value is ApiVerificationRequiredError {
  return value instanceof ApiVerificationRequiredError;
}

/**
 * 409 where an idempotency key is already bound to a different canonical request hash.
 * Retrying with the same key can never succeed — the caller must start a new attempt.
 */
export class ApiIdempotencyConflictError extends ApiError {
  constructor(
    status: number,
    message: string,
    public readonly conflict: IdempotencyConflict
  ) {
    super(status, message);
    this.name = "ApiIdempotencyConflictError";
  }

  get existingOperationId(): string | undefined {
    return this.conflict.existing_operation_id ?? undefined;
  }
}

export function isApiValidationError(value: unknown): value is ApiValidationError {
  return value instanceof ApiValidationError;
}

export function isApiIdempotencyConflictError(value: unknown): value is ApiIdempotencyConflictError {
  return value instanceof ApiIdempotencyConflictError;
}

/**
 * A 409 from the manage-booking endpoints, which answer with their own typed body rather than
 * ProblemDetails: `{ status: "already_cancelled" | "stale_version", ... }`.
 *
 * Worth its own class because the two mean opposite things to a retry — `stale_version` is
 * fixed by re-reading and trying again, `already_cancelled` can never succeed no matter how
 * many times it is sent.
 */
export class ApiBookingConflictError extends ApiError {
  constructor(
    status: number,
    message: string,
    /** The server's `status` discriminator, verbatim. */
    public readonly conflictStatus: string
  ) {
    super(status, message);
    this.name = "ApiBookingConflictError";
  }

  /** True when resubmitting can never succeed — the booking is already in the target state. */
  get isTerminal(): boolean {
    return this.conflictStatus === "already_cancelled";
  }
}

export function isApiBookingConflictError(value: unknown): value is ApiBookingConflictError {
  return value instanceof ApiBookingConflictError;
}

const BOOKING_CONFLICT_COPY: Record<string, string> = {
  already_cancelled: "This booking is already cancelled.",
  stale_version: "This booking changed while you were looking — reloading.",
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isIdempotencyConflict(body: Record<string, unknown>): boolean {
  // Distinguished from ProblemDetails by its snake_case keys; ProblemDetails never has these.
  return "existing_operation_id" in body || "idempotency_key" in body;
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((v) => typeof v === "string");
}

function readErrors(body: Record<string, unknown>): Record<string, string[]> | undefined {
  const raw = body.errors;
  if (!isRecord(raw)) return undefined;
  const out: Record<string, string[]> = {};
  for (const [field, messages] of Object.entries(raw)) {
    if (isStringArray(messages)) out[field] = messages;
  }
  return Object.keys(out).length > 0 ? out : undefined;
}

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/**
 * Turns a failed response into the most specific `ApiError` its body supports.
 * Never throws: a non-JSON or empty body degrades to a status-only message.
 */
export async function toApiError(response: Response, path: string): Promise<ApiError> {
  const fallback = `Request to ${path} failed: ${response.status}`;

  let body: unknown;
  try {
    const text = await response.text();
    body = text.length > 0 ? JSON.parse(text) : undefined;
  } catch {
    return new ApiError(response.status, fallback);
  }

  if (!isRecord(body)) return new ApiError(response.status, fallback);

  if (response.status === 403 && body.status === "requires_verification") {
    return new ApiVerificationRequiredError(
      response.status,
      "This booking needs the guest verified before it can be shown or changed.",
      body
    );
  }

  // The second, ProblemDetails-shaped form of "verify first" — thrown by the manage-booking
  // mutations rather than returned as a typed projection. Matched on the `code` extension, never
  // on `title`: the title is prose for a human and a client branching on copy breaks when copy
  // changes. A 403 WITHOUT this code (e.g. "Authorization failed") stays a plain ApiError, because
  // a role failure is not something guest verification can resolve.
  if (response.status === 403 && body.code === "verification_required") {
    return new ApiVerificationRequiredError(
      response.status,
      "This booking needs the guest verified before it can be changed.",
      body
    );
  }

  if (response.status === 409 && isIdempotencyConflict(body)) {
    const conflict = body as IdempotencyConflict;
    return new ApiIdempotencyConflictError(
      response.status,
      readString(conflict.message) ?? fallback,
      conflict
    );
  }

  // The manage-booking endpoints answer 409 with their own typed body, not ProblemDetails, so
  // there is no `detail`/`title` to read — without this the operator gets a bare
  // "Request to ... failed: 409" and the calling code cannot tell the two causes apart.
  if (response.status === 409 && typeof body.status === "string") {
    const conflictStatus = body.status;
    return new ApiBookingConflictError(
      response.status,
      BOOKING_CONFLICT_COPY[conflictStatus] ?? fallback,
      conflictStatus
    );
  }

  const problem = body as ProblemDetails;
  const message = readString(problem.detail) ?? readString(problem.title) ?? fallback;

  const errors = readErrors(body);
  if (errors) return new ApiValidationError(response.status, message, errors, problem);

  return new ApiError(response.status, message, problem);
}
