import { IconName } from "../../ui/Icon";

export interface NavItem {
  key: string;
  label: string;
  icon: IconName;
  badge?: 'queue' | 'camp';
  live?: boolean;
}

// Single source of truth for the console's navigable screens. Both the desktop
// Sidebar and the phone BottomNav derive from these so navigation can't drift.
export const NAV_OPERATE: NavItem[] = [
  { key: 'today',         label: 'Today',         icon: 'today',   },
  { key: 'queue',         label: 'Needs action',  icon: 'alert',   badge: 'queue' },
  { key: 'conversations', label: 'Conversations', icon: 'message', live: true },
  { key: 'waitlist',      label: 'Waitlist',      icon: 'clock',   },
];

export const NAV_MANAGE: NavItem[] = [
  { key: 'customers',  label: 'Customers',          icon: 'users',     },
  { key: 'deposits',   label: 'Deposits & payouts', icon: 'card',      },
  { key: 'campaigns',  label: 'Campaigns',          icon: 'megaphone', badge: 'camp' },
  { key: 'analytics',  label: 'Analytics',          icon: 'chart',     },
  { key: 'settings',   label: 'Settings',           icon: 'gear',      },
];

// Keys shown directly in the phone bottom bar (everything else lives under "More").
export const BOTTOM_PRIMARY_KEYS = ['today', 'queue', 'conversations', 'customers'] as const;

// Every screen not surfaced as a bottom-bar primary destination. Used to keep the
// "More" tab's active state correct as the nav config evolves.
export const MORE_SCREENS: string[] = [...NAV_OPERATE, ...NAV_MANAGE]
  .map(it => it.key)
  .filter(key => !BOTTOM_PRIMARY_KEYS.includes(key as typeof BOTTOM_PRIMARY_KEYS[number]));
