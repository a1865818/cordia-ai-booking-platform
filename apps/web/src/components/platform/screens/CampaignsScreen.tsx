import React, { useState } from "react";
import { CAMPAIGNS, campStatusMeta, chMeta } from "../../../data/console";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { SectionLabel } from "../../ui/SectionLabel";
import { DemoBadge } from "../../ui/DemoBadge";

const inputStyle: React.CSSProperties = {
  width: '100%', border: '1px solid #EAEAEA', borderRadius: 10,
  padding: '0 12px', fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 14, color: '#0A0A0A', background: '#fff', outline: 'none',
  height: 42,
};

export function CampaignsScreen() {
  const [state, setState] = useState<Record<string, { status: string; message: string; busy: boolean; scheduled: boolean }>>(() => {
    const o: Record<string, { status: string; message: string; busy: boolean; scheduled: boolean }> = {};
    CAMPAIGNS.forEach(c => (o[c.id] = { status: c.status, message: c.message, busy: false, scheduled: false }));
    return o;
  });
  const [editing, setEditing] = useState<string | null>(null);

  function set(id: string, patch: Partial<{ status: string; message: string; busy: boolean; scheduled: boolean }>) {
    setState(s => ({ ...s, [id]: { ...s[id], ...patch } }));
  }

  function approve(id: string) {
    if (state[id].busy) return;
    set(id, { busy: true });
    setTimeout(() => {
      set(id, { busy: false, status: 'scheduled', scheduled: true });
      setTimeout(() => set(id, { scheduled: false }), 3000);
    }, 900);
  }

  const awaiting = CAMPAIGNS.filter(c => state[c.id].status === 'awaiting');
  const rest = CAMPAIGNS.filter(c => state[c.id].status !== 'awaiting');

  return (
    <div>
      {/* MOCK: no endpoint yet — no backend and no spec owns campaigns. */}
      <div className="mb-3"><DemoBadge reason="no backend and no spec owns campaigns" /></div>
      {awaiting.length > 0 && (
        <>
          <SectionLabel line>{awaiting.length} awaiting your approval</SectionLabel>
          <div className="flex flex-col gap-3 mb-5">
            {awaiting.map(c => {
              const s = state[c.id];
              const isEdit = editing === c.id;
              const cm = chMeta(c.channel);
              return (
                <div key={c.id}
                  className="bg-white rounded-[14px] p-[15px_16px]"
                  style={{ border: '1px solid #EAEAEA', borderLeft: '3px solid #C98A09' }}>
                  <div className="flex items-start justify-between gap-[10px]">
                    <div className="min-w-0">
                      <div className="text-[15.5px] font-bold tracking-[-0.01em] text-ink">{c.title}</div>
                      <div className="text-[12.5px] text-t2 mt-[2px]">{c.audience}</div>
                    </div>
                    <span className="inline-flex items-center gap-[5px] shrink-0 text-[10.5px] font-semibold px-[9px] py-[3px] rounded-full"
                      style={{ background: campStatusMeta.awaiting.fill, color: campStatusMeta.awaiting.text }}>
                      <span className="w-[6px] h-[6px] rounded-full" style={{ background: campStatusMeta.awaiting.accent }} />
                      Awaiting approval
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
                  {isEdit ? (
                    <>
                      <textarea
                        value={s.message}
                        onChange={e => set(c.id, { message: e.target.value })}
                        rows={4}
                        className="mt-2 rounded-[10px] resize-y leading-[1.5] p-[11px_13px] outline-none w-full"
                        style={{ ...inputStyle, height: 'auto' }}
                      />
                      <div className="flex justify-end mt-1">
                        <span
                          className="font-mono text-[11px]"
                          style={{
                            color: s.message.length > 160 ? '#D23B30'
                              : s.message.length > 140 ? '#C98A09'
                              : '#9A9A9A',
                          }}
                        >
                          {s.message.length} / 160
                        </span>
                      </div>
                    </>
                  ) : (
                    <div className="text-[13px] text-ink leading-[1.55] rounded-[11px] p-[12px_13px] mt-2"
                      style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
                      {s.message}
                    </div>
                  )}
                  <div className="flex items-center gap-2 mt-[13px]">
                    <span className="font-mono text-[10.5px] tracking-[0.04em] uppercase text-t3">Never sends without you</span>
                    <div className="ml-auto flex gap-2">
                      {isEdit ? (
                        <PrimaryButton onClick={() => setEditing(null)}>Save</PrimaryButton>
                      ) : (
                        <>
                          <GhostButton onClick={() => set(c.id, { status: 'rejected' })} style={{ color: '#6B6B6B' }}>Reject</GhostButton>
                          <GhostButton icon="edit" onClick={() => setEditing(c.id)}>Edit</GhostButton>
                          <PrimaryButton busy={s.busy} disabled={s.busy} icon="check" onClick={() => approve(c.id)}>
                            {s.busy ? 'Approving' : 'Approve'}
                          </PrimaryButton>
                        </>
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
      <div className="flex flex-col gap-[9px]">
        {rest.map(c => {
          const stt = state[c.id].status;
          const m = campStatusMeta[stt];
          const isJustScheduled = state[c.id].scheduled;
          return (
            <div key={c.id} className="bg-white border border-cd-border rounded-[13px] p-[13px_15px]">
              <div className="flex items-start justify-between gap-[10px]">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <div className="text-[14.5px] font-semibold text-ink">{c.title}</div>
                    {isJustScheduled && (
                      <span className="text-[11px] font-semibold" style={{ color: '#0B6431', animation: 'fadeIn 160ms ease-out' }}>
                        Scheduled ✓
                      </span>
                    )}
                  </div>
                  <div className="text-[12px] text-t2 mt-[2px]">{c.when}</div>
                </div>
                {m && (
                  <span className="inline-flex items-center gap-[5px] shrink-0 text-[10.5px] font-semibold px-[9px] py-[3px] rounded-full"
                    style={{ background: m.fill, color: m.text }}>
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
