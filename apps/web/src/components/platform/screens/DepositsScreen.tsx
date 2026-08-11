import { useState } from "react";
import { DEPOSITS, PAYOUT, money } from "../../../data/console";
import { Avatar } from "../../ui/Avatar";
import { Icon } from "../../ui/Icon";
import { SectionLabel } from "../../ui/SectionLabel";
import { DepChip } from "../../ui/StatusTags";
import { Pills } from "../../ui/Pills";
import { DemoBadge } from "../../ui/DemoBadge";

export function DepositsScreen() {
  const [depFilter, setDepFilter] = useState('all');
  const filtered = depFilter === 'all' ? DEPOSITS : DEPOSITS.filter(d => d.status === depFilter);

  return (
    <div>
      {/* MOCK: no endpoint yet — spec 09 payment surface not exposed yet. */}
      <div className="mb-3"><DemoBadge reason="spec 09 payment surface not exposed yet" /></div>
      {/* Payout hero */}
      <div className="bg-ink rounded-[16px] p-[18px_19px] mb-4 text-white">
        <div className="flex items-start justify-between">
          <div>
            <div className="font-mono text-[10px] tracking-[0.14em] uppercase" style={{ color: 'rgba(255,255,255,0.55)' }}>
              Next payout · {PAYOUT.nextDate}
            </div>
            <span className="font-mono text-[34px] font-bold tracking-[-0.03em] text-white block mt-[6px]">
              {money(PAYOUT.nextAmount.toLocaleString())}
            </span>
          </div>
          <span className="inline-flex items-center gap-[5px] text-[11px] font-semibold px-[10px] py-1 rounded-full"
            style={{ background: 'rgba(221,242,71,0.16)', color: '#DDF247' }}>
            <Icon name="star" size={10} fill="#DDF247" />
            Stripe Connect
          </span>
        </div>
        <div className="flex gap-6 mt-4 pt-[15px] border-t" style={{ borderColor: 'rgba(255,255,255,0.12)' }}>
          <div>
            <span className="font-mono text-[16px] font-bold text-white">{money(PAYOUT.held.toLocaleString())}</span>
            <div className="text-[11px] mt-[2px]" style={{ color: 'rgba(255,255,255,0.55)' }}>held on bookings</div>
          </div>
          <div>
            <span className="font-mono text-[16px] font-bold text-white">{money(PAYOUT.available.toLocaleString())}</span>
            <div className="text-[11px] mt-[2px]" style={{ color: 'rgba(255,255,255,0.55)' }}>available</div>
          </div>
          <div className="ml-auto self-end">
            <span className="font-mono text-[11px]" style={{ color: 'rgba(255,255,255,0.5)' }}>{PAYOUT.account}</span>
          </div>
        </div>
      </div>

      <SectionLabel line right={
        <button
          onClick={() => window.open('https://dashboard.stripe.com/payments', '_blank', 'noopener,noreferrer')}
          className="inline-flex items-center gap-[5px] bg-none border-none cursor-pointer text-[12px] font-semibold text-ink70 hover:text-ink transition-colors"
          style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          View in Stripe
          <Icon name="externalLink" size={13} strokeWidth={2} />
        </button>
      }>
        Recent deposits
      </SectionLabel>

      {/* Filter tabs */}
      <div className="mb-3">
        <Pills
          value={depFilter}
          onChange={setDepFilter}
          options={[
            { value: 'all',      label: 'All' },
            { value: 'captured', label: 'Captured' },
            { value: 'held',     label: 'Held' },
            { value: 'failed',   label: 'Failed' },
            { value: 'refunded', label: 'Refunded' },
          ]}
        />
      </div>

      <div className="flex flex-col gap-2">
        {filtered.map(d => (
          <div key={d.id} className="flex items-center gap-3 bg-white border border-cd-border rounded-[13px] p-[12px_14px]">
            <Avatar name={d.name} size={36} radius={10} font="13px" />
            <div className="flex-1 min-w-0">
              <div className="text-[14px] font-semibold text-ink">{d.name}</div>
              <div className="text-[11.5px] text-t2 mt-[1px]">{d.service} · {d.when}</div>
            </div>
            <div className="flex flex-col items-end gap-[5px] shrink-0">
              <span className="font-mono text-[14px] font-bold"
                style={{
                  color: d.status === 'refunded' ? '#9A9A9A' : '#0A0A0A',
                  textDecoration: d.status === 'refunded' ? 'line-through' : 'none',
                }}>
                {money(d.amount)}
              </span>
              <DepChip status={d.status} />
            </div>
          </div>
        ))}
        {filtered.length === 0 && (
          <div className="text-center text-[13px] text-t3 py-8">No {depFilter} deposits</div>
        )}
      </div>
      <div className="h-2" />
    </div>
  );
}
