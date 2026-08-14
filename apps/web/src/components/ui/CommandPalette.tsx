import { useState, useEffect, useRef } from 'react';
import { Icon } from './Icon';
import { IconName } from './Icon';
import type { Customer } from '../../data/console';

interface CommandPaletteProps {
  open: boolean;
  onClose: () => void;
  onNavigate: (screen: string) => void;
  onCreateBooking: () => void;
  onOpenCustomer: (id: string) => void;
  /**
   * The tenant's real customers. When this is `undefined` the list has never loaded and
   * customer search is empty — never the mock directory.
   */
  customers?: Customer[];
}

const SCREENS: { key: string; label: string; icon: IconName; group: string }[] = [
  { key: 'today',         label: 'Today',              icon: 'today',    group: 'Navigate' },
  { key: 'queue',         label: 'Needs action',        icon: 'alert',    group: 'Navigate' },
  { key: 'conversations', label: 'Conversations',       icon: 'message',  group: 'Navigate' },
  { key: 'waitlist',      label: 'Waitlist',            icon: 'clock',    group: 'Navigate' },
  { key: 'customers',     label: 'Customers',           icon: 'users',    group: 'Navigate' },
  { key: 'deposits',      label: 'Deposits & payouts',  icon: 'card',     group: 'Navigate' },
  { key: 'campaigns',     label: 'Campaigns',           icon: 'megaphone',group: 'Navigate' },
  { key: 'analytics',     label: 'Analytics',           icon: 'chart',    group: 'Navigate' },
  { key: 'settings',      label: 'Settings',            icon: 'gear',     group: 'Navigate' },
];

interface Item {
  icon: IconName;
  label: string;
  sub: string;
  onSelect: () => void;
}

export function CommandPalette({ open, onClose, onNavigate, onCreateBooking, onOpenCustomer, customers }: CommandPaletteProps) {
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (open) {
      setQuery('');
      setSelected(0);
      setTimeout(() => inputRef.current?.focus(), 50);
    }
  }, [open]);

  const q = query.toLowerCase();

  const actionItems: Item[] = ('new booking'.includes(q) || q.length === 0)
    ? [{ icon: 'plus', label: 'New booking', sub: 'Action', onSelect: () => { onCreateBooking(); onClose(); } }]
    : [];

  const screenItems: Item[] = SCREENS.filter(s => s.label.toLowerCase().includes(q)).map(s => ({
    icon: s.icon,
    label: s.label,
    sub: 'Navigate',
    onSelect: () => { onNavigate(s.key); onClose(); },
  }));

  const searchable = customers ?? [];
  const customerItems: Item[] = q.length > 1
    ? searchable.filter(c => c.name.toLowerCase().includes(q)).slice(0, 4).map(c => ({
        icon: 'user' as IconName,
        label: c.name,
        sub: c.fav || `${c.visits} bookings`,
        onSelect: () => { onOpenCustomer(c.id); onClose(); },
      }))
    : [];

  const items: Item[] = [...actionItems, ...screenItems, ...customerItems];

  useEffect(() => { setSelected(0); }, [query]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { onClose(); return; }
      if (e.key === 'ArrowDown') { e.preventDefault(); setSelected(s => Math.min(s + 1, items.length - 1)); }
      if (e.key === 'ArrowUp') { e.preventDefault(); setSelected(s => Math.max(s - 1, 0)); }
      if (e.key === 'Enter') { e.preventDefault(); items[selected]?.onSelect(); }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, items, selected, onClose]);

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-[100] flex items-start justify-center pt-[15vh]"
      style={{ animation: 'fadeIn 100ms ease-out' }}
    >
      <div className="absolute inset-0 bg-black/30 backdrop-blur-[2px]" onClick={onClose} />
      <div className="relative w-full max-w-[560px] mx-4 bg-white rounded-[16px] shadow-[0_24px_64px_-16px_rgba(0,0,0,0.4)] overflow-hidden border border-cd-border">
        {/* Search input */}
        <div className="flex items-center gap-3 px-4 py-3.5 border-b border-hair">
          <Icon name="search" size={16} strokeWidth={2} color="#9A9A9A" />
          <input
            ref={inputRef}
            value={query}
            onChange={e => setQuery(e.target.value)}
            placeholder="Search screens, customers, actions…"
            className="flex-1 text-[15px] text-ink outline-none bg-transparent placeholder:text-t3"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          />
          <kbd className="font-mono text-[10px] text-t3 border border-cd-border rounded px-1.5 py-0.5">ESC</kbd>
        </div>

        {/* Results */}
        <div className="max-h-[380px] overflow-y-auto py-1.5">
          {items.length === 0 && (
            <div className="px-4 py-8 text-center text-[13.5px] text-t3">No results for "{query}"</div>
          )}
          {items.map((item, i) => (
            <button
              key={i}
              type="button"
              onMouseDown={item.onSelect}
              className="flex items-center gap-3 w-full px-4 py-2.5 border-none cursor-pointer transition-colors text-left"
              style={{
                background: i === selected ? '#F5F5F3' : 'transparent',
                fontFamily: "'Inter Tight', system-ui, sans-serif",
              }}
              onMouseEnter={() => setSelected(i)}
            >
              <span className="w-8 h-8 rounded-[9px] bg-wash2 flex items-center justify-center shrink-0">
                <Icon name={item.icon} size={16} strokeWidth={1.9} color="#0A0A0A" />
              </span>
              <div className="flex-1 min-w-0">
                <div className="text-[13.5px] font-semibold text-ink">{item.label}</div>
                <div className="text-[11.5px] text-t3">{item.sub}</div>
              </div>
              {i === selected && (
                <kbd className="font-mono text-[10px] text-t3 border border-cd-border rounded px-1.5 py-0.5 shrink-0">↵</kbd>
              )}
            </button>
          ))}
        </div>

        {/* Footer */}
        <div className="flex items-center gap-4 px-4 py-2 border-t border-hair bg-wash">
          <span className="font-mono text-[10px] text-t3">↑↓ navigate</span>
          <span className="font-mono text-[10px] text-t3">↵ select</span>
          <span className="font-mono text-[10px] text-t3">esc close</span>
        </div>
      </div>
    </div>
  );
}
