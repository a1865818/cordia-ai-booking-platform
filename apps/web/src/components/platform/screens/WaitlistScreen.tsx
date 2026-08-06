import { WaitlistItem } from "../../../data/console";
import { isOfferActionable } from "../../../features/waitlist/adapters";
import { Avatar } from "../../ui/Avatar";
import { Countdown } from "../../ui/Countdown";
import { Icon } from "../../ui/Icon";
import { Toggle } from "../../ui/Toggle";
import { DemoBadge } from "../../ui/DemoBadge";
import { useTickingNow } from "../../../hooks/useTickingNow";
import { SectionLabel } from "../../ui/SectionLabel";

interface WaitlistScreenProps {
  device: 'phone' | 'desktop';
  autoBook: boolean;
  toggleAutoBook: () => void;
  waitlist: WaitlistItem[];
  onToggleAuto: (id: string) => void;
  /** True once the enrolments list has answered at least once; `false` means "not yet known". */
  loaded?: boolean;
  onRemove: (id: string) => void;
  onBookNow: (id: string) => void;
  /** Answers a live offer. Absent on the demo-only rendering, which has no offers. */
  onAcceptOffer?: (offerId: string, name: string) => void;
  onDeclineOffer?: (offerId: string, name: string) => void;
  /** The offer currently being answered, so its buttons cannot be double-fired. */
  offerBusyId?: string | null;
}

/**
 * A clock that ticks while the board is open.
 *
 * An offer's expiry passes whether or not the list refetches, so actionability and the countdown
 * have to be re-evaluated over time. Without this a row kept a live Accept button and a frozen
 * "expires in 3 min" indefinitely, and clicking it 409'd.
 */
export function WaitlistScreen({
  autoBook, toggleAutoBook, waitlist, onToggleAuto, onRemove, onBookNow, loaded = true,
  onAcceptOffer, onDeclineOffer, offerBusyId,
}: WaitlistScreenProps) {
  const now = useTickingNow();
  // Any decision in flight disables every offer button, not just its own row's: the mutation is
  // shared, so a click on another row was silently swallowed with no spinner and no message.
  const anyOfferBusy = Boolean(offerBusyId);

  return (
    <div>
      {/* Auto-book banner */}
      <div className="flex items-start gap-[13px] bg-white border border-cd-border rounded-[14px] p-[15px_16px] mb-4">
        <span className="w-[38px] h-[38px] rounded-[11px] bg-ink flex items-center justify-center shrink-0">
          <Icon name="zap" size={19} fill="#DDF247" />
        </span>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-[8px]">
            <div className="text-[15px] font-semibold text-ink">Auto-book from waitlist</div>
            <DemoBadge reason="no venue auto-book setting endpoint — this switch is not saved" />
          </div>
          <div className="text-[12.5px] text-t2 mt-[3px] leading-[1.45]">
            When a matching slot opens, the agent offers it to the next eligible guest and books on
            reply. Whether a guest is eligible is <strong>their</strong> decision, recorded when they
            joined — it is shown per person below and cannot be changed from here.
          </div>
        </div>
        <Toggle on={autoBook} onClick={toggleAutoBook} />
      </div>

      {/* A count is a claim. Until the list has answered there is no count to state. */}
      <SectionLabel>{loaded ? `${waitlist.length} waiting` : 'Waitlist'}</SectionLabel>

      {waitlist.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-14 px-4 text-center bg-white border border-cd-border rounded-[14px]">
          <span className="w-12 h-12 rounded-full bg-wash2 flex items-center justify-center mb-3">
            <Icon name="clock" size={22} strokeWidth={1.8} color="#9A9A9A" />
          </span>
          {/* "No one waiting" states a fact about the venue. Before the list has loaded — or
              after it failed, where the page's error banner sits right above this panel — the
              app does not know that. */}
          <div className="text-[14.5px] font-semibold text-ink">
            {loaded ? 'No one waiting' : 'The waitlist hasn’t loaded'}
          </div>
          <div className="text-[12.5px] text-t2 mt-[4px] max-w-[220px] leading-[1.5]">
            Customers will appear here when they join the waitlist.
          </div>
        </div>
      ) : (
        <div className="flex flex-col gap-[9px]">
          {waitlist.map(w => {
            // A LIVE row's chip reflects only the guest's recorded grant. Folding in `autoBook`
            // — a venue switch this screen badges as unsaved — meant flipping an unpersisted
            // toggle visually withdrew every guest's consent while the label still read "Auto".
            // Demo rows keep the combined behaviour, which is what the mock screen demonstrates.
            const eligible = w.isLive ? w.auto : (autoBook && w.auto);
            // Recomputed every tick, so an offer that expires while the board is open loses its
            // actions and its countdown at the same moment.
            const offerActionable =
              Boolean(w.offerId) && isOfferActionable(w.offerStatus, w.offerExpiresAt, now);
            return (
              <div
                key={w.id}
                className="flex items-center gap-3 bg-white border border-cd-border rounded-[13px] p-[13px_14px]"
              >
                <Avatar name={w.name} size={34} radius={10} font="12px" />
                <div className="flex-1 min-w-0">
                  <div className="text-[14px] font-semibold text-ink">{w.name}</div>
                  <div className="text-[12px] text-t2 mt-[1px]">{w.service} · {w.want}</div>
                  {/* Offer state, straight from the projection. An offer that is terminal or
                      expired shows its state but carries no `offerId`, so no action appears. */}
                  {w.offerStatus && (
                    <div className="text-[11.5px] mt-[3px]" style={{ color: offerActionable ? '#7E5606' : '#9A9A9A' }}>
                      {/* `now` comes from the screen's single clock, so the countdown and the
                          accept/decline gate above can never disagree about the same instant. */}
                      Offer {w.offerStatus}
                      <Countdown expiresAt={w.offerExpiresAt} now={now} prefix=" · " />
                    </div>
                  )}
                </div>

                {/* Accept / decline a live offer. Rendered only when there is one to answer. */}
                {offerActionable && onAcceptOffer && onDeclineOffer && (
                  <div className="flex gap-[6px] shrink-0">
                    <button
                      type="button"
                      disabled={anyOfferBusy}
                      onClick={() => onAcceptOffer(w.offerId!, w.name)}
                      className="h-[30px] px-[10px] rounded-[8px] text-[11.5px] font-semibold border cursor-pointer transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
                      style={{ background: '#0A0A0A', color: '#fff', borderColor: '#0A0A0A', fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                    >
                      Accept
                    </button>
                    <button
                      type="button"
                      disabled={anyOfferBusy}
                      onClick={() => onDeclineOffer(w.offerId!, w.name)}
                      className="h-[30px] px-[10px] rounded-[8px] text-[11.5px] font-semibold border bg-white cursor-pointer transition-colors hover:bg-wash disabled:opacity-40 disabled:cursor-not-allowed"
                      style={{ color: '#55554F', borderColor: '#E5E5E5', fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                    >
                      Decline
                    </button>
                  </div>
                )}

                {/*
                  Per-guest auto-book. On a live row this is the guest's own consent, recorded
                  when they joined (spec 11 §20, Inv-1), and no endpoint changes it — so it is
                  rendered as a read-only state, not a toggle. Clicking it used to flip the chip
                  and write nothing, which showed a consent grant being given or withdrawn that
                  the platform never saw.
                */}
                {w.isLive ? (
                  <span
                    className="shrink-0 inline-flex items-center gap-[5px] text-[10.5px] font-semibold px-[9px] py-[4px] rounded-full border"
                    style={{
                      background: eligible ? '#E7F6EC' : '#F2F2F0',
                      color: eligible ? '#0B6431' : '#6B6B6B',
                      borderColor: eligible ? '#C0E8CE' : '#E5E5E5',
                      fontFamily: "'Inter Tight', system-ui, sans-serif",
                    }}
                    title={
                      w.auto
                        ? 'This guest agreed to auto-book when they joined the waitlist.'
                        : 'This guest did not agree to auto-book, so an offer is sent instead.'
                    }
                  >
                    <span
                      className="w-[5px] h-[5px] rounded-full"
                      style={{ background: eligible ? '#15924B' : '#9A9A9A' }}
                    />
                    {w.auto ? 'Auto' : 'Manual'}
                  </span>
                ) : (
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
                )}

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
