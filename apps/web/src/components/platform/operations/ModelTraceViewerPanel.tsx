import { useState } from "react";
import { Panel } from "../../ui/Panel";
import { Icon } from "../../ui/Icon";
import { ErrorState } from "../../ui/ErrorState";
import { PrimaryButton } from "../../ui/Button";
import { MaskedField } from "../../ui/MaskedField";
import { CorrelationIdChip } from "../../ui/CorrelationIdChip";
import { Timeline } from "../../ui/Timeline";
import { ReasonCodeDialog, composeReason } from "../../ui/ReasonCodeDialog";
import { getModelTrace, type ModelTraceView } from "../../../features/operations/modelTraces";
import { isApiError } from "../../../api/problem";

/**
 * Model-trace viewer (spec 13 / spec 14 §4.3 — docs §13 A2).
 *
 * **Every fetch writes an audit row — on allow AND on deny.** That single fact decides the
 * whole design:
 *
 *  • No TanStack query, deliberately. A `useQuery` here would refetch on window focus, on
 *    remount, and on cache invalidation, silently writing audit rows nobody asked for and
 *    filling the trail with reads no operator performed. This is a `useState` + an explicit
 *    click.
 *  • Nothing is prefetched, polled, or fired on hover.
 *  • The reason is collected BEFORE the read, because the audit row is written whether or not
 *    the read is allowed — so a refused attempt still has to carry a truthful reason.
 *
 * A **403 is a normal answer**, not an error state: it means the platform considered the
 * request and declined it. Rendering it as an empty view would imply the trace does not exist.
 */
export function ModelTraceViewerPanel() {
  const [traceId, setTraceId] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [loading, setLoading] = useState(false);
  const [trace, setTrace] = useState<ModelTraceView | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [failure, setFailure] = useState<{ error: unknown; retry: () => void } | null>(null);
  const [openedId, setOpenedId] = useState<string | null>(null);

  async function open(reasonCode: string, note: string) {
    const reason = composeReason(reasonCode, note);
    const id = traceId.trim();
    setConfirming(false);
    setLoading(true);
    setTrace(null);
    setRefusal(null);
    setFailure(null);
    setOpenedId(id);

    try {
      setTrace(await getModelTrace(id, reason));
    } catch (error) {
      if (isApiError(error) && error.status === 403) {
        // Expected, and already audited server-side. Not a failure to retry.
        setRefusal(
          error.problem?.detail
          ?? "The platform refused this read for your operator identity and reason."
        );
      } else if (isApiError(error) && error.status === 404) {
        setRefusal(`No trace is recorded under ${id}.`);
      } else {
        setFailure({ error, retry: () => { void open(reasonCode, note); } });
      }
    } finally {
      setLoading(false);
    }
  }

  return (
    <Panel style={{ marginBottom: 18, padding: '15px' }}>
      <div className="flex items-start gap-[11px] mb-[13px]">
        <span className="w-[34px] h-[34px] rounded-[10px] flex items-center justify-center shrink-0"
          style={{ background: '#F1F1EE' }}>
          <Icon name="search" size={17} strokeWidth={2.1} color="#0A0A0A" />
        </span>
        <div className="min-w-0">
          <div className="text-[13.5px] font-semibold text-ink">Open a model trace</div>
          <div className="text-[12px] text-t2 mt-[3px] leading-[1.5]">
            Opening a trace is recorded against you — <strong>whether or not it is allowed</strong>.
            Nothing is loaded until you ask for it, and nothing refreshes on its own.
          </div>
        </div>
      </div>

      <label className="block mb-[12px]">
        <div className="text-[12px] font-semibold mb-[5px]" style={{ color: '#55554F' }}>Trace id</div>
        <input
          value={traceId}
          onChange={e => setTraceId(e.target.value)}
          placeholder="trc_…"
          className="w-full h-[38px] border border-cd-border rounded-[9px] px-3 text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors font-mono"
        />
      </label>

      <PrimaryButton
        disabled={traceId.trim().length === 0 || loading}
        busy={loading}
        onClick={() => setConfirming(true)}
      >
        Open trace
      </PrimaryButton>

      {refusal && (
        <div
          role="status"
          className="mt-[12px] rounded-[11px] p-[12px_13px]"
          style={{ background: '#FBF3DF', border: '1px solid #F0DCA8' }}
        >
          <div className="text-[12.5px] font-semibold text-ink">Not shown</div>
          <div className="text-[12px] mt-[3px] leading-[1.5]" style={{ color: '#7E5606' }}>
            {refusal}
          </div>
          <div className="text-[11.5px] text-t3 mt-[6px] leading-[1.5]">
            This attempt is in the audit trail either way. It is a decision about access, not a
            statement that the trace is empty.
          </div>
        </div>
      )}

      {failure && (
        <div className="mt-[12px]">
          <ErrorState
            inline
            error={failure.error}
            title="Could not open the trace"
            onRetry={failure.retry}
          />
        </div>
      )}

      {trace && <TraceDetail trace={trace} requestedId={openedId} />}

      {/* Collected BEFORE the read: the audit row is written whether or not access is allowed,
          so even a refused attempt has to carry a truthful reason. */}
      <ReasonCodeDialog
        open={confirming}
        title="Open this model trace"
        confirmLabel="Open trace"
        busy={loading}
        onCancel={() => setConfirming(false)}
        onConfirm={(code, note) => { void open(code, note); }}
      />
    </Panel>
  );
}

function TraceDetail({ trace, requestedId }: { trace: ModelTraceView; requestedId: string | null }) {
  const id = trace.trace_id ?? requestedId ?? '';

  return (
    <div className="mt-[13px] rounded-[11px] p-[13px]" style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
      <div className="flex flex-wrap items-center gap-[6px] mb-[10px]">
        {id && <CorrelationIdChip label="trace" value={id} />}
        {trace.session_id && <CorrelationIdChip label="session" value={trace.session_id} />}
      </div>

      <Timeline
        entries={[
          {
            id: 'recorded',
            title: trace.kind?.trim() ? trace.kind : 'Trace recorded',
            at: trace.ts ?? null,
            tone: 'done',
          },
        ]}
      />

      {/* The server has already minimised this (spec 14 redaction). Masked by default because
          a trace body is the least predictable text on the platform — it is whatever the model
          was handed. Revealing is a local toggle over data already fetched, so it can never
          write a second audit row. */}
      <div className="mt-[8px]">
        <MaskedField
          label="Content (as the platform stored it)"
          value={trace.content}
          revealable
          emptyLabel="No content was recorded for this trace."
        />
      </div>
    </div>
  );
}
