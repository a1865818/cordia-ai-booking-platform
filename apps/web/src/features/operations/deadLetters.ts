/**
 * Dead-letter replay (spec 05 §6.1, §10).
 *
 * A quarantined message is replayed by the platform, not re-sent by the console: the server
 * applies the §6.1 identity rules and mints its own replay-command id, so the console never
 * fabricates one. The decision it returns may be `AckedWithoutReadmit` — a real outcome in
 * which nothing is re-dispatched — so the caller must report what came back rather than
 * assuming the message was requeued (Inv-16).
 *
 * The operator spine is mandatory and fail-closed server-side: `X-Actor`, `X-Actor-Role` and
 * the body's `reasonCode` are all rejected when empty, so none of them is defaulted here.
 *
 * Deliberately not retried: replay is a side-effecting operator action with no idempotency
 * key, so a repeat must be the operator's decision.
 */

import { fetchApi } from "../../api/client";

export interface ReplayDeadLetterInput {
  messageId: string;
  /** The consumer the message was quarantined for — part of its identity, not a filter. */
  consumerName: string;
  /** The operator's own justification. Audited; never defaulted. */
  reasonCode: string;
}

/**
 * The server's `ReplayDeadLetterResult`. Typed loosely on purpose: the endpoint is not in the
 * generated `paths` surface, so this describes the shape without claiming the contract
 * guarantees it. Read defensively.
 */
export interface ReplayDeadLetterResult {
  /** e.g. `Readmitted` | `AckedWithoutReadmit`. Report this — do not assume success. */
  decision?: string;
  replayCommandId?: string;
  /** Null when the decision was `AckedWithoutReadmit` — nothing was dispatched. */
  dispatchedMessageId?: string | null;
}

export function replayDeadLetterMessage(input: ReplayDeadLetterInput) {
  return fetchApi<ReplayDeadLetterResult>("/api/dead-letter-messages/replay", {
    method: "POST",
    actor: true,
    body: JSON.stringify({
      messageId: input.messageId,
      consumerName: input.consumerName,
      reasonCode: input.reasonCode,
    }),
  });
}
