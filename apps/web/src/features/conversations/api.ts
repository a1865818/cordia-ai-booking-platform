import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";
import type { SensitiveReadPurpose } from "./purposes";

export type ListSessionsResponse =
  paths["/api/conversation-sessions"]["get"]["responses"][200]["content"]["application/json"];

export type ConversationSessionSummary = NonNullable<ListSessionsResponse["sessions"]>[number];

export type TranscriptView =
  paths["/api/conversation-sessions/{sessionId}/transcript"]["get"]["responses"][200]["content"]["application/json"];

/** Session metadata only — transcript text never appears in this response. */
export function listSessions(limit?: number, signal?: AbortSignal) {
  const query = limit === undefined ? "" : `?limit=${limit}`;
  return fetchApi<ListSessionsResponse>(`/api/conversation-sessions${query}`, { signal });
}

/**
 * Reads a session transcript.
 *
 * This is a sensitive read (spec 14 §4.2): the API requires actor identity and a reason
 * code, and may additionally require a verification grant. All three are attached here so
 * the audit trail records who read what and why.
 */
export function getTranscript(
  sessionId: string,
  /**
   * Why this operator is reading it. **Required, and deliberately not defaulted** — it is written
   * verbatim into `audit_events`, and the constant this parameter replaced made every access
   * indistinguishable from every other (spec 14 §2). The server rejects anything outside the
   * ratified set with a 400.
   */
  reasonCode: SensitiveReadPurpose,
  options: { grantId?: string; signal?: AbortSignal } = {}
) {
  return fetchApi<TranscriptView>(
    `/api/conversation-sessions/${encodeURIComponent(sessionId)}/transcript`,
    {
      actor: true,
      reasonCode,
      grantId: options.grantId,
      signal: options.signal,
    }
  );
}
