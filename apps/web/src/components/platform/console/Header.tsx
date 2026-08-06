import React from "react";
import { Icon } from "../../ui/Icon";
import { PrimaryButton } from "../../ui/Button";
import { IconName } from "../../ui/Icon";
import { SESSION_IDENTITY_HINT, sessionIdentity } from "../../../features/session/identity";

const SCREEN_TITLES: Record<string, [string, string]> = {
  today:         ['Today',              'Today'],
  queue:         ['Exception queue',    'Needs action'],
  conversations: ['Agent activity',     'Conversations'],
  waitlist:      ['Auto-booking',       'Waitlist'],
  customers:     ['Directory',          'Customers'],
  deposits:      ['Stripe Connect',     'Deposits & payouts'],
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
 * The tenant this console is reading and writing.
 *
 * Shows the value actually sent as `X-Tenant-Id`, not a name — the previous literal
 * ("Bella Nails") named a business that is not the tenant.
 *
 * A venue display name IS now retrievable (`GET /api/tenant-profile/display-name`, set from
 * Settings), so the identifier shown here is a deliberate choice rather than a missing endpoint:
 * this label's job is to say which tenant the console is reading and writing, and a friendly name
 * would hide exactly the value that answers that question.
 *
 * Not a button, and no chevron: there is no workspace switcher, and an affordance that opens
 * nothing is a claim about a capability the console does not have.
 */
function WorkspaceLabel() {
  const session = sessionIdentity();
  return (
    <div
      className="flex items-center gap-2 h-[36px] px-3 border border-cd-border rounded-[9px] bg-white text-[13px] font-medium text-ink whitespace-nowrap"
      style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
      title={SESSION_IDENTITY_HINT}
    >
      <span className="w-5 h-5 rounded-[6px] bg-ink text-white flex items-center justify-center text-[10px] font-bold">
        {session.tenantInitial}
      </span>
      {session.tenant}
    </div>
  );
}

export function Header({ device, screen, primaryLabel, onPrimary, extra, scrolled, sub, onOpenSearch }: HeaderProps) {
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
        {!isPhone && <WorkspaceLabel />}
        {primaryLabel && (
          <PrimaryButton icon={'plus' as IconName} onClick={onPrimary}>{primaryLabel}</PrimaryButton>
        )}
      </div>
    </header>
  );
}
