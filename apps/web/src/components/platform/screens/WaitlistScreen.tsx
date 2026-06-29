import { WaitlistItem } from "../../../data/console";
import { Avatar } from "../../ui/Avatar";
import { Icon } from "../../ui/Icon";
import { Toggle } from "../../ui/Toggle";
import { SectionLabel } from "../../ui/SectionLabel";

interface WaitlistScreenProps {
  device: 'phone' | 'desktop';
  autoBook: boolean;
  toggleAutoBook: () => void;
  waitlist: WaitlistItem[];
  onToggleAuto: (id: string) => void;
  onRemove: (id: string) => void;
  onBookNow: (id: string) => void;
}

export function WaitlistScreen({ autoBook, toggleAutoBook, waitlist, onToggleAuto, onRemove, onBookNow }: WaitlistScreenProps) {
  return (
    <div>
      {/* Auto-book banner */}
      <div className="flex items-start gap-[13px] bg-white border border-cd-border rounded-[14px] p-[15px_16px] mb-4">
        <span className="w-[38px] h-[38px] rounded-[11px] bg-ink flex items-center justify-center shrink-0">
          <Icon name="zap" size={19} fill="#DDF247" />
        </span>
        <div className="flex-1 min-w-0">
          <div className="text-[15px] font-semibold text-ink">Auto-book from waitlist</div>
          <div className="text-[12.5px] text-t2 mt-[3px] leading-[1.45]">
            When a matching slot opens, the agent offers it to the next eligible guest and books on reply. Toggle eligibility per person below.
          </div>
        </div>
        <Toggle on={autoBook} onClick={toggleAutoBook} />
      </div>

      <SectionLabel>{waitlist.length} waiting</SectionLabel>

      {waitlist.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-14 px-4 text-center bg-white border border-cd-border rounded-[14px]">
          <span className="w-12 h-12 rounded-full bg-wash2 flex items-center justify-center mb-3">
            <Icon name="clock" size={22} strokeWidth={1.8} color="#9A9A9A" />
          </span>
          <div className="text-[14.5px] font-semibold text-ink">No one waiting</div>
          <div className="text-[12.5px] text-t2 mt-[4px] max-w-[220px] leading-[1.5]">
            Customers will appear here when they join the waitlist.
          </div>
        </div>
      ) : (
        <div className="flex flex-col gap-[9px]">
          {waitlist.map(w => {
            const eligible = autoBook && w.auto;
            return (
              <div
                key={w.id}
                className="flex items-center gap-3 bg-white border border-cd-border rounded-[13px] p-[13px_14px]"
              >
                <Avatar name={w.name} size={34} radius={10} font="12px" />
                <div className="flex-1 min-w-0">
                  <div className="text-[14px] font-semibold text-ink">{w.name}</div>
                  <div className="text-[12px] text-t2 mt-[1px]">{w.service} · {w.want}</div>
                </div>

                {/* Per-item auto-book toggle chip */}
                <button
                  type="button"
                  onClick={() => onToggleAuto(w.id)}
                  className="shrink-0 inline-flex items-center gap-[5px] text-[10.5px] font-semibold px-[9px] py-[4px] rounded-full border cursor-pointer transition-all duration-120 hover:opacity-80"
                  style={{
                    background: eligible ? '#E7F6EC' : '#F2F2F0',
                    color: eligible ? '#0B6431' : '#6B6B6B',
                    borderColor: eligible ? '#C0E8CE' : '#E5E5E5',
                    fontFamily: "'Inter Tight', system-ui, sans-serif",
                  }}
                  title={w.auto ? 'Click to make manual' : 'Click to enable auto-book'}
                >
                  <span
                    className="w-[5px] h-[5px] rounded-full"
                    style={{ background: eligible ? '#15924B' : '#9A9A9A' }}
                  />
                  {w.auto ? 'Auto' : 'Manual'}
                </button>

                {/* Book now */}
                <button
                  type="button"
                  onClick={() => onBookNow(w.id)}
                  className="shrink-0 w-8 h-8 flex items-center justify-center rounded-[8px] border border-cd-border bg-white cursor-pointer hover:bg-wash transition-colors"
                  title="Book now"
                >
                  <Icon name="calClock" size={15} strokeWidth={1.9} color="#0A0A0A" />
                </button>

                {/* Remove */}
                <button
                  type="button"
                  onClick={() => onRemove(w.id)}
                  className="shrink-0 w-8 h-8 flex items-center justify-center rounded-[8px] border border-cd-border bg-white cursor-pointer hover:bg-[#FFF2F2] transition-colors"
                  title="Remove from waitlist"
                >
                  <Icon name="x" size={14} strokeWidth={2.2} color="#9A9A9A" />
                </button>
              </div>
            );
          })}
        </div>
      )}
      <div className="h-2" />
    </div>
  );
}
