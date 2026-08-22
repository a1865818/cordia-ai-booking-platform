/**
 * Operator recovery tools — transport discipline.
 *
 * All three endpoints are fail-closed server-side on the operator spine, so what these pin is
 * that the console actually sends it: an omitted `X-Actor`, `X-Actor-Role` or `X-Reason-Code`
 * is a 400/403, not a silent success. They also pin the two details that are easy to get
 * wrong and expensive to get wrong:
 *
 *   • the reason code is the OPERATOR's own words, never a constant — an audit row that
 *     always says the same thing records nothing;
 *   • convert-standalone carries the customer's verification grant, because §21.4 makes a
 *     pre-order surviving a dead booking conditional on the customer agreeing.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "../../test/vitest-compat";
import { replayDeadLetterMessage } from "./deadLetters";
import { getModelTrace } from "./modelTraces";
import { cancelPreorder, convertPreorderToStandalone } from "./preorderRecovery";

describe("operator recovery transport", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn().mockImplementation(() => Promise.resolve(
      new Response(JSON.stringify({ ok: true }), { status: 200 })
    )));
  });
  afterEach(() => vi.unstubAllGlobals());

  function lastRequest(): { url: string; init: RequestInit; headers: Headers; body: unknown } {
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    return {
      url,
      init,
      headers: new Headers(init.headers),
      body: init.body ? JSON.parse(init.body as string) : undefined,
    };
  }

  it("DLQ replay: carries the operator spine and the reason in the body", async () => {
    await replayDeadLetterMessage({
      messageId: "msg 1",
      consumerName: "booking-consumer",
      reasonCode: "poison payload fixed upstream",
    });
    const { url, init, headers, body } = lastRequest();

    expect(url).toBe("/api/dead-letter-messages/replay");
    expect(init.method).toBe("POST");
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    // This endpoint reads the reason from the BODY, not the X-Reason-Code header.
    expect(body).toEqual({
      messageId: "msg 1",
      consumerName: "booking-consumer",
      reasonCode: "poison payload fixed upstream",
    });
  });

  it("DLQ replay: never mints a replay-command id — the server owns message identity", async () => {
    await replayDeadLetterMessage({
      messageId: "msg_1",
      consumerName: "c",
      reasonCode: "r",
    });
    const { body } = lastRequest();

    // §6.1: minting an id here would create a second logical identity for one message.
    expect(Object.keys(body as object)).not.toContain("replayCommandId");
    expect(Object.keys(body as object)).not.toContain("messageIdOverride");
  });

  it("model trace: audited read carries actor, role and the operator's reason", async () => {
    await getModelTrace("trace 7", "investigating a mis-quoted price");
    const { url, init, headers } = lastRequest();

    expect(url).toBe("/api/model-traces/trace%207");
    // A read, not a write — but still audited on both allow and deny.
    expect(init.method ?? "GET").toBe("GET");
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBe("investigating a mis-quoted price");
  });

  it("convert-standalone: sends the customer's grant — §21.4 requires their agreement", async () => {
    await convertPreorderToStandalone({
      sagaId: "saga 1",
      grantId: "grant_abc",
      reasonCode: "customer asked to keep the order",
    });
    const { url, headers, body } = lastRequest();

    expect(url).toBe("/api/preorder/sagas/saga%201/convert-standalone");
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBe("customer asked to keep the order");
    // snake_case — the server binds `grant_id`.
    expect(body).toEqual({ grant_id: "grant_abc" });
  });

  it("cancel: takes no grant — nothing is being kept on the customer's behalf", async () => {
    await cancelPreorder({ sagaId: "saga_1", reasonCode: "venue closed", reason: "kitchen fire" });
    const { url, headers, body } = lastRequest();

    expect(url).toBe("/api/preorder/sagas/saga_1/cancel");
    expect(headers.get("X-Reason-Code")).toBe("venue closed");
    expect(body).toEqual({ reason: "kitchen fire" });
    expect(Object.keys(body as object)).not.toContain("grant_id");
  });

  it("passes the operator's reason through verbatim rather than defaulting it", async () => {
    await cancelPreorder({ sagaId: "s", reasonCode: "a very specific operator justification" });
    expect(lastRequest().headers.get("X-Reason-Code")).toBe("a very specific operator justification");
  });
});
