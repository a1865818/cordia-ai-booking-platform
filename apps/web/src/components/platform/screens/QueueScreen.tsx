import { QueueItem, chMeta } from "../../../data/console";
import { Chip } from "../../ui/Chip";
import { Icon } from "../../ui/Icon";
import { Chan } from "../../ui/StatusTags";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { SectionLabel } from "../../ui/SectionLabel";
import { Empty } from "../../ui/Empty";

const MOCK_WAIT: Record<string, string> = { q1: '14m', q2: '31m', q3: '1h 2m' };
const waitingTime = (id: string, retries: number) =>
  MOCK_WAIT[id] ?? `${retries * 9 + 8}m`;

interface QueueScreenProps {
  device: 'phone' | 'desktop';
  queue: QueueItem[];
  busy: string | null;
  onFinish: (id: string) => void;
  onDismiss: (id: string) => void;
}

export function QueueScreen({ device, queue, busy, onFinish, onDismiss }: QueueScreenProps) {
  const isPhone = device === 'phone';
  if (queue.length === 0) {
    return (
      <Empty
        tone="good"
        icon="check"
        title="Nothing needs you right now"
        body="The agent is handling bookings automatically. Anything it can't finish on its own will land here."
      />
    );
  }

  return (
    <div>
      <SectionLabel line>{queue.length} need a human</SectionLabel>
      <div className="flex flex-col gap-[11px]">
        {queue.map(q => {
          const b = busy === q.id;
          return (
            <div key={q.id}
              className="bg-white rounded-[13px] p-[14px_15px] transition-shadow duration-150 hover:shadow-[0_2px_12px_-4px_rgba(10,10,10,0.1)]"
              style={{ border: '1px solid #EAEAEA', borderLeft: '3px solid #D23B30' }}>
              <div className="flex items-start justify-between gap-[10px]">
                <div className="min-w-0">
                  <div className="text-[15px] font-semibold tracking-[-0.01em] text-ink">{q.name}</div>
                  <div className="text-[12.5px] text-t2 mt-[1px]">{q.service} · {q.slot}</div>
                </div>
                <Chip status="failed" />
              </div>
              <div className="flex items-center gap-2 mt-[11px] px-[11px] py-[9px] rounded-[9px]" style={{ background: '#FBF7F6' }}>
                <Icon name="alert" size={15} strokeWidth={2} color="#B82C22" />
                <span className="text-[12.5px] flex-1" style={{ color: '#7A2620' }}>{q.reason}</span>
              </div>
              <div className={`mt-3 gap-[10px] ${isPhone ? 'flex flex-col' : 'flex items-center'}`}>
                <div className="flex items-center gap-[8px] flex-wrap min-w-0">
                  <Chan channel={q.channel} size={18} radius={5} />
                  <span className="font-mono text-[10.5px] text-t2">{chMeta(q.channel).short}</span>
                  <span className="font-mono text-[10.5px] tracking-[0.06em] uppercase text-t3">· {q.retries} retries</span>
                  <span className="font-mono text-[10.5px] text-t3">· {waitingTime(q.id, q.retries)} waiting</span>
                </div>
                <div className={`flex gap-2 ${isPhone ? '' : 'ml-auto'}`}>
                  <GhostButton onClick={() => onDismiss(q.id)} style={{ color: '#6B6B6B', fontWeight: 500, ...(isPhone ? { flex: 1 } : null) }}>Dismiss</GhostButton>
                  <PrimaryButton busy={b} onClick={() => onFinish(q.id)} style={isPhone ? { flex: 1 } : { padding: '0 15px' }}>
                    {b ? 'Re-booking' : 'Finish booking'}
                  </PrimaryButton>
                </div>
              </div>
            </div>
          );
        })}
      </div>
      <div className="h-2" />
    </div>
  );
}
