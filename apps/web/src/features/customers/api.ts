import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type ListCustomersResponse =
  paths["/api/customers"]["get"]["responses"][200]["content"]["application/json"];

export type CustomerSummary = NonNullable<ListCustomersResponse["customers"]>[number] & {
  origin?: string | null;
  no_show_count?: number;
  tags?: string[] | null;
  note?: string | null;
};

export type CustomerDetail = CustomerSummary;

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

export function getCustomer(ref: string, signal?: AbortSignal) {
  return fetchApi<CustomerDetail>(`/api/customers/${encodeURIComponent(ref)}`, { signal });
}

export function setCustomerNotes(ref: string, input: { note?: string; tags?: string[] }) {
  const body: Record<string, unknown> = {};
  if (input.note !== undefined) body.note = input.note;
  if (input.tags !== undefined) body.tags = input.tags;

  return fetchApi<CustomerDetail>(`/api/customers/${encodeURIComponent(ref)}/notes`, {
    method: "PUT",
    actor: true,
    reasonCode: "customer_notes_update",
    body: JSON.stringify(body),
  });
}
