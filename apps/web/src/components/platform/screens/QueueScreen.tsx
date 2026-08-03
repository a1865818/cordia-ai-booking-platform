import { QueueItem, chMeta } from "../../../data/console";
import { operationStatusLabel } from "../../../features/operations/adapters";
import { Chip } from "../../ui/Chip";
import { Icon } from "../../ui/Icon";
import { Chan } from "../../ui/StatusTags";
import { PrimaryButton, GhostButton } from "../../ui/Button";
import { SectionLabel } from "../../ui/SectionLabel";
import { Empty } from "../../ui/Empty";

/**
 * The platform's own status, in operator language.
 *
 * Neutral by design: the board carries manual_review and alternatives_required alongside
 * genuine failures, and colouring them all red told the operator four different situations
 * were the same one.
 */
function StatusPill({ label }: { label: string }) {
  return (
    <span
      className="inline-flex items-center gap-[5px] shrink-0 text-[11px] font-semibold px-[9px] py-[3px] rounded-full whitespace-nowrap"
      style={{ background: '#F2F2F0', color: '#55554F' }}
    >
      <span className="w-[6px] h-[6px] rounded-full shrink-0" style={{ background: '#9A9A9A' }} />
      {label}
    </span>
  );
}

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
                  <div className="text-[12.5px] text-t2 mt-[1px]">{q.service}</div>
                </div>
                {/* The board lists everything needing a human — manual_review,
                    alternatives_required, compensation_required. A hard-coded "Failed" chip
                    told the operator all four were failures. Show the platform's own status. */}
                {q.status
                  ? <StatusPill label={operationStatusLabel(q.status)} />
                  : <Chip status="failed" />}
              </div>
              <div className="flex items-center gap-2 mt-[11px] px-[11px] py-[9px] rounded-[9px]" style={{ background: '#FBF7F6' }}>
                <Icon name="alert" size={15} strokeWidth={2} color="#B82C22" />
                <span className="text-[12.5px] flex-1" style={{ color: '#7A2620' }}>{q.reason}</span>
              </div>
              <div className={`mt-3 gap-[10px] ${isPhone ? 'flex flex-col' : 'flex items-center'}`}>
                {/* Only what the operation projection actually carries. A channel badge, a
                    retry count and a "waiting" duration used to be rendered here; the API
                    models none of the three on an operation. */}
                <div className="flex items-center gap-[8px] flex-wrap min-w-0">
                  {q.channel && (
                    <>
                      <Chan channel={q.channel} size={18} radius={5} />
                      <span className="font-mono text-[10.5px] text-t2">{chMeta(q.channel).short}</span>
                    </>
                  )}
                  {q.retries !== undefined && (
                    <span className="font-mono text-[10.5px] tracking-[0.06em] uppercase text-t3">{q.retries} retries</span>
                  )}
                  {q.slot && <span className="font-mono text-[10.5px] text-t3">updated {q.slot}</span>}
                </div>
                <div className={`flex gap-2 ${isPhone ? '' : 'ml-auto'}`}>
                  <GhostButton onClick={() => onDismiss(q.id)} style={{ color: '#6B6B6B', fontWeight: 500, ...(isPhone ? { flex: 1 } : null) }}>Dismiss</GhostButton>
                  {/* "Finish booking" promised a server action this button never performed:
                      the only resolve endpoint is manual_review-only, so this is an
                      acknowledgement in the operator's own view. The label says so. */}
                  <span
                    title="Removes this from your queue. The platform keeps working on the operation."
                    style={isPhone ? { flex: 1, display: 'flex' } : undefined}
                  >
                    <PrimaryButton
                      busy={b}
                      onClick={() => onFinish(q.id)}
                      style={isPhone ? { flex: 1 } : { padding: '0 15px' }}
                    >
                      Clear from queue
                    </PrimaryButton>
                  </span>
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
