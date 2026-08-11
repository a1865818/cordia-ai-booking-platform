/**
 * The tenant's guest-facing venue name (docs §12 gap 11 / §13 B2).
 *
 * Load-bearing, not cosmetic: it is the source for spec 08's `business_name` on a
 * `ManageBookingCandidate`. Until a tenant sets one, `GET /api/bookings?phone_e164=` returns no
 * candidates at all — deliberately, because deriving a name from the tenant id would put a slug
 * in front of a guest. So this field is a setup step, and the panel says so.
 */

import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type TenantDisplayNameResponse =
  paths["/api/tenant-profile/display-name"]["get"]["responses"][200]["content"]["application/json"];

/** Not owner-gated: it is what guests are shown, so it is not a secret. */
export function getTenantDisplayName(signal?: AbortSignal) {
  return fetchApi<TenantDisplayNameResponse>("/api/tenant-profile/display-name", { signal });
}

/**
 * Owner-only and audited. Idempotent — the tenant is the key, so a repeat updates the one row
 * rather than adding a second.
 */
export function setTenantDisplayName(displayName: string) {
  return fetchApi<TenantDisplayNameResponse>("/api/tenant-profile/display-name", {
    method: "PUT",
    actor: true,
    reasonCode: "venue_profile_update",
    body: JSON.stringify({ display_name: displayName }),
  });
}
