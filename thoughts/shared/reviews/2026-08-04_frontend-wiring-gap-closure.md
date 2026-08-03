---
date: 2026-08-04
reviewer: /code-review xhigh (sonnet subagents)
range: 124a631..77a168b (9 phases) on feat/ui-wire
fixes: 0cc7431
status: all findings closed
---

# Review — frontend wiring gap closure

15 findings, **all real, all fixed** in `0cc7431`. No finding was refuted. The suite was green
before the review (292 web tests, `tsc -b --noEmit` clean), so every one of these was behaviour
the tests did not cover — which is the useful thing to know about them.

## The two that mattered most

Both **re-introduced the class of defect this branch exists to remove**, and both had the same
root cause: *a value outliving its context by one render, with something irreversible happening
in that window*. Clearing in a `useEffect` looks correct and is too late.

### 1. Week navigation showed real bookings under the wrong dates

`week` recomputes the instant `scheduleWindow` changes, but the new window's query starts at
`data === undefined`, so `if (scheduleQuery.data) setBookings(liveBookings)` did not fire. The
header read `MON 10 … SUN 16` while every row was still last week's; opening one showed a date
the booking is not on. If the new week's fetch failed, the wrong-week rows stayed indefinitely.

This is *exactly* the `9–15 June` defect Phase 1 was written to remove, reintroduced by Phase 1's
own navigation feature.

### 2. Switching conversations fired an unauthorised audited read

`useTranscript` subscribes during render; the reset effect keyed on `convOpen` runs after. So one
render escaped with the **new** session id and the **previous** conversation's reason code and
step-up grant — an audited `read_sensitive_transcript` row the operator never authorised for that
subject, using a grant issued for someone else. Directly contrary to Inv-11 and spec 14 §2, and
contrary to the comment sitting immediately above it.

**Fix for both:** store the owner alongside the value and compare at render
(`features/bookings/scoped.ts`), so no frame exists in which the two disagree. Extracted as pure
helpers precisely so the rule is pinned by tests rather than asserted in a comment
(`scoped.test.ts`, 7 cases).

## The rest

| # | Finding | Fix |
|---|---|---|
| 3 | *Book now* silently substituted the first catalogue service when the enrolment's `service_id` was absent, then **submitted it as a durable booking** | Reports the unmatched service; chooses nothing |
| 4 | `dismissQueue` stored a bare id while the filter matched `id\|status`, so a dismissed row reappeared instantly and Undo had nothing to undo *(pre-existing)* | Both paths use `dismissalKey` |
| 5 | The read-only consent chip was coloured by the venue toggle the same commit badged "not saved" — flipping it visually withdrew every guest's recorded grant | Live rows colour from `w.auto` alone |
| 6 | Shim-gate tests booted hosts without their sibling fixture's DB bootstrap (fails on a clean machine), and nothing asserted `UseSetting` still out-ranks `appsettings.Test.json` | Depends on the fixture; adds a precedence assertion so the Inv-13 guard cannot silently degrade |
| 7 | `TestIds` reached only 2 of 9 files exposed to the PAN false positive — ~85 raw hex ids left at ~0.6% each | Applied across all 9 |
| 8 | Expiry rounded to the nearest minute while actionability compared instants → live **Accept** beside the word "expired" | Shared threshold, floors throughout |
| 9 | Actionability and countdown frozen at fetch time → expired offer kept its button until a refetch | Recomputed against a ticking clock |
| 10 | A comment claimed reschedule mounts a verification gate; none is ever mounted | Comment corrected to describe what actually happens, and why it is acceptable while reschedule is disabled |
| 11 | The create drawer still searched mock `CUSTOMERS` unlabelled — the same thing the palette was fixed for in the same range | Live customers, demo rows labelled |
| 12 | Shared mutation left other rows' Accept enabled but swallowed the click silently | Any decision in flight disables all offer buttons |
| 13 | Date labels followed the host locale while their tests asserted English | `LABEL_LOCALE` pinned |
| 14 | `shiftWeekWindow` lacked the malformed-instant guard its sibling `buildWeek` has — `RangeError` inside a state updater | Same guard |
| 15 | `TestIds` XML summary documented an id format the code does not produce, and contradicted its own remarks | Corrected |

## What this says about the work

Findings 3, 5, 9 and 11 are all the **same failure the whole branch was about**: the console
telling an operator something the platform does not know. Fixing a class of defect in nine phases
does not stop new instances entering through the fixes themselves — findings 1 and 2 are the
sharpest possible demonstration. Worth remembering the next time a pass like this is declared
complete on the strength of a green suite.

**Verification after fixes:** web **302** · api **1176** · integration **419 passed, 2 skipped,
0 failed** · all lints 0 warnings · all builds clean.
