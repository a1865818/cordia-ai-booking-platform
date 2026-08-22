/**
 * The reschedule pre-flight's refusal handling.
 *
 * The load-bearing case is the LAST one: an unreachable pre-flight must not refuse. This read
 * holds nothing and the acceptance door re-checks everything authoritatively, so blocking on it
 * would let an outage on an advisory call take down a mutation that would have succeeded.
 */

import { afterEach, describe, expect, it, vi } from "../../test/vitest-compat";
import { preflightReschedule, refusalMessage } from "./preflight";

vi.mock("./submit", () => ({ validateChange: vi.fn() }));
import { validateChange } from "./submit";

const answer = (validation: unknown) => ({ status: "ok", validation });

afterEach(() => vi.clearAllMocks());

describe("preflightReschedule", () => {
  it("allows the change when the server says it is valid", async () => {
    vi.mocked(validateChange).mockResolvedValue(answer({ valid: true, code: "valid" }) as never);
    expect(await preflightReschedule("bk_1", 3, "2026-09-01T10:00:00Z", 60, 2)).toBeNull();
  });

  it("sends exactly the fields the submission will change", async () => {
    vi.mocked(validateChange).mockResolvedValue(answer({ valid: true }) as never);
    await preflightReschedule("bk_1", 7, "2026-09-01T10:00:00Z", 90, 4);

    expect(validateChange).toHaveBeenCalledWith("bk_1", {
      expectedBookingVersion: 7,
      requestedFields: {
        starts_at: "2026-09-01T10:00:00Z",
        // `duration_minutes` is in the server's supported-change set. It was not, which made the
        // pre-flight answer `policy_failed` for the commonest real reschedule.
        duration_minutes: 90,
        party_size: 4,
      },
    });
  });

  it.each([
    ["stale_version", /changed while the console was showing it/],
    ["capacity_unavailable", /no capacity declared for that time/],
    ["policy_failed", /booking policy does not allow/],
  ])("refuses on %s with wording that names the reason", async (code, expected) => {
    vi.mocked(validateChange).mockResolvedValue(answer({ valid: false, code }) as never);
    const refusal = await preflightReschedule("bk_1", 3, "2026-09-01T10:00:00Z", 60, 2);
    expect(refusal).toMatch(expected);
  });

  it("reports an unmodelled refusal code rather than proceeding past it", async () => {
    // The server's vocabulary grows on its schedule, not the console's. Silently treating an
    // unknown refusal as permission would submit a change the platform just declined.
    vi.mocked(validateChange).mockResolvedValue(answer({ valid: false, code: "something_new" }) as never);
    expect(await preflightReschedule("bk_1", 3, "2026-09-01T10:00:00Z", 60, 2))
      .toMatch(/refused before it was submitted \(something_new\)/);
  });

  it("FAILS OPEN when the pre-flight itself cannot be reached", async () => {
    vi.mocked(validateChange).mockRejectedValue(new Error("network dropped"));
    expect(await preflightReschedule("bk_1", 3, "2026-09-01T10:00:00Z", 60, 2)).toBeNull();
  });

  it("fails open when the server answers without a validation body", async () => {
    vi.mocked(validateChange).mockResolvedValue({ status: "ok" } as never);
    expect(await preflightReschedule("bk_1", 3, "2026-09-01T10:00:00Z", 60, 2)).toBeNull();
  });
});

describe("refusalMessage", () => {
  it("names the code when it has no wording for it", () => {
    expect(refusalMessage(null)).toMatch(/no reason given/);
  });
});
