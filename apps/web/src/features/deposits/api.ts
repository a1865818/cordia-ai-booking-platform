/**
 * Deposit sessions and payout snapshot.
 *
 * OpenAPI is stale for these paths — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";
import type { Deposit } from "../../data/console";

export interface DepositRow {
  id: string;
  operation_id: string;
  amount_minor: number;
  currency: string;
  status: string;
  created_at: string;
  paid_at: string | null;
  service_id?: string | null;
}

export interface DepositListResponse {
  deposits: DepositRow[];
}

export interface ListDepositsQuery {
  status?: string;
  from?: string;
  to?: string;
}

export interface RefundDepositResult {
  id: string;
  status: string;
}

export interface PayoutSummary {
  held_minor: number;
  available_minor: number;
  currency: string;
  next_date: string | null;
  next_amount_minor: number | null;
  account: string;
}

export function listDeposits(query: ListDepositsQuery = {}, signal?: AbortSignal) {
  const params = new URLSearchParams();
  if (query.status) params.set("status", query.status);
  if (query.from) params.set("from", query.from);
  if (query.to) params.set("to", query.to);

  const search = params.toString();
  return fetchApi<DepositListResponse>(
    search ? `/api/deposits?${search}` : "/api/deposits",
    { signal }
  );
}

/** Operator + reason. Records a local Paid → Refunded transition. */
export function refundDeposit(id: string) {
  return fetchApi<RefundDepositResult>(`/api/deposits/${encodeURIComponent(id)}/refund`, {
    method: "POST",
    actor: true,
    reasonCode: "deposit_refund",
  });
}

export function getPayoutSummary(signal?: AbortSignal) {
  return fetchApi<PayoutSummary>("/api/payouts/summary", { signal });
}

/** Operator + optional composed reason from ReasonCodeDialog. */
export function refundDepositWithReason(id: string, reasonCode: string) {
  return fetchApi<RefundDepositResult>(`/api/deposits/${encodeURIComponent(id)}/refund`, {
    method: "POST",
    actor: true,
    reasonCode,
  });
}

export function toConsoleDepositStatus(status: string): Deposit["status"] {
  switch (status) {
    case "created":
    case "link_sent":
    case "held":
      return "held";
    case "paid":
    case "captured":
      return "captured";
    case "expired":
    case "failed":
      return "failed";
    case "refunded":
      return "refunded";
    default:
      return "held";
  }
}

export function toConsoleDeposit(row: DepositRow): Deposit {
  const when = row.created_at ? new Date(row.created_at) : null;
  const whenLabel = when && !Number.isNaN(when.getTime())
    ? when.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })
    : "";
  return {
    id: row.id,
    name: row.operation_id || row.id,
    amount: row.amount_minor / 100,
    status: toConsoleDepositStatus(row.status),
    when: whenLabel,
    service: row.service_id ?? "",
  };
}
