/**
 * Runtime configuration for API calls.
 *
 * Resolution order for every value: `window.APP_CONFIG` (runtime injection in hosted
 * environments) → `import.meta.env.VITE_*` (build/dev time) → a documented default.
 *
 * Tenancy note (spec 15 / Inv-13): `X-Tenant-Id` is a **development shim**. The API only
 * honours it when a signed `X-Tenant-Context` did not already establish the tenant
 * (`TenantResolutionMiddleware`). A signed-in operator session is preferred over the shim
 * so query keys and headers follow the tenant the login response named.
 */

import { getSession } from "./session";

const DEFAULT_TENANT_ID = "demo";
const DEFAULT_ACTOR = "console-operator";
const DEFAULT_ACTOR_ROLE = "operator";

/** The API's own constraint on `X-Correlation-Id` (CorrelationMiddleware). */
export const CORRELATION_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

function firstNonEmpty(...values: (string | undefined | null)[]): string | undefined {
  for (const value of values) {
    if (typeof value === "string" && value.trim().length > 0) return value.trim();
  }
  return undefined;
}

/** Base URL for API requests, or `undefined` to use relative `/api` paths via the dev proxy. */
export function apiBaseUrl(): string | undefined {
  return firstNonEmpty(window.APP_CONFIG?.API_URL);
}

/** Tenant sent as `X-Tenant-Id` on every request. Never empty — the API 400s on a blank tenant. */
export function getTenantId(): string {
  const signedIn = getSession()?.tenantId;
  return (
    firstNonEmpty(signedIn, window.APP_CONFIG?.TENANT_ID, import.meta.env.VITE_DEV_TENANT_ID) ??
    DEFAULT_TENANT_ID
  );
}

/** Operator identity sent as `X-Actor` on staff writes (waitlist, menu, preorder, DLQ, resume/resolve). */
export function getActor(): string {
  return (
    firstNonEmpty(window.APP_CONFIG?.ACTOR, import.meta.env.VITE_DEV_ACTOR) ?? DEFAULT_ACTOR
  );
}

/** Operator role sent as `X-Actor-Role` alongside `X-Actor`. */
export function getActorRole(): string {
  return (
    firstNonEmpty(window.APP_CONFIG?.ACTOR_ROLE, import.meta.env.VITE_DEV_ACTOR_ROLE) ??
    DEFAULT_ACTOR_ROLE
  );
}

/**
 * Mints a correlation id the API will echo rather than replace.
 *
 * Must match `CORRELATION_ID_PATTERN`; `crypto.randomUUID()` contains hyphens, which are
 * allowed, so the `corr_` prefix plus the UUID is 41 chars and always valid.
 */
export function newCorrelationId(): string {
  const id =
    typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2) + Date.now().toString(36);
  return `corr_${id}`;
}
