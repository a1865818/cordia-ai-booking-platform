import { Icon, IconName } from "../../ui/Icon";
import { sessionIdentity, sessionIdentityHint } from "../../../features/session/identity";

interface MoreScreenProps {
  go: (s: string) => void;
  autoText: string;
  onSignOut: () => void;
  /**
   * How many guests are on the waitlist, from the live list — or `undefined` while it has
   * never loaded.
   */
  waitlistCount?: number;
  /** Payout held line, or omitted until the summary has loaded. */
  depositsSub?: string;
  /** Awaiting-approval line, or omitted until campaigns have loaded. */
  campaignsSub?: string;
}

interface Row {
  key: string;
  icon: IconName;
  label: string;
  sub: string;
}

export function MoreScreen({
  go, autoText, onSignOut, waitlistCount, depositsSub, campaignsSub,
}: MoreScreenProps) {
  const session = sessionIdentity();

  const rows: Row[] = [
    {
      key: 'waitlist',
      icon: 'clock',
      label: 'Waitlist',
      sub: waitlistCount === undefined
        ? 'Guests waiting for a slot'
        : `${waitlistCount} waiting · auto-book ${autoText}`,
    },
    {
      key: 'deposits',
      icon: 'card',
      label: 'Deposits & payouts',
      sub: depositsSub ?? 'Deposits & payouts',
    },
    {
      key: 'campaigns',
      icon: 'megaphone',
      label: 'Campaigns',
      sub: campaignsSub ?? 'Campaigns',
    },
    {
      key: 'analytics',
      icon: 'chart',
      label: 'Analytics',
      sub: 'Funnel, bookings per day, channels',
    },
    {
      key: 'settings',
      icon: 'gear',
      label: 'Settings',
      sub: 'Business, services, hours, agent',
    },
  ];

  return (
    <div>
      <div className="flex flex-col gap-[9px]">
        {rows.map(r => (
          <button
            key={r.key}
            onClick={() => go(r.key)}
            className="flex items-center gap-[13px] w-full bg-white border border-cd-border rounded-[13px] p-[14px_15px] cursor-pointer text-left transition-all duration-150 hover:shadow-[0_8px_24px_-10px_rgba(10,10,10,0.18)]"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          >
            <span className="w-9 h-9 rounded-[10px] bg-wash2 flex items-center justify-center shrink-0 text-ink">
              <Icon name={r.icon} size={18} strokeWidth={1.9} />
            </span>
            <div className="flex-1 min-w-0">
              <div className="text-[14.5px] font-semibold text-ink">{r.label}</div>
              <div className="flex items-center gap-[7px] mt-[1px] flex-wrap">
                <span className="text-[12px] text-t3">{r.sub}</span>
              </div>
            </div>
            <Icon name="chevR" size={17} strokeWidth={2.2} color="#C5C5C0" />
          </button>
        ))}
      </div>

      <div
        className="flex items-center gap-[11px] mt-4 p-[14px_15px] bg-white border border-cd-border rounded-[13px]"
        title={sessionIdentityHint()}
      >
        <span className="w-9 h-9 rounded-[11px] bg-ink text-white flex items-center justify-center text-[13px] font-semibold shrink-0">
          {session.actorInitials}
        </span>
        <div className="flex-1 min-w-0">
          <div className="text-[14px] font-semibold text-ink truncate">{session.actor}</div>
          <div className="text-[12px] text-t3 truncate">{session.tenant} · {session.role}</div>
        </div>
        <button
          onClick={onSignOut}
          className="text-[12.5px] text-t2 bg-none border-none cursor-pointer font-medium hover:text-[#D23B30] transition-colors shrink-0"
          style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          Sign out
        </button>
      </div>
      <div className="h-2" />
    </div>
  );
}
