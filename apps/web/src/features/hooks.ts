/**
 * Query hooks for every console read surface.
 *
 * Kept in one module because each is a three-line `useQuery` over the feature's typed
 * fetcher; splitting them per feature would be more files than logic. The fetchers and
 * adapters stay in their feature folders, where the domain knowledge lives.
 */

import { useQuery } from "@tanstack/react-query";
import { queryKeys } from "../api/queryKeys";
import { getBookingSchedule, type ScheduleWindow } from "./bookings/api";
import { listOperations, type OperationFilters } from "./operations/api";
import { listSessions, getTranscript } from "./conversations/api";
import type { SensitiveReadPurpose } from "./conversations/purposes";
import { listCustomers } from "./customers/api";
import { listEnrollments } from "./waitlist/api";
import { getConsentPolicy, listCatalogItems } from "./settings/api";

export function useBookingSchedule(window: ScheduleWindow, enabled = true) {
  return useQuery({
    queryKey: queryKeys.bookings.schedule(window.from, window.to),
    queryFn: ({ signal }) => getBookingSchedule(window, signal),
    enabled,
  });
}

export function useOperations(filters: OperationFilters = {}, enabled = true) {
  return useQuery({
    queryKey: queryKeys.operations.list({
      statuses: filters.statuses?.join(","),
      needsAttention: filters.needsAttention,
      limit: filters.limit,
    }),
    queryFn: ({ signal }) => listOperations(filters, signal),
    enabled,
  });
}

export function useSessions(limit = 50, enabled = true) {
  return useQuery({
    queryKey: queryKeys.conversations.sessions(limit),
    queryFn: ({ signal }) => listSessions(limit, signal),
    enabled,
  });
}

/**
 * Reads a transcript — an audited disclosure of PII, not an ordinary query.
 *
 * Two consequences follow from that. It does not run until the operator has stated a purpose
 * (`reasonCode`), and once it has run it does not silently run again: `refetchOnWindowFocus` and
 * `refetchOnReconnect` are off and the result never goes stale. Leaving the shared defaults in
 * place meant alt-tabbing back to a tab left open on a conversation wrote another
 * `read_sensitive_transcript` row, drowning deliberate accesses in incidental ones.
 */
export function useTranscript(
  sessionId: string | null,
  reasonCode: SensitiveReadPurpose | null,
  enabled = true
) {
  return useQuery({
    queryKey: queryKeys.conversations.transcript(sessionId ?? ""),
    queryFn: ({ signal }) => getTranscript(sessionId!, reasonCode!, { signal }),
    enabled: enabled && Boolean(sessionId) && Boolean(reasonCode),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
}

export function useCustomers(query = "", limit = 50, enabled = true) {
  return useQuery({
    queryKey: queryKeys.customers.list(query, limit),
    queryFn: ({ signal }) => listCustomers(query || undefined, limit, signal),
    enabled,
  });
}

export function useWaitlistEnrollments(activeOnly = true, limit = 100, enabled = true) {
  return useQuery({
    queryKey: queryKeys.waitlist.enrollments({ activeOnly: String(activeOnly), limit }),
    queryFn: ({ signal }) => listEnrollments({ activeOnly, limit, signal }),
    enabled,
  });
}

export function useConsentPolicy(channel: string, enabled = true) {
  return useQuery({
    queryKey: queryKeys.settings.consentPolicy(channel),
    queryFn: ({ signal }) => getConsentPolicy(channel, signal),
    enabled,
  });
}

export function useCatalogItems(query = "", enabled = true) {
  return useQuery({
    queryKey: queryKeys.settings.catalogItems(query),
    queryFn: ({ signal }) => listCatalogItems(query || undefined, 50, signal),
    enabled,
  });
}
