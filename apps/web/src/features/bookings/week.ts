/**
 * The displayed week, derived from the window the schedule query actually used.
 *
 * Before this module the grid's dates came from `WEEK` in `data/console.ts` — a frozen
 * literal reading "9–15 June, today = Thursday" — while `weekWindow()` fetched the *real*
 * current week. Live bookings were therefore rendered under fabricated dates: a booking on
 * the real Monday appeared in a column labelled "MON 9 June". Dates are now computed from
 * one source, the same window the rows were fetched for, so a label can never disagree with
 * the data underneath it.
 *
 * Everything here is **local** time: the grid is a venue's day, not a UTC day. The week
 * boundary comes from `startOfWeek` so a DST transition inside the week cannot shift a
 * column (see `toDayAndStart`, which counts calendar days for the same reason).
 */

import { startOfWeek } from "./adapters";

/**
 * The locale every date label is formatted in.
 *
 * Explicit, not the host default (`undefined`). Passing `undefined` made every label follow the
 * machine's ICU locale while `buildRangeLabel` still assembled its parts in English word order —
 * so a non-English machine produced a mixed-language label, and the tests, which necessarily
 * assert concrete strings, failed on any CI runner whose `LANG` was not English. Pinning it makes
 * the UI and its tests agree everywhere; a real localisation pass would thread a user locale
 * through here and replace `buildRangeLabel` with `Intl.DateTimeFormat.formatRange`.
 */
export const LABEL_LOCALE = "en-GB";

export interface ConsoleWeek {
  /** Local Monday 00:00 of the displayed week. */
  start: Date;
  /** The seven local dates, Monday first — index-aligned with `Booking.day`. */
  dates: Date[];
  /** Uppercase short weekday names, e.g. `MON`. */
  dows: string[];
  /** Day-of-month numbers. */
  doms: number[];
  /**
   * Index of today within this week, or **null** when the displayed week is not the current
   * one. Null rather than a fallback index: highlighting a day as "today" in a week that does
   * not contain today is exactly the kind of confident-but-false claim this module exists to
   * remove.
   */
  todayIndex: number | null;
  /** Whether the displayed week contains today. */
  isCurrentWeek: boolean;
  /** e.g. `3–9 August`, `28 July – 3 August`, `29 December 2025 – 4 January 2026`. */
  rangeLabel: string;
}

function localMidnight(date: Date): Date {
  const copy = new Date(date);
  copy.setHours(0, 0, 0, 0);
  return copy;
}

function shortDow(date: Date): string {
  return date.toLocaleDateString(LABEL_LOCALE, { weekday: "short" }).toUpperCase();
}

function monthName(date: Date): string {
  return date.toLocaleDateString(LABEL_LOCALE, { month: "long" });
}

function buildRangeLabel(first: Date, last: Date): string {
  if (first.getFullYear() !== last.getFullYear()) {
    return `${first.getDate()} ${monthName(first)} ${first.getFullYear()} – ${last.getDate()} ${monthName(last)} ${last.getFullYear()}`;
  }
  if (first.getMonth() !== last.getMonth()) {
    return `${first.getDate()} ${monthName(first)} – ${last.getDate()} ${monthName(last)}`;
  }
  return `${first.getDate()}–${last.getDate()} ${monthName(first)}`;
}

/**
 * Builds the displayed week from the schedule window's `from` instant.
 *
 * `now` is injectable so tests can pin "today" without touching the system clock.
 */
export function buildWeek(windowFrom: string, now: Date = new Date()): ConsoleWeek {
  const parsed = new Date(windowFrom);
  // A malformed window would otherwise produce `Invalid Date` labels across the whole grid;
  // falling back to the current week keeps the console usable and still truthful.
  const start = startOfWeek(Number.isNaN(parsed.getTime()) ? now : parsed);

  const dates: Date[] = [];
  for (let i = 0; i < 7; i += 1) {
    const day = new Date(start);
    // setDate (not +i*86_400_000): a DST transition makes one local day 23 or 25 hours long,
    // and millisecond arithmetic would slide every later column by an hour.
    day.setDate(day.getDate() + i);
    dates.push(day);
  }

  const currentWeekStart = startOfWeek(now);
  const isCurrentWeek = localMidnight(currentWeekStart).getTime() === localMidnight(start).getTime();

  return {
    start,
    dates,
    dows: dates.map(shortDow),
    doms: dates.map((d) => d.getDate()),
    todayIndex: isCurrentWeek ? (now.getDay() + 6) % 7 : null,
    isCurrentWeek,
    rangeLabel: buildRangeLabel(dates[0], dates[6]),
  };
}

/** `Thu, 6 August` — the header/drawer form. */
export function dayLabel(week: ConsoleWeek, index: number): string {
  const date = week.dates[index];
  if (!date) return "";
  const dow = date.toLocaleDateString(LABEL_LOCALE, { weekday: "short" });
  return `${dow}, ${date.getDate()} ${monthName(date)}`;
}

/** `Thu 6` — the compact form for pickers and chips. */
export function shortDayLabel(week: ConsoleWeek, index: number): string {
  const date = week.dates[index];
  if (!date) return "";
  const dow = date.toLocaleDateString(LABEL_LOCALE, { weekday: "short" });
  return `${dow} ${date.getDate()}`;
}

/** Day options for the create/reschedule pickers, dated from the displayed week. */
export function dayOptions(week: ConsoleWeek): { value: string; label: string }[] {
  return week.dates.map((_, i) => ({ value: String(i), label: shortDayLabel(week, i) }));
}

/** The seven-day ISO window `deltaWeeks` away from the one given. */
export function shiftWeekWindow(
  window: { from: string; to: string },
  deltaWeeks: number,
  now: Date = new Date()
): { from: string; to: string } {
  // The same guard `buildWeek` carries, for the same input. Without it an unparseable window
  // propagates an Invalid Date into `toISOString()`, which throws RangeError inside the state
  // updater and unmounts the console — a harder failure than the labels the guard was added for.
  const parsed = new Date(window.from);
  const start = startOfWeek(Number.isNaN(parsed.getTime()) ? now : parsed);
  start.setDate(start.getDate() + deltaWeeks * 7);
  const to = new Date(start);
  to.setDate(to.getDate() + 7);
  return { from: start.toISOString(), to: to.toISOString() };
}
