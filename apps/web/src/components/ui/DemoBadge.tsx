import { Icon } from "./Icon";

interface DemoBadgeProps {
  /** What is still mocked and why, e.g. "no deposits endpoint yet". */
  reason?: string;
}

/**
 * Marks a surface that is still rendering demo data.
 *
 * The console is wired screen by screen, so some views are live and some are not. Showing
 * plausible-looking numbers with no marker would let an operator mistake them for their
 * venue's real figures — the badge makes the distinction visible rather than implied.
 */
export function DemoBadge({ reason }: DemoBadgeProps) {
  return (
    <span
      className="inline-flex items-center gap-[5px] px-[8px] py-[3px] rounded-[7px] text-[10.5px] font-semibold align-middle"
      style={{ background: '#FBF3DF', color: '#8A6400', border: '1px solid #F0DCA8' }}
      title={reason ? `Demo data — ${reason}` : 'Demo data — not from your account'}
    >
      <Icon name="alert" size={11} strokeWidth={2.4} color="#8A6400" />
      Demo data
    </span>
  );
}

interface PartialBadgeProps {
  /** What was cut short, e.g. "only the most recent bookings are shown". */
  reason: string;
}

/**
 * Marks a list the server reported as truncated.
 *
 * The read endpoints scan a bounded number of rows, so a very busy tenant can get a
 * partial list. Showing it unmarked would read as "this is everything" — which is exactly
 * the wrong thing to believe about a day's bookings or a customer search.
 */
export function PartialBadge({ reason }: PartialBadgeProps) {
  return (
    <span
      className="inline-flex items-center gap-[5px] px-[8px] py-[3px] rounded-[7px] text-[10.5px] font-semibold align-middle"
      style={{ background: '#FDF2F2', color: '#8A3A2F', border: '1px solid #F5DADA' }}
      title={`Partial results — ${reason}`}
    >
      <Icon name="alert" size={11} strokeWidth={2.4} color="#8A3A2F" />
      Partial results
    </span>
  );
}
