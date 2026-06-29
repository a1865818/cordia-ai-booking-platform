import React, { useEffect, useState } from "react";
import { Service, STAFF, CUSTOMERS, WEEK, fmtT, fmtDur, money, TIME_SLOTS, DAY_OPTS, Booking } from "../../../data/console";
import { Drawer } from "../../ui/Drawer";
import { PrimaryButton } from "../../ui/Button";
import { Icon } from "../../ui/Icon";

const inputStyle: React.CSSProperties = {
  width: '100%', height: 42, border: '1px solid #EAEAEA', borderRadius: 10,
  padding: '0 12px', fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 14, color: '#0A0A0A', background: '#fff', outline: 'none',
};
const selectStyle: React.CSSProperties = { ...inputStyle, appearance: 'none', cursor: 'pointer', paddingRight: 34 };

function Field({ label, children, hint }: { label: string; children: React.ReactNode; hint?: string }) {
  return (
    <label className="block mb-[13px]">
      <div className="text-[12px] font-semibold mb-[6px]" style={{ color: '#55554F' }}>{label}</div>
      {children}
      {hint && <div className="text-[11.5px] text-t3 mt-[5px]">{hint}</div>}
    </label>
  );
}

function SelectField({ value, onChange, options }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[] }) {
  return (
    <div className="relative">
      <select value={value} onChange={e => onChange(e.target.value)} style={selectStyle}>
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
      <span className="absolute right-3 top-1/2 -translate-y-1/2 pointer-events-none">
        <Icon name="chevD" size={14} strokeWidth={2.2} color="#9A9A9A" />
      </span>
    </div>
  );
}

interface CreateBookingDrawerProps {
  open: boolean;
  device: 'phone' | 'desktop';
  onClose: () => void;
  onCreated: (b: Omit<Booking, 'id'>) => void;
  services: Service[];
}

export function CreateBookingDrawer({ open, device, onClose, onCreated, services }: CreateBookingDrawerProps) {
  const [name, setName] = useState('');
  const [customerSearch, setCustomerSearch] = useState('');
  const [showDropdown, setShowDropdown] = useState(false);
  const [svc, setSvc] = useState('');
  const [staff, setStaff] = useState('s1');
  const [day, setDay] = useState(String(WEEK.today));
  const [time, setTime] = useState('600');
  const [phase, setPhase] = useState<'form' | 'confirming' | 'done'>('form');

  useEffect(() => {
    if (open) {
      setName(''); setCustomerSearch('');
      setSvc(services[0]?.key ?? ''); setStaff('s1');
      setDay(String(WEEK.today)); setTime('600'); setPhase('form'); setShowDropdown(false);
    }
  }, [open, services]);

  const service = services.find(s => s.key === svc) ?? services[0];
  const eligible = STAFF; // all staff eligible across business types

  useEffect(() => {
    if (!eligible.find(s => s.id === staff) && eligible[0]) setStaff(eligible[0].id);
  }, [svc]);

  const suggestions = CUSTOMERS.filter(c =>
    c.name.toLowerCase().includes(customerSearch.toLowerCase()) && customerSearch.length > 0
  ).slice(0, 6);

  function submit() {
    if (phase !== 'form' || !name.trim()) return;
    setPhase('confirming');
    setTimeout(() => {
      setPhase('done');
      onCreated({
        day: Number(day), staff, start: Number(time), service: svc, serviceName: service.name,
        name: name.trim(), prov: 'you', status: 'confirmed', channel: null, dep: service.dep, transcript: null,
        dur: service.dur,
      });
    }, 1000);
  }

  const footer = phase === 'done'
    ? <PrimaryButton full onClick={onClose}>Done</PrimaryButton>
    : <PrimaryButton full busy={phase === 'confirming'} disabled={!name.trim() || phase !== 'form'} onClick={submit}>
        {phase === 'confirming' ? 'Confirming' : 'Confirm booking'}
      </PrimaryButton>;

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Manual booking" title="New booking" footer={footer}>
      {phase === 'done' ? (
        <div className="text-center py-6 px-2">
          <span className="w-14 h-14 rounded-full inline-flex items-center justify-center mb-[14px]" style={{ background: '#E7F6EC' }}>
            <Icon name="check" size={26} strokeWidth={2.4} color="#15924B" />
          </span>
          <div className="text-[18px] font-bold tracking-[-0.02em] text-ink">Booking confirmed</div>
          <div className="text-[13px] text-t2 mt-[5px] leading-[1.5]">
            {name} · {service.name}<br />
            {DAY_OPTS[Number(day)]?.label} at {fmtT(Number(time))}
          </div>
          <div className="inline-flex items-center gap-[6px] mt-[14px] px-[11px] py-[6px] rounded-full bg-wash2">
            <span className="font-mono text-[10.5px] font-semibold text-ink">{money(service.dep)} deposit requested</span>
          </div>
        </div>
      ) : (
        <fieldset disabled={phase === 'confirming'} className="border-none p-0 m-0" style={{ opacity: phase === 'confirming' ? 0.6 : 1 }}>
          {/* Customer autocomplete */}
          <Field label="Customer">
            <div className="relative">
              <input
                value={customerSearch || name}
                onChange={e => {
                  const v = e.target.value;
                  setCustomerSearch(v);
                  setName(v);
                  setShowDropdown(true);
                }}
                onFocus={() => setShowDropdown(true)}
                onBlur={() => setTimeout(() => setShowDropdown(false), 150)}
                placeholder="Search or add a customer"
                className="w-full h-[42px] border border-cd-border rounded-[10px] px-3 text-[14px] text-ink outline-none focus:border-[#2F6BEB] transition-colors"
                style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
              />
              {showDropdown && suggestions.length > 0 && (
                <div className="absolute left-0 right-0 top-[46px] bg-white border border-cd-border rounded-[11px] shadow-[0_8px_24px_-8px_rgba(0,0,0,0.15)] z-10 overflow-hidden">
                  {suggestions.map(c => (
                    <button
                      key={c.id}
                      type="button"
                      onMouseDown={() => { setName(c.name); setCustomerSearch(''); setShowDropdown(false); }}
                      className="flex items-center gap-3 w-full px-3 py-2.5 hover:bg-wash text-left border-none bg-white cursor-pointer transition-colors"
                    >
                      <span className="w-8 h-8 rounded-[9px] bg-wash2 flex items-center justify-center text-[12px] font-semibold text-ink shrink-0">
                        {c.name.split(' ').map(w => w[0]).slice(0, 2).join('').toUpperCase()}
                      </span>
                      <div>
                        <div className="text-[13.5px] font-semibold text-ink">{c.name}</div>
                        <div className="text-[11px] text-t3">{c.fav}</div>
                      </div>
                    </button>
                  ))}
                  <button
                    type="button"
                    onMouseDown={() => setShowDropdown(false)}
                    className="flex items-center gap-2 w-full px-3 py-2.5 hover:bg-wash text-left border-none border-t border-hair bg-white cursor-pointer text-[13px] text-t2 transition-colors"
                  >
                    + New customer "{customerSearch}"
                  </button>
                </div>
              )}
            </div>
          </Field>

          <Field label="Service">
            <SelectField value={svc} onChange={setSvc} options={services.map(s => ({ value: s.key, label: `${s.name} · ${fmtDur(s.dur)}` }))} />
          </Field>
          <Field label="With">
            <SelectField value={staff} onChange={setStaff} options={eligible.map(s => ({ value: s.id, label: `${s.name} — ${s.role}` }))} />
          </Field>
          <div className="flex gap-[10px]">
            <div className="flex-1">
              <Field label="Day">
                <SelectField value={day} onChange={setDay} options={DAY_OPTS} />
              </Field>
            </div>
            <div className="flex-1">
              <Field label="Time">
                <SelectField value={time} onChange={setTime} options={TIME_SLOTS} />
              </Field>
            </div>
          </div>
          <div className="flex items-center gap-[10px] p-[12px_13px] rounded-[11px] mt-[3px]"
            style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
            <Icon name="card" size={17} strokeWidth={1.9} color="#6B6B6B" />
            <div className="flex-1">
              <div className="text-[12.5px] font-semibold text-ink">{money(service.dep)} deposit</div>
              <div className="text-[11px] text-t3">Mirrors your deposit policy</div>
            </div>
            <span className="inline-flex items-center gap-1 text-[10.5px] font-semibold px-2 py-[3px] rounded-[7px] bg-wash2 text-ink70">Auto</span>
          </div>
        </fieldset>
      )}
    </Drawer>
  );
}
