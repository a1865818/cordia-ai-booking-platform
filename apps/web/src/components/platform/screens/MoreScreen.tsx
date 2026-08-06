import { Icon, IconName } from "../../ui/Icon";
import { DemoBadge } from "../../ui/DemoBadge";
import { SESSION_IDENTITY_HINT, sessionIdentity } from "../../../features/session/identity";

interface MoreScreenProps {
  go: (s: string) => void;
  autoText: string;
  onSignOut: () => void;
  /**
   * How many guests are on the waitlist, from the live list — or `undefined` while it has
   * never loaded. Previously this row read `WAITLIST.length`, the mock array's size, on the
   * phone's primary navigation surface and next to a waitlist screen showing the real count.
   */
  waitlistCount?: number;
}

interface Row {
  key: string;
  icon: IconName;
  label: string;
  sub: string;
  /** True when `sub` describes a surface with no backend, so it must be badged. */
  demo?: boolean;
}

export function MoreScreen({ go, autoText, onSignOut, waitlistCount }: MoreScreenProps) {
  const session = sessionIdentity();

  const rows: Row[] = [
    {
      key: 'waitlist',
      icon: 'clock',
      label: 'Waitlist',
      // The venue auto-book switch is itself unsaved local state (badged on the waitlist
      // screen), so it is not restated as fact here.
      sub: waitlistCount === undefined
        ? 'Guests waiting for a slot'
        : `${waitlistCount} waiting · auto-book ${autoText}`,
    },
    {
      key: 'deposits',
      icon: 'card',
      label: 'Deposits & payouts',
      // No money figure. `PAYOUT.held` is a hand-written constant and spec 09 has not shipped a
      // payment surface, so the platform has never held a cent — stating an amount here was the
      // most credible fabrication in the product.
      sub: 'No payment surface yet',
      demo: true,
    },
    {
      key: 'campaigns',
      icon: 'megaphone',
      label: 'Campaigns',
      // No "1 awaiting your approval" and no badge: nothing is awaiting approval, because no
      // endpoint and no spec owns campaigns.
      sub: 'No backend yet',
      demo: true,
    },
    {
      key: 'analytics',
      icon: 'chart',
      label: 'Analytics',
      // Not "no-shows, AI handled" — neither is recorded server-side (Inv-16).
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
                {r.demo && <DemoBadge reason="this screen has no endpoint — the figures on it are samples" />}
              </div>
            </div>
            <Icon name="chevR" size={17} strokeWidth={2.2} color="#C5C5C0" />
          </button>
        ))}
      </div>

      {/* Session card — the actor and tenant actually sent on every request, not an invented
          person and venue. There is no sign-in and no profile endpoint. */}
      <div
        className="flex items-center gap-[11px] mt-4 p-[14px_15px] bg-white border border-cd-border rounded-[13px]"
        title={SESSION_IDENTITY_HINT}
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
