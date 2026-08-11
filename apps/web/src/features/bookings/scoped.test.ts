/**
 * Regression tests for two defects that both came from a value outliving its context by one
 * render. Written against the pure helpers because the bug is in the *rule*, not in React:
 * clearing in a `useEffect` looks correct and is too late.
 */

import { describe, expect, it } from "vitest";
import { updateForKey, valueForKey, type ScopedTo } from "./scoped";

interface Booking {
  id: string;
}
const EMPTY: Booking[] = [];

describe("the schedule grid's rows are scoped to their week", () => {
  const weekA = "2026-08-03T00:00:00.000Z";
  const weekB = "2026-08-10T00:00:00.000Z";
  const rowsA: Booking[] = [{ id: "bk_1" }, { id: "bk_2" }];

  it("shows nothing for a week whose rows have not arrived", () => {
    // The defect: `bookings` was an untagged array, and the new window's query starts at
    // `data === undefined`, so last week's bookings stayed on screen under this week's dates —
    // the exact "label disagrees with the data underneath it" failure week.ts exists to remove.
    const state: ScopedTo<string, Booking[]> = { key: weekA, value: rowsA };

    expect(valueForKey(state, weekA, EMPTY)).toBe(rowsA);
    expect(valueForKey(state, weekB, EMPTY)).toEqual([]);
  });

  it("returns a stable empty identity on a miss, so it cannot loop a render", () => {
    const state: ScopedTo<string, Booking[]> = { key: weekA, value: rowsA };

    expect(valueForKey(state, weekB, EMPTY)).toBe(valueForKey(state, weekB, EMPTY));
  });

  it("re-bases a late optimistic write instead of resurrecting the old week", () => {
    // An optimistic create resolving after the operator moved on must not merge into — and so
    // bring back — the previous week's rows.
    const state: ScopedTo<string, Booking[]> = { key: weekA, value: rowsA };

    const next = updateForKey(state, weekB, EMPTY, rows => [...rows, { id: "bk_new" }]);

    expect(next.key).toBe(weekB);
    expect(next.value).toEqual([{ id: "bk_new" }]);
  });

  it("applies an update normally while the week is unchanged", () => {
    const state: ScopedTo<string, Booking[]> = { key: weekA, value: rowsA };

    const next = updateForKey(state, weekA, EMPTY, rows => [...rows, { id: "bk_3" }]);

    expect(next.value.map(r => r.id)).toEqual(["bk_1", "bk_2", "bk_3"]);
  });

  it("accepts a plain value as well as an updater", () => {
    const state: ScopedTo<string, Booking[]> = { key: weekA, value: rowsA };

    expect(updateForKey(state, weekB, EMPTY, [{ id: "bk_x" }]).value).toEqual([{ id: "bk_x" }]);
  });
});

describe("a transcript's purpose and grant are scoped to their session", () => {
  interface Auth {
    purpose: string | null;
    grant: string | null;
  }
  const NONE: Auth = { purpose: null, grant: null };

  it("does not carry a reason code or a grant to the next conversation", () => {
    // The defect: these were cleared in a `useEffect` keyed on the open session, but
    // `useTranscript` subscribes during render — so one render escaped with the NEW session id
    // and the PREVIOUS conversation's reason code and grant, firing an audited sensitive read
    // the operator never authorised, with a grant issued for a different subject
    // (spec 14 §2, Inv-11). Comparing at render makes that frame impossible.
    const state: ScopedTo<string, Auth> = {
      key: "sess_A",
      value: { purpose: "dispute_review", grant: "grant_A" },
    };

    expect(valueForKey(state, "sess_A", NONE).purpose).toBe("dispute_review");

    const forB = valueForKey(state, "sess_B", NONE);
    expect(forB.purpose).toBeNull();
    expect(forB.grant).toBeNull();
  });

  it("attaches a grant only to the session it was obtained for", () => {
    const state: ScopedTo<string, Auth> = {
      key: "sess_A",
      value: { purpose: "dispute_review", grant: null },
    };

    // Same session: the grant lands beside the purpose already stated.
    const sameSession = updateForKey(state, "sess_A", NONE, a => ({ ...a, grant: "grant_A" }));
    expect(sameSession.value).toEqual({ purpose: "dispute_review", grant: "grant_A" });

    // Different session: it does NOT inherit the earlier purpose. A grant arriving for another
    // conversation must not silently authorise a read under a purpose stated for this one.
    const otherSession = updateForKey(state, "sess_B", NONE, a => ({ ...a, grant: "grant_B" }));
    expect(otherSession.value).toEqual({ purpose: null, grant: "grant_B" });
  });
});
