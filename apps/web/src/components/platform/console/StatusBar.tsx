import { useEffect, useState } from "react";

/** Local wall-clock time as `h:mm`, matching the phone chrome this bar imitates. */
export function clockLabel(now: Date): string {
  const hours = now.getHours() % 12 || 12;
  return `${hours}:${String(now.getMinutes()).padStart(2, '0')}`;
}

/**
 * The phone-viewport status bar.
 *
 * The time is the **device's actual clock**, ticking. It was the literal `9:41` — Apple's
 * marketing-mockup convention — which is a confidently wrong statement about the present moment,
 * rendered on every phone screen. It is cheaper to be right than to argue about whether anyone
 * reads it.
 *
 * The signal and battery glyphs stay decorative: they imitate OS chrome rather than claiming
 * anything about the venue's data, and no web API reports either. They are `aria-hidden` so a
 * screen reader is not told about them at all.
 */
export function StatusBar() {
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    // Aligned to the next minute boundary rather than polled every second, so the displayed
    // minute changes when it actually changes.
    const msToNextMinute = (from: Date) =>
      60_000 - (from.getSeconds() * 1000 + from.getMilliseconds());

    let timer: ReturnType<typeof setTimeout>;
    const tick = () => {
      const current = new Date();
      setNow(current);
      timer = setTimeout(tick, msToNextMinute(current));
    };
    timer = setTimeout(tick, msToNextMinute(new Date()));
    return () => clearTimeout(timer);
  }, []);

  return (
    <div className="h-[30px] shrink-0 flex items-center justify-between px-6 font-mono text-[12px] font-semibold text-ink">
      <span>{clockLabel(now)}</span>
      <span className="inline-flex items-center gap-[5px]" aria-hidden="true">
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
