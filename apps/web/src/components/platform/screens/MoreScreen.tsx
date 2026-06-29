import { WAITLIST, PAYOUT, money } from "../../../data/console";
import { Icon, IconName } from "../../ui/Icon";

interface MoreScreenProps {
  go: (s: string) => void;
  autoText: string;
  onSignOut: () => void;
}

export function MoreScreen({ go, autoText, onSignOut }: MoreScreenProps) {
  const rows = [
    { key: 'waitlist',  icon: 'clock'    as IconName, label: 'Waitlist',            sub: `${WAITLIST.length} waiting · auto-book ${autoText}`,              badge: 0 },
    { key: 'deposits',  icon: 'card'     as IconName, label: 'Deposits & payouts',  sub: `${money(PAYOUT.held)} held · next payout ${PAYOUT.nextDate.split(' ')[0]}`, badge: 0 },
    { key: 'campaigns', icon: 'megaphone'as IconName, label: 'Campaigns',           sub: '1 awaiting your approval',                                         badge: 1 },
    { key: 'analytics', icon: 'chart'    as IconName, label: 'Analytics',           sub: 'Bookings, no-shows, AI handled',                                   badge: 0 },
    { key: 'settings',  icon: 'gear'     as IconName, label: 'Settings',            sub: 'Business, services, hours, agent',                                 badge: 0 },
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
            <div className="flex-1">
              <div className="text-[14.5px] font-semibold text-ink">{r.label}</div>
              <div className="text-[12px] text-t3 mt-[1px]">{r.sub}</div>
            </div>
            {r.badge > 0 ? (
              <span className="min-w-[18px] h-[18px] px-[5px] rounded-full font-mono text-[10px] font-bold flex items-center justify-center text-white"
                style={{ background: '#C98A09' }}>
                {r.badge}
              </span>
            ) : (
              <Icon name="chevR" size={17} strokeWidth={2.2} color="#C5C5C0" />
            )}
          </button>
        ))}
      </div>

      {/* User card */}
      <div className="flex items-center gap-[11px] mt-4 p-[14px_15px] bg-white border border-cd-border rounded-[13px]">
        <span className="w-9 h-9 rounded-[11px] bg-ink text-white flex items-center justify-center text-[13px] font-semibold">MC</span>
        <div className="flex-1">
          <div className="text-[14px] font-semibold text-ink">Maya Chen</div>
          <div className="text-[12px] text-t3">Bella Nails · Owner</div>
        </div>
        <button
          onClick={onSignOut}
          className="text-[12.5px] text-t2 bg-none border-none cursor-pointer font-medium hover:text-[#D23B30] transition-colors"
          style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          Sign out
        </button>
      </div>
      <div className="h-2" />
    </div>
  );
}
