import { useState } from "react";
import { ANALYTICS_BY_RANGE, WEEK } from "../../../data/console";
import { KpiCard } from "../../ui/KpiCard";
import { Panel } from "../../ui/Panel";
import { SectionLabel } from "../../ui/SectionLabel";
import { Segmented } from "../../ui/Segmented";

interface AnalyticsScreenProps {
  device: 'phone' | 'desktop';
}

const CHART_LABEL: Record<string, string> = {
  '7d': 'Bookings per day', '30d': 'Bookings per week', '90d': 'Bookings per month',
};

export function AnalyticsScreen({ device }: AnalyticsScreenProps) {
  const isPhone = device === 'phone';
  const [range, setRange] = useState<'7d' | '30d' | '90d'>('7d');
  const a = ANALYTICS_BY_RANGE[range];
  const maxDay = Math.max(...a.perDay.map(d => d.v));
  const maxSvc = Math.max(...a.topServices.map(s => s.v));
  const totalCh = a.channels.reduce((x, c) => x + c.v, 0);

  return (
    <div>
      {/* Date range */}
      <div className="mb-4">
        <Segmented
          value={range}
          onChange={(v) => setRange(v as '7d' | '30d' | '90d')}
          size="sm"
          options={[
            { value: '7d',  label: '7 days' },
            { value: '30d', label: '30 days' },
            { value: '90d', label: '90 days' },
          ]}
        />
      </div>

      {/* KPI grid */}
      <div className={`grid gap-[9px] mb-[18px] ${isPhone ? 'grid-cols-2' : 'grid-cols-4'}`}>
        {a.kpis.map(k => (
          <KpiCard key={k.key} value={k.value} label={k.label} delta={k.delta} up={k.up} sub={k.sub} />
        ))}
      </div>

      {/* Bar chart */}
      <SectionLabel>{CHART_LABEL[range]}</SectionLabel>
      <Panel style={{ padding: '18px 16px 14px', marginBottom: 18 }}>
        <div className="flex items-end gap-2 md:gap-4" style={{ height: 160 }}>
          {a.perDay.map((d, i) => {
            const accent = range === '7d' ? i === WEEK.today : i === a.perDay.length - 1;
            return (
              <div key={d.d} className="flex-1 flex flex-col items-center gap-[9px] h-full justify-end">
                <span className="font-mono text-[11px] font-semibold text-t3">{d.v}</span>
                <div
                  className="w-full rounded-[11px]"
                  title={`${d.v} bookings`}
                  style={{
                    maxWidth: range === '90d' ? 90 : 104,
                    height: (6 + d.v / maxDay * 104) + 'px',
                    background: accent ? '#DDF247' : '#0A0A0A',
                  }}
                />
                <span className="font-mono text-[9.5px] tracking-[0.04em] text-t3">{d.d}</span>
              </div>
            );
          })}
        </div>
      </Panel>

      {/* Channels + Top services */}
      <div className={isPhone ? 'block' : 'flex gap-4'}>
        <div className={`flex-1 min-w-0 ${isPhone ? 'mb-[18px]' : ''}`}>
          <SectionLabel>Where bookings came from</SectionLabel>
          <Panel style={{ padding: '15px 16px' }}>
            {a.channels.map(c => (
              <div key={c.label} className="mb-3">
                <div className="flex justify-between mb-[5px]">
                  <span className="text-[12.5px] text-ink">{c.label}</span>
                  <span className="font-mono text-[11.5px] font-semibold text-t2">
                    {c.v} · {Math.round(c.v / totalCh * 100)}%
                  </span>
                </div>
                <div className="h-[7px] rounded-full overflow-hidden bg-wash3">
                  <div className="h-full rounded-full" style={{ width: (c.v / totalCh * 100) + '%', background: c.color }} />
                </div>
              </div>
            ))}
          </Panel>
        </div>
        <div className="flex-1 min-w-0">
          <SectionLabel>Top services</SectionLabel>
          <Panel style={{ padding: '15px 16px' }}>
            {a.topServices.map((s, i) => (
              <div key={s.name} className="flex items-center gap-3" style={{ marginBottom: i < a.topServices.length - 1 ? 11 : 0 }}>
                <span className="text-[12.5px] text-ink shrink-0" style={{ width: isPhone ? 120 : 140 }}>{s.name}</span>
                <div className="flex-1 h-[7px] rounded-full overflow-hidden bg-wash3">
                  <div className="h-full rounded-full bg-ink" style={{ width: (s.v / maxSvc * 100) + '%' }} />
                </div>
                <span className="font-mono text-[12px] font-semibold text-t2 w-[24px] text-right">{s.v}</span>
              </div>
            ))}
          </Panel>
        </div>
      </div>

      <div className="h-[18px]" />
      <SectionLabel>Who handled them</SectionLabel>
      <Panel style={{ padding: 16 }}>
        <div className="flex h-[14px] rounded-full overflow-hidden mb-[14px]">
          <div style={{ width: a.handled.ai + '%', background: '#0A0A0A' }} />
          <div style={{ width: a.handled.you + '%', background: '#DDF247' }} />
        </div>
        <div className="flex gap-[18px]">
          <div>
            <div className="flex items-center gap-[6px]">
              <span className="w-[9px] h-[9px] rounded-[3px] bg-ink" />
              <span className="text-[12px] text-t2">Agent</span>
            </div>
            <span className="font-mono text-[19px] font-bold text-ink ml-[15px]">{a.handled.ai}%</span>
          </div>
          <div>
            <div className="flex items-center gap-[6px]">
              <span className="w-[9px] h-[9px] rounded-[3px] border border-[#C9D63E]" style={{ background: '#DDF247' }} />
              <span className="text-[12px] text-t2">You</span>
            </div>
            <span className="font-mono text-[19px] font-bold text-ink ml-[15px]">{a.handled.you}%</span>
          </div>
        </div>
      </Panel>
      <div className="h-2" />
    </div>
  );
}
