import type { CSSProperties } from "react";

export type ActionKind = 'primary' | 'ghost' | 'danger';

export interface ActionSpec {
  id: string;
  label: string;
  /**
   * Whether the platform will accept this action in the subject's CURRENT state.
   *
   * The whole contract of the component: an illegal action is **not rendered**. See below for
   * why it is not merely disabled.
   */
  legal: boolean;
  onClick: () => void;
  kind?: ActionKind;
  busy?: boolean;
  /** Marks an audited action — the caller opens a ReasonCodeDialog before firing it. */
  reason?: boolean;
  /** One-line reason the action is unavailable, shown only when `explainIllegal` is set. */
  whenIllegal?: string;
}

interface ActionBarProps {
  actions: readonly ActionSpec[];
  /**
   * Explain the actions that were withheld. Off by default — listing every state an object is
   * not in is noise. Turn it on where an operator is likely to come looking for one specific
   * action and needs to know why it is absent.
   */
  explainIllegal?: boolean;
  emptyLabel?: string;
}

/**
 * Renders **only the actions the platform will currently accept** (design 4a · pattern layer,
 * "only state-legal actions shown").
 *
 * Illegal actions are omitted, not disabled. A disabled button still advertises a capability
 * and invites the operator to hunt for the state that unlocks it; worse, "disabled" and "would
 * 409" look identical, so the operator learns to distrust the whole bar.
 *
 * The bar never decides legality itself — the caller derives `legal` from the subject's status,
 * which is the only place that knowledge lives (the design expresses this as a status→actions
 * map, and so should every caller).
 */
export function ActionBar({ actions, explainIllegal = false, emptyLabel }: ActionBarProps) {
  const legal = actions.filter(a => a.legal);
  const withheld = explainIllegal ? actions.filter(a => !a.legal && a.whenIllegal) : [];

  if (legal.length === 0 && withheld.length === 0) {
    return emptyLabel ? <div className="text-[12.5px] text-t3 py-[8px]">{emptyLabel}</div> : null;
  }

  return (
    <div>
      {legal.length > 0 ? (
        <div className="flex flex-wrap items-center gap-[8px]">
          {legal.map(a => (
            <button
              key={a.id}
              type="button"
              onClick={a.onClick}
              disabled={a.busy}
              className="cursor-pointer transition-all disabled:opacity-50 disabled:cursor-not-allowed hover:brightness-105"
              style={KINDS[a.kind ?? 'ghost']}
            >
              {a.busy ? 'Working…' : a.label}
            </button>
          ))}
        </div>
      ) : (
        emptyLabel && <div className="text-[12.5px] text-t3 py-[8px]">{emptyLabel}</div>
      )}

      {withheld.length > 0 && (
        <ul className="list-none p-0 mt-[9px] m-0">
          {withheld.map(a => (
            <li key={a.id} className="text-[11.5px] text-t3 leading-[1.5]">
              <span className="font-semibold">{a.label}</span> — {a.whenIllegal}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

const BASE: CSSProperties = {
  height: 32, padding: '0 12px', borderRadius: 9, fontSize: 12, fontWeight: 600,
  fontFamily: "'Inter Tight', system-ui, sans-serif",
};

const KINDS: Record<ActionKind, CSSProperties> = {
  primary: { ...BASE, background: '#0A0A0A', color: '#fff', border: '1px solid #0A0A0A' },
  ghost:   { ...BASE, background: '#fff', color: '#0A0A0A', border: '1px solid #E0E0DC' },
  danger:  { ...BASE, background: '#fff', color: '#B82C22', border: '1px solid #F0D2CF' },
};
