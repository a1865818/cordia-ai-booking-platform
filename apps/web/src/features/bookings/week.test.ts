import { describe, expect, it } from "../../test/vitest-compat";
import { buildWeek, dayLabel, dayOptions, shiftWeekWindow, shortDayLabel, todayIndexAt } from "./week";
import { toIsoInstant } from "./buildSubmission";
import { weekWindow } from "./adapters";

/** Local Monday 00:00 as the schedule query would send it. */
function windowFrom(year: number, month: number, day: number): string {
  return new Date(year, month - 1, day, 0, 0, 0, 0).toISOString();
}

describe("buildWeek", () => {
  it("dates the week from the window the rows were fetched for", () => {
    const week = buildWeek(windowFrom(2026, 8, 3), new Date(2026, 7, 5, 10, 0));

    expect(week.doms).toEqual([3, 4, 5, 6, 7, 8, 9]);
    expect(week.dows[0]).toBe("MON");
    expect(week.rangeLabel).toBe("3–9 August");
  });

  it("names both months when the week spans a boundary", () => {
    const week = buildWeek(windowFrom(2026, 7, 27), new Date(2026, 6, 28, 9, 0));

    expect(week.doms).toEqual([27, 28, 29, 30, 31, 1, 2]);
    expect(week.rangeLabel).toBe("27 July – 2 August");
  });

  it("names both years when the week spans a new year", () => {
    const week = buildWeek(windowFrom(2025, 12, 29), new Date(2025, 11, 31, 9, 0));

    expect(week.rangeLabel).toBe("29 December 2025 – 4 January 2026");
  });

  it("reports today's index only when the displayed week contains today", () => {
    const current = buildWeek(windowFrom(2026, 8, 3), new Date(2026, 7, 6, 15, 0));
    expect(current.isCurrentWeek).toBe(true);
    expect(current.todayIndex).toBe(3); // Thursday

    // The old code hard-coded `today = 3`, so every week highlighted a Thursday. A week that
    // does not contain today must highlight nothing at all.
    const other = buildWeek(windowFrom(2026, 8, 10), new Date(2026, 7, 6, 15, 0));
    expect(other.isCurrentWeek).toBe(false);
    expect(other.todayIndex).toBeNull();
  });

  it("keeps seven local midnights across a DST transition", () => {
    // 2026-04-05 is the southern-hemisphere DST end; the week must still have 7 columns
    // each at local midnight rather than sliding by an hour.
    const week = buildWeek(windowFrom(2026, 3, 30), new Date(2026, 2, 30, 12, 0));

    expect(week.dates).toHaveLength(7);
    expect(week.doms).toEqual([30, 31, 1, 2, 3, 4, 5]);
    for (const date of week.dates) {
      expect(date.getHours()).toBe(0);
      expect(date.getMinutes()).toBe(0);
    }
  });

  it("falls back to the current week rather than rendering Invalid Date", () => {
    const week = buildWeek("not-an-instant", new Date(2026, 7, 6, 15, 0));

    expect(week.doms).toEqual([3, 4, 5, 6, 7, 8, 9]);
    expect(week.isCurrentWeek).toBe(true);
  });
});

describe("labels", () => {
  const week = buildWeek(windowFrom(2026, 8, 3), new Date(2026, 7, 6, 9, 0));

  it("labels a day with its real date", () => {
    expect(dayLabel(week, 3)).toBe("Thu, 6 August");
    expect(shortDayLabel(week, 3)).toBe("Thu 6");
  });

  it("returns an empty label rather than throwing for an out-of-range index", () => {
    expect(dayLabel(week, 9)).toBe("");
  });

  it("builds seven dated picker options", () => {
    const options = dayOptions(week);
    expect(options).toHaveLength(7);
    expect(options[0]).toEqual({ value: "0", label: "Mon 3" });
  });
});

describe("shiftWeekWindow", () => {
  it("moves a whole week and keeps the seven-day span", () => {
    const base = { from: windowFrom(2026, 8, 3), to: windowFrom(2026, 8, 10) };

    const next = shiftWeekWindow(base, 1);
    expect(buildWeek(next.from).doms[0]).toBe(10);

    const prev = shiftWeekWindow(base, -1);
    expect(buildWeek(prev.from).doms[0]).toBe(27);

    const spanDays = (new Date(next.to).getTime() - new Date(next.from).getTime()) / 86_400_000;
    expect(Math.round(spanDays)).toBe(7);
  });

  it("round-trips back to the same week", () => {
    const base = weekWindow(new Date(2026, 7, 6));
    const roundTripped = shiftWeekWindow(shiftWeekWindow(base, 3), -3);
    expect(roundTripped.from).toBe(base.from);
  });
});

describe("submission uses the displayed week", () => {
  it("places a booking on the displayed week, not the current one", () => {
    // The create drawer used `startOfWeek(new Date())`, so a booking made while viewing next
    // week was submitted for *this* week — silently the wrong date.
    const displayed = buildWeek(windowFrom(2026, 8, 10), new Date(2026, 7, 6, 9, 0));

    const iso = toIsoInstant(displayed.start, 2, 10 * 60); // Wednesday 10:00
    const instant = new Date(iso);

    expect(instant.getDate()).toBe(12);
    expect(instant.getMonth()).toBe(7);
    expect(instant.getHours()).toBe(10);
  });
});

describe("todayIndexAt", () => {
  /**
   * The console is a screen people leave open all day — including past midnight.
   *
   * `ConsoleWeek.todayIndex` is frozen when the week is built from the schedule window, and the
   * week is rebuilt only when a parent state change moves that window. So after midnight the
   * now-line stayed drawn on yesterday's column and the "remaining" tile went on counting against
   * a day that had ended, while the real current day was treated as an ordinary column.
   */
  it("advances across local midnight without the week being rebuilt", () => {
    const week = buildWeek(windowFrom(2026, 8, 3), new Date(2026, 7, 6, 23, 30));

    expect(week.todayIndex).toBe(3); // Thursday, frozen at build time

    // 23:30 Thursday -> still Thursday.
    expect(todayIndexAt(week, new Date(2026, 7, 6, 23, 30))).toBe(3);
    // 00:05 Friday -> the column moves, though `week` never changed.
    expect(todayIndexAt(week, new Date(2026, 7, 7, 0, 5))).toBe(4);
    expect(week.todayIndex).toBe(3);
  });

  it("returns null once midnight rolls the day out of the displayed week", () => {
    const week = buildWeek(windowFrom(2026, 8, 3), new Date(2026, 7, 9, 23, 30));

    expect(todayIndexAt(week, new Date(2026, 7, 9, 23, 30))).toBe(6); // Sunday
    // Monday belongs to the NEXT week — nothing in this one is today.
    expect(todayIndexAt(week, new Date(2026, 7, 10, 0, 5))).toBeNull();
  });

  it("compares calendar days, so a DST transition cannot shift the answer", () => {
    // 2026-04-05 is a DST boundary; one local day is 23 or 25 hours long, which is why this
    // counts calendar days rather than dividing a millisecond offset.
    const week = buildWeek(windowFrom(2026, 3, 30), new Date(2026, 2, 30, 12, 0));

    expect(todayIndexAt(week, new Date(2026, 3, 5, 12, 0))).toBe(6);
    expect(todayIndexAt(week, new Date(2026, 3, 4, 12, 0))).toBe(5);
  });

  it("is null for an unusable instant rather than guessing a column", () => {
    const week = buildWeek(windowFrom(2026, 8, 3), new Date(2026, 7, 6, 15, 0));
    expect(todayIndexAt(week, new Date("nonsense"))).toBeNull();
  });
});
