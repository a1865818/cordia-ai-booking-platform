import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type ListCustomersResponse =
  paths["/api/customers"]["get"]["responses"][200]["content"]["application/json"];

export type CustomerSummary = NonNullable<ListCustomersResponse["customers"]>[number];

/**
 * Customers derived from booking history.
 *
 * Carries counts and timestamps only — no name, phone or email (spec 14 minimisation).
 * Contact details stay behind the verification-gated single-booking read.
 */
export function listCustomers(query?: string, limit?: number, signal?: AbortSignal) {
  const params = new URLSearchParams();
  if (query) params.set("query", query);
  if (limit !== undefined) params.set("limit", String(limit));

  const search = params.toString();
  return fetchApi<ListCustomersResponse>(
    search ? `/api/customers?${search}` : "/api/customers",
    { signal }
  );
}
