import { useMemo } from "react";
import { KpiCard } from "../../ui/KpiCard";
import { Panel } from "../../ui/Panel";
import { SectionLabel } from "../../ui/SectionLabel";
import { Segmented } from "../../ui/Segmented";
import { Skeleton } from "../../ui/Skeleton";
import { PartialBadge } from "../../ui/DemoBadge";
import { ErrorState } from "../../ui/ErrorState";
import { rangeToWindow, type KpiMetric } from "../../../features/reporting/api";
import { useBookingFunnel, useBookingOverview, useKpis } from "../../../features/reporting/hooks";
import { useCatalogItems } from "../../../features/hooks";
import { toBookableServices } from "../../../features/bookings/services";

export type AnalyticsRange = "7d" | "30d" | "90d";

interface AnalyticsScreenProps {
  device: "phone" | "desktop";
  range: AnalyticsRange;
  onRangeChange: (range: AnalyticsRange) => void;
}

const CHANNEL_LABEL: Record<string, string> = {
  voice: "Phone", messaging: "Messaging", web: "Website", email: "Email",
};

const FUNNEL_STAGES: { key: "accepted" | "confirmed" | "notified"; label: string; sub: string }[] = [
  { key: "accepted",  label: "Requests accepted", sub: "Durably received" },
  { key: "confirmed", label: "Confirmed",         sub: "Booked with the venue" },
  { key: "notified",  label: "Guest notified",    sub: "Delivery confirmed" },
];

const KPI_META: Record<string, { label: string; sub: string }> = {
  bookings: { label: "Bookings", sub: "vs prior window" },
  noshow: { label: "No-show rate", sub: "of known attendance" },
  ai: { label: "AI-handled", sub: "of attributed bookings" },
  rev: { label: "Deposits captured", sub: "this window" },
};

function formatKpi(k: KpiMetric): { value: string; delta?: string; up?: boolean; label: string; sub: string } {
  const meta = KPI_META[k.key] ?? { label: k.key, sub: "" };
  if (!k.available || k.value == null) return { value: "—", ...meta };

  let value: string;
  if (k.unit === "percent") value = `${k.value.toFixed(1)}%`;
  else if (k.unit === "minor") value = `A$${(k.value / 100).toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  else value = String(Math.round(k.value));

  let delta: string | undefined;
  if (k.delta != null) {
    const sign = k.delta > 0 ? "+" : "";
    if (k.unit === "percent") delta = `${sign}${k.delta.toFixed(1)}pt`;
    else if (k.unit === "minor") delta = `${sign}A$${(k.delta / 100).toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
    else delta = `${sign}${Math.round(k.delta)}`;
  }

  return { value, delta, up: k.up ?? undefined, ...meta };
}

export function AnalyticsScreen({ device, range, onRangeChange }: AnalyticsScreenProps) {
  const isPhone = device === "phone";
  const window = useMemo(() => rangeToWindow(range), [range]);
  const funnel = useBookingFunnel(window);
  const overview = useBookingOverview(window);
  const kpis = useKpis(window);
  const catalog = useCatalogItems();
  const serviceNames = useMemo(() => {
    const map: Record<string, string> = {};
    for (const s of toBookableServices(catalog.data)) map[s.id] = s.name;
    return map;
  }, [catalog.data]);

  const liveDays = overview.data?.per_day ?? [];
  const perDay = liveDays.map((d) => ({ d: (d.date ?? "").slice(5), v: d.accepted ?? 0 }));
  const overviewLoaded = overview.data !== undefined;
  const chartDays = overviewLoaded ? perDay : [];
  const maxDay = Math.max(1, ...chartDays.map((d) => d.v));

  const liveServices = overview.data?.top_services ?? [];
  const liveChannels = overview.data?.channels ?? [];
  const kpiCards = kpis.data?.kpis ?? [];
  const handled = kpis.data?.handled ?? null;
  const handledTotal = handled ? handled.ai + handled.you : 0;
  const aiPct = handledTotal > 0 ? Math.round((handled!.ai / handledTotal) * 100) : 0;
  const youPct = handledTotal > 0 ? 100 - aiPct : 0;

  return (
    <div>
      <div className="mb-4">
        <Segmented
          value={range}
          onChange={(v) => onRangeChange(v as AnalyticsRange)}
          size="sm"
          options={[
            { value: "7d",  label: "7 days" },
            { value: "30d", label: "30 days" },
            { value: "90d", label: "90 days" },
          ]}
        />
      </div>

      <SectionLabel>Booking funnel</SectionLabel>
      <div className={`grid gap-[9px] mb-[18px] ${isPhone ? "grid-cols-1" : "grid-cols-3"}`}>
        {funnel.isPending && FUNNEL_STAGES.map((s) => <Skeleton key={s.key} h={86} />)}
        {!funnel.isPending && funnel.isError && (
          <div className={isPhone ? "" : "col-span-3"}>
            <ErrorState inline error={funnel.error} onRetry={() => void funnel.refetch()} />
          </div>
        )}
        {!funnel.isPending && !funnel.isError && FUNNEL_STAGES.map((stage) => (
          <KpiCard
            key={stage.key}
            value={String(funnel.data?.[stage.key] ?? 0)}
            label={stage.label}
            sub={stage.sub}
          />
        ))}
      </div>

      {(funnel.data?.failedTerminal || funnel.data?.ambiguous || funnel.data?.notifyPending) ? (
        <Panel style={{ padding: "12px 15px", marginBottom: 18 }}>
          <div className="flex flex-wrap gap-[18px]">
            <SubCount label="Couldn't complete" value={funnel.data?.failedTerminal ?? 0} />
            <SubCount label="Still checking" value={funnel.data?.ambiguous ?? 0} />
            <SubCount label="Awaiting notification" value={funnel.data?.notifyPending ?? 0} />
          </div>
        </Panel>
      ) : null}

      <SectionLabel>Highlights</SectionLabel>
      <div className={`grid gap-[9px] mb-[18px] ${isPhone ? "grid-cols-2" : "grid-cols-4"}`}>
        {kpis.isPending && [0, 1, 2, 3].map((i) => <Skeleton key={i} h={86} />)}
        {!kpis.isPending && kpis.isError && (
          <div className={isPhone ? "col-span-2" : "col-span-4"}>
            <ErrorState inline error={kpis.error} onRetry={() => void kpis.refetch()} />
          </div>
        )}
        {!kpis.isPending && !kpis.isError && kpiCards.map((k) => {
          const card = formatKpi(k);
          return (
            <KpiCard key={k.key} value={card.value} label={card.label} delta={card.delta} up={card.up} sub={card.sub} />
          );
        })}
      </div>

      {overview.isError && (
        <div style={{ marginBottom: 14 }}>
          <ErrorState inline error={overview.error} onRetry={() => void overview.refetch()} />
        </div>
      )}

      <div className="flex items-center gap-[8px]">
        <SectionLabel>Requests accepted per day</SectionLabel>
        {overview.data?.breakdown_truncated && <PartialBadge reason="too many operations in this window to break down fully" />}
      </div>
      <Panel style={{ padding: "18px 16px 14px", marginBottom: 18 }}>
        {overview.isPending && <Skeleton h={160} />}
        {overviewLoaded && chartDays.length === 0 && (
          <div className="text-[12.5px] text-t3 leading-[1.5]">
            No requests were accepted in this window. That is the platform's answer, not a
            gap in it.
          </div>
        )}
        {chartDays.length > 0 && (
          <div className="flex items-end gap-2 md:gap-4" style={{ height: 160 }}>
            {chartDays.map((d, i) => {
              const accent = i === chartDays.length - 1;
              return (
                <div key={d.d} className="flex-1 flex flex-col items-center gap-[9px] h-full justify-end">
                  <span className="font-mono text-[11px] font-semibold text-t3">{d.v}</span>
                  <div
                    className="w-full rounded-[11px]"
                    title={`${d.v} bookings`}
                    style={{
                      maxWidth: range === "90d" ? 90 : 104,
                      height: (6 + d.v / maxDay * 104) + "px",
                      background: accent ? "#DDF247" : "#0A0A0A",
                    }}
                  />
                  <span className="font-mono text-[9.5px] tracking-[0.04em] text-t3">{d.d}</span>
                </div>
              );
            })}
          </div>
        )}
      </Panel>

      <div className={isPhone ? "block" : "flex gap-4"}>
        <div className={`flex-1 min-w-0 ${isPhone ? "mb-[18px]" : ""}`}>
          <div className="flex items-center gap-[8px]">
            <SectionLabel>Where bookings came from</SectionLabel>
            {overview.data?.breakdown_truncated && <PartialBadge reason="too many operations in this window to break down fully" />}
          </div>
          <Panel style={{ padding: "15px 16px" }}>
            {overviewLoaded && liveChannels.length === 0 && (
              <div className="text-[12.5px] text-t3 leading-[1.5]">
                No conversations were recorded in this window. That is the platform's answer, not a
                gap in it.
              </div>
            )}
            {liveChannels.map((c) => {
              const total = Math.max(1, liveChannels.reduce((sum, x) => sum + (x.count ?? 0), 0));
              const value = c.count ?? 0;
              return (
                <div key={c.channel} className="mb-3">
                  <div className="flex justify-between mb-[5px]">
                    <span className="text-[12.5px] text-ink">{CHANNEL_LABEL[c.channel ?? ""] ?? c.channel}</span>
                    <span className="font-mono text-[11.5px] font-semibold text-t2">
                      {value} · {Math.round(value / total * 100)}%
                    </span>
                  </div>
                  <div className="h-[7px] rounded-full overflow-hidden bg-wash3">
                    <div className="h-full rounded-full bg-ink" style={{ width: (value / total * 100) + "%" }} />
                  </div>
                </div>
              );
            })}
          </Panel>
        </div>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-[8px]">
            <SectionLabel>Top services</SectionLabel>
            {overview.data?.breakdown_truncated && <PartialBadge reason="too many operations in this window to break down fully" />}
          </div>
          <Panel style={{ padding: "15px 16px" }}>
            {overviewLoaded && liveServices.length === 0 && (
              <div className="text-[12.5px] text-t3 leading-[1.5]">
                No services were booked in this window.
              </div>
            )}
            {liveServices.map((s, i) => {
              const max = Math.max(1, ...liveServices.map((x) => x.count ?? 0));
              const value = s.count ?? 0;
              return (
                <div key={s.service_id} className="flex items-center gap-3" style={{ marginBottom: i < liveServices.length - 1 ? 11 : 0 }}>
                  <span className="text-[12.5px] text-ink shrink-0 truncate" style={{ width: isPhone ? 120 : 140 }}>
                    {serviceNames[s.service_id ?? ""] ?? s.service_id}
                  </span>
                  <div className="flex-1 h-[7px] rounded-full overflow-hidden bg-wash3">
                    <div className="h-full rounded-full bg-ink" style={{ width: (value / max * 100) + "%" }} />
                  </div>
                  <span className="font-mono text-[12px] font-semibold text-t2 w-[24px] text-right">{value}</span>
                </div>
              );
            })}
          </Panel>
        </div>
      </div>

      <div className="h-[18px]" />
      <SectionLabel>Who handled them</SectionLabel>
      <Panel style={{ padding: 16 }}>
        {kpis.isPending && <Skeleton h={60} />}
        {!kpis.isPending && handled == null && (
          <div className="text-[12.5px] text-t3 leading-[1.5]">
            Attribution is not recorded for bookings in this window.
          </div>
        )}
        {handled != null && (
          <>
            <div className="flex h-[14px] rounded-full overflow-hidden mb-[14px]">
              <div style={{ width: aiPct + "%", background: "#0A0A0A" }} />
              <div style={{ width: youPct + "%", background: "#DDF247" }} />
            </div>
            <div className="flex gap-[18px]">
              <div>
                <div className="flex items-center gap-[6px]">
                  <span className="w-[9px] h-[9px] rounded-[3px] bg-ink" />
                  <span className="text-[12px] text-t2">Agent</span>
                </div>
                <span className="font-mono text-[19px] font-bold text-ink ml-[15px]">{aiPct}%</span>
              </div>
              <div>
                <div className="flex items-center gap-[6px]">
                  <span className="w-[9px] h-[9px] rounded-[3px] border border-[#C9D63E]" style={{ background: "#DDF247" }} />
                  <span className="text-[12px] text-t2">You</span>
                </div>
                <span className="font-mono text-[19px] font-bold text-ink ml-[15px]">{youPct}%</span>
              </div>
            </div>
          </>
        )}
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
