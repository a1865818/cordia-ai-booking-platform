/**
 * Agent knowledge-base (`GET`/`POST`/`PUT /api/agent-knowledge`).
 *
 * OpenAPI is stale for this path — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";

export interface KnowledgeEntry {
  id: string;
  q: string;
  a: string | null;
  status: string;
  source: string;
}

export interface KnowledgeListResponse {
  entries: KnowledgeEntry[];
}

export function listKnowledge(signal?: AbortSignal) {
  return fetchApi<KnowledgeListResponse>("/api/agent-knowledge", { signal });
}

/** Owner-only. Empty `a` → `needs-answer`; otherwise `answered`. */
export function createKnowledge(input: { q: string; a?: string }) {
  const body: Record<string, unknown> = { q: input.q };
  if (input.a !== undefined) body.a = input.a;

  return fetchApi<KnowledgeEntry>("/api/agent-knowledge", {
    method: "POST",
    actor: true,
    reasonCode: "knowledge_create",
    body: JSON.stringify(body),
  });
}

/** Owner-only. Unknown id → 404. */
export function updateKnowledge(id: string, input: { a?: string; status?: string }) {
  const body: Record<string, unknown> = {};
  if (input.a !== undefined) body.a = input.a;
  if (input.status !== undefined) body.status = input.status;

  return fetchApi<KnowledgeEntry>(`/api/agent-knowledge/${encodeURIComponent(id)}`, {
    method: "PUT",
    actor: true,
    reasonCode: "knowledge_update",
    body: JSON.stringify(body),
  });
}
