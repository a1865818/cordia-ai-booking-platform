import { useEffect, useId, useRef, useState } from "react";
import { PrimaryButton, GhostButton } from "./Button";

export interface ReasonOption {
  /** The value sent to the server. */
  value: string;
  /** Operator-facing label. */
  label: string;
}

/**
 * The canonical app-wide reason vocabulary (design 4a / 1c).
 *
 * Shared so every audited action offers the same words — a per-screen vocabulary makes the
 * audit trail unqueryable. `other` is always appended by the dialog itself.
 */
export const REASON_CODES: readonly ReasonOption[] = [
  { value: 'provider_unavailable', label: 'Provider unavailable' },
  { value: 'guest_requested_change', label: 'Guest requested change' },
  { value: 'duplicate_booking', label: 'Duplicate booking' },
  { value: 'payment_issue', label: 'Payment issue' },
  { value: 'capacity_conflict', label: 'Capacity conflict' },
];

export const OTHER_REASON = 'other';

/** Longest reason we will put on the wire. Headers are not a place for prose. */
const MAX_REASON = 400;

/**
 * Folds an optional note into a single reason string for endpoints that expose only one
 * reason field.
 *
 * The code stays the **leading token** so the audit trail stays groupable by it; the note
 * follows. Dropping the note instead would discard the only part the operator wrote themselves.
 *
 * **The result is sent as the `X-Reason-Code` HTTP header on most of these endpoints**, and a
 * header value is far more restricted than the `<textarea>` it came from:
 *
 *  • `Headers.set` throws a `TypeError` on CR/LF — a two-line note would reject the request
 *    before `fetch` was reached, surfacing as an opaque browser error with no hint that the
 *    note caused it;
 *  • it also throws above U+00FF, which an em dash or a curly quote (both of which a browser
 *    or an operator pasting from a document produces routinely) exceeds.
 *
 * So the note is flattened to single spaces and transliterated to a Latin-1-safe form here,
 * rather than left to explode at the call site. Sanitising beats rejecting: the operator's
 * words survive in substance, and the request goes through.
 */
export function composeReason(code: string, note: string): string {
  const flattened = note
    // Smart punctuation first — a browser or a pasted document produces these routinely, and
    // all four are above U+00FF.
    .replace(/[‘’]/g, "'")
    .replace(/[“”]/g, '"')
    .replace(/[–—]/g, '-')
    .replace(/…/g, '...')
    // Anything still outside Latin-1 (emoji, CJK, …) cannot be a header value at all.
    .replace(/[^ -ÿ]/g, '?')
    // Collapses the CR/LF a textarea produces, along with tabs and runs of spaces.
    .replace(/\s+/g, ' ')
    .trim();

  const composed = flattened ? `${code}: ${flattened}` : code;
  return composed.length > MAX_REASON ? composed.slice(0, MAX_REASON) : composed;
}

interface ReasonCodeDialogProps {
  open: boolean;
  /** The triggering action's own label, e.g. "Cancel" or `Resume at "Reserve capacity"`. */
  title: string;
  /** Extra consequence-specific context, shown under the standard subtitle. */
  body?: string;
  /** Offered reasons. Defaults to the canonical set; `Other` is always added. */
  options?: readonly ReasonOption[];
  /** Label for the confirming button. */
  confirmLabel: string;
  busy?: boolean;
  onCancel: () => void;
  /** Receives the chosen reason code and the optional free-text note. */
  onConfirm: (reasonCode: string, note: string) => void;
}

/**
 * Collects the operator's reason before an audited action (design 4a · pattern layer).
 *
 * **Nothing is preselected, and that is the point.** Every one of these actions writes the
 * reason to an immutable audit row; a default would put the same sentence on every row, which
 * records nothing and is worse than an empty field because it looks like evidence. Confirm
 * stays disabled until a reason is actually chosen.
 *
 * The note is separate from the code and optional: the code is the queryable dimension, the
 * note is the context a fixed vocabulary cannot carry.
 *
 * Beyond the mock: this adds `role="dialog"`, `aria-modal`, a labelled heading and Escape-to-
 * cancel. The design doc states outright that keyboard focus handling was not specified there,
 * so it is specified here rather than shipped missing.
 */
export function ReasonCodeDialog({
  open, title, body, options = REASON_CODES, confirmLabel, busy, onCancel, onConfirm,
}: ReasonCodeDialogProps) {
  const [choice, setChoice] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const headingId = useId();
  const groupId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);

  // Reset on each open: a reason chosen for a previous action must never carry into the next.
  useEffect(() => {
    if (open) { setChoice(null); setNote(''); }
  }, [open]);

  useEffect(() => {
    if (!open) return;
    dialogRef.current?.focus();
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape' && !busy) onCancel();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, busy, onCancel]);

  if (!open) return null;

  // "Other" with an empty note would write the literal string "other" to the audit trail —
  // which records strictly less than the named codes it was chosen over. If the operator is
  // reaching for Other, the note IS the reason.
  const otherNeedsNote = choice === OTHER_REASON && note.trim().length === 0;
  const canConfirm = choice !== null && !otherNeedsNote && !busy;
  const all = [...options, { value: OTHER_REASON, label: 'Other' }];

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-[18px]"
      style={{ background: 'rgba(20,20,18,0.32)' }}
      onMouseDown={e => { if (e.target === e.currentTarget && !busy) onCancel(); }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        tabIndex={-1}
        className="bg-white rounded-[14px] border border-cd-border w-full max-w-[430px] p-[18px] outline-none"
        style={{ boxShadow: '0 40px 80px -34px rgba(0,0,0,0.32)' }}
      >
        <div id={headingId} className="text-[16px] font-bold tracking-[-0.02em] text-ink">{title}</div>
        <div className="text-[12.5px] text-t2 mt-[5px] leading-[1.5]">
          Pick a reason — it's recorded on the audit trail.
        </div>
        {body && <div className="text-[12px] text-t2 mt-[7px] leading-[1.5]">{body}</div>}

        <div
          role="radiogroup"
          aria-labelledby={groupId}
          aria-required="true"
          className="mt-[14px]"
        >
          <div id={groupId} className="text-[12px] font-semibold mb-[8px]" style={{ color: '#55554F' }}>
            Reason <span style={{ color: '#D23B30' }} aria-hidden="true">*</span>
            <span className="sr-only">(required)</span>
          </div>
          <div className="flex flex-wrap gap-[6px]">
            {all.map(opt => {
              const selected = choice === opt.value;
              return (
                <button
                  key={opt.value}
                  type="button"
                  role="radio"
                  aria-checked={selected}
                  disabled={busy}
                  onClick={() => setChoice(opt.value)}
                  className="cursor-pointer transition-all disabled:cursor-not-allowed"
                  style={{
                    fontFamily: "'Inter Tight', system-ui, sans-serif",
                    fontSize: 12, fontWeight: 600, padding: '6px 11px', borderRadius: 999,
                    background: selected ? '#0A0A0A' : '#fff',
                    color: selected ? '#fff' : '#55554F',
                    border: `1px solid ${selected ? '#0A0A0A' : '#EAEAEA'}`,
                  }}
                >
                  {opt.label}
                </button>
              );
            })}
          </div>
        </div>

        <label className="block mt-[14px]">
          <div className="text-[12px] font-semibold mb-[6px]" style={{ color: '#55554F' }}>
            {choice === OTHER_REASON ? (
              <>Note <span style={{ color: '#D23B30' }} aria-hidden="true">*</span></>
            ) : 'Note (optional)'}
          </div>
          <textarea
            rows={3}
            value={note}
            onChange={e => setNote(e.target.value)}
            placeholder={choice === OTHER_REASON
              ? "Required — describe what happened"
              : "Add context for your team…"}
            aria-required={choice === OTHER_REASON}
            disabled={busy}
            className="w-full border border-cd-border rounded-[9px] p-[9px_10px] text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors resize-none"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          />
        </label>

        <div className="flex items-center justify-end gap-[8px] mt-[14px]">
          <GhostButton onClick={busy ? undefined : onCancel}>Cancel</GhostButton>
          <PrimaryButton
            busy={busy}
            disabled={!canConfirm}
            onClick={() => { if (choice) onConfirm(choice, note.trim()); }}
          >
            {confirmLabel}
          </PrimaryButton>
        </div>
      </div>
    </div>
  );
}
