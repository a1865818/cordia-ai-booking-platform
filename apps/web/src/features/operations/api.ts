import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type ListOperationsResponse =
  paths["/api/booking-operations"]["get"]["responses"][200]["content"]["application/json"];

export type OperationSummary = NonNullable<ListOperationsResponse["operations"]>[number];

export interface OperationFilters {
  /** Snake_case values from the ratified 16-value set (D1). An unknown value is a 400. */
  statuses?: string[];
  /** Shorthand for the states waiting on a person or no longer progressing on their own. */
  needsAttention?: boolean;
  limit?: number;
}

export function listOperations(filters: OperationFilters = {}, signal?: AbortSignal) {
  const params = new URLSearchParams();
  for (const status of filters.statuses ?? []) params.append("status", status);
  if (filters.needsAttention) params.set("needs_attention", "true");
  if (filters.limit !== undefined) params.set("limit", String(filters.limit));

  const query = params.toString();
  return fetchApi<ListOperationsResponse>(
    query ? `/api/booking-operations?${query}` : "/api/booking-operations",
    { signal }
  );
}
