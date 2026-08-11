import { describe, expect, it } from "vitest";
import {
  operationStatusLabel,
  relativeAge,
  relativeDeadline,
  toQueueItem,
  toQueueItems,
  toWorkItemIndex,
  workItemReasonLabel,
} from "./adapters";
import type { OperationSummary } from "./api";

function summary(overrides: Partial<OperationSummary> = {}): OperationSummary {
  return {
    operation_id: "op_2f9c1b7e4a6d5c3b8e0f1a2b",
    booking_id: null,
    intent_id: "int_1",
    intent_revision: 1,
    operation_type: "create",
    status: "manual_review",
    status_reason: null,
    is_preorder: false,
    correlation_id: "corr_1",
    created_at: "2026-08-03T09:00:00Z",
    updated_at: "2026-08-03T09:30:00Z",
    ...overrides,
  } as OperationSummary;
}

describe("toQueueItem", () => {
  it("asserts no retry count — the projection does not carry one", () => {
    // `retries: 0` rendered as "0 retries" on every live row, which reads as a fact.
    expect(toQueueItem(summary()).retries).toBeUndefined();
  });

  it("asserts no channel — an operation has none", () => {
    // Previously hard-coded to `web`, and rendered as a channel badge next to real data.
    expect(toQueueItem(summary()).channel).toBeUndefined();
  });

  it("carries the platform's own status so the row is not styled as a failure", () => {
    expect(toQueueItem(summary({ status: "manual_review" })).status).toBe("manual_review");
    expect(operationStatusLabel("manual_review")).toBe("Needs your review");
    expect(operationStatusLabel("compensation_required")).toBe("Needs attention — refund pending");
  });

  it("shortens a bare operation id rather than presenting a UUID as a guest name", () => {
    const item = toQueueItem(summary());
    expect(item.name).toBe("op_2f9c1…");
    expect(item.name.length).toBeLessThan(15);
  });

  it("prefers a booking id when the operation has one", () => {
    expect(toQueueItem(summary({ booking_id: "bk_42" })).name).toBe("bk_42");
  });

  it("puts the operation type in operator language", () => {
    expect(toQueueItem(summary({ operation_type: "cancel" })).service).toBe("Cancellation");
    expect(toQueueItem(summary({ operation_type: "reschedule" })).service).toBe("Reschedule");
  });

  it("reports the real last-updated age instead of an invented waiting time", () => {
    const item = toQueueItem(summary({ updated_at: "2026-08-03T09:30:00Z" }));
    // The old screen computed `retries * 9 + 8` minutes, so every live row said "8m waiting".
    expect(item.slot).not.toContain("waiting");
    expect(item.slot).toBeTruthy();
  });

  it("maps an empty list to an empty list", () => {
    expect(toQueueItems(undefined)).toEqual([]);
    expect(toQueueItems([])).toEqual([]);
  });
});

describe("relativeAge", () => {
  const now = new Date("2026-08-03T12:00:00Z").getTime();

  it("phrases recent, hourly and daily ages", () => {
    expect(relativeAge("2026-08-03T11:59:40Z", now)).toBe("just now");
    expect(relativeAge("2026-08-03T11:42:00Z", now)).toBe("18m ago");
    expect(relativeAge("2026-08-03T09:30:00Z", now)).toBe("2h 30m ago");
    expect(relativeAge("2026-08-01T09:30:00Z", now)).toBe("2d ago");
  });

  it("never reports a negative age for a clock skewed into the future", () => {
    expect(relativeAge("2026-08-03T12:05:00Z", now)).toBe("just now");
  });

  it("returns an empty string for an unparseable instant", () => {
    expect(relativeAge("not-a-date", now)).toBe("");
  });
});

describe("work items on the queue", () => {
  const now = Date.parse("2026-08-07T12:00:00Z");

  function workItem(overrides: Record<string, unknown> = {}) {
    return {
      work_item_id: "wi_1",
      operation_id: "op_2f9c1b7e4a6d5c3b8e0f1a2b",
      reason: "compensation_ambiguous",
      sla_deadline: "2026-08-07T13:00:00Z",
      assignee: null,
      created_at: "2026-08-07T11:00:00Z",
      operation_status: "manual_review",
      ...overrides,
    } as never;
  }

  it("joins the open work item onto its operation's row", () => {
    const index = toWorkItemIndex([workItem()], now);
    const [row] = toQueueItems([summary()], index);

    expect(row.workItem?.reason).toBe("The venue's cancellation wasn't confirmed");
    expect(row.workItem?.overdue).toBe(false);
  });

  it("leaves a row without a work item untouched rather than inventing one", () => {
    const [row] = toQueueItems([summary()], toWorkItemIndex([], now));

    expect(row.workItem).toBeUndefined();
  });

  it("flags an item past its deadline", () => {
    const index = toWorkItemIndex([workItem({ sla_deadline: "2026-08-07T11:00:00Z" })], now);

    expect(index["op_2f9c1b7e4a6d5c3b8e0f1a2b"].overdue).toBe(true);
  });

  it("does not call an item overdue when the deadline could not be read", () => {
    // Guessing "late" from a value nobody can check would put a red flag on a row for a
    // reason the operator cannot verify.
    const index = toWorkItemIndex([workItem({ sla_deadline: "not-a-date" })], now);

    expect(index["op_2f9c1b7e4a6d5c3b8e0f1a2b"].overdue).toBe(false);
  });

  it("translates machine reason tags into operator language", () => {
    expect(workItemReasonLabel("sla_escalation")).toBe("Taking longer than expected");
    // Suffixed reasons name the thing that raised them; the suffix is not shown.
    expect(workItemReasonLabel("poison:invalid_state_transition"))
      .toBe("A message the platform couldn't process");
    expect(workItemReasonLabel("preorder_manual_review:saga_123"))
      .toBe("Pre-order needs you and the guest to agree");
  });

  it("degrades an unknown reason to readable words rather than showing a raw tag", () => {
    expect(workItemReasonLabel("some_new_reason")).toBe("some new reason");
    expect(workItemReasonLabel(null)).toBe("Needs your review");
  });

  it("skips a work item with no operation to attach to", () => {
    expect(toWorkItemIndex([workItem({ operation_id: null })], now)).toEqual({});
  });

  it("says how long is left, or how late it already is", () => {
    expect(relativeDeadline("2026-08-07T12:20:00Z", now)).toBe("in 20m");
    expect(relativeDeadline("2026-08-07T09:00:00Z", now)).toBe("3h ago");
    expect(relativeDeadline("2026-08-07T12:00:00Z", now)).toBe("now");
    expect(relativeDeadline("not-a-date", now)).toBe("");
  });
});
