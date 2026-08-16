import { useState, useRef, useEffect } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Customer, chMeta, money } from "../../../data/console";
import { Avatar } from "../../ui/Avatar";
import { Icon } from "../../ui/Icon";
import { Drawer } from "../../ui/Drawer";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { SectionLabel } from "../../ui/SectionLabel";
import { Tag } from "../../ui/StatusTags";
import { Pills } from "../../ui/Pills";
import { queryKeys } from "../../../api/queryKeys";
import { setCustomerNotes } from "../../../features/customers/api";

function useRetained<T>(val: T | null, open: boolean): T | null {
  const ref = useRef<T | null>(val);
  if (val) ref.current = val;
  return open ? val : ref.current;
}

interface CustomerProfileProps {
  device: 'phone' | 'desktop';
  c: Customer | null;
  onClose: () => void;
  onNewBooking?: () => void;
  onMessage?: (name: string) => void;
}

function CustomerProfile({ device, c, onClose, onNewBooking, onMessage }: CustomerProfileProps) {
  const open = !!c;
  const cc = useRetained(c, open);
  const queryClient = useQueryClient();
  const [noteDraft, setNoteDraft] = useState("");
  const [editingNote, setEditingNote] = useState(false);

  useEffect(() => {
    setNoteDraft(cc?.note ?? "");
    setEditingNote(false);
  }, [cc?.id, cc?.note]);

  const noteMut = useMutation({
    mutationFn: (note: string) => setCustomerNotes(cc!.id, { note }),
    onSuccess: () => {
      setEditingNote(false);
      void queryClient.invalidateQueries({ queryKey: queryKeys.customers.all() });
    },
  });

  const risk = !!cc && cc.noShows >= 2;
  const cm = cc ? chMeta(cc.channel) : null;
  const stats: [string, string][] = cc
    ? [
        ['Bookings', String(cc.visits)],
        ['No-shows', String(cc.noShows)],
        ...(cc.spend > 0 ? [['Lifetime', money(cc.spend)] as [string, string]] : []),
      ]
    : [];

  return (
    <Drawer
      open={open}
      onClose={onClose}
      device={device}
      eyebrow="Customer profile"
      title={cc?.name ?? 'Customer'}
      footer={
        <div className="flex gap-2">
          <GhostButton
            style={{ flex: 1 }}
            icon="message"
            onClick={() => { onClose(); onMessage?.(cc?.name ?? ''); }}
          >
            Message
          </GhostButton>
          <PrimaryButton
            style={{ flex: 1 }}
            icon="plus"
            onClick={() => { onClose(); setTimeout(() => onNewBooking?.(), 120); }}
          >
            New booking
          </PrimaryButton>
        </div>
      }
    >
      {cc && (
        <>
          <div className="flex items-center gap-3 mb-4">
            <Avatar name={cc.name} size={52} radius={15} font="19px" />
            <div className="flex-1 min-w-0">
              <div className="flex gap-[6px] flex-wrap mb-[5px]">
                {cc.tags.length ? cc.tags.map(t => <Tag key={t}>{t}</Tag>) : <Tag>Customer</Tag>}
              </div>
              {cc.phone ? <span className="font-mono text-[11.5px] text-t2">{cc.phone}</span> : null}
            </div>
          </div>

          <div className="flex gap-[9px] mb-[14px]">
            {stats.map((s, i) => (
              <div key={i}
                className="flex-1 rounded-[12px] p-[12px_13px]"
                style={{
                  background: s[0] === 'No-shows' && risk ? '#FBF7F6' : '#FBFBFA',
                  border: `1px solid ${s[0] === 'No-shows' && risk ? '#F2D7D4' : '#EFEFEF'}`,
                }}>
                <span className="font-mono text-[19px] font-bold tracking-[-0.02em] block"
                  style={{ color: s[0] === 'No-shows' && risk ? '#B82C22' : '#0A0A0A' }}>
                  {s[1]}
                </span>
                <div className="text-[11px] text-t2 mt-[2px]">{s[0]}</div>
              </div>
            ))}
          </div>

          {risk && (
            <div className="flex items-start gap-[9px] p-[11px_13px] rounded-[12px] mb-[14px]"
              style={{ background: '#FBF3DF', border: '1px solid #F0DCA8' }}>
              <Icon name="alert" size={16} strokeWidth={2} color="#C98A09" style={{ flexShrink: 0, marginTop: 1 }} />
              <div className="text-[12.5px] leading-[1.45]" style={{ color: '#7E5606' }}>
                No-show risk. The agent requires a deposit on every booking for this customer.
              </div>
            </div>
          )}

          <div className="bg-white border border-cd-border rounded-[14px] p-[4px_14px] mb-[14px]">
            {cc.fav && (
              <div className="flex items-center justify-between py-[11px] border-b border-hair">
                <span className="text-[12.5px] text-t2">Usual</span>
                <span className="text-[13.5px] font-medium text-ink">{cc.fav}</span>
              </div>
            )}
            {cc.channel && cc.channel !== 'web' && (
              <div className="flex items-center justify-between py-[11px] border-b border-hair">
                <span className="text-[12.5px] text-t2">Prefers</span>
                <span className="inline-flex items-center gap-[5px] text-[13.5px] font-medium text-ink">
                  <span className="w-[7px] h-[7px] rounded-full" style={{ background: cm?.color }} />
                  {cm?.label}
                </span>
              </div>
            )}
            <div className="flex items-center justify-between py-[11px]">
              <span className="text-[12.5px] text-t2">Last visit</span>
              <span className="text-[13.5px] font-medium text-ink">{cc.lastVisit}</span>
            </div>
          </div>

          <div>
            <SectionLabel>Note</SectionLabel>
            {editingNote ? (
              <div>
                <textarea
                  value={noteDraft}
                  onChange={e => setNoteDraft(e.target.value)}
                  rows={3}
                  className="w-full border border-cd-border rounded-[10px] px-3 py-2 text-[13px] text-ink resize-none outline-none"
                  style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                />
                <div className="flex gap-2 mt-2">
                  <button
                    type="button"
                    disabled={noteMut.isPending}
                    onClick={() => noteMut.mutate(noteDraft)}
                    className="text-[12.5px] font-semibold text-white bg-ink px-3 py-1.5 rounded-[7px] border-none cursor-pointer disabled:opacity-40"
                    style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                  >
                    Save
                  </button>
                  <button
                    type="button"
                    onClick={() => { setEditingNote(false); setNoteDraft(cc.note); }}
                    className="text-[12.5px] text-t2 bg-transparent border-none cursor-pointer"
                    style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                  >
                    Cancel
                  </button>
                </div>
              </div>
            ) : (
              <>
                {cc.note ? (
                  <div className="text-[13px] text-t2 leading-[1.55] px-[2px]">{cc.note}</div>
                ) : (
                  <div className="text-[12.5px] text-t3 px-[2px]">No note yet.</div>
                )}
                <button
                  type="button"
                  onClick={() => setEditingNote(true)}
                  className="mt-2 text-[12px] font-semibold text-[#2F6BEB] bg-transparent border-none cursor-pointer p-0"
                  style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                >
                  {cc.note ? "Edit note" : "+ Add note"}
                </button>
              </>
            )}
          </div>
        </>
      )}
    </Drawer>
  );
}

interface CustomersScreenProps {
  device: 'phone' | 'desktop';
  /** Live customers from the API. Undefined while the list has never loaded — empty, not mocks. */
  customers?: Customer[];
  onNewBooking?: () => void;
  onMessage?: (customerName: string) => void;
  focusCustomerId?: string | null;
  onClearFocus?: () => void;
}

export function CustomersScreen({ device, customers, onNewBooking, onMessage, focusCustomerId, onClearFocus }: CustomersScreenProps) {
  const source = customers ?? [];
  const [q, setQ] = useState('');
  const [sel, setSel] = useState<Customer | null>(null);

  useEffect(() => {
    if (focusCustomerId) {
      const c = source.find(c => c.id === focusCustomerId) ?? null;
      if (c) setSel(c);
      onClearFocus?.();
    }
  }, [focusCustomerId]);
  const [customerFilter, setCustomerFilter] = useState<'all' | 'vip' | 'at-risk' | 'new'>('all');
  const [sort, setSort] = useState<'default' | 'visits' | 'noshows'>('default');

  let list = source.filter(c => c.name.toLowerCase().includes(q.toLowerCase()));
  if (customerFilter === 'vip') list = list.filter(c => c.visits >= 5);
  if (customerFilter === 'at-risk') list = list.filter(c => c.noShows >= 2);
  if (customerFilter === 'new') list = list.filter(c => c.tags.includes('New'));
  if (sort === 'visits') list = [...list].sort((a, b) => b.visits - a.visits);
  if (sort === 'noshows') list = [...list].sort((a, b) => b.noShows - a.noShows);

  return (
    <div>
      <div className="relative mb-[10px]">
        <span className="absolute left-[13px] top-1/2 -translate-y-1/2">
          <Icon name="search" size={16} strokeWidth={2} color="#9A9A9A" />
        </span>
        <input
          value={q}
          onChange={e => setQ(e.target.value)}
          placeholder="Search customers"
          className="w-full h-[40px] border border-cd-border rounded-[10px] text-[13px] text-ink bg-white outline-none"
          style={{ paddingLeft: 38, paddingRight: 12, fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        />
      </div>

      <div className="flex items-center justify-between gap-3 mb-3">
        <Pills
          value={customerFilter}
          onChange={(v) => setCustomerFilter(v as 'all' | 'vip' | 'at-risk' | 'new')}
          options={[
            { value: 'all',     label: 'All' },
            { value: 'vip',     label: 'VIP' },
            { value: 'at-risk', label: 'At-risk' },
            { value: 'new',     label: 'New' },
          ]}
        />
        <select
          value={sort}
          onChange={e => setSort(e.target.value as 'default' | 'visits' | 'noshows')}
          className="text-[12px] font-medium text-t2 bg-white border border-cd-border rounded-lg px-2 py-1.5 cursor-pointer outline-none hover:border-[#DEDEDA] transition-colors shrink-0"
          style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          <option value="default">Default</option>
          <option value="visits">Most visits</option>
          <option value="noshows">Most no-shows</option>
        </select>
      </div>

      <SectionLabel>{list.length} customers</SectionLabel>
      <div className="flex flex-col gap-2">
        {list.map(c => (
          <button
            key={c.id}
            onClick={() => setSel(c)}
            className="flex items-center gap-3 w-full bg-white border border-cd-border rounded-[13px] p-[12px_14px] cursor-pointer text-left transition-all duration-150 hover:shadow-[0_4px_14px_-6px_rgba(10,10,10,0.12)] hover:-translate-y-px"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          >
            <Avatar name={c.name} size={38} radius={11} font="14px" />
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-[7px]">
                <span className="text-[14.5px] font-semibold text-ink whitespace-nowrap overflow-hidden text-ellipsis">{c.name}</span>
                {c.isOperator && <Tag>You · console bookings</Tag>}
                {c.tags.slice(0, 2).map(t => <Tag key={t}>{t}</Tag>)}
              </div>
              <div className="text-[12px] text-t2 mt-[3px] whitespace-nowrap overflow-hidden text-ellipsis">
                {c.isOperator ? 'Bookings you made in the console — not a guest record' : c.fav}
              </div>
              <div className="text-[11px] text-t3 mt-[1px]">{c.lastVisit}</div>
            </div>
            <div className="text-right shrink-0">
              <span className="font-mono text-[13px] font-semibold text-ink">{c.visits}</span>
              <div className="text-[10.5px] mt-[1px]" style={{ color: c.noShows >= 2 ? '#B82C22' : '#9A9A9A' }}>
                {`${c.noShows} no-show${c.noShows === 1 ? '' : 's'}`}
              </div>
            </div>
          </button>
        ))}
      </div>
      <div className="h-2" />
      <CustomerProfile device={device} c={sel} onClose={() => setSel(null)} onNewBooking={onNewBooking} onMessage={onMessage} />
    </div>
  );
}
