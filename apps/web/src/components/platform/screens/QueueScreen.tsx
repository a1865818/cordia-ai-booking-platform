import React, { useEffect, useState } from "react";
import { QueueItem, chMeta } from "../../../data/console";
import { operationActions, operationStatusLabel } from "../../../features/operations/adapters";
import { Chip } from "../../ui/Chip";
import { Icon } from "../../ui/Icon";
import { Chan } from "../../ui/StatusTags";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { SectionLabel } from "../../ui/SectionLabel";
import { Empty } from "../../ui/Empty";

/**
 * The platform's own status, in operator language.
 *
 * Neutral by design: the board carries manual_review and alternatives_required alongside
 * genuine failures, and colouring them all red told the operator four different situations
 * were the same one.
 */
function StatusPill({ label }: { label: string }) {
  return (
    <span
      className="inline-flex items-center gap-[5px] shrink-0 text-[11px] font-semibold px-[9px] py-[3px] rounded-full whitespace-nowrap"
      style={{ background: '#F2F2F0', color: '#55554F' }}
    >
      <span className="w-[6px] h-[6px] rounded-full shrink-0" style={{ background: '#9A9A9A' }} />
      {label}
    </span>
  );
}

const actionButtonStyle: React.CSSProperties = {
  height: 30,
  padding: '0 10px',
  borderRadius: 8,
  fontSize: 11.5,
  fontWeight: 600,
  fontFamily: "'Inter Tight', system-ui, sans-serif",
  cursor: 'pointer',
  transition: 'all 120ms ease',
};

/** A server-action button — small, bordered, disabled together while any action is in flight. */
function ServerActionButton({ onClick, disabled, title, children, emphasis }: {
  onClick: () => void;
  disabled?: boolean;
  title: string;
  children: React.ReactNode;
  emphasis?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="disabled:opacity-40 disabled:cursor-not-allowed hover:brightness-105"
      style={{
        ...actionButtonStyle,
        background: emphasis ? '#0A0A0A' : '#fff',
        color: emphasis ? '#fff' : '#3A3A38',
        border: `1px solid ${emphasis ? '#0A0A0A' : '#E0E0DC'}`,
      }}
    >
      {children}
    </button>
  );
}

const formInputStyle: React.CSSProperties = {
  width: '100%', height: 34, border: '1px solid #E0E0DC', borderRadius: 8,
  padding: '0 10px', fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 12.5, color: '#0A0A0A', background: '#fff', outline: 'none',
};

interface ResolveDraft {
  resolution: 'booked' | 'failed_final';
  providerRef: string;
  reason: string;
}

interface QueueScreenProps {
  device: 'phone' | 'desktop';
  queue: QueueItem[];
  busy: string | null;
  onFinish: (id: string) => void;
  /**
   * True once the operations list has answered at least once. `false` means "not yet known" —
   * which must not render as "nothing needs you".
   */
  loaded?: boolean;
  onDismiss: (id: string) => void;
  /**
   * Server follow-ups (docs §10). Optional as a group: a rendering without them (tests,
   * demo) simply offers no server actions, exactly like WaitlistScreen's offer callbacks.
   */
  onRequestCancellation?: (id: string) => void;
  onResume?: (id: string, reason: string) => void;
  onResolve?: (id: string, resolution: 'booked' | 'failed_final', providerRef: string | undefined, reason: string) => void;
  onRespondAlternatives?: (id: string, response: 'waitlist' | 'decline') => void;
  /** The operation whose follow-up is in flight — all server actions disable together. */
  serverBusyId?: string | null;
}

export function QueueScreen({
  device, queue, busy, onFinish, onDismiss, loaded = true,
  onRequestCancellation, onResume, onResolve, onRespondAlternatives, serverBusyId,
}: QueueScreenProps) {
  const isPhone = device === 'phone';

  /** The one expanded follow-up form (resolve or resume) — one at a time across the board. */
  const [form, setForm] = useState<{ id: string; kind: 'resolve' | 'resume' } | null>(null);
  const [resolveDraft, setResolveDraft] = useState<ResolveDraft>({
    resolution: 'booked', providerRef: '', reason: '',
  });
  const [resumeReason, setResumeReason] = useState('');

  function openForm(id: string, kind: 'resolve' | 'resume') {
    setForm({ id, kind });
    setResolveDraft({ resolution: 'booked', providerRef: '', reason: '' });
    setResumeReason('');
  }

  // Close a form whose row no longer permits it. The board refetches under an open form
  // (any follow-up settling invalidates the operations query, and window focus refetches),
  // so the status can move while the operator types — manual_review → reconciling, say.
  // Keeping the form open would keep an enabled submit for a call that is now certain to
  // 409; the status change is new information, exactly like the dismissal-key rule.
  useEffect(() => {
    if (!form) return;
    const target = queue.find(x => x.id === form.id);
    const permitted = form.kind === 'resume'
      ? operationActions(target?.status, target?.isPreorder).canResume
      : operationActions(target?.status, target?.isPreorder).canResolve;
    if (!target || !permitted) setForm(null);
  }, [form, queue]);

  if (queue.length === 0) {
    // "Nothing needs you right now" is an ASSERTION, and an emphatic one — it is styled as good
    // news and tells the operator the agent has everything in hand. It may only be made once the
    // platform has actually answered. Before the first successful load (and after a failed one,
    // where the page's error banner sits directly above this panel) the honest state is "we do
    // not know yet", not "you are all clear".
    return loaded ? (
      <Empty
        tone="good"
        icon="check"
        title="Nothing needs you right now"
        body="The agent is handling bookings automatically. Anything it can't finish on its own will land here."
      />
    ) : (
      <Empty
        tone="neutral"
        icon="clock"
        // True while the request is in flight AND after it failed — in which case the page's
        // error banner sits directly above with the detail. "Loading…" would be a false
        // statement once the request has already failed.
        title="The queue hasn’t loaded"
        body="Anything the agent could not finish on its own will appear here."
      />
    );
  }

  return (
    <div>
      <SectionLabel line>{queue.length} need a human</SectionLabel>
      <div className="flex flex-col gap-[11px]">
        {queue.map(q => {
          const b = busy === q.id;
          const actions = operationActions(q.status, q.isPreorder);
          const serverBusy = serverBusyId === q.id;
          const anyServerBusy = Boolean(serverBusyId);
          const rowForm = form?.id === q.id ? form.kind : null;

          // Only actions whose callback is wired AND whose status permits them (docs §10:
          // never a button whose call is certain to fail for the row's current status).
          const showResume = actions.canResume && Boolean(onResume);
          const showResolve = actions.canResolve && Boolean(onResolve);
          const showAlternatives = actions.canRespondAlternatives && Boolean(onRespondAlternatives);
          const showCancel = actions.canRequestCancellation && Boolean(onRequestCancellation);
          const hasServerActions = showResume || showResolve || showAlternatives || showCancel;

          const resolveReady =
            resolveDraft.reason.trim() !== '' &&
            (resolveDraft.resolution === 'failed_final' || resolveDraft.providerRef.trim() !== '');

          return (
            <div key={q.id}
              className="bg-white rounded-[13px] p-[14px_15px] transition-shadow duration-150 hover:shadow-[0_2px_12px_-4px_rgba(10,10,10,0.1)]"
              style={{ border: '1px solid #EAEAEA', borderLeft: '3px solid #D23B30' }}>
              <div className="flex items-start justify-between gap-[10px]">
                <div className="min-w-0">
                  <div className="text-[15px] font-semibold tracking-[-0.01em] text-ink">{q.name}</div>
                  <div className="text-[12.5px] text-t2 mt-[1px]">{q.service}</div>
                </div>
                {/* The board lists everything needing a human — manual_review,
                    alternatives_required, compensation_required. A hard-coded "Failed" chip
                    told the operator all four were failures. Show the platform's own status. */}
                {q.status
                  ? <StatusPill label={operationStatusLabel(q.status)} />
                  : <Chip status="failed" />}
              </div>
              <div className="flex items-center gap-2 mt-[11px] px-[11px] py-[9px] rounded-[9px]" style={{ background: '#FBF7F6' }}>
                <Icon name="alert" size={15} strokeWidth={2} color="#B82C22" />
                <span className="text-[12.5px] flex-1" style={{ color: '#7A2620' }}>{q.reason}</span>
              </div>

              {/* Server follow-ups — a real platform action, visually separate from the
                  local clear/dismiss row below. Everything here round-trips the API and
                  reports only what the server answered. */}
              {hasServerActions && (
                <div className="mt-[11px] rounded-[9px] p-[10px_11px]" style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
                  <div className="font-mono text-[9.5px] tracking-[0.1em] uppercase text-t3 mb-[8px]">
                    Platform actions
                  </div>

                  {/* Both form branches re-check the LIVE capability, not just the local
                      form state: the status can move under an open form, and rendering it
                      from `rowForm` alone would keep an enabled submit for a certain 409. */}
                  {rowForm === 'resume' && showResume ? (
                    <div>
                      <div className="text-[12px] font-medium text-ink mb-[6px]">
                        Send this back for checking — why?
                      </div>
                      {/* The reason is recorded verbatim in the audit trail (X-Reason-Code),
                          so it is the operator's own statement, never defaulted. */}
                      <input
                        value={resumeReason}
                        onChange={e => setResumeReason(e.target.value)}
                        placeholder="Reason — recorded in the audit log"
                        style={formInputStyle}
                        aria-label="Reason for sending back"
                      />
                      <div className="flex gap-[7px] mt-[8px]">
                        <ServerActionButton
                          emphasis
                          disabled={anyServerBusy || resumeReason.trim() === ''}
                          title="The platform re-checks what happened with the provider."
                          onClick={() => {
                            onResume!(q.id, resumeReason.trim());
                            setForm(null);
                          }}
                        >
                          Send back for checks
                        </ServerActionButton>
                        <ServerActionButton title="Close this form" onClick={() => setForm(null)}>
                          Back
                        </ServerActionButton>
                      </div>
                    </div>
                  ) : rowForm === 'resolve' && showResolve ? (
                    <div>
                      <div className="text-[12px] font-medium text-ink mb-[6px]">
                        Record the outcome you confirmed with the provider
                      </div>
                      <label className="flex items-center gap-[7px] text-[12px] text-ink mb-[6px] cursor-pointer">
                        <input
                          type="radio"
                          name={`resolve-${q.id}`}
                          checked={resolveDraft.resolution === 'booked'}
                          onChange={() => setResolveDraft(d => ({ ...d, resolution: 'booked' }))}
                        />
                        It was booked — I have the provider’s reference
                      </label>
                      {resolveDraft.resolution === 'booked' && (
                        <div className="mb-[6px]">
                          <input
                            value={resolveDraft.providerRef}
                            onChange={e => setResolveDraft(d => ({ ...d, providerRef: e.target.value }))}
                            placeholder="Provider booking reference"
                            style={formInputStyle}
                            aria-label="Provider booking reference"
                          />
                        </div>
                      )}
                      <label className="flex items-center gap-[7px] text-[12px] text-ink mb-[6px] cursor-pointer">
                        <input
                          type="radio"
                          name={`resolve-${q.id}`}
                          checked={resolveDraft.resolution === 'failed_final'}
                          onChange={() => setResolveDraft(d => ({ ...d, resolution: 'failed_final' }))}
                        />
                        It failed — close the operation
                      </label>
                      <input
                        value={resolveDraft.reason}
                        onChange={e => setResolveDraft(d => ({ ...d, reason: e.target.value }))}
                        placeholder="Reason — recorded in the audit log"
                        style={formInputStyle}
                        aria-label="Reason for resolving"
                      />
                      <div className="flex gap-[7px] mt-[8px]">
                        <ServerActionButton
                          emphasis
                          disabled={anyServerBusy || !resolveReady}
                          title="Records the outcome on the platform. Booked requires the provider's own reference."
                          onClick={() => {
                            onResolve!(
                              q.id,
                              resolveDraft.resolution,
                              resolveDraft.resolution === 'booked' ? resolveDraft.providerRef.trim() : undefined,
                              resolveDraft.reason.trim()
                            );
                            setForm(null);
                          }}
                        >
                          Record outcome
                        </ServerActionButton>
                        <ServerActionButton title="Close this form" onClick={() => setForm(null)}>
                          Back
                        </ServerActionButton>
                      </div>
                    </div>
                  ) : (
                    <div className="flex gap-[7px] flex-wrap">
                      {showAlternatives && (
                        <>
                          {/* Waitlist only when the server can actually do it: it refuses
                              to route a pre-order there (spec 11 §21.4), so a pre-order
                              row offers decline alone, and says why. */}
                          {actions.canWaitlistAlternatives ? (
                            <ServerActionButton
                              emphasis
                              disabled={anyServerBusy}
                              title="Enrols the guest on the waitlist for a matching slot."
                              onClick={() => onRespondAlternatives!(q.id, 'waitlist')}
                            >
                              {serverBusy ? 'Working…' : 'Waitlist this guest'}
                            </ServerActionButton>
                          ) : (
                            <span
                              className="inline-flex items-center text-[11px] text-t3"
                              title="The platform does not waitlist pre-order bookings — re-confirm a new time with the guest, or decline."
                            >
                              Pre-order — can’t be waitlisted
                            </span>
                          )}
                          <ServerActionButton
                            disabled={anyServerBusy}
                            title="Closes the operation as not booked."
                            onClick={() => onRespondAlternatives!(q.id, 'decline')}
                          >
                            Decline request
                          </ServerActionButton>
                        </>
                      )}
                      {showResume && (
                        <ServerActionButton
                          emphasis
                          disabled={anyServerBusy}
                          title="The platform re-checks what happened with the provider. You'll be asked why."
                          onClick={() => openForm(q.id, 'resume')}
                        >
                          {serverBusy ? 'Working…' : 'Send back for checks'}
                        </ServerActionButton>
                      )}
                      {showResolve && (
                        <ServerActionButton
                          disabled={anyServerBusy}
                          title="Record the outcome you confirmed with the provider."
                          onClick={() => openForm(q.id, 'resolve')}
                        >
                          Resolve…
                        </ServerActionButton>
                      )}
                      {showCancel && (
                        <ServerActionButton
                          disabled={anyServerBusy}
                          title="Asks the platform to stop this operation at its next safe point. Not instant, and not a booking cancellation."
                          onClick={() => onRequestCancellation!(q.id)}
                        >
                          Request cancellation
                        </ServerActionButton>
                      )}
                    </div>
                  )}
                </div>
              )}

              <div className={`mt-3 gap-[10px] ${isPhone ? 'flex flex-col' : 'flex items-center'}`}>
                {/* Only what the operation projection actually carries. A channel badge, a
                    retry count and a "waiting" duration used to be rendered here; the API
                    models none of the three on an operation. */}
                <div className="flex items-center gap-[8px] flex-wrap min-w-0">
                  {q.channel && (
                    <>
                      <Chan channel={q.channel} size={18} radius={5} />
                      <span className="font-mono text-[10.5px] text-t2">{chMeta(q.channel).short}</span>
                    </>
                  )}
                  {q.retries !== undefined && (
                    <span className="font-mono text-[10.5px] tracking-[0.06em] uppercase text-t3">{q.retries} retries</span>
                  )}
                  {q.slot && <span className="font-mono text-[10.5px] text-t3">updated {q.slot}</span>}
                </div>
                <div className={`flex gap-2 ${isPhone ? '' : 'ml-auto'}`}>
                  <GhostButton onClick={() => onDismiss(q.id)} style={{ color: '#6B6B6B', fontWeight: 500, ...(isPhone ? { flex: 1 } : null) }}>Dismiss</GhostButton>
                  {/* A LOCAL acknowledgement, deliberately distinct from the platform actions
                      above: the only resolve endpoint is manual_review-only, so claiming
                      "resolved" here would assert an outcome the platform never produced. */}
                  <span
                    title="Removes this from your queue only. The platform keeps working on the operation."
                    style={isPhone ? { flex: 1, display: 'flex' } : undefined}
                  >
                    <PrimaryButton
                      busy={b}
                      onClick={() => onFinish(q.id)}
                      style={isPhone ? { flex: 1 } : { padding: '0 15px' }}
                    >
                      Clear from queue
                    </PrimaryButton>
                  </span>
                </div>
              </div>
            </div>
          );
        })}
      </div>
      <div className="h-2" />
    </div>
  );
}
