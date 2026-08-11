import { Icon } from "./Icon";

export interface Step {
  id: string;
  label: string;
}

interface StepperProps {
  steps: readonly Step[];
  /**
   * The step the subject is on. An id that is not in `steps` renders everything as upcoming
   * rather than throwing — a lifecycle can grow a value this console has not been rebuilt for,
   * and a crashed panel is worse than an unhighlighted one.
   */
  currentId: string;
  /**
   * Ids that are terminal-but-not-the-happy-path (e.g. `quarantined`). Drawn as **stopped**, so
   * a dead lifecycle is never rendered as one still in progress.
   */
  haltedIds?: readonly string[];
}

/**
 * Progress through a known, ordered lifecycle (design 4a · primitive).
 *
 * Completed steps carry a check, the current step its number, later steps a muted number, and
 * the connector is solid up to the current step. When `currentId` is halted the step is drawn
 * stopped and nothing later is implied — a halted lifecycle that reads as "in progress" tells
 * the operator to keep waiting for something that will never arrive.
 *
 * State reaches assistive tech through `aria-current` and a visually-hidden status word, not
 * through colour.
 */
export function Stepper({ steps, currentId, haltedIds = [] }: StepperProps) {
  const currentIndex = steps.findIndex(s => s.id === currentId);
  const halted = haltedIds.includes(currentId);

  return (
    <ol className="flex flex-wrap items-center list-none p-0 m-0">
      {steps.map((step, i) => {
        const state: StepState =
          currentIndex < 0 ? 'upcoming'
            : i < currentIndex ? 'done'
              : i === currentIndex ? (halted ? 'stopped' : 'current')
                : 'upcoming';
        const s = STATES[state];
        // Solid up to the current step; the tail stays grey so "how far" is legible at a glance.
        const connectorDone = currentIndex >= 0 && i < currentIndex && !halted;

        return (
          <li key={step.id} className="flex items-center">
            <span
              className="inline-flex items-center gap-[7px]"
              aria-current={state === 'current' || state === 'stopped' ? 'step' : undefined}
            >
              <span
                aria-hidden="true"
                className="w-[22px] h-[22px] rounded-full inline-flex items-center justify-center shrink-0 text-[11px] font-bold"
                style={{ background: s.bg, color: s.fg, border: `1px solid ${s.border}` }}
              >
                {state === 'done'
                  ? <Icon name="check" size={12} strokeWidth={2.6} color={s.fg} />
                  : state === 'stopped'
                    ? <Icon name="alert" size={12} strokeWidth={2.4} color={s.fg} />
                    : i + 1}
              </span>
              <span className="text-[12px] font-semibold whitespace-nowrap" style={{ color: s.label }}>
                {step.label}
              </span>
              <span className="sr-only"> — {s.word}</span>
            </span>
            {i < steps.length - 1 && (
              <span
                aria-hidden="true"
                className="w-[18px] h-[2px] mx-[8px] rounded-full shrink-0"
                style={{ background: connectorDone ? '#0A0A0A' : '#E4E4E0' }}
              />
            )}
          </li>
        );
      })}
    </ol>
  );
}

type StepState = 'done' | 'current' | 'upcoming' | 'stopped';

const STATES: Record<StepState, {
  bg: string; fg: string; border: string; label: string; word: string;
}> = {
  done:     { bg: '#0A0A0A', fg: '#fff',    border: '#0A0A0A', label: '#0A0A0A', word: 'complete' },
  current:  { bg: '#0A0A0A', fg: '#fff',    border: '#0A0A0A', label: '#0A0A0A', word: 'in progress' },
  stopped:  { bg: '#FBEBEA', fg: '#93231B', border: '#F0D2CF', label: '#93231B', word: 'stopped' },
  upcoming: { bg: '#fff',    fg: '#9A9A9A', border: '#EAEAEA', label: '#9A9A9A', word: 'not started' },
};
