/**
 * Tenant business profile (`GET`/`PUT /api/tenant-profile`).
 *
 * Distinct from `tenantProfile.ts`, which owns the guest-facing display-name routes.
 * OpenAPI is stale for this path — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";

export interface TenantProfile {
  tenant_id: string;
  display_name: string;
  business_type: string | null;
  waitlist_auto_book: boolean;
}

export interface SetTenantProfileInput {
  business_type?: string | null;
  waitlist_auto_book?: boolean;
}

export function getTenantProfile(signal?: AbortSignal) {
  return fetchApi<TenantProfile>("/api/tenant-profile", { signal });
}

/** Owner-only and audited. Partial — omitted fields are left as stored. */
export function setTenantProfile(input: SetTenantProfileInput) {
  const body: Record<string, unknown> = {};
  if (input.business_type !== undefined) body.business_type = input.business_type;
  if (input.waitlist_auto_book !== undefined) body.waitlist_auto_book = input.waitlist_auto_book;

  return fetchApi<TenantProfile>("/api/tenant-profile", {
    method: "PUT",
    actor: true,
    reasonCode: "venue_profile_update",
    body: JSON.stringify(body),
  });
}
