import { Icon, IconName } from "../../ui/Icon";
import { MORE_SCREENS } from "./nav";

interface BottomNavProps {
  screen: string;
  go: (s: string) => void;
  queueCount: number;
  hasLive: boolean;
}

const BOTTOM = [
  { key: 'today',         label: 'Today',  icon: 'today'   as IconName },
  { key: 'queue',         label: 'Action', icon: 'alert'   as IconName, badge: 'queue' },
  { key: 'conversations', label: 'Chats',  icon: 'message' as IconName, live: true },
  { key: 'customers',     label: 'Clients',icon: 'users'   as IconName },
  { key: 'more',          label: 'More',   icon: 'dots'    as IconName },
];

export function BottomNav({ screen, go, queueCount, hasLive }: BottomNavProps) {
  return (
    <nav className="shrink-0 h-[62px] border-t border-cd-border flex items-stretch px-[6px] pb-2 pt-1"
      style={{ background: 'rgba(255,255,255,0.92)', backdropFilter: 'saturate(180%) blur(12px)' }}>
      {BOTTOM.map(it => {
        const active = screen === it.key || (it.key === 'more' && MORE_SCREENS.includes(screen));
        return (
          <button
            key={it.key}
            type="button"
            onClick={() => go(it.key)}
            aria-current={active ? 'page' : undefined}
            aria-label={it.label}
            className="relative flex-1 flex flex-col items-center justify-center gap-[3px] border-none bg-transparent cursor-pointer p-1 text-[10px]"
            style={{
              fontFamily: "'Inter Tight', system-ui, sans-serif",
              fontWeight: active ? 600 : 500,
              color: active ? '#0A0A0A' : '#9A9A9A',
            }}
          >
            {active && (
              <span className="absolute top-0 left-1/2 -translate-x-1/2 w-[22px] h-[3px] rounded-full bg-lime" />
            )}
            <span className="relative inline-flex">
              <Icon name={it.icon} size={21} strokeWidth={1.9} />
              {it.badge === 'queue' && queueCount > 0 && (
                <span className="absolute top-[-5px] right-[-9px] min-w-[16px] h-4 px-1 rounded-full font-mono text-[10px] font-bold flex items-center justify-center text-white border-[2px] border-white"
                  style={{ background: '#D23B30' }}>
                  {queueCount}
                </span>
              )}
              {it.live && hasLive && (
                <span className="absolute top-[-2px] right-[-3px] w-2 h-2 rounded-full bg-[#2F6BEB] border-[2px] border-white" />
              )}
            </span>
            {it.label}
          </button>
        );
      })}
    </nav>
  );
}
