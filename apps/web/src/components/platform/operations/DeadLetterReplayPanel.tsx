import { useState } from "react";
import { Panel } from "../../ui/Panel";
import { Icon } from "../../ui/Icon";
import { ErrorState } from "../../ui/ErrorState";
import { CorrelationIdChip } from "../../ui/CorrelationIdChip";
import { ReasonCodeDialog, composeReason } from "../../ui/ReasonCodeDialog";
import { PrimaryButton } from "../../ui/Button";
import {
  operationActionFailureMessage, useReplayDeadLetterMessage,
} from "../../../features/operations/mutations";
import type {
  ReplayDeadLetterInput, ReplayDeadLetterResult,
} from "../../../features/operations/deadLetters";

/**
 * Dead-letter replay (spec 05 §6.1, §10 — docs §13 A1).
 *
 * **There is no list endpoint**, and this panel does not pretend otherwise: it takes a message
 * id the operator arrived with (from an alert, a log, or the audit trail). Rendering an empty
 * "quarantined messages" table would assert that the queue is clear, which the console has no
 * way to know.
 *
 * **`AckedWithoutReadmit` is not a failure and is not a replay.** The server may decide the
 * message should be acknowledged and dropped rather than re-dispatched; in that case nothing
 * was sent anywhere. Reporting "replayed" would be a false statement about a side effect
 * (Inv-16), so the decision is echoed verbatim and the wording is chosen from it.
 *
 * The reason travels in the **body**, not `X-Reason-Code` — this endpoint reads it from the
 * request body (`ReplayRequest.reasonCode`), unlike the operation follow-ups.
 */
export function DeadLetterReplayPanel() {
  const [messageId, setMessageId] = useState('');
  const [consumerName, setConsumerName] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [result, setResult] = useState<ReplayDeadLetterResult | null>(null);
  const [failure, setFailure] = useState<{ error: unknown; retry: () => void } | null>(null);

  const replay = useReplayDeadLetterMessage();

  const canSubmit = messageId.trim().length > 0 && consumerName.trim().length > 0;

  /**
   * One submission path, so a retry behaves exactly like the first attempt.
   *
   * The retry used to be its own inline `replay.mutate(input, { onSuccess: setResult })`, which
   * differed from this in two ways that both showed the operator something untrue: it never
   * cleared `failure`, so a retry that SUCCEEDED rendered "Could not replay this message"
   * directly above "Re-admitted to its consumer"; and it carried no `onError`, so a retry that
   * failed again left the FIRST error on screen — a stale message describing a different attempt.
   * Recursing through the same function makes the third attempt behave like the second.
   */
  function submit(input: ReplayDeadLetterInput) {
    setFailure(null);
    setResult(null);
    replay.mutate(input, {
      onSuccess: setResult,
      // Named failure with a deliberate retry — never automatic: replay has no idempotency
      // key, so a silent re-send could re-admit the message twice.
      onError: error => setFailure({ error, retry: () => submit(input) }),
    });
  }

  /**
   * Both identity fields clear the previous outcome AND the previous failure.
   *
   * Two things were wrong without this. A stale success/error banner sat above a form the
   * operator had already changed, describing a different message. Worse, `failure.retry` closes
   * over the `input` of the call that failed — so after editing the fields, "Try again" replayed
   * the OLD message id against the OLD consumer while the form showed the new ones. Replay has no
   * idempotency key, so that mistaken re-admission is a real and irreversible side effect.
   */
  function retarget(set: (v: string) => void, value: string) {
    set(value);
    setResult(null);
    setFailure(null);
  }

  function run(reasonCode: string, note: string) {
    setConfirming(false);
    submit({
      messageId: messageId.trim(),
      consumerName: consumerName.trim(),
      // This endpoint has ONE reason field, so the operator's note rides with the code rather
      // than being dropped — the code stays the leading, queryable token.
      reasonCode: composeReason(reasonCode, note),
    });
  }

  return (
    <Panel style={{ marginBottom: 18, padding: '15px' }}>
      <div className="flex items-start gap-[11px] mb-[13px]">
        <span className="w-[34px] h-[34px] rounded-[10px] flex items-center justify-center shrink-0"
          style={{ background: '#FDF2F2' }}>
          <Icon name="alert" size={17} strokeWidth={2.1} color="#C0392B" />
        </span>
        <div className="min-w-0">
          <div className="text-[13.5px] font-semibold text-ink">Replay a dead-lettered message</div>
          <div className="text-[12px] text-t2 mt-[3px] leading-[1.5]">
            The platform exposes no way to list quarantined messages, so this takes an id you
            already have — from an alert, the logs, or the audit trail. Nothing here can tell
            you whether the queue is otherwise clear.
          </div>
        </div>
      </div>

      <label className="block mb-[10px]">
        <div className="text-[12px] font-semibold mb-[5px]" style={{ color: '#55554F' }}>Message id</div>
        <input
          value={messageId}
          onChange={e => retarget(setMessageId, e.target.value)}
          placeholder="obx_…"
          className="w-full h-[38px] border border-cd-border rounded-[9px] px-3 text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors font-mono"
        />
      </label>

      <label className="block mb-[12px]">
        <div className="text-[12px] font-semibold mb-[5px]" style={{ color: '#55554F' }}>Consumer</div>
        <input
          value={consumerName}
          onChange={e => retarget(setConsumerName, e.target.value)}
          placeholder="booking-command-consumer"
          className="w-full h-[38px] border border-cd-border rounded-[9px] px-3 text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors"
        />
        <div className="text-[11.5px] text-t3 mt-[4px] leading-[1.5]">
          Part of the message's identity, not a filter — a message is quarantined <em>for</em> a
          consumer, and replaying names which one.
        </div>
      </label>

      <PrimaryButton
        disabled={!canSubmit || replay.isPending}
        busy={replay.isPending}
        onClick={() => setConfirming(true)}
      >
        Replay
      </PrimaryButton>

      {failure && (
        <div className="mt-[12px]">
          <ErrorState
            inline
            error={failure.error}
            title={operationActionFailureMessage(failure.error, "replay this message")}
            onRetry={failure.retry}
          />
        </div>
      )}

      {result && <ReplayOutcome result={result} />}

      <ReasonCodeDialog
        open={confirming}
        title="Replay this message"
        body="The platform decides what happens to it. It may be re-admitted to its consumer, or acknowledged and dropped without being re-dispatched."
        confirmLabel="Replay"
        busy={replay.isPending}
        onCancel={() => setConfirming(false)}
        onConfirm={run}
      />
    </Panel>
  );
}

/**
 * Reports what the SERVER decided.
 *
 * The heading is derived from `decision` rather than from "the request succeeded", because a
 * 200 covers both "re-dispatched" and "dropped without re-dispatch" and those are opposite
 * outcomes for the operator.
 */
function ReplayOutcome({ result }: { result: ReplayDeadLetterResult }) {
  const decision = result.decision ?? null;
  const redispatched = Boolean(result.dispatchedMessageId);

  // The server's RATIFIED vocabulary — `ReplayDecision` in `ReplayDeadLetterCommand.cs`, serialised
  // by exact member name. There are three values and none of them is `Readmitted`:
  //
  //   PreservedOriginalMessageId — never admitted, so the original message id is reused (§6.1 row 1)
  //   NewControlMessageId        — the step was already admitted, so a new control id is used (row 2)
  //   AckedWithoutReadmit        — the inbox already records success; acked, NOT replayed (row 3)
  //
  // This checked for `'Readmitted'`, a string the platform cannot emit. Both genuine re-admit
  // outcomes therefore fell through to "The platform answered: PreservedOriginalMessageId" plus
  // the "this console does not recognise that decision" footnote — so every successful replay was
  // reported as an unrecognised one, and the operator had no way to tell a working replay from a
  // console that had lost track of the contract.
  //
  // Still only the server's own word: an unrecognised decision is shown verbatim rather than
  // mapped to the nearest familiar one.
  const readmitted =
    decision === 'PreservedOriginalMessageId' || decision === 'NewControlMessageId';
  const known = readmitted || decision === 'AckedWithoutReadmit';

  const headline = decision === 'AckedWithoutReadmit'
    ? 'Acknowledged without re-admitting'
    : readmitted
      ? 'Re-admitted to its consumer'
      : decision
        ? `The platform answered: ${decision}`
        : 'The platform accepted the request but named no decision';

  const explain = decision === 'AckedWithoutReadmit'
    ? 'Nothing was dispatched. The message is settled and will not be processed — this is not a replay.'
    : decision === 'NewControlMessageId'
      ? 'The message was handed back to its consumer under a new control id, because the original had already been admitted. Watch the operation for what happens next.'
      : decision === 'PreservedOriginalMessageId'
        ? 'The message was handed back to its consumer under its original id. Watch the operation for what happens next.'
        : 'Treat this as unconfirmed until you have checked the audit trail.';

  return (
    <div
      role="status"
      className="mt-[12px] rounded-[11px] p-[12px_13px]"
      style={
        redispatched
          ? { background: '#E7F6EC', border: '1px solid #CBE8D6' }
          : { background: '#FBF3DF', border: '1px solid #F0DCA8' }
      }
    >
      <div className="text-[12.5px] font-semibold text-ink">{headline}</div>
      <div className="text-[12px] text-t2 mt-[3px] leading-[1.5]">{explain}</div>

      <div className="flex flex-wrap gap-[6px] mt-[9px]">
        {result.replayCommandId && (
          <CorrelationIdChip label="replay command" value={result.replayCommandId} />
        )}
        {result.dispatchedMessageId
          ? <CorrelationIdChip label="dispatched" value={result.dispatchedMessageId} />
          : <span className="text-[11.5px] text-t3 self-center">No message was dispatched.</span>}
      </div>

      {!known && decision && (
        <div className="text-[11.5px] text-t3 mt-[7px] leading-[1.5]">
          This console does not recognise that decision, so it is shown exactly as returned
          rather than interpreted.
        </div>
      )}
    </div>
  );
}
