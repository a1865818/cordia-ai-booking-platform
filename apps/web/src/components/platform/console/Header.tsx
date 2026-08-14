import React from "react";
import { Icon } from "../../ui/Icon";
import { PrimaryButton } from "../../ui/Button";
import { IconName } from "../../ui/Icon";
import { sessionIdentity, sessionIdentityHint } from "../../../features/session/identity";

const SCREEN_TITLES: Record<string, [string, string]> = {
  today:         ['Today',              'Today'],
  queue:         ['Exception queue',    'Needs action'],
  conversations: ['Agent activity',     'Conversations'],
  waitlist:      ['Auto-booking',       'Waitlist'],
  customers:     ['Directory',          'Customers'],
  deposits:      ['Payouts',            'Deposits & payouts'],
  campaigns:     ['Approval required',  'Campaigns'],
  analytics:     ['Last 7 days',        'Analytics'],
  settings:      ['Workspace',          'Settings'],
  more:          ['Console',            'More'],
};

interface HeaderProps {
  device: 'phone' | 'desktop';
  screen: string;
  primaryLabel?: string;
  onPrimary?: () => void;
  extra?: React.ReactNode;
  scrolled?: boolean;
  sub?: string;
  onOpenSearch?: () => void;
  /** Guest-facing venue name from `GET /api/tenant-profile`. Empty → tenant id. */
  venueName?: string;
}

function GlobalSearch({ onOpen }: { onOpen?: () => void }) {
  return (
    <div className="relative">
      <span className="absolute left-[11px] top-1/2 -translate-y-1/2 pointer-events-none">
        <Icon name="search" size={15} strokeWidth={2} color="#9A9A9A" />
      </span>
      <input
        id="cdSearch"
        type="text"
        aria-label="Search bookings and customers"
        placeholder="Search bookings, customers"
        readOnly
        onFocus={(e) => { e.currentTarget.blur(); onOpen?.(); }}
        onClick={() => onOpen?.()}
        className="h-[36px] border border-cd-border rounded-[9px] text-[13px] text-t3 bg-white outline-none cursor-pointer hover:border-[#DEDEDA] transition-colors duration-150"
        style={{
          width: 200,
          padding: '0 46px 0 32px',
          fontFamily: "'Inter Tight', system-ui, sans-serif",
        }}
      />
      <span
        aria-hidden="true"
        className="absolute right-2 top-1/2 -translate-y-1/2 font-mono text-[10px] text-t3 border border-cd-border rounded-[5px] px-[5px] py-[2px] pointer-events-none"
      >
        ⌘K
      </span>
    </div>
  );
}

/**
 * The venue this console is operating.
 *
 * Prefers the guest-facing display name from the tenant profile. Falls back to the tenant
 * id actually sent as `X-Tenant-Id` when the profile has not loaded or has no name.
 */
function WorkspaceLabel({ venueName }: { venueName?: string }) {
  const session = sessionIdentity();
  const label = venueName?.trim() || session.tenant;
  return (
    <div
      className="flex items-center gap-2 h-[36px] px-3 border border-cd-border rounded-[9px] bg-white text-[13px] font-medium text-ink whitespace-nowrap"
      style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
      title={sessionIdentityHint()}
    >
      <span className="w-5 h-5 rounded-[6px] bg-ink text-white flex items-center justify-center text-[10px] font-bold">
        {session.tenantInitial}
      </span>
      {label}
    </div>
  );
}

export function Header({ device, screen, primaryLabel, onPrimary, extra, scrolled, sub, onOpenSearch, venueName }: HeaderProps) {
  const isPhone = device === 'phone';
  const tt = SCREEN_TITLES[screen] || SCREEN_TITLES.today;
  const subtitle = sub ?? tt[0];

  return (
    <header
      className={`shrink-0 flex gap-3 border-b transition-shadow duration-150 ${scrolled ? 'shadow-[0_2px_8px_-4px_rgba(0,0,0,0.08)]' : ''}`}
      style={{
        alignItems: isPhone ? 'flex-end' : 'center',
        justifyContent: 'space-between',
        padding: isPhone ? '6px 18px 12px' : '18px 22px',
        borderColor: isPhone ? '#F2F2F0' : '#EAEAEA',
      }}
    >
      <div className="min-w-0">
        <div className="font-mono text-[10px] tracking-[0.14em] uppercase text-t3 mb-[3px]">{subtitle}</div>
        <h1
          className="font-bold tracking-[-0.03em] text-ink m-0 leading-[1.05] whitespace-nowrap"
          style={{ fontSize: isPhone ? 24 : 22 }}
        >
          {tt[1]}
        </h1>
      </div>
      <div className="flex items-center gap-[9px] shrink-0">
        {!isPhone && extra}
        {!isPhone && <GlobalSearch onOpen={onOpenSearch} />}
        {!isPhone && <WorkspaceLabel venueName={venueName} />}
        {primaryLabel && (
          <PrimaryButton icon={'plus' as IconName} onClick={onPrimary}>{primaryLabel}</PrimaryButton>
        )}
      </div>
    </header>
  );
}
