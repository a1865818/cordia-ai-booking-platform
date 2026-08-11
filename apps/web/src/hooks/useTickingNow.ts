import { useEffect, useState } from "react";

/**
 * The current time, re-rendering on an interval.
 *
 * Anything that renders a *relative* claim — "expires in 4 min", a now-line on today's schedule,
 * "3 bookings remaining" — reads the clock at render time, and React does not re-render on its
 * own. Without this the value freezes at whatever moment last happened to trigger a render (a
 * query settling, a click), so an offer stays "expiring soon" after it has expired and the
 * schedule's now-line stops moving.
 *
 * The default 30 s cadence is chosen against the finest thing shown: minutes. Anything faster
 * re-renders for no visible change.
 */
export function useTickingNow(intervalMs = 30_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);
  return now;
}
