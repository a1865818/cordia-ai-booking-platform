interface MoneyAmountProps {
  /**
   * The amount in **minor units** exactly as the platform stores it (cents, not dollars).
   * `null`/`undefined` is a real answer — the catalog genuinely records no price for some
   * items — and renders as {@link MoneyAmountProps.emptyLabel} rather than as a zero.
   */
  minor: number | null | undefined;
  /** ISO currency as the server stated it. Absent renders the bare amount, never a guessed symbol. */
  currency: string | null | undefined;
  /** Shown when there is no amount. A blank cell reads as "failed to load"; this does not. */
  emptyLabel?: string;
}

/**
 * Renders money the way the platform stores it: **minor units, converted once, never a float**.
 *
 * Two rules this exists to hold in one place:
 *
 *  • **No floating-point arithmetic on money.** The conversion is a single divide-and-fix at the
 *    render boundary; nothing accumulates, compares or rounds a currency value as a `number`.
 *  • **No invented currency.** A missing currency renders the amount alone. Defaulting to `A$`
 *    would state a currency the server never sent — and this console is not single-currency by
 *    contract, only by circumstance.
 *
 * A zero amount is NOT absent: `0` is a real price (a complimentary item) and renders as `0.00`.
 * Only `null`/`undefined` are treated as "not recorded", which is why the guard tests for those
 * two specifically rather than for falsiness.
 */
export function MoneyAmount({ minor, currency, emptyLabel = "No price" }: MoneyAmountProps) {
  const formatted = formatMoney(minor, currency);

  if (formatted === null) {
    return <span className="text-[12px] text-t3 italic">{emptyLabel}</span>;
  }

  return <span className="font-mono text-[12px] text-ink whitespace-nowrap">{formatted}</span>;
}

/**
 * The same conversion as a plain string, for callers that need text rather than an element —
 * `DiffView` compares `before`/`after` as strings, so a price diff cannot use the component.
 *
 * Returns `null` (not `""`) when there is no amount, so a caller can tell "not recorded" apart
 * from "recorded as empty" instead of both collapsing into a falsy string.
 */
export function formatMoney(
  minor: number | null | undefined,
  currency: string | null | undefined
): string | null {
  if (minor === null || minor === undefined || !Number.isFinite(minor)) return null;

  const amount = (minor / 100).toFixed(2);
  return currency ? `${currency} ${amount}` : amount;
}
