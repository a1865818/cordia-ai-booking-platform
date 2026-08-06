import { useState } from "react";
import { Icon } from "./Icon";

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
  if (v.length <= tail + 2) return '•'.repeat(Math.max(v.length, 4));
  return '•'.repeat(v.length - tail) + v.slice(-tail);
}
