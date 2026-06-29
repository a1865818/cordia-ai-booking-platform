import React, { useEffect, useState, useRef } from "react";
import { Booking, WEEK, STAFF, fmtT, TIME_SLOTS, DAY_OPTS } from "../../../data/console";
import { Drawer } from "../../ui/Drawer";
import { PrimaryButton } from "../../ui/Button";
import { Icon } from "../../ui/Icon";

function useRetained<T>(val: T | null, open: boolean): T | null {
  const ref = useRef<T | null>(val);
  if (val) ref.current = val;
  return open ? val : ref.current;
}

const inputStyle: React.CSSProperties = {
  width: '100%', height: 42, border: '1px solid #EAEAEA', borderRadius: 10,
  padding: '0 12px', fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 14, color: '#0A0A0A', background: '#fff', outline: 'none',
};
const selectStyle: React.CSSProperties = { ...inputStyle, appearance: 'none', cursor: 'pointer', paddingRight: 34 };

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block mb-[13px]">
      <div className="text-[12px] font-semibold mb-[6px]" style={{ color: '#55554F' }}>{label}</div>
      {children}
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

interface RescheduleDrawerProps {
  open: boolean;
  device: 'phone' | 'desktop';
  booking: Booking | null;
  onClose: () => void;
  onDone: (id: string, patch: { day: number; start: number; staff: string }) => void;
}

export function RescheduleDrawer({ open, device, booking, onClose, onDone }: RescheduleDrawerProps) {
  const b = useRetained(booking, open);
  const [day, setDay] = useState('0');
  const [time, setTime] = useState('600');
  const [staff, setStaff] = useState('s1');
  const [phase, setPhase] = useState<'form' | 'confirming' | 'done'>('form');
  const confirmTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (open && booking) {
      setDay(String(booking.day));
      setTime(String(booking.start));
      setStaff(booking.staff);
      setPhase('form');
    }
  }, [open, booking?.id]);

  // Cancel a pending confirm when the drawer closes or unmounts, so onDone
  // never fires after the user dismisses it.
  useEffect(() => {
    if (open) return;
    if (confirmTimer.current) { clearTimeout(confirmTimer.current); confirmTimer.current = null; }
  }, [open]);

  useEffect(() => () => {
    if (confirmTimer.current) clearTimeout(confirmTimer.current);
  }, []);

  if (!b) return <Drawer open={open} onClose={onClose} device={device} title="Reschedule">{null}</Drawer>;

  const eligible = STAFF.filter(s => s.skills.includes(b.service));

  function submit() {
    if (phase !== 'form') return;
    setPhase('confirming');
    confirmTimer.current = setTimeout(() => {
      confirmTimer.current = null;
      setPhase('done');
      onDone(b!.id, { day: Number(day), start: Number(time), staff });
    }, 950);
  }

  const footer = phase === 'done'
    ? <PrimaryButton full onClick={onClose}>Done</PrimaryButton>
    : <PrimaryButton full busy={phase === 'confirming'} onClick={submit}>
        {phase === 'confirming' ? 'Updating' : 'Confirm new time'}
      </PrimaryButton>;

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Reschedule" title={b.name} footer={footer}>
      {phase === 'done' ? (
        <div className="text-center py-6 px-2">
          <span className="w-14 h-14 rounded-full inline-flex items-center justify-center mb-[14px]" style={{ background: '#E7F6EC' }}>
            <Icon name="check" size={26} strokeWidth={2.4} color="#15924B" />
          </span>
          <div className="text-[18px] font-bold tracking-[-0.02em] text-ink">Moved</div>
          <div className="text-[13px] text-t2 mt-[5px] leading-[1.5]">
            {b.serviceName} is now<br />
            {DAY_OPTS[Number(day)]?.label} at {fmtT(Number(time))}
          </div>
          <div className="text-[12px] text-t3 mt-3 leading-[1.5]">
            The customer gets an SMS with the new time and a link to confirm.
          </div>
        </div>
      ) : (
        <fieldset disabled={phase === 'confirming'} className="border-none p-0 m-0" style={{ opacity: phase === 'confirming' ? 0.6 : 1 }}>
          <div className="flex items-center gap-[9px] p-[11px_13px] rounded-[11px] mb-[15px]"
            style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
            <Icon name="clock" size={16} strokeWidth={1.9} color="#9A9A9A" />
            <div className="text-[12.5px] text-t2">
              Currently{' '}
              <span className="font-semibold text-ink">
                {WEEK.dows[b.day].charAt(0) + WEEK.dows[b.day].slice(1).toLowerCase()} {WEEK.doms[b.day]}
              </span>
              {' '}at{' '}
              <span className="font-semibold text-ink">{fmtT(b.start)}</span>
            </div>
          </div>
          <div className="flex gap-[10px]">
            <div className="flex-1">
              <Field label="New day">
                <SelectField value={day} onChange={setDay} options={DAY_OPTS} />
              </Field>
            </div>
            <div className="flex-1">
              <Field label="New time">
                <SelectField value={time} onChange={setTime} options={TIME_SLOTS} />
              </Field>
            </div>
          </div>
          <Field label="With">
            <SelectField value={staff} onChange={setStaff} options={eligible.map(s => ({ value: s.id, label: `${s.name} — ${s.role}` }))} />
          </Field>
        </fieldset>
      )}
    </Drawer>
  );
}
