import { useEffect, useRef, useState } from "react";
import { Icon } from "./Icon";

/**
 * How long a revealed value stays on screen.
 *
 * Long enough to read a phone number aloud or copy it; short enough that walking away does not
 * leave PII up indefinitely.
 */
export const REVEAL_TTL_MS = 20_000;

interface MaskedFieldProps {
  label: string;
  /**
   * The value as the SERVER returned it. If the server already redacted it, that is what is
   * shown — this component never un-redacts anything, because it cannot: the plaintext is not
   * in the browser.
   */
  value: string | null | undefined;
  /**
   * Whether revealing is offered at all. Default false: the safe assumption for a field
   * someone bothered to mark as masked.
   */
  revealable?: boolean;
  /** Shown when `value` is null/empty — an absent value is a real answer, not an error. */
  emptyLabel?: string;
  /** Characters left visible at the end when masked. */
  tail?: number;
}

/**
 * Displays a value that should not be shoulder-surfed.
 *
 * Two things this deliberately does NOT do:
 *
 *  • It does not decrypt or un-redact. If the API returned `[REDACTED]`, that string is the
 *    value — masking here is about the screen, not about access control, and pretending
 *    otherwise would invite someone to "just reveal it" and be surprised.
 *  • It does not fetch anything on reveal. Reveal is a local toggle over data already in hand,
 *    so it can never trigger an audited read as a side effect of a UI gesture.
 *
 * An absent value renders as an explicit "not recorded" rather than blank, because blank reads
 * as "we failed to load it".
 */
export function MaskedField({
  label, value, revealable = false, emptyLabel = "Not recorded", tail = 4,
}: MaskedFieldProps) {
  const [revealed, setRevealed] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Auto-remask on a TIMER, not on the toggle button's blur.
  //
  // Blur looked right and worked on neither end: Safari does not focus a <button> on click, so
  // `focusout` never fired there and a revealed number stayed up for the life of the panel — the
  // indefinite exposure this is supposed to prevent, silently absent on that platform. In
  // Chrome/Firefox it fired too eagerly instead: clicking the value to select and copy it blurs
  // the button and re-masks before the selection is made, so reveal could not serve its one
  // purpose. A timer behaves identically everywhere and does not fight the copy gesture.
  useEffect(() => {
    if (!revealed) return;
    timer.current = setTimeout(() => setRevealed(false), REVEAL_TTL_MS);
    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
  }, [revealed]);

  const isEmpty = value === null || value === undefined || value.trim() === '';
  const masked = isEmpty ? '' : maskValue(value, tail);

  return (
    <div className="py-[7px]">
      <div className="text-[11px] text-t3 mb-[3px]">{label}</div>
      {isEmpty ? (
        <div className="text-[13px] text-t3 italic">{emptyLabel}</div>
      ) : (
        <div className="flex items-center gap-[8px]">
          <code className="font-mono text-[12.5px] text-ink break-all">
            {revealed ? value : masked}
          </code>
          {revealable && (
            <button
              type="button"
              onClick={() => setRevealed(r => !r)}
              aria-pressed={revealed}
              aria-label={revealed ? `Hide ${label}` : `Reveal ${label}`}
              className="shrink-0 inline-flex items-center border-none bg-transparent cursor-pointer p-0 text-t3 hover:text-ink transition-colors"
            >
              <Icon name={revealed ? 'shield' : 'search'} size={13} strokeWidth={2.1} color="currentColor" />
            </button>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Masks all but the last `tail` characters.
 *
 * Short values are masked ENTIRELY rather than partially: leaving the tail of a 5-character
 * value exposes most of it, which defeats the point.
 */
export function maskValue(value: string, tail = 4): string {
  const v = value.trim();

  // Clamped, and zero handled explicitly. `v.slice(-0)` is `v.slice(0)` — JavaScript has no
  // negative-zero index — so `tail = 0`, which reads as "reveal nothing", returned the ENTIRE
  // value appended after the bullets. The most conservative argument produced the least masking.
  // A negative or oversized tail is clamped for the same reason: this helper must be safe for
  // every value of a prop it accepts.
  const t = Number.isFinite(tail) ? Math.min(Math.max(Math.trunc(tail), 0), v.length) : 4;

  if (v.length <= t + 2) return '•'.repeat(Math.max(v.length, 4));
  return '•'.repeat(v.length - t) + (t > 0 ? v.slice(-t) : '');
}
