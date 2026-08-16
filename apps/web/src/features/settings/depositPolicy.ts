/**
 * Tenant deposit-policy settings (`GET`/`PUT /api/deposit-policy`).
 *
 * OpenAPI is stale for this path — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";

export interface DepositPolicy {
  require: boolean;
  amount_policy: string;
  no_show_only: boolean;
  refund_window: string;
  amount_minor?: number | null;
  currency?: string | null;
}

export interface SetDepositPolicyInput {
  require: boolean;
  amount_policy: string;
  no_show_only: boolean;
  refund_window: string;
  amount_minor?: number | null;
  currency?: string | null;
}

export function getDepositPolicy(signal?: AbortSignal) {
  return fetchApi<DepositPolicy>("/api/deposit-policy", { signal });
}

/** Owner-only and audited. */
export function setDepositPolicy(input: SetDepositPolicyInput) {
  const body: Record<string, unknown> = {
    require: input.require,
    amount_policy: input.amount_policy,
    no_show_only: input.no_show_only,
    refund_window: input.refund_window,
  };
  if (input.amount_minor !== undefined) body.amount_minor = input.amount_minor;
  if (input.currency !== undefined) body.currency = input.currency;

  return fetchApi<DepositPolicy>("/api/deposit-policy", {
    method: "PUT",
    actor: true,
    reasonCode: "deposit_policy_update",
    body: JSON.stringify(body),
  });
}
