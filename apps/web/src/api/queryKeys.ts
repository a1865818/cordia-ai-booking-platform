/**
 * Query-key factory.
 *
 * Every key is **rooted at the tenant id** (Inv-13 hygiene): the cache is keyed by the
 * same scope the server enforces, so switching tenants can never serve another tenant's
 * cached rows. Keys carry ids and filters only — never PII.
 */

import { getTenantId } from "./config";

const root = (): readonly [string, string] => ["cordia", getTenantId()] as const;

export const queryKeys = {
  all: root,

  bookings: {
    all: () => [...root(), "bookings"] as const,
    schedule: (from: string, to: string) => [...root(), "bookings", "schedule", from, to] as const,
    detail: (bookingId: string) => [...root(), "bookings", "detail", bookingId] as const,
    find: (hints: Record<string, string | undefined>) =>
      [...root(), "bookings", "find", stableFilter(hints)] as const,
    availability: (from: string, to: string) =>
      [...root(), "bookings", "availability", from, to] as const,
  },

  operations: {
    all: () => [...root(), "operations"] as const,
    list: (filters: Record<string, string | number | boolean | undefined>) =>
      [...root(), "operations", "list", stableFilter(filters)] as const,
    status: (operationId: string) => [...root(), "operations", "status", operationId] as const,
    /**
     * The open manual-recovery queue. A separate key from `list` because it is a separate
     * table with its own lifetime: a work item stays open until a person closes it, so it
     * neither appears nor disappears when an operation's status changes.
     */
    workItems: (limit: number | undefined) =>
      [...root(), "operations", "work-items", limit ?? "default"] as const,
  },

  conversations: {
    all: () => [...root(), "conversations"] as const,
    sessions: (limit: number) => [...root(), "conversations", "sessions", limit] as const,
    /**
     * The grant is part of the key. A gated session's read fails without one, and react-query
     * would otherwise serve that cached failure forever — the operator would complete step-up
     * and see the same error.
     */
    transcript: (sessionId: string, grantId?: string, reasonCode?: string) =>
      [
        ...root(),
        "conversations",
        "transcript",
        sessionId,
        grantId ?? "no-grant",
        // The stated PURPOSE is part of the key, because it is part of the disclosure.
        //
        // `X-Reason-Code` is what the server writes on the audit row, and the read is cached with
        // `staleTime: Infinity`. Keyed without it, re-opening a session inside `gcTime` prompted
        // the operator for a purpose and then served the cached transcript — the PII was disclosed
        // under a purpose that never reached the server and produced no audit row at all. Every
        // distinct stated purpose has to be its own audited read (spec 14).
        reasonCode ?? "no-reason",
      ] as const,
  },

  waitlist: {
    all: () => [...root(), "waitlist"] as const,
    /** The tenant's declared locations — a usage list, cached longer than the board. */
    locations: () => [...root(), "waitlist", "locations"] as const,
    enrollments: (filters: Record<string, string | number | undefined>) =>
      [...root(), "waitlist", "enrollments", stableFilter(filters)] as const,
  },

  customers: {
    all: () => [...root(), "customers"] as const,
    list: (query: string, limit: number) => [...root(), "customers", "list", query, limit] as const,
  },

  reporting: {
    all: () => [...root(), "reporting"] as const,
    funnel: (from: string, to: string) => [...root(), "reporting", "funnel", from, to] as const,
    overview: (from: string, to: string) => [...root(), "reporting", "overview", from, to] as const,
  },

  settings: {
    all: () => [...root(), "settings"] as const,
    consentPolicy: (channel: string) => [...root(), "settings", "consent-policy", channel] as const,
    catalogItems: (query: string) => [...root(), "settings", "catalog-items", query] as const,
    /** The tenant's guest-facing venue name — the source for spec 08's `business_name`. */
    venueName: () => [...root(), "settings", "venue-name"] as const,
    /** Catalog discovery — owner-gated and audited, so keyed once and never focus-refetched. */
    catalogList: () => [...root(), "settings", "catalog-list"] as const,
    /** A single catalog's DRAFT items — owner-gated and audited, so keyed per catalog. */
    catalogDraft: (catalogId: string) => [...root(), "settings", "catalog-draft", catalogId] as const,
  },
} as const;

/**
 * Renders a filter object as a stable string so two equivalent filters produce one cache
 * entry regardless of key order, and `undefined` never widens the key.
 *
 * Serialised as JSON rather than `key=value&...`: an operator-typed value containing `&`
 * or `=` (a customer named "Smith & Co", say) would otherwise produce the same string as
 * a genuinely different filter set, and the console would render another query's rows
 * from cache. JSON also keeps `"5"` and `5` distinct.
 */
function stableFilter(filters: Record<string, string | number | boolean | undefined>): string {
  const entries = Object.entries(filters)
    .filter(([, value]) => value !== undefined && value !== "")
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));

  return JSON.stringify(entries);
}
