export type DiffKind = 'added' | 'changed' | 'removed' | 'unchanged';

export interface DiffItem {
  id: string;
  /** The item's name, as the operator knows it. */
  name: string;
  kind: DiffKind;
  /** Secondary line, e.g. "45 min". */
  meta?: string;
  /** Previous value (money already formatted). Shown struck through on `changed`/`removed`. */
  before?: string | null;
  /** New value. Shown bold on `added`/`changed`. */
  after?: string | null;
}

interface DiffViewProps {
  items: readonly DiffItem[];
  /** Include `unchanged` rows. Off by default — the point is what MOVES. */
  showUnchanged?: boolean;
  emptyLabel?: string;
}

/**
 * Before/after review of a proposed change set (design 4a · "DiffView row").
 *
 * The operator is about to publish these values, so an unintended change must be impossible to
 * miss. Unchanged rows are therefore hidden by default — a list where every item is shown
 * buries the one that actually moved — and the rollup states the counts outright so the shape
 * of the change is legible before reading a single row.
 *
 * Change kind is carried by an accent bar, a **word** ("New"/"Changed"/"Removed") and the
 * struck-through old value — never by colour alone.
 */
export function DiffView({ items, showUnchanged = false, emptyLabel = "No changes." }: DiffViewProps) {
  const moved = items.filter(i => i.kind !== 'unchanged');
  const shown = showUnchanged ? items : moved;

  if (shown.length === 0) {
    return <div className="text-[12.5px] text-t3 py-[10px]">{emptyLabel}</div>;
  }

  return (
    <div>
      <div className="text-[11.5px] text-t3 mb-[8px]">{summarise(items)}</div>
      <ul className="list-none p-0 m-0 flex flex-col gap-[6px]">
        {shown.map(item => {
          const meta = DIFF_META[item.kind];
          return (
            <li
              key={item.id}
              className="flex items-center gap-[10px] rounded-[10px] border border-cd-border overflow-hidden"
              style={{ background: '#fff' }}
            >
              <span aria-hidden="true" className="w-[3px] self-stretch shrink-0" style={{ background: meta.accent }} />
              <div className="flex-1 min-w-0 py-[9px]">
                <div className="text-[13px] font-semibold text-ink truncate">{item.name}</div>
                {item.meta && <div className="text-[11.5px] text-t3">{item.meta}</div>}
              </div>

              {(item.before || item.after) && (
                <div className="flex items-center gap-[7px] shrink-0 text-[12.5px]">
                  {item.before && item.kind !== 'added' && (
                    <span className="text-t3 line-through">{item.before}</span>
                  )}
                  {item.after && item.kind !== 'removed' && (
                    <span className="font-semibold text-ink">{item.after}</span>
                  )}
                </div>
              )}

              {meta.label && (
                <span
                  className="shrink-0 mr-[10px] text-[9.5px] font-bold uppercase tracking-[0.08em] px-[7px] py-[3px] rounded-full"
                  style={{ background: meta.fill, color: meta.text }}
                >
                  {meta.label}
                </span>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/** "2 new · 1 changed · 1 removed" — or an explicit no-op. */
export function summarise(items: readonly DiffItem[]): string {
  const n = (k: DiffKind) => items.filter(i => i.kind === k).length;
  const parts = [
    n('added') ? `${n('added')} new` : null,
    n('changed') ? `${n('changed')} changed` : null,
    n('removed') ? `${n('removed')} removed` : null,
  ].filter(Boolean);
  return parts.length ? parts.join(' · ') : 'Nothing changed';
}

const DIFF_META: Record<DiffKind, { label: string | null; accent: string; fill: string; text: string }> = {
  added:     { label: 'New',       accent: '#15924B', fill: '#E7F6EC', text: '#0B6431' },
  changed:   { label: 'Changed',   accent: '#C98A09', fill: '#FBF3DF', text: '#7E5606' },
  removed:   { label: 'Removed',   accent: '#D23B30', fill: '#FBEBEA', text: '#93231B' },
  unchanged: { label: null,        accent: '#E4E4E0', fill: '#F5F5F3', text: '#6B6B6B' },
};
