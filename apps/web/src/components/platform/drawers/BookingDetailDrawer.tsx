import { useEffect, useState, useRef } from "react";
import { Booking, WEEK, staffById, chMeta, fmtRange, idemKey } from "../../../data/console";
import { Drawer } from "../../ui/Drawer";
import { Chip } from "../../ui/Chip";
import { DepTag, ProvTag } from "../../ui/StatusTags";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { Icon } from "../../ui/Icon";

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
  booking: Booking | null;
  onClose: () => void;
  onStartReschedule: (id: string) => void;
  onCancelBooking: (id: string) => void;
  cancelBusy: boolean;
  onViewTranscript: (tid: string) => void;
}

export function BookingDetailDrawer({
  open, device, booking, onClose,
  onStartReschedule, onCancelBooking, cancelBusy, onViewTranscript,
}: BookingDetailDrawerProps) {
  const b = useRetained(booking, open);
  const [confirmCancel, setConfirmCancel] = useState(false);

  useEffect(() => {
    if (open) setConfirmCancel(false);
  }, [open, booking?.id]);

  if (!b) return <Drawer open={open} onClose={onClose} device={device} title="Booking">{null}</Drawer>;

  const st = staffById(b.staff);
  const dayLabel = WEEK.dows[b.day].charAt(0) + WEEK.dows[b.day].slice(1).toLowerCase() + ' ' + WEEK.doms[b.day] + ' ' + WEEK.month;
  const cancellable = !['cancelled', 'failed'].includes(b.status);

  const footer = (
    <div className="flex gap-2">
      {confirmCancel ? (
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
          <PrimaryButton full icon="calClock" onClick={() => onStartReschedule(b.id)}>
            Reschedule
          </PrimaryButton>
        </>
      )}
    </div>
  );

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Booking detail" title={b.name} footer={footer}>
      <div className="flex items-center gap-[9px] mb-[14px]">
        <Chip status={b.status} /><ProvTag prov={b.prov} />
      </div>
      <div className="rounded-[13px] p-[4px_14px] mb-[14px]" style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
        <MetaRow label="Service">{b.serviceName}</MetaRow>
        <MetaRow label="With">
          {st.name}
          <div className="font-mono text-[10px] text-t3 tracking-[0.06em]">{st.role}</div>
        </MetaRow>
        <MetaRow label="When">
          {dayLabel}
          <div className="font-mono text-[11px] text-ink">{fmtRange(b.start, b.dur)}</div>
        </MetaRow>
        <MetaRow label="Deposit"><DepTag dep={b.dep} /></MetaRow>
        <MetaRow label="Booked via" top>
          <span className="inline-flex items-center gap-[5px]">
            <span className="w-[7px] h-[7px] rounded-full" style={{ background: chMeta(b.channel || 'phone').color }} />
            {chMeta(b.channel || 'phone').label}
          </span>
        </MetaRow>
        <div className="flex items-center justify-between gap-3 py-[11px]">
          <span className="text-[12.5px] text-t2">Idempotency key</span>
          <span className="font-mono text-[11px] text-t3">{idemKey(b.id)}</span>
        </div>
      </div>

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
