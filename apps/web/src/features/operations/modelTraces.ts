/**
 * Model-trace viewer (spec 13 / spec 14 §4.3).
 *
 * Reading a trace is a staff-only, reason-coded, audited action — **both** the allow and the
 * deny land on the audit trail. So this is not an ordinary read: `X-Actor`, `X-Actor-Role` and
 * `X-Reason-Code` all travel, and the reason is the operator's own statement rather than a
 * constant, or the audit record would be a lie about why the trace was opened.
 *
 * Because every fetch writes an audit row, callers must not poll this or fire it on hover —
 * only on an explicit operator request.
 */

import { fetchApi } from "../../api/client";

/**
 * The server's `ModelTraceView`. Not in the generated `paths` surface, so this describes the
 * shape rather than asserting a contract guarantee — read the fields defensively.
 *
 * The content is already minimised server-side (spec 14 redaction); the console must never
 * re-expand it or copy it into another store.
 */
/**
 * The trace as the server actually serialises it.
 *
 * **snake_case, matching the wire.** `GetModelTraceQuery.ModelTraceView` stamps
 * `[JsonPropertyName]` on every member ("snake_case wire shape for cross-service consistency"),
 * and `fetchApi` does a plain `JSON.parse` with no key transformation — so a camelCase interface
 * here does not rename anything, it simply reads properties that are never present. `traceId`,
 * `sessionId` and `occurredAt` were all permanently `undefined`: the session chip never rendered,
 * the recorded-at timestamp was always null, and the trace-id chip silently fell back to the id
 * the operator had typed, which made the read failure look like a successful read.
 *
 * `kind` and `content` survived only because those two names are identical in both casings.
 *
 * Note the timestamp is `ts`, not `occurred_at` — the server has no `occurred_at` on this DTO.
 */
export interface ModelTraceView {
  trace_id?: string;
  session_id?: string | null;
  kind?: string;
  content?: string;
  ts?: string;
}

/**
 * Fetches one trace. A 403 is a legitimate, expected answer (role or reason refused) and must
 * be surfaced as such — not swallowed into an empty view, which would read as "no trace".
 */
export function getModelTrace(traceId: string, reasonCode: string, signal?: AbortSignal) {
  return fetchApi<ModelTraceView>(
    `/api/model-traces/${encodeURIComponent(traceId)}`,
    { actor: true, reasonCode, signal }
  );
}
