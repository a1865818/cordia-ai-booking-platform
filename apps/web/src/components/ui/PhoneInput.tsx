import React from "react";
import { normalisePhone } from "../../features/bookings/buildSubmission";

interface PhoneInputProps {
  value: string;
  onChange: (value: string) => void;
  /** Rendered under the field when the value cannot match what the platform stores. */
  warningId: string;
  placeholder?: string;
  ariaLabel?: string;
  /** The warning shown when the number is not in E.164 form. Callers word it for their context. */
  warning: React.ReactNode;
  style?: React.CSSProperties;
  className?: string;
}

/**
 * A phone field that warns, in one consistent way, when a number cannot be matched later.
 *
 * **It does not offer a country picker, and that omission is the point.** A `+61` prefix
 * segment would make `0400 111 222` structurally unenterable — genuinely attractive, since the
 * number lands in an immutable, hashed payload that can never be tidied afterwards. But the
 * platform records no country for a tenant anywhere, so the console would be *guessing* which
 * country to prepend. `PhoneMatching` (apps/api) and `normalisePhone` are both deliberately
 * formatting-only for exactly this reason: converting a national trunk prefix to a country code
 * would silently match one guest's booking to a stranger's number in any country whose national
 * format collides. Warning is the honest ceiling until a tenant country exists.
 *
 * What it does fix is drift: the "is this number usable?" rule was written twice, with two
 * different definitions, so the same number could be accepted by one screen and flagged by the
 * other. {@link looksUnnormalisedPhone} is now the single rule.
 */
export function PhoneInput({
  value, onChange, warningId, placeholder, ariaLabel, warning, style, className,
}: PhoneInputProps) {
  const unnormalised = looksUnnormalisedPhone(value);

  return (
    <>
      <input
        value={value}
        onChange={e => onChange(e.target.value)}
        inputMode="tel"
        autoComplete="tel"
        placeholder={placeholder}
        aria-label={ariaLabel}
        aria-describedby={unnormalised ? warningId : undefined}
        className={className}
        style={style}
      />
      {unnormalised && (
        <div
          id={warningId}
          role="status"
          className="mt-[6px] text-[11.5px] leading-[1.5]"
          style={{ color: '#7E5606' }}
        >
          {warning}
        </div>
      )}
    </>
  );
}

/**
 * Is this value something the platform will fail to match later?
 *
 * True only for a **non-empty** value that does not normalise to E.164. Empty is not a warning:
 * the guest phone is optional on a booking, and nagging about a field nobody filled in trains
 * operators to ignore the warning that matters.
 *
 * The rule is E.164 proper — a leading `+`, a country code that cannot start with `0`, and 7–15
 * digits total. The looser "does it start with `+`" check this replaces passed values like
 * `+0` and `+1`, which are equally unmatchable; a stricter rule warns on strictly more of the
 * numbers that genuinely will not be found.
 */
export function looksUnnormalisedPhone(raw: string): boolean {
  if (raw.trim() === "") return false;
  return !/^\+[1-9]\d{6,14}$/.test(normalisePhone(raw) ?? "");
}
