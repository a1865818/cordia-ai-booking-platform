import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type BookingFunnel =
  paths["/api/reporting/funnel"]["get"]["responses"][200]["content"]["application/json"];

export type BookingOverview =
  paths["/api/reporting/overview"]["get"]["responses"][200]["content"]["application/json"];

/** ISO-8601 window, exclusive on `to`, as both reporting endpoints expect. */
export interface ReportingWindow {
  from: string;
  to: string;
}

function windowQuery({ from, to }: ReportingWindow): string {
  return `from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`;
}

/** Separated funnel counts (spec 13). Never collapse these into one number (Inv-16). */
export function getBookingFunnel(window: ReportingWindow, signal?: AbortSignal) {
  return fetchApi<BookingFunnel>(`/api/reporting/funnel?${windowQuery(window)}`, { signal });
}

/** Per-day / per-channel / per-service aggregates for the analytics charts. */
export function getBookingOverview(window: ReportingWindow, signal?: AbortSignal) {
  return fetchApi<BookingOverview>(`/api/reporting/overview?${windowQuery(window)}`, { signal });
}

/** Resolves a range selector to a concrete UTC window ending now. */
export function rangeToWindow(range: "7d" | "30d" | "90d", now = new Date()): ReportingWindow {
  const days = range === "7d" ? 7 : range === "30d" ? 30 : 90;
  const from = new Date(now.getTime() - days * 24 * 60 * 60 * 1000);
  return { from: from.toISOString(), to: now.toISOString() };
}
