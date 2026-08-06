import { useMemo } from "react";
import { ANALYTICS_BY_RANGE, SERVICES } from "../../../data/console";
import { KpiCard } from "../../ui/KpiCard";
import { Panel } from "../../ui/Panel";
import { SectionLabel } from "../../ui/SectionLabel";
import { Segmented } from "../../ui/Segmented";
import { Skeleton } from "../../ui/Skeleton";
import { DemoBadge, PartialBadge } from "../../ui/DemoBadge";
import { ErrorState } from "../../ui/ErrorState";
import { rangeToWindow } from "../../../features/reporting/api";
import { useBookingFunnel, useBookingOverview } from "../../../features/reporting/hooks";

export type AnalyticsRange = '7d' | '30d' | '90d';

interface AnalyticsScreenProps {
  device: 'phone' | 'desktop';
  /**
   * Lifted so the page header can label the window truthfully. It used to be local state while
   * the header's eyebrow was the static literal "Last 7 days", so choosing 30 or 90 days left
   * the chrome contradicting the live figures beneath it.
   */
  range: AnalyticsRange;
  onRangeChange: (range: AnalyticsRange) => void;
}

/** Operator-facing labels for the API's channel vocabulary. */
const CHANNEL_LABEL: Record<string, string> = {
  voice: 'Phone', messaging: 'Messaging', web: 'Website', email: 'Email',
};

const CHART_LABEL: Record<string, string> = {
  '7d': 'Bookings per day', '30d': 'Bookings per week', '90d': 'Bookings per month',
};

/**
 * The separated funnel counts (spec 13 / Inv-16). They are shown as distinct figures and
 * never collapsed into a single "bookings" number — "accepted" is not "confirmed", and
 * "confirmed" is not "the guest was told".
 */
const FUNNEL_STAGES: { key: 'accepted' | 'confirmed' | 'notified'; label: string; sub: string }[] = [
  { key: 'accepted',  label: 'Requests accepted', sub: 'Durably received' },
  { key: 'confirmed', label: 'Confirmed',         sub: 'Booked with the venue' },
  { key: 'notified',  label: 'Guest notified',    sub: 'Delivery confirmed' },
];

export function AnalyticsScreen({ device, range, onRangeChange }: AnalyticsScreenProps) {
  const isPhone = device === 'phone';

  // Anchor the window to the selected range so the query key is stable across renders.
  const window = useMemo(() => rangeToWindow(range), [range]);
  const funnel = useBookingFunnel(window);
  const overview = useBookingOverview(window);

  const a = ANALYTICS_BY_RANGE[range];
  const maxSvc = Math.max(...a.topServices.map(s => s.v));
  const totalCh = a.channels.reduce((x, c) => x + c.v, 0);

  // Live per-day series when the overview has loaded; the mock series otherwise so the
  // chart keeps its shape rather than collapsing while loading.
  const liveDays = overview.data?.per_day ?? [];
  const perDay = liveDays.length > 0
    ? liveDays.map(d => ({ d: (d.date ?? '').slice(5), v: d.accepted ?? 0 }))
    : [];
  const chartDays = perDay.length > 0 ? perDay : a.perDay;
  const maxDay = Math.max(1, ...chartDays.map(d => d.v));

  // A failed overview leaves every series empty, which is indistinguishable from a genuinely
  // quiet week. Saying "no activity in this window" in that case states as fact something the
  // app does not know, because the request never answered.
  const overviewLoaded = overview.data !== undefined;
  const emptyReason = overviewLoaded
    ? 'no activity in this window'
    : 'this data could not be loaded';

  const liveServices = overview.data?.top_services ?? [];
  const liveChannels = overview.data?.channels ?? [];

  return (
    <div>
      {/* Date range */}
      <div className="mb-4">
        <Segmented
          value={range}
          onChange={(v) => onRangeChange(v as AnalyticsRange)}
          size="sm"
          options={[
            { value: '7d',  label: '7 days' },
            { value: '30d', label: '30 days' },
            { value: '90d', label: '90 days' },
          ]}
        />
      </div>

      {/* Live booking funnel (spec 13) */}
      <SectionLabel>Booking funnel</SectionLabel>
      <div className={`grid gap-[9px] mb-[18px] ${isPhone ? 'grid-cols-1' : 'grid-cols-3'}`}>
        {funnel.isPending && FUNNEL_STAGES.map(s => <Skeleton key={s.key} h={86} />)}

        {!funnel.isPending && funnel.isError && (
          <div className={isPhone ? '' : 'col-span-3'}>
            <ErrorState inline error={funnel.error} onRetry={() => void funnel.refetch()} />
          </div>
        )}

        {!funnel.isPending && !funnel.isError && FUNNEL_STAGES.map(stage => (
          <KpiCard
            key={stage.key}
            value={String(funnel.data?.[stage.key] ?? 0)}
            label={stage.label}
            sub={stage.sub}
          />
        ))}
      </div>

      {(funnel.data?.failedTerminal || funnel.data?.ambiguous || funnel.data?.notifyPending) ? (
        <Panel style={{ padding: '12px 15px', marginBottom: 18 }}>
          <div className="flex flex-wrap gap-[18px]">
            <SubCount label="Couldn't complete" value={funnel.data?.failedTerminal ?? 0} />
            <SubCount label="Still checking" value={funnel.data?.ambiguous ?? 0} />
            <SubCount label="Awaiting notification" value={funnel.data?.notifyPending ?? 0} />
          </div>
        </Panel>
      ) : null}

      {/* KPI grid — MOCK: no endpoint exposes these headline metrics yet. */}
      <div className="flex items-center gap-[8px] mb-[7px]">
        <SectionLabel>Highlights</SectionLabel>
        <DemoBadge reason="no KPI endpoint yet" />
      </div>
      <div className={`grid gap-[9px] mb-[18px] ${isPhone ? 'grid-cols-2' : 'grid-cols-4'}`}>
        {a.kpis.map(k => (
          <KpiCard key={k.key} value={k.value} label={k.label} delta={k.delta} up={k.up} sub={k.sub} />
        ))}
      </div>

      {overview.isError && (
        <div style={{ marginBottom: 14 }}>
          <ErrorState
            inline
            error={overview.error}
            title="Couldn’t load these figures. The shapes below are sample data, not your account."
            onRetry={() => void overview.refetch()}
          />
        </div>
      )}

      {/* Bar chart */}
      <div className="flex items-center gap-[8px]">
        <SectionLabel>{perDay.length > 0 ? 'Requests accepted per day' : CHART_LABEL[range]}</SectionLabel>
        {perDay.length === 0 && !overview.isPending && <DemoBadge reason={emptyReason} />}
      </div>
      <Panel style={{ padding: '18px 16px 14px', marginBottom: 18 }}>
        <div className="flex items-end gap-2 md:gap-4" style={{ height: 160 }}>
          {chartDays.map((d, i) => {
            // The most recent bucket, always. It used to be `WEEK.today` for the 7-day mock
            // series — an index into the frozen `9-15 June` literal, which highlighted a
            // Thursday whatever day it actually was.
            const accent = i === chartDays.length - 1;
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
          <div className="flex items-center gap-[8px]">
            <SectionLabel>Where bookings came from</SectionLabel>
            {liveChannels.length === 0 && !overview.isPending && <DemoBadge reason={emptyReason} />}
            {overview.data?.breakdown_truncated && <PartialBadge reason="too many operations in this window to break down fully" />}
          </div>
          <Panel style={{ padding: '15px 16px' }}>
            {liveChannels.length > 0
              ? liveChannels.map(c => {
                  const total = Math.max(1, liveChannels.reduce((sum, x) => sum + (x.count ?? 0), 0));
                  const value = c.count ?? 0;
                  return (
                    <div key={c.channel} className="mb-3">
                      <div className="flex justify-between mb-[5px]">
                        <span className="text-[12.5px] text-ink">{CHANNEL_LABEL[c.channel ?? ''] ?? c.channel}</span>
                        <span className="font-mono text-[11.5px] font-semibold text-t2">
                          {value} · {Math.round(value / total * 100)}%
                        </span>
                      </div>
                      <div className="h-[7px] rounded-full overflow-hidden bg-wash3">
                        <div className="h-full rounded-full bg-ink" style={{ width: (value / total * 100) + '%' }} />
                      </div>
                    </div>
                  );
                })
              : a.channels.map(c => (
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
          <div className="flex items-center gap-[8px]">
            <SectionLabel>Top services</SectionLabel>
            {liveServices.length === 0 && !overview.isPending && <DemoBadge reason={emptyReason} />}
            {overview.data?.breakdown_truncated && <PartialBadge reason="too many operations in this window to break down fully" />}
          </div>
          <Panel style={{ padding: '15px 16px' }}>
            {liveServices.length > 0
              ? liveServices.map((s, i) => {
                  const max = Math.max(1, ...liveServices.map(x => x.count ?? 0));
                  const value = s.count ?? 0;
                  return (
                    <div key={s.service_id} className="flex items-center gap-3" style={{ marginBottom: i < liveServices.length - 1 ? 11 : 0 }}>
                      <span className="text-[12.5px] text-ink shrink-0 truncate" style={{ width: isPhone ? 120 : 140 }}>
                        {SERVICES[s.service_id ?? '']?.name ?? s.service_id}
                      </span>
                      <div className="flex-1 h-[7px] rounded-full overflow-hidden bg-wash3">
                        <div className="h-full rounded-full bg-ink" style={{ width: (value / max * 100) + '%' }} />
                      </div>
                      <span className="font-mono text-[12px] font-semibold text-t2 w-[24px] text-right">{value}</span>
                    </div>
                  );
                })
              : a.topServices.map((s, i) => (
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
      <div className="flex items-center gap-[8px]">
        <SectionLabel>Who handled them</SectionLabel>
        {/* MOCK: nothing in the data model records who handled a booking, so this cannot
            be reported truthfully (Inv-16). It stays demo data until a spec owns it. */}
        <DemoBadge reason="not recorded server-side" />
      </div>
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

function SubCount({ label, value }: { label: string; value: number }) {
  return (
    <div>
      <div className="text-[11.5px] text-t3">{label}</div>
      <div className="font-mono text-[17px] font-bold text-ink">{value}</div>
    </div>
  );
}
