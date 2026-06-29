import { BookingStatus, statusMeta } from "../../data/console";

interface ChipProps {
  status: BookingStatus;
  withSpinner?: boolean;
}

function Spinner({ size = 9, color = '#fff', track = 'rgba(255,255,255,0.4)' }: { size?: number; color?: string; track?: string }) {
  return (
    <span style={{
      width: size, height: size, borderRadius: '50%',
      border: `${size > 14 ? '2px' : '1.7px'} solid ${track}`, borderTopColor: color,
      animation: 'cdspin .7s linear infinite', display: 'inline-block', flexShrink: 0,
    }} />
  );
}

export function Chip({ status, withSpinner = true }: ChipProps) {
  const m = statusMeta(status);
  return (
    <span className="inline-flex items-center gap-[5px] shrink-0 text-[11px] font-semibold px-[9px] py-[3px] rounded-full whitespace-nowrap"
      style={{ background: m.fill, color: m.text }}>
      {m.spin && withSpinner
        ? <Spinner size={9} color={m.accent} track={m.accent + '55'} />
        : <span className="w-[6px] h-[6px] rounded-full shrink-0" style={{ background: m.accent }} />}
      {m.label}
    </span>
  );
}
