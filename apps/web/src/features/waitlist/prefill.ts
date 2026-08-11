/**
 * Turns a waitlist row into create-drawer defaults.
 *
 * Kept out of the page so it is testable on its own: the day/time mapping is the part that
 * can be silently wrong (a guest's acceptable window in a different week would otherwise
 * prefill the same weekday of the week on screen).
 */

import type { WaitlistItem } from "../../data/console";
import { toDayAndStart } from "../bookings/adapters";
import type { ConsoleWeek } from "../bookings/week";

export interface CreatePrefill {
  /** Guest label. The platform records the booking against the operator, not this. */
  name?: string;
  serviceId?: string;
  partySize?: number;
  /** Index into the displayed week, 0 = Monday. */
  day?: number;
  /** Minutes since midnight. */
  start?: number;
}

export function prefillFromWaitlist(entry: WaitlistItem, week: ConsoleWeek): CreatePrefill {
  const prefill: CreatePrefill = {
    name: entry.name,
    serviceId: entry.serviceId,
    partySize: entry.partySize,
  };

  if (!entry.acceptableFrom) return prefill;

  const from = new Date(entry.acceptableFrom);
  if (Number.isNaN(from.getTime())) return prefill;

  const { day, start } = toDayAndStart(from, week.start);
  // Outside the displayed week: carry the time but not a day index that would point at the
  // wrong date. The caller moves the grid to the right week before building this, so this
  // guard only fires when that was not possible.
  if (day >= 0 && day <= 6) prefill.day = day;
  prefill.start = start;

  return prefill;
}
