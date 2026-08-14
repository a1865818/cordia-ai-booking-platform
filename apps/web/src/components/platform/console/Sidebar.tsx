import { Icon } from "../../ui/Icon";
import { Logo } from "../../ui/Logo";
import { NAV_OPERATE, NAV_MANAGE, NavItem } from "./nav";
import { sessionIdentity, sessionIdentityHint } from "../../../features/session/identity";

interface SidebarProps {
  screen: string;
  go: (s: string) => void;
  queueCount: number;
  campCount: number;
  hasLive: boolean;
  isCollapsed?: boolean;
  onToggle?: () => void;
  onSignOut?: () => void;
}

interface SideItemProps {
  item: NavItem;
  active: boolean;
  onClick: () => void;
  queueCount: number;
  campCount: number;
  hasLive: boolean;
  isCollapsed?: boolean;
}

function SideItem({ item, active, onClick, queueCount, campCount, hasLive, isCollapsed }: SideItemProps) {
  const badgeVal = item.badge === 'queue' ? queueCount : item.badge === 'camp' ? campCount : 0;
  const showBadge = item.badge && badgeVal > 0;
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? 'page' : undefined}
      title={isCollapsed ? item.label : undefined}
      className={`relative flex items-center ${isCollapsed ? 'justify-center px-0' : 'gap-[10px] px-[11px]'} w-full py-[9px] mb-[2px] rounded-[9px] border-none cursor-pointer text-[13.5px] transition-all duration-150 hover:bg-[#F5F5F4]`}
      style={{
        fontFamily: "'Inter Tight', system-ui, sans-serif",
        fontWeight: active ? 600 : 500,
        color: active ? '#0A0A0A' : '#55554F',
        background: active ? '#EBEBEA' : 'transparent',
        textAlign: 'left',
      }}
    >
      {active && (
        <span
          className="absolute top-1/2 -translate-y-1/2 w-[3px] h-[18px] rounded-full bg-lime"
          style={{ left: isCollapsed ? '-3px' : '-11px' }}
        />
      )}
      <Icon name={item.icon} size={17} strokeWidth={1.9} />
      {!isCollapsed && <span className="flex-1">{item.label}</span>}
      {!isCollapsed && showBadge && (
        <span className="min-w-[16px] h-4 px-1 rounded-full font-mono text-[10px] font-bold flex items-center justify-center text-white"
          style={{ background: item.badge === 'queue' ? '#D23B30' : '#C98A09' }}>
          {badgeVal}
        </span>
      )}
      {!isCollapsed && item.live && hasLive && !showBadge && (
        <span className="w-[7px] h-[7px] rounded-full bg-[#2F6BEB]" />
      )}
    </button>
  );
}

export function Sidebar({ screen, go, queueCount, campCount, hasLive, isCollapsed = false, onToggle, onSignOut }: SidebarProps) {
  const session = sessionIdentity();
  return (
    <aside
      className={`${isCollapsed ? 'w-14' : 'w-[236px]'} shrink-0 border-r border-cd-border bg-surface2 flex flex-col transition-all duration-200`}
      style={{ padding: isCollapsed ? '16px 8px' : '16px 13px' }}
    >
      <div className={`flex items-center gap-[9px] px-[6px] pb-4`}>
        <Logo />
        {!isCollapsed && <span className="font-bold text-[16px] tracking-[-0.04em] flex-1">Cordia</span>}
        {!isCollapsed && (
          <button
            type="button"
            onClick={onToggle}
            title="Collapse sidebar"
            className="flex items-center justify-center w-[26px] h-[26px] border-none bg-transparent cursor-pointer hover:bg-[#EBEBEA] rounded-[7px] transition-colors duration-150 shrink-0"
          >
            <Icon name="chevL" size={14} strokeWidth={2.2} color="#9A9A9A" />
          </button>
        )}
      </div>

      {!isCollapsed && (
        <div className="font-mono text-[9.5px] tracking-[0.14em] uppercase text-t3 px-2 py-[6px]">Operate</div>
      )}
      {NAV_OPERATE.map(it => (
        <SideItem key={it.key} item={it} active={screen === it.key} onClick={() => go(it.key)}
          queueCount={queueCount} campCount={campCount} hasLive={hasLive} isCollapsed={isCollapsed} />
      ))}

      {!isCollapsed
        ? <div className="font-mono text-[9.5px] tracking-[0.14em] uppercase text-t3 px-2 pt-4 pb-[6px]">Manage</div>
        : <div className="my-2 border-t border-hair" />
      }
      {NAV_MANAGE.map(it => (
        <SideItem key={it.key} item={it} active={screen === it.key} onClick={() => go(it.key)}
          queueCount={queueCount} campCount={campCount} hasLive={hasLive} isCollapsed={isCollapsed} />
      ))}

      {/* Expand button — only shown when collapsed, at the bottom */}
      {isCollapsed && (
        <button
          type="button"
          onClick={onToggle}
          title="Expand sidebar"
          className="mt-3 flex items-center justify-center w-full py-1.5 border-none bg-transparent cursor-pointer hover:bg-[#F5F5F4] rounded-[8px] transition-colors duration-150"
        >
          <Icon name="chevR" size={15} strokeWidth={2.2} color="#9A9A9A" />
        </button>
      )}

      {/* User section */}
      <div className={`mt-auto flex items-center ${isCollapsed ? 'justify-center' : 'gap-[10px]'} px-2 pt-[11px] pb-[2px] border-t border-hair group`}>
        <span
          className="w-[30px] h-[30px] shrink-0 rounded-[9px] bg-ink text-white flex items-center justify-center text-[12px] font-semibold"
          title={sessionIdentityHint()}
        >
          {session.actorInitials}
        </span>
        {!isCollapsed && (
          <>
            {/* The actor and tenant the console actually sends, not an invented person and venue.
                No endpoint returns a display name for the ACTOR, so the raw identity is shown.
                (The tenant does have one — `GET /api/tenant-profile/display-name` — but this row
                names who is signed in and where, which is what the raw values answer.) */}
            <div className="min-w-0 flex-1" title={sessionIdentityHint()}>
              <div className="text-[13px] font-semibold truncate">{session.actor}</div>
              <div className="text-[11px] text-t3 truncate">{session.tenant} · {session.role}</div>
            </div>
            <button
              type="button"
              onClick={onSignOut}
              className="opacity-0 group-hover:opacity-100 transition-opacity duration-150 text-[12px] text-t3 bg-transparent border-none cursor-pointer shrink-0 hover:text-[#D23B30] transition-colors p-0"
              style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
            >
              Sign out
            </button>
          </>
        )}
      </div>
    </aside>
  );
}
