import React, { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { BUSINESS_TYPES, fmtDur, fmtT, parseTime, type TenantConfig } from "../../../data/console";
import { Toggle } from "../../ui/Toggle";
import { Panel } from "../../ui/Panel";
import { SectionLabel } from "../../ui/SectionLabel";
import { Icon, IconName } from "../../ui/Icon";
import { ErrorState } from "../../ui/ErrorState";
import { Skeleton } from "../../ui/Skeleton";
import { MoneyAmount } from "../../ui/MoneyAmount";
import { queryKeys } from "../../../api/queryKeys";
import {
  useCatalogItems, useConsentPolicy, useDepositPolicy, useGuardrails, useKnowledge,
  useOperatingHours, useTenantProfile,
} from "../../../features/hooks";
import { setTenantProfile } from "../../../features/settings/profile";
import { setOperatingHours, toHHmm, type OperatingHour } from "../../../features/settings/hours";
import { setGuardrails, type Guardrail } from "../../../features/settings/guardrails";
import { createKnowledge, updateKnowledge } from "../../../features/settings/knowledge";
import { setDepositPolicy } from "../../../features/settings/depositPolicy";
import { MenuCatalogPanel } from "../settings/MenuCatalogPanel";
import { VenueNamePanel } from "../settings/VenueNamePanel";
import { StaffRosterPanel } from "../settings/StaffRosterPanel";
import { CapacitySlotPanel } from "../settings/CapacitySlotPanel";
import { BookingLookupPanel } from "../operations/BookingLookupPanel";
import { DeadLetterReplayPanel } from "../operations/DeadLetterReplayPanel";
import { ModelTraceViewerPanel } from "../operations/ModelTraceViewerPanel";
import { PreorderRecoveryPanel } from "../operations/PreorderRecoveryPanel";

function tenantFromProfile(businessType: string | null | undefined): TenantConfig {
  const bt = BUSINESS_TYPES.find(b => b.key === businessType) ?? BUSINESS_TYPES.find(b => b.key === "other")!;
  return {
    name: "",
    businessType: bt.key,
    resourceLabel: bt.resourceLabel,
    resourceLabelPlural: bt.resourceLabelPlural,
  };
}

interface BusinessSetupProps {
  tenantCfg: TenantConfig;
  onSelect: (key: string) => void;
  device?: "phone" | "desktop";
  busy?: boolean;
}

function BusinessSetup({ tenantCfg, onSelect, device, busy }: BusinessSetupProps) {
  return (
    <Panel style={{ marginBottom: 18 }}>
      <div
        style={{
          display: "grid",
          gridTemplateColumns: device === "phone" ? "repeat(2, 1fr)" : "repeat(4, 1fr)",
          gap: 10,
          padding: "4px 0 2px",
        }}
      >
        {BUSINESS_TYPES.map(bt => {
          const active = tenantCfg.businessType === bt.key;
          return (
            <button
              key={bt.key}
              type="button"
              disabled={busy}
              onClick={() => onSelect(bt.key)}
              style={{
                display: "flex",
                flexDirection: "column",
                alignItems: "center",
                gap: 7,
                padding: "12px 8px 10px",
                borderRadius: 12,
                border: `1.5px solid ${active ? "#0A0A0A" : "#EAEAEA"}`,
                background: active ? "#EBEBEA" : "#FAFAF9",
                cursor: busy ? "not-allowed" : "pointer",
                opacity: busy ? 0.7 : 1,
                transition: "all 120ms ease",
                fontFamily: "'Inter Tight', system-ui, sans-serif",
              }}
            >
              <span
                style={{
                  width: 34,
                  height: 34,
                  borderRadius: 9,
                  display: "flex",
                  alignItems: "center",
                  justifyContent: "center",
                  background: active ? "#DDF247" : "#F1F1EE",
                  transition: "background 120ms ease",
                }}
              >
                <Icon name={bt.icon as IconName} size={17} strokeWidth={1.9} color={active ? "#0A0A0A" : "#6B6B6B"} />
              </span>
              <span
                style={{
                  fontSize: 11.5,
                  fontWeight: 600,
                  color: active ? "#0A0A0A" : "#55554F",
                  textAlign: "center",
                  lineHeight: 1.25,
                  letterSpacing: "-0.01em",
                }}
              >
                {bt.label}
              </span>
            </button>
          );
        })}
      </div>
      <div
        className="flex items-center justify-between"
        style={{ marginTop: 14, paddingTop: 13, borderTop: "1px solid #EFEFEF" }}
      >
        <div>
          <div className="text-[13.5px] font-medium text-ink">Resource unit</div>
          <div className="text-[11.5px] text-t3 mt-[1px]">Label used for rows in the schedule grid</div>
        </div>
        <span
          className="font-mono text-[12px] font-semibold px-[10px] py-[5px] rounded-[8px]"
          style={{ background: "#F1F1EE", color: "#0A0A0A" }}
        >
          {tenantCfg.resourceLabel} / {tenantCfg.resourceLabelPlural}
        </span>
      </div>
    </Panel>
  );
}

const inputStyle: React.CSSProperties = {
  height: 38, border: "1px solid #EAEAEA", borderRadius: 9,
  padding: "0 10px", fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 13, color: "#0A0A0A", background: "#fff", outline: "none",
};

interface SettingsScreenProps {
  device?: "phone" | "desktop";
}

export function SettingsScreen({ device }: SettingsScreenProps) {
  const queryClient = useQueryClient();
  const consentPolicy = useConsentPolicy("web");
  const catalog = useCatalogItems();
  const catalogItems = catalog.data ?? [];
  const profileQuery = useTenantProfile();
  const hoursQuery = useOperatingHours();
  const guardrailsQuery = useGuardrails();
  const knowledgeQuery = useKnowledge();
  const depositQuery = useDepositPolicy();

  const tenantCfg = tenantFromProfile(profileQuery.data?.business_type);

  const profileMut = useMutation({
    mutationFn: setTenantProfile,
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: queryKeys.settings.profile() }); },
  });
  const hoursMut = useMutation({
    mutationFn: setOperatingHours,
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: queryKeys.settings.hours() }); },
  });
  const guardMut = useMutation({
    mutationFn: setGuardrails,
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: queryKeys.settings.guardrails() }); },
  });
  const depositMut = useMutation({
    mutationFn: setDepositPolicy,
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: queryKeys.settings.depositPolicy() }); },
  });

  const [hoursDraft, setHoursDraft] = useState<OperatingHour[] | null>(null);
  useEffect(() => {
    if (hoursQuery.data?.hours) setHoursDraft(hoursQuery.data.hours.map(h => ({ ...h })));
  }, [hoursQuery.data]);

  const [editingKb, setEditingKb] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [newQ, setNewQ] = useState("");
  const [kbBusy, setKbBusy] = useState(false);

  const hours = hoursDraft ?? hoursQuery.data?.hours ?? [];
  const guardrails = guardrailsQuery.data?.guardrails ?? [];
  const knowledge = knowledgeQuery.data?.entries ?? [];
  const deposit = depositQuery.data;

  async function saveKnowledge(id: string) {
    if (!draft.trim()) return;
    setKbBusy(true);
    try {
      await updateKnowledge(id, { a: draft.trim(), status: "answered" });
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings.knowledge() });
      setEditingKb(null);
    } finally {
      setKbBusy(false);
    }
  }

  async function addKnowledge() {
    if (!newQ.trim()) return;
    setKbBusy(true);
    try {
      await createKnowledge({ q: newQ.trim() });
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings.knowledge() });
      setNewQ("");
    } finally {
      setKbBusy(false);
    }
  }

  return (
    <div>
      <SectionLabel>Business setup</SectionLabel>
      {profileQuery.isPending && <div className="mb-[18px]"><Skeleton h={120} /></div>}
      {profileQuery.isError && (
        <div className="mb-[18px]">
          <ErrorState inline error={profileQuery.error} onRetry={() => void profileQuery.refetch()} />
        </div>
      )}
      {!profileQuery.isPending && !profileQuery.isError && (
        <BusinessSetup
          tenantCfg={tenantCfg}
          device={device}
          busy={profileMut.isPending}
          onSelect={(key) => profileMut.mutate({ business_type: key })}
        />
      )}

      <SectionLabel>Venue name</SectionLabel>
      <VenueNamePanel />

      <StaffRosterPanel />

      <CapacitySlotPanel />

      <SectionLabel>Consent policy</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {consentPolicy.isPending && <div className="py-3"><Skeleton h={52} /></div>}

        {!consentPolicy.isPending && consentPolicy.isError && (
          <div className="py-2">
            <ErrorState inline error={consentPolicy.error} onRetry={() => void consentPolicy.refetch()} />
          </div>
        )}

        {!consentPolicy.isPending && !consentPolicy.isError && (
          <>
            <div className="flex items-center justify-between py-3 border-b border-hair">
              <div>
                <div className="text-[13.5px] font-medium text-ink">Notice required before recording</div>
                <div className="text-[11.5px] text-t3 mt-[1px]">
                  Policy {consentPolicy.data?.consent_policy_version ?? "unknown"}
                  {consentPolicy.data?.jurisdiction_policy_id
                    ? ` · ${consentPolicy.data.jurisdiction_policy_id}`
                    : ""}
                </div>
              </div>
              <span className="text-[12.5px] font-semibold text-ink">
                {consentPolicy.data?.notice_required ? "Yes" : "No"}
              </span>
            </div>
            {consentPolicy.data?.notice_text && (
              <div className="py-3 text-[12.5px] text-t2 leading-[1.5]">
                {consentPolicy.data.notice_text}
              </div>
            )}
          </>
        )}
      </Panel>

      <div className="flex items-center gap-[8px]">
        <SectionLabel>Menu catalog</SectionLabel>
      </div>
      <Panel style={{ marginBottom: 18 }}>
        {catalog.isPending && <div className="py-3"><Skeleton h={52} /></div>}

        {!catalog.isPending && catalog.isError && (
          <div className="py-2">
            <ErrorState inline error={catalog.error} onRetry={() => void catalog.refetch()} />
          </div>
        )}

        {!catalog.isPending && !catalog.isError && catalogItems.length === 0 && (
          <div className="py-4 text-[12.5px] text-t3">
            No published items yet. Upload and publish a menu to make items bookable.
          </div>
        )}

        {catalogItems.slice(0, 8).map((item, i) => (
          <div key={item.item_id ?? i}
            className="flex items-center justify-between py-[11px]"
            style={{ borderBottom: i < Math.min(catalogItems.length, 8) - 1 ? "1px solid #EFEFEF" : "none" }}>
            <div className="min-w-0">
              <div className="text-[13.5px] text-ink font-medium truncate">{item.name ?? item.item_id}</div>
              {item.category && <div className="text-[11.5px] text-t3 mt-[1px]">{item.category}</div>}
            </div>
            <MoneyAmount minor={item.price_minor} currency={item.currency} />
          </div>
        ))}
      </Panel>

      <SectionLabel>Menu catalog management</SectionLabel>
      <MenuCatalogPanel />

      <SectionLabel>Find a booking</SectionLabel>
      <BookingLookupPanel />

      <SectionLabel>Operator recovery</SectionLabel>
      <DeadLetterReplayPanel />
      <ModelTraceViewerPanel />
      <PreorderRecoveryPanel />

      <SectionLabel>Services</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {catalog.isPending && <div className="py-3"><Skeleton h={52} /></div>}
        {!catalog.isPending && catalog.isError && (
          <div className="py-2">
            <ErrorState inline error={catalog.error} onRetry={() => void catalog.refetch()} />
          </div>
        )}
        {!catalog.isPending && !catalog.isError && catalogItems.length === 0 && (
          <div className="py-4 text-[12.5px] text-t3">
            No published items yet. Create them in the menu catalog above.
          </div>
        )}
        {catalogItems.map((item, i) => (
          <div
            key={item.item_id ?? i}
            className="flex items-center justify-between py-[11px]"
            style={{ borderBottom: i < catalogItems.length - 1 ? "1px solid #EFEFEF" : "none" }}
          >
            <div className="min-w-0">
              <div className="text-[13.5px] font-semibold text-ink truncate">{item.name ?? item.item_id}</div>
              <div className="font-mono text-[11px] text-t3 mt-[2px]">
                {item.duration_minutes != null ? fmtDur(item.duration_minutes) : "Duration not set"}
              </div>
            </div>
            <MoneyAmount minor={item.price_minor} currency={item.currency} />
          </div>
        ))}
      </Panel>

      <SectionLabel>Hours</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {hoursQuery.isPending && <div className="py-3"><Skeleton h={52} /></div>}
        {hoursQuery.isError && (
          <div className="py-2">
            <ErrorState inline error={hoursQuery.error} onRetry={() => void hoursQuery.refetch()} />
          </div>
        )}
        {hours.map((h, i) => (
          <div key={h.d}
            className="flex items-center gap-3 py-[11px]"
            style={{ borderBottom: i < hours.length - 1 ? "1px solid #EFEFEF" : "none" }}>
            <span className="text-[13.5px] text-ink font-medium w-[90px] shrink-0">{h.d}</span>
            <Toggle
              on={h.on}
              onClick={() => setHoursDraft(prev => (prev ?? hours).map((row, idx) => idx === i ? { ...row, on: !row.on } : row))}
            />
            {h.on ? (
              <div className="flex items-center gap-2 min-w-0" style={{ marginLeft: "auto" }}>
                <input
                  type="time"
                  value={toHHmm(parseTime(h.open))}
                  onChange={e => setHoursDraft(prev => (prev ?? hours).map((row, idx) => idx === i ? { ...row, open: e.target.value } : row))}
                  aria-label={`${h.d} open`}
                  style={{ ...inputStyle, width: 118 }}
                />
                <span className="text-[12px] text-t3">–</span>
                <input
                  type="time"
                  value={toHHmm(parseTime(h.close))}
                  onChange={e => setHoursDraft(prev => (prev ?? hours).map((row, idx) => idx === i ? { ...row, close: e.target.value } : row))}
                  aria-label={`${h.d} close`}
                  style={{ ...inputStyle, width: 118 }}
                />
                <span className="font-mono text-[11px] text-t3 hidden md:inline">
                  {fmtT(parseTime(h.open))} – {fmtT(parseTime(h.close))}
                </span>
              </div>
            ) : (
              <span className="text-[12px] text-t3" style={{ marginLeft: "auto" }}>Closed</span>
            )}
          </div>
        ))}
        {hours.length > 0 && (
          <div style={{ padding: "12px 0" }}>
          <button
            type="button"
            disabled={hoursMut.isPending}
            onClick={() => hoursMut.mutate(hours.map(h => ({
              ...h,
              open: toHHmm(parseTime(h.open)),
              close: toHHmm(parseTime(h.close)),
            })))}
            className="text-[12.5px] font-semibold text-white bg-ink px-4 py-[7px] rounded-[8px] border-none cursor-pointer hover:brightness-110 transition-all disabled:opacity-40"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          >
            {hoursMut.isPending ? "Saving…" : "Save hours"}
          </button>
          </div>
        )}
      </Panel>

      <SectionLabel>Deposit policy</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {depositQuery.isPending && <div className="py-3"><Skeleton h={52} /></div>}
        {depositQuery.isError && (
          <div className="py-2">
            <ErrorState inline error={depositQuery.error} onRetry={() => void depositQuery.refetch()} />
          </div>
        )}
        {deposit && (
          <>
            <div className="flex items-center justify-between py-3 border-b border-hair">
              <div>
                <div className="text-[13.5px] font-medium text-ink">Require a deposit</div>
                <div className="text-[11.5px] text-t3 mt-[1px]">
                  {deposit.amount_policy} · refundable up to {deposit.refund_window}
                </div>
              </div>
              <Toggle
                on={deposit.require}
                disabled={depositMut.isPending}
                onClick={() => depositMut.mutate({ ...deposit, require: !deposit.require })}
              />
            </div>
            <div className="flex items-center justify-between py-3 border-b border-hair">
              <span className="text-[13.5px] text-ink">No-show only</span>
              <Toggle
                on={deposit.no_show_only}
                disabled={depositMut.isPending}
                onClick={() => depositMut.mutate({ ...deposit, no_show_only: !deposit.no_show_only })}
              />
            </div>
            {deposit.amount_minor != null && (
              <div className="flex items-center justify-between py-3">
                <span className="text-[13.5px] text-ink">Amount</span>
                <MoneyAmount minor={deposit.amount_minor} currency={deposit.currency} />
              </div>
            )}
          </>
        )}
      </Panel>

      <SectionLabel>Agent guardrails</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {guardrailsQuery.isPending && <div className="py-3"><Skeleton h={52} /></div>}
        {guardrailsQuery.isError && (
          <div className="py-2">
            <ErrorState inline error={guardrailsQuery.error} onRetry={() => void guardrailsQuery.refetch()} />
          </div>
        )}
        {guardrails.map((g, i) => (
          <div key={g.id}
            className="flex items-center gap-3 py-3"
            style={{ borderBottom: i < guardrails.length - 1 ? "1px solid #EFEFEF" : "none" }}>
            <div className="flex-1">
              <div className="text-[13.5px] text-ink flex items-center gap-[7px]">
                {g.label}
                {g.locked && <Icon name="shield" size={13} strokeWidth={2} color="#9A9A9A" />}
              </div>
              {g.locked && <div className="text-[11px] text-t3 mt-[2px]">Core safety rule · always on</div>}
            </div>
            <Toggle
              on={g.on}
              disabled={g.locked || guardMut.isPending}
              onClick={() => {
                if (g.locked) return;
                const next: Guardrail[] = guardrails.map(row => row.id === g.id ? { ...row, on: !row.on } : row);
                guardMut.mutate(next);
              }}
            />
          </div>
        ))}
      </Panel>

      <SectionLabel line right={
        <span className="font-mono text-[10.5px] text-t3">
          {knowledge.filter(k => k.status === "needs-answer").length} needs an answer
        </span>
      }>
        Agent knowledge base
      </SectionLabel>
      {knowledgeQuery.isPending && <div className="mb-3"><Skeleton h={72} /></div>}
      {knowledgeQuery.isError && (
        <div className="mb-3">
          <ErrorState inline error={knowledgeQuery.error} onRetry={() => void knowledgeQuery.refetch()} />
        </div>
      )}
      <div className="flex flex-col gap-2">
        {knowledge.map((k) => (
          <div key={k.id}
            className="bg-white rounded-[13px] p-[13px_15px]"
            style={{ border: `1px solid ${k.status === "needs-answer" ? "#F0DCA8" : "#EAEAEA"}` }}>
            <div className="flex items-start justify-between gap-[10px]">
              <div className="text-[13.5px] font-semibold text-ink">{k.q}</div>
              {k.status === "needs-answer" && (
                <span className="shrink-0 text-[10px] font-semibold px-2 py-[2px] rounded-full"
                  style={{ background: "#FBF3DF", color: "#7E5606" }}>
                  Needs answer
                </span>
              )}
            </div>
            {k.status === "needs-answer" && editingKb === k.id ? (
              <div className="mt-3">
                <textarea
                  value={draft}
                  onChange={e => setDraft(e.target.value)}
                  rows={3}
                  className="w-full border border-cd-border rounded-[10px] px-3 py-2 text-[13px] text-ink resize-none outline-none focus:border-[#2F6BEB] transition-colors"
                  placeholder="Add your answer…"
                  style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                />
                <div className="flex gap-2 mt-2">
                  <button
                    disabled={!draft.trim() || kbBusy}
                    onClick={() => { void saveKnowledge(k.id); }}
                    className="text-[12.5px] font-semibold text-white bg-ink px-3 py-1.5 rounded-[7px] border-none cursor-pointer hover:brightness-110 transition-all disabled:opacity-40 disabled:cursor-not-allowed"
                    style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                  >Save</button>
                  <button
                    onClick={() => setEditingKb(null)}
                    className="text-[12.5px] text-t2 bg-transparent border-none cursor-pointer hover:text-ink"
                    style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                  >Cancel</button>
                </div>
              </div>
            ) : k.status === "needs-answer" ? (
              <button
                onClick={() => { setEditingKb(k.id); setDraft(k.a || ""); }}
                className="mt-2 text-[12px] font-semibold text-[#2F6BEB] bg-transparent border-none cursor-pointer hover:underline p-0"
                style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
              >+ Add answer</button>
            ) : (
              <div className="text-[12.5px] text-t2 mt-[5px] leading-relaxed">{k.a}</div>
            )}
          </div>
        ))}
        <div className="flex gap-2 mt-1">
          <input
            value={newQ}
            onChange={e => setNewQ(e.target.value)}
            placeholder="Add a question the agent should know…"
            className="flex-1 h-[40px] border border-cd-border rounded-[10px] px-3 text-[13px] text-ink outline-none"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          />
          <button
            type="button"
            disabled={!newQ.trim() || kbBusy}
            onClick={() => { void addKnowledge(); }}
            className="text-[12.5px] font-semibold text-white bg-ink px-3 py-1.5 rounded-[8px] border-none cursor-pointer hover:brightness-110 disabled:opacity-40"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          >
            Add
          </button>
        </div>
      </div>
      <div className="h-2" />
    </div>
  );
}
