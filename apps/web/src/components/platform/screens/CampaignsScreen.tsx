import React, { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { campStatusMeta, chMeta } from "../../../data/console";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { SectionLabel } from "../../ui/SectionLabel";
import { ErrorState } from "../../ui/ErrorState";
import { Skeleton } from "../../ui/Skeleton";
import { queryKeys } from "../../../api/queryKeys";
import { useCampaigns } from "../../../features/hooks";
import {
  approveCampaign, createCampaign, rejectCampaign, scheduleCampaign, toConsoleCampaign,
} from "../../../features/campaigns/api";

const inputStyle: React.CSSProperties = {
  width: "100%", border: "1px solid #EAEAEA", borderRadius: 10,
  padding: "0 12px", fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 14, color: "#0A0A0A", background: "#fff", outline: "none",
  height: 42,
};

export function CampaignsScreen() {
  const queryClient = useQueryClient();
  const campaignsQuery = useCampaigns();
  const campaigns = (campaignsQuery.data?.campaigns ?? []).map(toConsoleCampaign);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [scheduleFor, setScheduleFor] = useState<string | null>(null);
  const [sendAt, setSendAt] = useState("");
  const [creating, setCreating] = useState(false);
  const [draft, setDraft] = useState({ title: "", audience: "", channel: "sms", message: "" });
  const [createBusy, setCreateBusy] = useState(false);

  function invalidate() {
    void queryClient.invalidateQueries({ queryKey: queryKeys.campaigns.all() });
  }

  async function run(id: string, fn: () => Promise<unknown>) {
    if (busyId) return;
    setBusyId(id);
    try {
      await fn();
      invalidate();
    } finally {
      setBusyId(null);
    }
  }

  async function submitCreate() {
    if (!draft.title.trim() || !draft.audience.trim() || !draft.message.trim()) return;
    setCreateBusy(true);
    try {
      await createCampaign({
        title: draft.title.trim(),
        audience: draft.audience.trim(),
        channel: draft.channel,
        message: draft.message.trim(),
      });
      setDraft({ title: "", audience: "", channel: "sms", message: "" });
      setCreating(false);
      invalidate();
    } finally {
      setCreateBusy(false);
    }
  }

  const awaiting = campaigns.filter((c) => c.status === "awaiting");
  const rest = campaigns.filter((c) => c.status !== "awaiting");

  return (
    <div>
      {campaignsQuery.isError && (
        <div className="mb-3">
          <ErrorState inline error={campaignsQuery.error} onRetry={() => void campaignsQuery.refetch()} />
        </div>
      )}

      {creating ? (
        <div className="bg-white border border-cd-border rounded-[14px] p-[15px_16px] mb-4">
          <div className="text-[15px] font-semibold text-ink mb-3">New campaign</div>
          <label className="block mb-2">
            <div className="text-[12px] font-semibold text-t2 mb-[5px]">Title</div>
            <input value={draft.title} onChange={(e) => setDraft((d) => ({ ...d, title: e.target.value }))} style={inputStyle} />
          </label>
          <label className="block mb-2">
            <div className="text-[12px] font-semibold text-t2 mb-[5px]">Audience</div>
            <input value={draft.audience} onChange={(e) => setDraft((d) => ({ ...d, audience: e.target.value }))} style={inputStyle} />
          </label>
          <label className="block mb-2">
            <div className="text-[12px] font-semibold text-t2 mb-[5px]">Channel</div>
            <select
              value={draft.channel}
              onChange={(e) => setDraft((d) => ({ ...d, channel: e.target.value }))}
              style={{ ...inputStyle, appearance: "none" }}
            >
              <option value="sms">SMS</option>
              <option value="email">Email</option>
              <option value="web">Web</option>
              <option value="voice">Phone</option>
            </select>
          </label>
          <label className="block mb-3">
            <div className="text-[12px] font-semibold text-t2 mb-[5px]">Message</div>
            <textarea
              value={draft.message}
              onChange={(e) => setDraft((d) => ({ ...d, message: e.target.value }))}
              rows={4}
              className="w-full rounded-[10px] resize-y leading-[1.5] p-[11px_13px] outline-none"
              style={{ ...inputStyle, height: "auto" }}
            />
          </label>
          <div className="flex gap-2 justify-end">
            <GhostButton onClick={() => setCreating(false)}>Cancel</GhostButton>
            <PrimaryButton
              busy={createBusy}
              disabled={createBusy || !draft.title.trim() || !draft.audience.trim() || !draft.message.trim()}
              onClick={() => void submitCreate()}
            >
              Create
            </PrimaryButton>
          </div>
        </div>
      ) : (
        <div className="mb-4">
          <GhostButton icon="plus" onClick={() => setCreating(true)}>New campaign</GhostButton>
        </div>
      )}

      {campaignsQuery.isPending && (
        <div className="flex flex-col gap-2 mb-4">
          <Skeleton h={120} /><Skeleton h={64} />
        </div>
      )}

      {awaiting.length > 0 && (
        <>
          <SectionLabel line>{awaiting.length} awaiting your approval</SectionLabel>
          <div className="flex flex-col gap-3 mb-5">
            {awaiting.map((c) => {
              const cm = chMeta(c.channel);
              const busy = busyId === c.id;
              return (
                <div
                  key={c.id}
                  className="bg-white rounded-[14px] p-[15px_16px]"
                  style={{ border: "1px solid #EAEAEA", borderLeft: "3px solid #C98A09" }}
                >
                  <div className="flex items-start justify-between gap-[10px]">
                    <div className="min-w-0">
                      <div className="text-[15.5px] font-bold tracking-[-0.01em] text-ink">{c.title}</div>
                      <div className="text-[12.5px] text-t2 mt-[2px]">{c.audience}</div>
                    </div>
                    <span
                      className="inline-flex items-center gap-[5px] shrink-0 text-[10.5px] font-semibold px-[9px] py-[3px] rounded-full"
                      style={{ background: campStatusMeta.awaiting.fill, color: campStatusMeta.awaiting.text }}
                    >
                      <span className="w-[6px] h-[6px] rounded-full" style={{ background: campStatusMeta.awaiting.accent }} />
                      {c.approved ? "Approved · schedule next" : "Awaiting approval"}
                    </span>
                  </div>
                  <div className="flex items-center gap-2 mt-[11px] mb-1">
                    <span className="inline-flex items-center gap-[5px] text-[11px] font-medium text-t2">
                      <span className="w-[7px] h-[7px] rounded-full" style={{ background: cm.color }} />
                      {cm.short}
                    </span>
                    <span className="text-[11px] text-t3">·</span>
                    <span className="font-mono text-[11px] text-t2">{c.recipients} recipients</span>
                  </div>
                  <div
                    className="text-[13px] text-ink leading-[1.55] rounded-[11px] p-[12px_13px] mt-2"
                    style={{ background: "#FBFBFA", border: "1px solid #EFEFEF" }}
                  >
                    {c.message}
                  </div>
                  <div className="flex items-center gap-2 mt-[13px]">
                    <span className="font-mono text-[10.5px] tracking-[0.04em] uppercase text-t3">Never sends without you</span>
                    <div className="ml-auto flex gap-2 flex-wrap justify-end">
                      {!c.approved && (
                        <>
                          <GhostButton disabled={busy} onClick={() => void run(c.id, () => rejectCampaign(c.id))} style={{ color: "#6B6B6B" }}>
                            Reject
                          </GhostButton>
                          <PrimaryButton busy={busy} disabled={busy} icon="check" onClick={() => void run(c.id, () => approveCampaign(c.id))}>
                            {busy ? "Approving" : "Approve"}
                          </PrimaryButton>
                        </>
                      )}
                      {c.approved && (
                        scheduleFor === c.id ? (
                          <>
                            <input
                              type="datetime-local"
                              value={sendAt}
                              onChange={(e) => setSendAt(e.target.value)}
                              style={{ ...inputStyle, width: 210, height: 36 }}
                            />
                            <GhostButton onClick={() => setScheduleFor(null)}>Cancel</GhostButton>
                            <PrimaryButton
                              busy={busy}
                              disabled={busy || !sendAt}
                              onClick={() => void run(c.id, () => scheduleCampaign(c.id, new Date(sendAt).toISOString())).then(() => setScheduleFor(null))}
                            >
                              Schedule
                            </PrimaryButton>
                          </>
                        ) : (
                          <PrimaryButton onClick={() => { setScheduleFor(c.id); setSendAt(""); }}>
                            Schedule
                          </PrimaryButton>
                        )
                      )}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}

      <SectionLabel line>Scheduled &amp; sent</SectionLabel>
      {!campaignsQuery.isPending && rest.length === 0 && awaiting.length === 0 && (
        <div className="text-center text-[13px] text-t3 py-8">No campaigns</div>
      )}
      <div className="flex flex-col gap-[9px]">
        {rest.map((c) => {
          const m = campStatusMeta[c.status];
          return (
            <div key={c.id} className="bg-white border border-cd-border rounded-[13px] p-[13px_15px]">
              <div className="flex items-start justify-between gap-[10px]">
                <div className="min-w-0">
                  <div className="text-[14.5px] font-semibold text-ink">{c.title}</div>
                  <div className="text-[12px] text-t2 mt-[2px]">{c.when}</div>
                </div>
                {m && (
                  <span
                    className="inline-flex items-center gap-[5px] shrink-0 text-[10.5px] font-semibold px-[9px] py-[3px] rounded-full"
                    style={{ background: m.fill, color: m.text }}
                  >
                    <span className="w-[6px] h-[6px] rounded-full" style={{ background: m.accent }} />
                    {m.label}
                  </span>
                )}
              </div>
            </div>
          );
        })}
      </div>
      <div className="h-2" />
    </div>
  );
}
