import { useState } from "react";
import { Icon } from "./Icon";

interface CorrelationIdChipProps {
  /** The id itself. Rendered verbatim — never truncated in the copied value. */
  value: string;
  /** What kind of id this is, e.g. "correlation", "message", "trace". Shown as the label. */
  label?: string;
  /** Visible characters before eliding the middle. The full value is always copied. */
  head?: number;
  tail?: number;
}

/**
 * A copyable identifier.
 *
 * These ids are the only way an operator can hand a case to someone who can read the logs, so
 * the whole point is that what lands on the clipboard is the **complete** value. The display is
 * elided in the middle to fit a row; `copy` always writes `value` in full, and the `title`
 * carries it too for anyone who cannot use the button.
 *
 * Domain-free: it does not know what a correlation id, message id or trace id means.
 */
export function CorrelationIdChip({ value, label = "id", head = 8, tail = 6 }: CorrelationIdChipProps) {
  const [copied, setCopied] = useState(false);

  const shown =
    value.length > head + tail + 1 ? `${value.slice(0, head)}…${value.slice(-tail)}` : value;

  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1400);
    } catch {
      // Clipboard is permission-gated and absent in some embedded webviews. The full value is
      // in the title attribute, so a failure degrades to "select it yourself" rather than to
      // a broken control claiming success.
      setCopied(false);
    }
  }

  return (
    <span
      className="inline-flex items-center gap-[6px] max-w-full text-[11px] px-[8px] py-[3px] rounded-[7px] border border-cd-border"
      style={{ background: '#F7F7F5' }}
    >
      <span className="text-t3 shrink-0">{label}</span>
      <code
        className="font-mono text-[11px] text-ink truncate"
        title={value}
      >
        {shown}
      </code>
      <button
        type="button"
        onClick={() => { void copy(); }}
        aria-label={copied ? `${label} copied` : `Copy ${label}`}
        className="shrink-0 inline-flex items-center border-none bg-transparent cursor-pointer p-0 text-t3 hover:text-ink transition-colors"
      >
        <Icon name={copied ? 'check' : 'columns'} size={12} strokeWidth={2.2} color={copied ? '#15924B' : 'currentColor'} />
      </button>
      {/* Announced without stealing focus — the icon swap alone is invisible to a screen reader. */}
      <span role="status" aria-live="polite" className="sr-only">
        {copied ? `${label} copied to clipboard` : ''}
      </span>
    </span>
  );
}
