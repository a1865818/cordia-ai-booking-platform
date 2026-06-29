import { BookingStatus, statusMeta } from "../../data/console";
import { Spinner } from "./Spinner";

interface ChipProps {
  status: BookingStatus;
  withSpinner?: boolean;
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
