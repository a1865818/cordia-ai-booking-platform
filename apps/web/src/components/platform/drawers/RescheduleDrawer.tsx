import React, { useEffect, useMemo, useState, useRef } from "react";
import { Booking, fmtT, TIME_SLOTS } from "../../../data/console";
import { dayOptions, shortDayLabel, type ConsoleWeek } from "../../../features/bookings/week";
import type { GridResource } from "../../../features/bookings/resources";
import { timeSlotsFromHours, type OperatingHour } from "../../../features/settings/hours";
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
  /** The displayed week — both the "currently" label and the day picker are dated from it. */
  week: ConsoleWeek;
  booking: Booking | null;
  onClose: () => void;
  onDone: (id: string, patch: { day: number; start: number; staff: string; dur: number }) => void;
  hours?: OperatingHour[];
  resources?: readonly GridResource[];
}

export function RescheduleDrawer({ open, device, week, booking, onClose, onDone, hours, resources }: RescheduleDrawerProps) {
  const b = useRetained(booking, open);
  const [day, setDay] = useState('0');
  const [time, setTime] = useState('600');
  const namedResources = (resources ?? []).filter(r => r.name);
  const showStaffPicker = namedResources.length > 0;
  const [resourceId, setResourceId] = useState('');

  useEffect(() => {
    if (open && booking) {
      setDay(String(booking.day));
      setTime(String(booking.start));
      setResourceId(booking.staff);
    }
  }, [open, booking?.id]);

  const baseSlots = useMemo(() => {
    const slots = timeSlotsFromHours(hours);
    return slots.length > 0 ? slots : TIME_SLOTS;
  }, [hours]);

  const timeOptions = useMemo(() => {
    const current = b ? String(b.start) : null;
    if (current === null || baseSlots.some(o => o.value === current)) return baseSlots;
    return [...baseSlots, { value: current, label: fmtT(b!.start) }]
      .sort((x, y) => Number(x.value) - Number(y.value));
  }, [b, baseSlots]);

  if (!b) return <Drawer open={open} onClose={onClose} device={device} title="Reschedule">{null}</Drawer>;

  // No local phase, no local timer and no local "done".
  //
  // The drawer hands the change to the parent, which owns the real submission and reports the
  // server's outcome. It used to keep a `phase` and a `confirmTimer` and render its own green
  // "Moved" panel on a fixed delay — declaring a booking moved before anything had been asked of
  // the server. Once the submission moved out, nothing ever set those states again: the panel and
  // the timer became unreachable code that still described the behaviour they had been removed
  // for. Success and failure are ConsolePage's to report, via toasts on the real operation.
  function submit() {
    // `staff` comes from the BOOKING, not from a picker. The picker was removed (the platform
    // models no resource) but its `useState('s1')` default was still being submitted, so every
    // drawer reschedule quietly claimed the booking had moved to a hard-coded lane. The drag
    // path legitimately varies this, which is why the field stays in the shared patch type.
    //
    // `dur` is carried through unchanged. Omitting it sent `durationMinutes: undefined`, which
    // `toCanonicalPayload` omits entirely — so the reschedule wrote a payload with no duration and
    // the booking came back from the projection as `duration_minutes: null`, collapsing to the
    // grid's 60-minute default. A 90-minute service silently became a 60-minute one, durably.
    onDone(b!.id, {
      day: Number(day),
      start: Number(time),
      staff: showStaffPicker && resourceId ? resourceId : b!.staff,
      dur: b!.dur,
    });
    onClose();
  }

  const footer = <PrimaryButton full onClick={submit}>Confirm new time</PrimaryButton>;

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Reschedule" title={b.name} footer={footer}>
      <fieldset className="border-none p-0 m-0">
          <div className="flex items-center gap-[9px] p-[11px_13px] rounded-[11px] mb-[15px]"
            style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
            <Icon name="clock" size={16} strokeWidth={1.9} color="#9A9A9A" />
            <div className="text-[12.5px] text-t2">
              Currently{' '}
              <span className="font-semibold text-ink">{shortDayLabel(week, b.day)}</span>
              {' '}at{' '}
              <span className="font-semibold text-ink">{fmtT(b.start)}</span>
            </div>
          </div>
          <div className="flex gap-[10px]">
            <div className="flex-1">
              <Field label="New day">
                <SelectField value={day} onChange={setDay} options={dayOptions(week)} />
              </Field>
            </div>
            <div className="flex-1">
              <Field label="New time">
                <SelectField value={time} onChange={setTime} options={timeOptions} />
              </Field>
            </div>
          </div>
          {showStaffPicker && (
            <Field label="With">
              <SelectField
                value={resourceId}
                onChange={setResourceId}
                options={namedResources.map(r => ({
                  value: r.id,
                  label: r.role ? `${r.name} · ${r.role}` : r.name,
                }))}
              />
            </Field>
          )}
      </fieldset>
    </Drawer>
  );
}
