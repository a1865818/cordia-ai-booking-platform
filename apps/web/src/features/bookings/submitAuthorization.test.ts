import { afterEach, describe, expect, it, vi } from "../../test/vitest-compat";
import { submitBooking } from "./submit";

/**
 * The acceptance front door authorizes a mutation of an existing booking by EITHER a verification
 * grant or an authenticated staff role (spec 08 §12, Inv-11). The console is the staff caller, so
 * every reschedule it sends depends on these two headers being present — without them the server
 * answers 403 and no booking can be moved from the console at all.
 *
 * This is pinned here rather than left to the reschedule flow's own tests because the failure is
 * silent at the type level: dropping `actor: true` compiles, passes typecheck, and breaks every
 * reschedule at runtime.
 */
// Not `typeof fetch`: bun's real global `fetch` also carries a `preconnect` static method,
// which a plain implementation function doesn't have. This is just the call signature the
// client actually uses `fetch` with.
type FetchLike = (url: string | URL | Request, init?: RequestInit) => Promise<Response>;

function captureFetch() {
  const spy = vi.fn<FetchLike>(
    async () =>
      new Response(JSON.stringify({ operation_id: "op_1" }), {
        status: 202,
        headers: { "Content-Type": "application/json" },
      })
  );
  vi.stubGlobal("fetch", spy);
  return spy;
}

// The request body is irrelevant to what this asserts — the headers are the subject.
const anyRequest = {} as Parameters<typeof submitBooking>[0];

describe("submitBooking authorization headers", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("presents the operator's identity and role so a mutation is authorized", async () => {
    const spy = captureFetch();

    await submitBooking(anyRequest);

    const init = spy.mock.calls[0]?.[1] as RequestInit | undefined;
    const headers = new Headers(init?.headers);
    expect(headers.get("X-Actor")).toBeTruthy();
    // The role must be one the server's matrix recognises; anything else fails closed (GD-1).
    // `?? ""` is only to satisfy `toContain`'s stricter (non-nullable) typed overload here — a
    // missing header still legitimately fails the assertion, same as `null` would.
    expect(["owner", "operator"]).toContain(headers.get("X-Actor-Role") ?? "");
  });
});
