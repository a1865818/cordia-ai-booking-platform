/**
 * Agent guardrail toggles (`GET`/`PUT /api/agent-policy/guardrails`).
 *
 * OpenAPI is stale for this path — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";

export interface Guardrail {
  id: string;
  label: string;
  on: boolean;
  locked: boolean;
}

export interface GuardrailsResponse {
  guardrails: Guardrail[];
}

export function getGuardrails(signal?: AbortSignal) {
  return fetchApi<GuardrailsResponse>("/api/agent-policy/guardrails", { signal });
}

/** Owner-only. Locked rails ignore a client `false`. */
export function setGuardrails(guardrails: Guardrail[]) {
  return fetchApi<GuardrailsResponse>("/api/agent-policy/guardrails", {
    method: "PUT",
    actor: true,
    reasonCode: "guardrails_update",
    body: JSON.stringify({ guardrails }),
  });
}
