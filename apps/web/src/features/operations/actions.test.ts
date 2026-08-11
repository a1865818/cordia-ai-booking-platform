/**
 * Operator follow-ups — eligibility and transport.
 *
 * `operationActions` is what keeps the queue from offering a button whose call is certain
 * to fail for the row's current status (docs §10), so it is pinned against the full
 * ratified 16-value set (D1). The transport tests pin the header discipline: resume and
 * resolve carry the mandatory operator spine, cancel and alternatives carry none of it.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { operationActions, TERMINAL_OPERATION_STATUSES } from "./adapters";
import {
  requestOperationCancellation, resolveOperation, respondToAlternatives, resumeOperation,
  statusFromResponse,
} from "./api";

const ALL_STATUSES = [
  "accepted", "validating", "awaiting_user_action", "reserving_capacity", "held",
  "awaiting_payment", "finalizing", "booked", "alternatives_required", "reconciling",
  "compensation_required", "manual_review", "rejected", "expired", "waitlisted",
  "failed_final",
];

describe("operationActions", () => {
  it("permits resume/resolve from manual_review only — the server's own guard", () => {
    for (const status of ALL_STATUSES) {
      const actions = operationActions(status);
      expect(actions.canResume, status).toBe(status === "manual_review");
      expect(actions.canResolve, status).toBe(status === "manual_review");
    }
  });

  it("permits the alternatives answer from alternatives_required only", () => {
    for (const status of ALL_STATUSES) {
      expect(operationActions(status).canRespondAlternatives, status)
        .toBe(status === "alternatives_required");
    }
  });

  it("never offers the waitlist answer for a pre-order — the server refuses that route", () => {
    // spec 11 §21.4: a pre-order cannot go to the waitlist; only re-confirm or decline apply.
    expect(operationActions("alternatives_required", false).canWaitlistAlternatives).toBe(true);
    expect(operationActions("alternatives_required", true).canWaitlistAlternatives).toBe(false);
    // Decline stays available either way.
    expect(operationActions("alternatives_required", true).canRespondAlternatives).toBe(true);
    // The flag widens nothing on other statuses.
    expect(operationActions("manual_review", true).canWaitlistAlternatives).toBe(false);
  });

  it("permits a cancellation request from every non-terminal status and no terminal one", () => {
    for (const status of ALL_STATUSES) {
      expect(operationActions(status).canRequestCancellation, status)
        .toBe(!TERMINAL_OPERATION_STATUSES.has(status));
    }
    // The terminal set is exactly the five the server computes `Terminal` from.
    expect([...TERMINAL_OPERATION_STATUSES].sort()).toEqual(
      ["booked", "expired", "failed_final", "rejected", "waitlisted"]
    );
  });

  it("permits nothing when the status is unknown — no status, no claim", () => {
    for (const status of [null, undefined, ""]) {
      expect(operationActions(status)).toEqual({
        canRequestCancellation: false,
        canResume: false,
        canResolve: false,
        canRespondAlternatives: false,
        canWaitlistAlternatives: false,
      });
    }
  });
});

describe("follow-up transport", () => {
  beforeEach(() => {
    // A fresh Response per call — a Response body can only be read once.
    vi.stubGlobal("fetch", vi.fn().mockImplementation(() => Promise.resolve(
      new Response(JSON.stringify({ operation_id: "op_1", status: "booked" }), { status: 202 })
    )));
  });
  afterEach(() => vi.unstubAllGlobals());

  function lastRequest(): { url: string; init: RequestInit; headers: Headers; body: unknown } {
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    return {
      url, init,
      headers: new Headers(init.headers),
      body: init.body ? JSON.parse(init.body as string) : undefined,
    };
  }

  it("cancel: POSTs with no operator headers and no body — the endpoint reads neither", async () => {
    await requestOperationCancellation("op 1");
    const { url, init, headers } = lastRequest();
    expect(url).toBe("/api/booking-operations/op%201/cancel");
    expect(init.method).toBe("POST");
    expect(headers.get("X-Actor")).toBeNull();
    expect(headers.get("X-Reason-Code")).toBeNull();
    expect(init.body).toBeUndefined();
  });

  it("resume: carries the full mandatory operator spine and the operator's reason", async () => {
    await resumeOperation("op_1", "provider confirmed by phone");
    const { url, headers, body } = lastRequest();
    expect(url).toBe("/api/booking-operations/op_1/resume");
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBe("provider confirmed by phone");
    // Empty body: the server mints the replay command id.
    expect(body).toEqual({});
  });

  it("resolve: sends the resolution with the provider ref only when booked", async () => {
    await resolveOperation("op_1", { resolution: "booked", providerRef: "prov_9" }, "checked with venue");
    let { url, headers, body } = lastRequest();
    expect(url).toBe("/api/booking-operations/op_1/resolve");
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBe("checked with venue");
    expect(body).toEqual({ resolution: "booked", provider_ref: "prov_9" });

    vi.mocked(fetch).mockClear();
    await resolveOperation("op_1", { resolution: "failed_final" }, "venue refused");
    ({ url, headers, body } = lastRequest());
    expect(body).toEqual({ resolution: "failed_final" });
  });

  it("alternatives: POSTs the answer with no operator headers", async () => {
    await respondToAlternatives("op_1", "waitlist");
    const { url, headers, body } = lastRequest();
    expect(url).toBe("/api/booking-operations/op_1/alternatives");
    expect(headers.get("X-Actor")).toBeNull();
    expect(body).toEqual({ response: "waitlist" });
  });
});

describe("statusFromResponse", () => {
  it("reads an echoed status and refuses to invent one", () => {
    expect(statusFromResponse({ status: "booked" })).toBe("booked");
    expect(statusFromResponse({ status: "" })).toBeNull();
    expect(statusFromResponse({})).toBeNull();
    expect(statusFromResponse(null)).toBeNull();
    expect(statusFromResponse("booked")).toBeNull();
  });
});
