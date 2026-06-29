export function StatusBar() {
  return (
    <div className="h-[30px] shrink-0 flex items-center justify-between px-6 font-mono text-[12px] font-semibold text-ink">
      <span>9:41</span>
      <span className="inline-flex items-center gap-[5px]">
        <svg width="16" height="11" viewBox="0 0 18 12" fill="#0A0A0A">
          <rect x="0" y="7" width="3" height="5" rx="1" />
          <rect x="5" y="4" width="3" height="8" rx="1" />
          <rect x="10" y="1.5" width="3" height="10.5" rx="1" />
          <rect x="15" y="0" width="3" height="12" rx="1" opacity="0.3" />
        </svg>
        <svg width="22" height="12" viewBox="0 0 26 13" fill="none">
          <rect x="1" y="1" width="21" height="11" rx="3" stroke="#0A0A0A" strokeWidth="1.2" opacity="0.5" />
          <rect x="3" y="3" width="15" height="7" rx="1.5" fill="#0A0A0A" />
          <rect x="23.5" y="4.5" width="1.6" height="4" rx="0.8" fill="#0A0A0A" opacity="0.5" />
        </svg>
      </span>
    </div>
  );
}
