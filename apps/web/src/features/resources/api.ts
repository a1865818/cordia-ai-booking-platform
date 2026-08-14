/**
 * Staff roster (`GET`/`POST /api/resources`).
 *
 * Distinct from `/api/tenant-resources`, which names bookable services and locations.
 * OpenAPI is stale for this path — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";
import type { GridResource } from "../bookings/resources";

export interface StaffResource {
  resource_id: string;
  name: string;
  role: string | null;
  active: boolean;
}

export interface StaffResourceListResponse {
  resources: StaffResource[];
}

export function listStaffResources(signal?: AbortSignal) {
  return fetchApi<StaffResourceListResponse>("/api/resources", { signal });
}

/** Owner-only. Adds an active staff member. */
export function createStaffResource(input: { name: string; role?: string }) {
  const body: Record<string, unknown> = { name: input.name };
  if (input.role !== undefined) body.role = input.role;

  return fetchApi<StaffResource>("/api/resources", {
    method: "POST",
    actor: true,
    reasonCode: "resource_create",
    body: JSON.stringify(body),
  });
}

export function toGridResources(rows: readonly StaffResource[] | undefined): GridResource[] {
  return (rows ?? [])
    .filter((r) => r.active)
    .map((r) => ({
      id: r.resource_id,
      name: r.name,
      role: r.role ?? "",
    }));
}
