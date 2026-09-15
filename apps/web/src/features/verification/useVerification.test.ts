/**
 * The idempotency key the step-up flow sends.
 *
 * `POST /api/verifications` returns the ORIGINAL challenge verbatim for a repeated
 * `verification_idempotency_key` and does not look at the channel, so a key that outlives one
 * send silently turns a later send into a replay of an earlier one. The hook's own header comment
 * always said "a key per *send*"; the code minted it once at mount and only re-minted it in
 * `reset()`, so every `start()` reused it.
 *
 * The case that bites: after a failed or rate-limited SMS the operator is still shown both
 * buttons, and pressing "Email instead" replayed the SMS challenge — the hook reported a code was
 * sent while no email ever went out.
 */

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "../../test/vitest-compat";
import { useVerification } from "./useVerification";

const requestVerification = vi.fn();

vi.mock("./api", () => ({
  requestVerification: (...args: unknown[]) => requestVerification(...args),
  verifyCode: vi.fn(),
}));

function sent(status: string) {
  return {
    status,
    verification: { verification_id: "vrf_server_1" },
  };
}

describe("the verification idempotency key", () => {
  beforeEach(() => {
    requestVerification.mockReset();
    requestVerification.mockResolvedValue(sent("sent"));
  });

  it("is different for each send, so a second channel is a genuinely new challenge", async () => {
    const { result } = renderHook(() => useVerification({ action: "modify", customerId: "cust_1" }));

    await act(async () => {
      await result.current.start("sms");
    });
    await waitFor(() => expect(requestVerification).toHaveBeenCalledTimes(1));

    // The SMS was rate-limited, so the operator switches channel — the exact path that replayed.
    requestVerification.mockResolvedValue(sent("sent"));
    await act(async () => {
      await result.current.start("email");
    });
    await waitFor(() => expect(requestVerification).toHaveBeenCalledTimes(2));

    const [first, second] = requestVerification.mock.calls.map(
      (call) => (call[0] as { idempotencyKey: string }).idempotencyKey
    );

    expect(first).toBeTruthy();
    expect(second).toBeTruthy();
    expect(second).not.toBe(first);
    expect(requestVerification.mock.calls[1][0]).toMatchObject({ channel: "email" });
  });

  it("does not send twice while a send is already in flight", async () => {
    // The phase guard, not the key, is what makes a double-click safe — worth pinning, because
    // minting per send removes the accidental protection a stable key used to give.
    let release: (value: unknown) => void = () => {};
    requestVerification.mockImplementation(
      () => new Promise((resolve) => { release = resolve; })
    );

    const { result } = renderHook(() => useVerification({ action: "cancel" }));

    act(() => {
      void result.current.start("sms");
    });
    await waitFor(() => expect(result.current.phase).toBe("sending"));

    act(() => {
      void result.current.start("sms");
    });

    expect(requestVerification).toHaveBeenCalledTimes(1);

    await act(async () => {
      release(sent("sent"));
    });
  });

  it("reports an `already_sent` replay as a replay rather than implying a fresh code", async () => {
    requestVerification.mockResolvedValue(sent("already_sent"));

    const { result } = renderHook(() => useVerification({ action: "modify" }));

    await act(async () => {
      await result.current.start("sms");
    });

    await waitFor(() => expect(result.current.phase).toBe("awaiting_code"));
    expect(result.current.message).toContain("already sent");
  });
});

describe("the development code (#71)", () => {
  beforeEach(() => requestVerification.mockReset());

  it("is surfaced when the API returns one", async () => {
    requestVerification.mockResolvedValue({ ...sent("sent"), development_code: "482913" });
    const { result } = renderHook(() => useVerification({ action: "cancel" }));

    await act(async () => {
      await result.current.start("sms");
    });

    expect(result.current.phase).toBe("awaiting_code");
    expect(result.current.developmentCode).toBe("482913");
  });

  it("is absent when the API does not return one, as in every deployment", async () => {
    requestVerification.mockResolvedValue(sent("sent"));
    const { result } = renderHook(() => useVerification({ action: "cancel" }));

    await act(async () => {
      await result.current.start("sms");
    });

    expect(result.current.phase).toBe("awaiting_code");
    expect(result.current.developmentCode ?? null).toBeNull();
  });
});
