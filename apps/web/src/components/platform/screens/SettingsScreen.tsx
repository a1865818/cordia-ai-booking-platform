import React, { useState } from "react";
import { SETTINGS, SettingsKnowledge, Service, TenantConfig, BUSINESS_TYPES } from "../../../data/console";
import { Toggle } from "../../ui/Toggle";
import { Panel } from "../../ui/Panel";
import { SectionLabel } from "../../ui/SectionLabel";
import { Icon, IconName } from "../../ui/Icon";

// ---- Business setup ----

interface BusinessSetupProps {
  tenantCfg: TenantConfig;
  onUpdateTenant: (t: TenantConfig) => void;
  device?: 'phone' | 'desktop';
}

function BusinessSetup({ tenantCfg, onUpdateTenant, device }: BusinessSetupProps) {
  function select(key: string) {
    const bt = BUSINESS_TYPES.find(b => b.key === key);
    if (!bt) return;
    onUpdateTenant({ ...tenantCfg, businessType: key, resourceLabel: bt.resourceLabel, resourceLabelPlural: bt.resourceLabelPlural });
  }

  return (
    <Panel style={{ marginBottom: 18 }}>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: device === 'phone' ? 'repeat(2, 1fr)' : 'repeat(4, 1fr)',
          gap: 10,
          padding: '4px 0 2px',
        }}
      >
        {BUSINESS_TYPES.map(bt => {
          const active = tenantCfg.businessType === bt.key;
          return (
            <button
              key={bt.key}
              type="button"
              onClick={() => select(bt.key)}
              style={{
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                gap: 7,
                padding: '12px 8px 10px',
                borderRadius: 12,
                border: `1.5px solid ${active ? '#0A0A0A' : '#EAEAEA'}`,
                background: active ? '#EBEBEA' : '#FAFAF9',
                cursor: 'pointer',
                transition: 'all 120ms ease',
                fontFamily: "'Inter Tight', system-ui, sans-serif",
              }}
            >
              <span
                style={{
                  width: 34,
                  height: 34,
                  borderRadius: 9,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  background: active ? '#DDF247' : '#F1F1EE',
                  transition: 'background 120ms ease',
                }}
              >
                <Icon name={bt.icon as IconName} size={17} strokeWidth={1.9} color={active ? '#0A0A0A' : '#6B6B6B'} />
              </span>
              <span
                style={{
                  fontSize: 11.5,
                  fontWeight: 600,
                  color: active ? '#0A0A0A' : '#55554F',
                  textAlign: 'center',
                  lineHeight: 1.25,
                  letterSpacing: '-0.01em',
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
        style={{ marginTop: 14, paddingTop: 13, borderTop: '1px solid #EFEFEF' }}
      >
        <div>
          <div className="text-[13.5px] font-medium text-ink">Resource unit</div>
          <div className="text-[11.5px] text-t3 mt-[1px]">Label used for rows in the schedule grid</div>
        </div>
        <span
          className="font-mono text-[12px] font-semibold px-[10px] py-[5px] rounded-[8px]"
          style={{ background: '#F1F1EE', color: '#0A0A0A' }}
        >
          {tenantCfg.resourceLabel} / {tenantCfg.resourceLabelPlural}
        </span>
      </div>
    </Panel>
  );
}

// ---- Services management ----

interface ServiceRowProps {
  svc: Service;
  onEdit: () => void;
  onDelete: () => void;
}

function ServiceRow({ svc, onEdit, onDelete }: ServiceRowProps) {
  const dur = (() => {
    const h = Math.floor(svc.dur / 60), m = svc.dur % 60;
    return (h ? h + 'h' : '') + (m ? (h ? ' ' : '') + m + 'm' : '');
  })();

  return (
    <div
      className="flex items-center gap-3 py-[10px]"
      style={{ borderBottom: '1px solid #EFEFEF' }}
    >
      <div className="flex-1 min-w-0">
        <div className="text-[13.5px] font-semibold text-ink">{svc.name}</div>
        <div className="font-mono text-[11px] text-t3 mt-[2px]">
          {dur} · A${svc.dep} deposit
        </div>
      </div>
      <button
        type="button"
        onClick={onEdit}
        className="w-8 h-8 flex items-center justify-center rounded-[8px] border border-cd-border bg-white cursor-pointer hover:bg-wash transition-colors"
      >
        <Icon name="edit" size={14} strokeWidth={1.9} color="#6B6B6B" />
      </button>
      <button
        type="button"
        onClick={onDelete}
        className="w-8 h-8 flex items-center justify-center rounded-[8px] border border-cd-border bg-white cursor-pointer hover:bg-[#FFF2F2] transition-colors"
      >
        <Icon name="trash" size={14} strokeWidth={1.9} color="#D23B30" />
      </button>
    </div>
  );
}

interface ServiceEditorProps {
  initial: Service | null;
  onSave: (s: Service) => void;
  onCancel: () => void;
}

const DUR_OPTS = [15, 30, 45, 60, 75, 90, 105, 120, 150, 180, 240];

function fmtDurOpt(m: number): string {
  const h = Math.floor(m / 60), mm = m % 60;
  return (h ? h + 'h' : '') + (mm ? (h ? ' ' : '') + mm + 'm' : '');
}

function ServiceEditor({ initial, onSave, onCancel }: ServiceEditorProps) {
  const [name, setName] = useState(initial?.name ?? '');
  const [dur, setDur] = useState(initial?.dur ?? 60);
  const [dep, setDep] = useState(initial?.dep ?? 0);

  function save() {
    if (!name.trim()) return;
    onSave({
      key: initial?.key ?? name.toLowerCase().replace(/\s+/g, '_').replace(/[^a-z0-9_]/g, ''),
      name: name.trim(),
      dur,
      dep,
    });
  }

  const inputStyle: React.CSSProperties = {
    height: 38, border: '1px solid #EAEAEA', borderRadius: 9,
    padding: '0 10px', fontFamily: "'Inter Tight', system-ui, sans-serif",
    fontSize: 13, color: '#0A0A0A', background: '#fff', outline: 'none',
    width: '100%',
  };

  return (
    <div
      className="rounded-[12px] p-[13px_14px]"
      style={{ background: '#FBFBFA', border: '1px solid #E5E5E5', marginTop: 4 }}
    >
      <div className="mb-[10px]">
        <div className="text-[11.5px] font-semibold text-t2 mb-[5px]">Service name</div>
        <input
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder="e.g. Full set, Lawn mow…"
          style={inputStyle}
          autoFocus
        />
      </div>
      <div className="flex gap-[10px] mb-[12px]">
        <div className="flex-1">
          <div className="text-[11.5px] font-semibold text-t2 mb-[5px]">Duration</div>
          <div className="relative">
            <select
              value={dur}
              onChange={e => setDur(Number(e.target.value))}
              style={{ ...inputStyle, appearance: 'none', paddingRight: 28, cursor: 'pointer' }}
            >
              {DUR_OPTS.map(m => (
                <option key={m} value={m}>{fmtDurOpt(m)}</option>
              ))}
            </select>
            <span className="absolute right-2 top-1/2 -translate-y-1/2 pointer-events-none">
              <Icon name="chevD" size={13} strokeWidth={2.2} color="#9A9A9A" />
            </span>
          </div>
        </div>
        <div className="flex-1">
          <div className="text-[11.5px] font-semibold text-t2 mb-[5px]">Deposit (A$)</div>
          <input
            type="number"
            min={0}
            value={dep}
            onChange={e => setDep(Math.max(0, Number(e.target.value)))}
            style={inputStyle}
          />
        </div>
      </div>
      <div className="flex gap-2">
        <button
          type="button"
          onClick={save}
          disabled={!name.trim()}
          className="text-[12.5px] font-semibold text-white bg-ink px-4 py-[7px] rounded-[8px] border-none cursor-pointer hover:brightness-110 transition-all disabled:opacity-40 disabled:cursor-not-allowed"
          style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          {initial ? 'Save changes' : 'Add service'}
        </button>
        <button
          type="button"
          onClick={onCancel}
          className="text-[12.5px] text-t2 bg-transparent border-none cursor-pointer hover:text-ink px-2"
          style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          Cancel
        </button>
      </div>
    </div>
  );
}

interface ServicesManagementProps {
  services: Service[];
  onUpdateServices: (s: Service[]) => void;
}

function ServicesManagement({ services, onUpdateServices }: ServicesManagementProps) {
  const [editingKey, setEditingKey] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  function handleEdit(updated: Service) {
    onUpdateServices(services.map(s => s.key === updated.key ? updated : s));
    setEditingKey(null);
  }

  function handleAdd(svc: Service) {
    const key = services.some(s => s.key === svc.key)
      ? svc.key + '_' + Date.now()
      : svc.key;
    onUpdateServices([...services, { ...svc, key }]);
    setAdding(false);
  }

  function handleDelete(key: string) {
    onUpdateServices(services.filter(s => s.key !== key));
  }

  return (
    <Panel style={{ marginBottom: 18 }}>
      {services.map(svc => (
        <div key={svc.key}>
          {editingKey === svc.key ? (
            <ServiceEditor
              initial={svc}
              onSave={handleEdit}
              onCancel={() => setEditingKey(null)}
            />
          ) : (
            <ServiceRow
              svc={svc}
              onEdit={() => { setAdding(false); setEditingKey(svc.key); }}
              onDelete={() => handleDelete(svc.key)}
            />
          )}
        </div>
      ))}
      {services.length === 0 && !adding && (
        <div className="py-6 text-center text-[13px] text-t3">No services yet.</div>
      )}
      {adding ? (
        <ServiceEditor initial={null} onSave={handleAdd} onCancel={() => setAdding(false)} />
      ) : (
        <button
          type="button"
          onClick={() => { setEditingKey(null); setAdding(true); }}
          className="flex items-center gap-[6px] text-[13px] font-semibold text-ink mt-[10px] bg-transparent border-none cursor-pointer hover:opacity-70 transition-opacity p-0"
          style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          <Icon name="plus" size={15} strokeWidth={2.4} color="#0A0A0A" />
          Add service
        </button>
      )}
    </Panel>
  );
}

// ---- Main screen ----

interface SettingsScreenProps {
  services: Service[];
  tenantCfg: TenantConfig;
  onUpdateServices: (s: Service[]) => void;
  onUpdateTenant: (t: TenantConfig) => void;
  device?: 'phone' | 'desktop';
}

export function SettingsScreen({ services, tenantCfg, onUpdateServices, onUpdateTenant, device }: SettingsScreenProps) {
  const [guards, setGuards] = useState<Record<string, boolean>>(() => {
    const o: Record<string, boolean> = {};
    SETTINGS.guardrails.forEach(g => (o[g.id] = g.on));
    return o;
  });
  const [reqDep, setReqDep] = useState(SETTINGS.deposit.require);
  const [kb, setKb] = useState<SettingsKnowledge[]>(SETTINGS.knowledge.map(k => ({ ...k })));
  const [editing, setEditing] = useState<number | null>(null);
  const [draft, setDraft] = useState('');

  return (
    <div>
      {/* Business setup */}
      <SectionLabel>Business setup</SectionLabel>
      <BusinessSetup tenantCfg={tenantCfg} onUpdateTenant={onUpdateTenant} device={device} />

      {/* Services */}
      <SectionLabel>Services</SectionLabel>
      <ServicesManagement services={services} onUpdateServices={onUpdateServices} />

      {/* Hours */}
      <SectionLabel>Hours</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {SETTINGS.hours.map((h, i) => (
          <div key={h.d}
            className="flex items-center justify-between py-[11px]"
            style={{ borderBottom: i < SETTINGS.hours.length - 1 ? '1px solid #EFEFEF' : 'none' }}>
            <span className="text-[13.5px] text-ink font-medium">{h.d}</span>
            {h.on
              ? <span className="font-mono text-[12px] text-t2">{h.open} – {h.close}</span>
              : <span className="text-[12px] text-t3">Closed</span>}
          </div>
        ))}
      </Panel>

      {/* Deposit policy */}
      <SectionLabel>Deposit policy</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        <div className="flex items-center justify-between py-3 border-b border-hair">
          <div>
            <div className="text-[13.5px] font-medium text-ink">Require a deposit</div>
            <div className="text-[11.5px] text-t3 mt-[1px]">
              {SETTINGS.deposit.amountPolicy} · refundable up to {SETTINGS.deposit.refundWindow}
            </div>
          </div>
          <Toggle on={reqDep} onClick={() => setReqDep(v => !v)} />
        </div>
        <div className="flex items-center justify-between py-3">
          <span className="text-[13.5px] text-ink">Per-service amounts</span>
          <span className="inline-flex items-center gap-[5px] text-[12px] text-t2">
            {services.length} services
            <Icon name="chevR" size={14} strokeWidth={2.2} color="#9A9A9A" />
          </span>
        </div>
      </Panel>

      {/* Guardrails */}
      <SectionLabel>Agent guardrails</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {SETTINGS.guardrails.map((g, i) => (
          <div key={g.id}
            className="flex items-center gap-3 py-3"
            style={{ borderBottom: i < SETTINGS.guardrails.length - 1 ? '1px solid #EFEFEF' : 'none' }}>
            <div className="flex-1">
              <div className="text-[13.5px] text-ink flex items-center gap-[7px]">
                {g.label}
                {g.locked && <Icon name="shield" size={13} strokeWidth={2} color="#9A9A9A" />}
              </div>
              {g.locked && <div className="text-[11px] text-t3 mt-[2px]">Core safety rule · always on</div>}
            </div>
            {g.locked
              ? <span className="font-mono text-[10px] tracking-[0.06em] uppercase px-[9px] py-1 rounded-full"
                  style={{ color: '#0B6431', background: '#E7F6EC' }}>On</span>
              : <Toggle on={guards[g.id]} onClick={() => setGuards(s => ({ ...s, [g.id]: !s[g.id] }))} />}
          </div>
        ))}
      </Panel>

      {/* Knowledge base */}
      <SectionLabel line right={
        <span className="font-mono text-[10.5px] text-t3">{kb.filter(k => k.status === 'needs-answer').length} needs an answer</span>
      }>
        Agent knowledge base
      </SectionLabel>
      <div className="flex flex-col gap-2">
        {kb.map((k, i) => (
          <div key={i}
            className="bg-white rounded-[13px] p-[13px_15px]"
            style={{ border: `1px solid ${k.status === 'needs-answer' ? '#F0DCA8' : '#EAEAEA'}` }}>
            <div className="flex items-start justify-between gap-[10px]">
              <div className="text-[13.5px] font-semibold text-ink">{k.q}</div>
              {k.status === 'needs-answer' && (
                <span className="shrink-0 text-[10px] font-semibold px-2 py-[2px] rounded-full"
                  style={{ background: '#FBF3DF', color: '#7E5606' }}>
                  Needs answer
                </span>
              )}
            </div>
            {k.status === 'needs-answer' && editing === i ? (
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
                    onClick={() => {
                      setKb(prev => prev.map((item, idx) => idx === i ? { ...item, a: draft, status: 'answered' as const } : item));
                      setEditing(null);
                    }}
                    className="text-[12.5px] font-semibold text-white bg-ink px-3 py-1.5 rounded-[7px] border-none cursor-pointer hover:brightness-110 transition-all"
                    style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                  >Save</button>
                  <button
                    onClick={() => setEditing(null)}
                    className="text-[12.5px] text-t2 bg-transparent border-none cursor-pointer hover:text-ink"
                    style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                  >Cancel</button>
                </div>
              </div>
            ) : k.status === 'needs-answer' ? (
              <button
                onClick={() => { setEditing(i); setDraft(k.a || ''); }}
                className="mt-2 text-[12px] font-semibold text-[#2F6BEB] bg-transparent border-none cursor-pointer hover:underline p-0"
                style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
              >+ Add answer</button>
            ) : (
              <div className="text-[12.5px] text-t2 mt-[5px] leading-relaxed">{k.a}</div>
            )}
          </div>
        ))}
      </div>
      <div className="h-2" />
    </div>
  );
}
