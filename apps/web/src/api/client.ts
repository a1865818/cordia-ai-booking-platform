/**
 * Typed fetch wrapper for the Cordia API.
 *
 * Resolves the API base from `window.APP_CONFIG.API_URL` when present (runtime
 * injection in hosted environments); otherwise uses relative `/api` paths so the
 * Vite dev-server proxy (see vite.config.ts) forwards to the local API.
 *
 * Every request carries tenant scope (Inv-13) and a correlation id; error bodies are
 * parsed into the most specific `ApiError` subclass their shape supports — see
 * `problem.ts`, which also owns `ApiError` itself.
 */

import { apiBaseUrl, getActor, getActorRole, getTenantId, newCorrelationId } from "./config";
import { toApiError } from "./problem";
import { getSession } from "./session";

export { ApiError, isApiError } from "./problem";
export type { ProblemDetails, ValidationProblemDetails, IdempotencyConflict } from "./problem";

/** Per-request options layered on top of `RequestInit`. */
export interface ApiRequestOptions extends RequestInit {
  /** Spec-08 verification grant → `X-Grant-Id`. */
  grantId?: string;
  /** Operator identity → `X-Actor` + `X-Actor-Role`. Pass `true` for the configured default. */
  actor?: string | true;
  /** Reason code for audited operator actions → `X-Reason-Code`. */
  reasonCode?: string;
  /** Correlation id to send instead of a freshly minted one. */
  correlationId?: string;
  /**
   * Omit tenant / signed-context headers. Login has no session yet and must not send a
   * leftover `X-Tenant-Context` from a previous operator.
   */
  anonymous?: true;
}

function resolveUrl(path: string): string {
  const base = apiBaseUrl();
  if (base) {
    return `${base.replace(/\/$/, "")}${path}`;
  }
  return path;
}

function buildHeaders(options: ApiRequestOptions | undefined): Headers {
  const headers = new Headers();
  const anonymous = options?.anonymous === true;
  const session = anonymous ? null : getSession();

  headers.set("Accept", "application/json");
  headers.set("X-Correlation-Id", options?.correlationId ?? newCorrelationId());

  if (!anonymous) {
    // Signed-in tenant wins over the Development shim (`getTenantId` also prefers session).
    headers.set("X-Tenant-Id", session?.tenantId ?? getTenantId());
    if (session) {
      headers.set("X-Tenant-Context", session.tenantContext);
    }
  }

  // Only a string body is assumed to be JSON. FormData, Blob and URLSearchParams each
  // carry their own content type — and FormData in particular needs the browser to
  // generate a multipart boundary, which it only does when the header is absent.
  if (typeof options?.body === "string") {
    headers.set("Content-Type", "application/json");
  }

  if (options?.grantId) {
    headers.set("X-Grant-Id", options.grantId);
  }

  if (options?.actor) {
    headers.set(
      "X-Actor",
      options.actor === true ? (session?.actorId ?? getActor()) : options.actor
    );
    headers.set("X-Actor-Role", session?.actorRole ?? getActorRole());
  }

  if (options?.reasonCode) {
    headers.set("X-Reason-Code", options.reasonCode);
  }

  // Caller-supplied headers win — they are the most specific intent at the call site.
  new Headers(options?.headers).forEach((value, key) => headers.set(key, value));

  return headers;
}

/**
 * Performs a request and returns the parsed JSON body.
 *
 * A 204 (or any empty body) resolves to `undefined`, so callers of void endpoints do not
 * have to guard against a JSON parse error.
 */
export async function fetchApi<T>(path: string, options?: ApiRequestOptions): Promise<T> {
  const {
    grantId: _grantId,
    actor: _actor,
    reasonCode: _reasonCode,
    correlationId: _correlationId,
    anonymous: _anonymous,
    ...init
  } = options ?? {};

  const response = await fetch(resolveUrl(path), {
    ...init,
    headers: buildHeaders(options),
  });

  if (!response.ok) {
    throw await toApiError(response, path);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  if (text.length === 0) {
    return undefined as T;
  }

  return JSON.parse(text) as T;
}
