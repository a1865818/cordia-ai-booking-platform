import type { ReactNode } from "react";
import { useEffect, useState, useRef } from "react";
import { Booking, chMeta, fmtRange } from "../../../data/console";
import { dayLabel as weekDayLabel, type ConsoleWeek } from "../../../features/bookings/week";
import { Drawer } from "../../ui/Drawer";
import { Chip } from "../../ui/Chip";
import { DepTag, ProvTag } from "../../ui/StatusTags";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { Icon } from "../../ui/Icon";
import { useRecordAttendance } from "../../../features/bookings/mutations";

function useRetained<T>(val: T | null, open: boolean): T | null {
  const ref = useRef<T | null>(val);
  if (val) ref.current = val;
  return open ? val : ref.current;
}

interface MetaRowProps {
  label: string;
  children: React.ReactNode;
  top?: boolean;
}
function MetaRow({ label, children, top }: MetaRowProps) {
  return (
    <div className="flex justify-between gap-3 py-[11px] border-b border-hair last:border-b-0"
      style={{ alignItems: top ? 'flex-start' : 'center' }}>
      <span className="text-[12.5px] text-t2 shrink-0">{label}</span>
      <div className="text-[13.5px] text-ink font-medium text-right min-w-0">{children}</div>
    </div>
  );
}

interface BookingDetailDrawerProps {
  open: boolean;
  device: 'phone' | 'desktop';
  /** The displayed week, so the booking's date is the real one rather than a fixed literal. */
  week: ConsoleWeek;
  booking: Booking | null;
  onClose: () => void;
  onStartReschedule: (id: string) => void;
  onCancelBooking: (id: string) => void;
  cancelBusy: boolean;
  onViewTranscript: (tid: string) => void;
  /**
   * Step-up verification, rendered in place of the cancel confirmation when the platform needs
   * a grant before it will accept the cancellation (spec 08, Inv-11). Owned by the page so the
   * grant never has to live in this component.
   */
  verificationGate?: ReactNode;
}

export function BookingDetailDrawer({
  open, device, week, booking, onClose,
  onStartReschedule, onCancelBooking, cancelBusy, onViewTranscript, verificationGate,
}: BookingDetailDrawerProps) {
  const b = useRetained(booking, open);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const attendance = useRecordAttendance();

  useEffect(() => {
    if (open) setConfirmCancel(false);
  }, [open, booking?.id]);

  if (!b) return <Drawer open={open} onClose={onClose} device={device} title="Booking">{null}</Drawer>;

  const dayLabel = weekDayLabel(week, b.day);
  // `isBooking === false` means the platform has accepted the request but not finalised it, so
  // `b.id` is an OPERATION id. Cancel and reschedule both address a booking; sending an operation
  // id would 404, and offering the buttons implies an action that cannot be taken yet.
  const isRealBooking = b.isBooking !== false;
  const cancellable = isRealBooking && !['cancelled', 'failed'].includes(b.status);

  const footer = (
    <div className="flex gap-2">
      {verificationGate ? (
        // The gate owns the interaction until the server issues a grant or the operator backs
        // out; re-offering "Confirm cancel" here would just re-submit without one.
        <GhostButton onClick={() => setConfirmCancel(false)} style={{ flex: 1 }}>
          Keep booking
        </GhostButton>
      ) : confirmCancel ? (
        <>
          <GhostButton onClick={() => setConfirmCancel(false)} style={{ flex: 1 }}>Keep booking</GhostButton>
          <PrimaryButton
            busy={cancelBusy}
            onClick={() => onCancelBooking(b.id)}
            style={{ flex: 1, background: cancelBusy ? '#9a2a22' : '#D23B30', boxShadow: 'none' }}
          >
            {cancelBusy ? 'Cancelling' : 'Confirm cancel'}
          </PrimaryButton>
        </>
      ) : (
        <>
          {cancellable && (
            <GhostButton
              onClick={() => setConfirmCancel(true)}
              icon="trash"
              style={{ color: '#B82C22', borderColor: '#F2D7D4' }}
            >
              Cancel
            </GhostButton>
          )}
          {isRealBooking ? (
            <PrimaryButton full icon="calClock" onClick={() => onStartReschedule(b.id)}>
              Reschedule
            </PrimaryButton>
          ) : (
            <div className="text-[12.5px] text-t2 leading-[1.5]" role="status">
              Still being confirmed — it can be changed once the platform finishes.
            </div>
          )}
        </>
      )}
    </div>
  );

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Booking detail" title={b.name} footer={footer}>
      <div className="flex items-center gap-[9px] mb-[14px]">
        <Chip status={b.status} /><ProvTag prov={b.prov} />
      </div>
      {verificationGate && <div className="mb-[14px]">{verificationGate}</div>}
      <div className="rounded-[13px] p-[4px_14px] mb-[14px]" style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
        <MetaRow label="Service">{b.serviceName}</MetaRow>
        <MetaRow label="When">
          {dayLabel}
          <div className="font-mono text-[11px] text-ink">{fmtRange(b.start, b.dur)}</div>
        </MetaRow>
        {/* Both rows are omitted for a live booking rather than defaulted. The schedule
            projection records no channel and no deposit, so `|| 'phone'` would report a
            web-originated booking to the operator as a phone call, and DepTag would print a
            payment hold that spec 09 defers and that never charged anyone. `ProvTag` already
            renders nothing for unknown provenance; these follow it. */}
        {b.dep > 0 && (
          <MetaRow label="Deposit"><DepTag dep={b.dep} /></MetaRow>
        )}
        {b.channel && (
          <MetaRow label="Booked via" top>
            <span className="inline-flex items-center gap-[5px]">
              <span className="w-[7px] h-[7px] rounded-full" style={{ background: chMeta(b.channel).color }} />
              {chMeta(b.channel).label}
            </span>
          </MetaRow>
        )}
        {/* No "Idempotency key" row. `idemKey()` derived a plausible-looking key from a
            character sum of the booking's DISPLAY id — it had no relationship to the key the
            request was actually submitted under (`AttemptIdentity`, `features/bookings/identity.ts`),
            and the schedule projection does not return one. An invented technical fact is
            worse than a missing one: it is the field an engineer would quote in an incident. */}
      </div>

      {isRealBooking && (
        <div className="flex gap-2 mb-[14px]">
          <GhostButton
            style={{ flex: 1 }}
            disabled={attendance.isPending}
            onClick={() => attendance.mutate({ bookingId: b.id, attendance: "attended" })}
          >
            Mark attended
          </GhostButton>
          <GhostButton
            style={{ flex: 1 }}
            disabled={attendance.isPending}
            onClick={() => attendance.mutate({ bookingId: b.id, attendance: "no_show" })}
          >
            Mark no-show
          </GhostButton>
        </div>
      )}
      {b.prov === 'ai' && (
        <button
          onClick={() => b.transcript ? onViewTranscript(b.transcript) : undefined}
          className="flex items-center gap-[11px] w-full bg-ink border-none rounded-[13px] p-[13px_14px] text-left"
          style={{
            cursor: b.transcript ? 'pointer' : 'default',
            opacity: b.transcript ? 1 : 0.55,
            fontFamily: "'Inter Tight', system-ui, sans-serif",
          }}
        >
          <span className="w-[34px] h-[34px] rounded-[10px] flex items-center justify-center shrink-0"
            style={{ background: 'rgba(255,255,255,0.12)' }}>
            <Icon name="star" size={15} fill="#DDF247" />
          </span>
          <div className="flex-1 min-w-0">
            <div className="text-[13.5px] font-semibold text-white">Booked by the agent</div>
            <div className="text-[11.5px] mt-[1px]" style={{ color: 'rgba(255,255,255,0.65)' }}>
              {b.transcript ? 'View the source transcript' : 'No transcript on file'}
            </div>
          </div>
          {b.transcript && <Icon name="chevR" size={17} strokeWidth={2.2} color="rgba(255,255,255,0.6)" />}
        </button>
      )}
    </Drawer>
  );
}
