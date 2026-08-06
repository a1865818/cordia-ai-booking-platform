import { useState } from "react";
import { Panel } from "../../ui/Panel";
import { Icon } from "../../ui/Icon";
import { ErrorState } from "../../ui/ErrorState";
import { ActionBar } from "../../ui/ActionBar";
import { CorrelationIdChip } from "../../ui/CorrelationIdChip";
import { ReasonCodeDialog, composeReason } from "../../ui/ReasonCodeDialog";
import { VerificationGate } from "../../../features/verification/VerificationGate";
import {
  operationActionFailureMessage, useCancelPreorder, useConvertPreorderToStandalone,
} from "../../../features/operations/mutations";
import type { PreorderResolutionResult } from "../../../features/operations/preorderRecovery";

type Pending = 'convert' | 'cancel';

/**
 * Pre-order manual resolution (spec 11 §21.4 — docs §13 A3).
 *
 * When a pre-order saga parks there are exactly two exits, and they are **not** symmetric:
 *
 *  • **Convert to standalone** keeps the order alive after the booking is gone. §21.4 makes
 *    that conditional on the *customer* agreeing, so the server demands a spec-08 verification
 *    grant. The grant is obtained through the same `VerificationGate` the booking flows use —
 *    the console never fabricates or reuses one, because the grant IS the customer's consent.
 *  • **Cancel** takes no grant. Nothing is being kept on the customer's behalf, so there is
 *    nothing for them to agree to.
 *
 * There is no saga list or status endpoint, so this is driven by a saga id the operator
 * arrived with. It cannot tell whether a saga is genuinely parked — so it says so, rather than
 * implying the actions are pre-validated. Both can legitimately 409 when the saga has moved.
 */
export function PreorderRecoveryPanel() {
  const [sagaId, setSagaId] = useState('');
  const [customerId, setCustomerId] = useState('');
  const [grantId, setGrantId] = useState<string | null>(null);
  const [verifying, setVerifying] = useState(false);
  const [pending, setPending] = useState<Pending | null>(null);
  const [result, setResult] = useState<PreorderResolutionResult | null>(null);
  const [failure, setFailure] = useState<{ error: unknown; action: string; retry: () => void } | null>(null);

  const convert = useConvertPreorderToStandalone();
  const cancel = useCancelPreorder();

  const id = sagaId.trim();
  const busy = convert.isPending || cancel.isPending;

  function reset() { setResult(null); setFailure(null); }

  function run(reasonCode: string, note: string) {
    const reason = composeReason(reasonCode, note);
    const action = pending;
    setPending(null);
    reset();
    if (action === 'cancel') {
      const input = { sagaId: id, reasonCode: reason };
      cancel.mutate(input, {
        onSuccess: setResult,
        onError: error => setFailure({
          error, action: 'cancel this pre-order',
          retry: () => cancel.mutate(input, { onSuccess: setResult }),
        }),
      });
      return;
    }
    if (action === 'convert' && !grantId) {
      // Falling through here would close the dialog and do nothing at all — no request, no
      // message, no clue that the missing grant was the reason. The grant is single-use and is
      // cleared after a conversion, so this is reachable on a second attempt.
      setFailure({
        error: new Error(
          "The customer's agreement is no longer held — it is single-use. Get it again before converting."
        ),
        action: 'convert this pre-order',
        retry: () => setVerifying(true),
      });
      return;
    }
    if (action === 'convert' && grantId) {
      const input = { sagaId: id, grantId, reasonCode: reason };
      convert.mutate(input, {
        onSuccess: r => {
          setResult(r);
          // The grant is single-use server-side; holding it would invite a second convert
          // that is certain to be refused.
          setGrantId(null);
        },
        onError: error => setFailure({
          error, action: 'convert this pre-order',
          retry: () => convert.mutate(input, { onSuccess: setResult }),
        }),
      });
    }
  }

  /**
   * A grant is issued for ONE (saga, customer) pair, so changing either invalidates it.
   *
   * Only the saga field used to reset, and only partially: it cleared the grant but left an open
   * VerificationGate mid-challenge, so a challenge started for one identity could resolve and hand
   * its grant to whatever was typed afterwards. Changing only the CUSTOMER reset nothing at all —
   * and since the convert payload carries `{ sagaId, grantId }` and no customer id, an
   * already-issued grant stayed usable under a completely different identity while the panel
   * still showed "the customer has agreed".
   */
  function resetIdentity() {
    setGrantId(null);
    setVerifying(false);
    reset();
  }

  return (
    <Panel style={{ marginBottom: 18, padding: '15px' }}>
      <div className="flex items-start gap-[11px] mb-[13px]">
        <span className="w-[34px] h-[34px] rounded-[10px] flex items-center justify-center shrink-0"
          style={{ background: '#FBF3DF' }}>
          <Icon name="takeover" size={17} strokeWidth={2.1} color="#C98A09" />
        </span>
        <div className="min-w-0">
          <div className="text-[13.5px] font-semibold text-ink">Resolve a parked pre-order</div>
          <div className="text-[12px] text-t2 mt-[3px] leading-[1.5]">
            No endpoint lists or reports saga status, so this takes a saga id you already have.
            It cannot confirm the saga is parked — the platform decides that, and will refuse
            anything else.
          </div>
        </div>
      </div>

      <label className="block mb-[10px]">
        <div className="text-[12px] font-semibold mb-[5px]" style={{ color: '#55554F' }}>Saga id</div>
        <input
          value={sagaId}
          onChange={e => { setSagaId(e.target.value); resetIdentity(); }}
          placeholder="pos_…"
          className="w-full h-[38px] border border-cd-border rounded-[9px] px-3 text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors font-mono"
        />
      </label>

      <label className="block mb-[12px]">
        <div className="text-[12px] font-semibold mb-[5px]" style={{ color: '#55554F' }}>
          Customer id <span className="font-normal text-t3">— only needed to convert</span>
        </div>
        <input
          value={customerId}
          onChange={e => { setCustomerId(e.target.value); resetIdentity(); }}
          placeholder="cust_…"
          className="w-full h-[38px] border border-cd-border rounded-[9px] px-3 text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors font-mono"
        />
      </label>

      {grantId && (
        <div
          role="status"
          className="mb-[12px] rounded-[11px] p-[10px_12px] text-[12px]"
          style={{ background: '#E7F6EC', border: '1px solid #CBE8D6', color: '#0B6431' }}
        >
          The customer has agreed. This grant is single-use — it covers one conversion.
        </div>
      )}

      {verifying ? (
        <VerificationGate
          action="modify"
          customerId={customerId.trim() || undefined}
          purpose="convert this pre-order to a standalone order"
          onGranted={g => { setGrantId(g); setVerifying(false); }}
          onCancel={() => setVerifying(false)}
        />
      ) : (
        <ActionBar
          explainIllegal
          emptyLabel="Enter a saga id to see what can be done."
          actions={[
            {
              id: 'convert',
              label: grantId ? 'Convert to standalone' : 'Get the customer’s agreement',
              // Convert is only offered once the customer has agreed, because §21.4 makes
              // their agreement the precondition — not a form field to fill in afterwards.
              legal: id.length > 0 && (grantId !== null || customerId.trim().length > 0),
              kind: 'primary',
              reason: true,
              busy: convert.isPending,
              onClick: () => (grantId ? setPending('convert') : setVerifying(true)),
              whenIllegal: 'Needs a saga id and the customer id to verify against.',
            },
            {
              id: 'cancel',
              label: 'Cancel the pre-order',
              legal: id.length > 0,
              kind: 'danger',
              reason: true,
              busy: cancel.isPending,
              onClick: () => setPending('cancel'),
              whenIllegal: 'Needs a saga id.',
            },
          ]}
        />
      )}

      {failure && (
        <div className="mt-[12px]">
          <ErrorState
            inline
            error={failure.error}
            title={operationActionFailureMessage(failure.error, failure.action)}
            onRetry={failure.retry}
          />
        </div>
      )}

      {result && (
        <div
          role="status"
          className="mt-[12px] rounded-[11px] p-[12px_13px]"
          style={{ background: '#E7F6EC', border: '1px solid #CBE8D6' }}
        >
          {/* The server's own word for what happened — not "done". */}
          <div className="text-[12.5px] font-semibold text-ink">
            {result.status ? `The platform recorded: ${result.status}` : 'The platform accepted the request'}
          </div>
          {result.saga_id && (
            <div className="mt-[8px]"><CorrelationIdChip label="saga" value={result.saga_id} /></div>
          )}
        </div>
      )}

      <ReasonCodeDialog
        open={pending !== null}
        title={pending === 'cancel' ? 'Cancel this pre-order' : 'Convert to a standalone order'}
        body={pending === 'cancel'
          ? 'The pre-order is ended. Any items reserved for it are released.'
          : 'The order survives without its booking, on the agreement the customer just gave.'}
        confirmLabel={pending === 'cancel' ? 'Cancel pre-order' : 'Convert'}
        busy={busy}
        onCancel={() => setPending(null)}
        onConfirm={run}
      />
    </Panel>
  );
}
