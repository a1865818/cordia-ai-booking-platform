import type { ReactNode } from "react";
import { Icon } from "./Icon";
import { Spinner } from "./Spinner";

/** The five workflow-step states the operations drawer distinguishes (design 4a / 1c). */
export type TimelineTone = 'done' | 'active' | 'failed' | 'attention' | 'pending';

export interface TimelineEntry {
  id: string;
  /** What happened. Use the server's own word where there is one. */
  title: string;
  /** ISO instant, or a relative label the server supplied. Absent when none was recorded. */
  at?: string | null;
  detail?: ReactNode;
  tone?: TimelineTone;
  /** e.g. "retry 2 / 4 · next in 2m" — shown verbatim, never computed here. */
  retryLabel?: string;
  /** Rendered under the step; present only when the platform says the step is recoverable. */
  action?: ReactNode;
}

interface TimelineProps {
  entries: readonly TimelineEntry[];
  emptyLabel?: string;
}

/**
 * An ordered list of things that happened (design 4a · primitive).
 *
 * Renders **only** what it is given. It does not infer a current step, synthesise a pending
 * tail, or fill gaps — these are read when someone is working out what actually occurred, and
 * a timeline that invents entries is indistinguishable from one reporting real events.
 *
 * An entry with no timestamp shows the event without one rather than guessing a time.
 * Marked up as an ordered list so sequence and length are conveyed non-visually, and each
 * state carries a visually-hidden word so it does not depend on the dot's colour.
 */
export function Timeline({ entries, emptyLabel = "Nothing recorded yet." }: TimelineProps) {
  if (entries.length === 0) {
    return <div className="text-[12.5px] text-t3 py-[10px]">{emptyLabel}</div>;
  }

  return (
    <ol className="list-none p-0 m-0">
      {entries.map((entry, i) => {
        const tone = TONES[entry.tone ?? 'pending'];
        const last = i === entries.length - 1;
        return (
          <li key={entry.id} className="flex gap-[11px]">
            <div className="flex flex-col items-center shrink-0" aria-hidden="true">
              <span
                className="w-[20px] h-[20px] rounded-full flex items-center justify-center shrink-0 mt-[1px]"
                style={{ background: tone.fill }}
              >
                {tone.spin
                  ? <Spinner size={10} color={tone.accent} track={tone.accent + '55'} />
                  : tone.icon
                    ? <Icon name={tone.icon} size={11} strokeWidth={2.6} color={tone.accent} />
                    : <span className="w-[6px] h-[6px] rounded-full" style={{ background: tone.accent }} />}
              </span>
              {!last && (
                <span
                  className="w-[2px] flex-1 my-[3px]"
                  style={{ background: entry.tone === 'done' ? '#CBE8D6' : '#EDEDEA' }}
                />
              )}
            </div>

            <div className={last ? "pb-[2px] min-w-0 flex-1" : "pb-[15px] min-w-0 flex-1"}>
              <div className="flex items-baseline gap-[7px] flex-wrap">
                <span className="text-[13px] font-semibold text-ink">{entry.title}</span>
                {entry.at && (
                  <time dateTime={isInstant(entry.at) ? entry.at : undefined} className="text-[11.5px] text-t3">
                    {formatInstant(entry.at)}
                  </time>
                )}
                <span className="sr-only">— {tone.word}</span>
              </div>

              {entry.detail && (
                <div className="text-[12.5px] text-t2 mt-[3px] leading-[1.5]">{entry.detail}</div>
              )}

              {entry.retryLabel && (
                <span
                  className="inline-block mt-[5px] font-mono text-[10.5px] px-[7px] py-[2px] rounded-full"
                  style={{ background: '#FBF3DF', color: '#7E5606' }}
                >
                  {entry.retryLabel}
                </span>
              )}

              {entry.action && <div className="mt-[7px]">{entry.action}</div>}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

const TONES: Record<TimelineTone, {
  accent: string; fill: string; icon?: 'check' | 'x' | 'alert'; spin?: boolean; word: string;
}> = {
  done:      { accent: '#15924B', fill: '#E7F6EC', icon: 'check', word: 'done' },
  active:    { accent: '#C98A09', fill: '#FBF3DF', spin: true,    word: 'in progress' },
  failed:    { accent: '#D23B30', fill: '#FBEBEA', icon: 'x',     word: 'failed' },
  attention: { accent: '#C98A09', fill: '#FBF3DF', icon: 'alert', word: 'needs attention' },
  pending:   { accent: '#9A9A9A', fill: '#F2F2F0',                word: 'not started' },
};

function isInstant(v: string): boolean {
  return !Number.isNaN(new Date(v).getTime());
}

/** Returns the raw string when it is not a parseable instant — never "Invalid Date". */
export function formatInstant(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString(undefined, {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  });
}
