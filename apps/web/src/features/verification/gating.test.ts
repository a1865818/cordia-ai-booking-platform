/**
 * What counts as a step-up-recoverable 403.
 *
 * Two different failures share the 403 status. `VerificationRequiredException` carries
 * `code: "verification_required"` and IS resolvable — the guest verifies and the call is
 * retried with a grant. `AuthorizationFailedException` deliberately does **not** carry the
 * code: it means "your role does not permit this", which no amount of guest verification
 * fixes. Mounting the gate for the second would loop an operator through a flow that cannot
 * succeed.
 *
 * These go through the real response classifier rather than hand-built error objects, so they
 * pin the wire shape the API actually sends.
 */

import { describe, expect, it } from "vitest";
import { toApiError, isApiVerificationRequiredError } from "../../api/problem";
import { queryKeys } from "../../api/queryKeys";

function response(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/problem+json" },
  });
}

describe("classifying a 403", () => {
  it("recognises the resolvable 403 by its code", async () => {
    const error = await toApiError(
      response(403, {
        code: "verification_required",
        title: "Verification required",
        detail: "The guest must confirm a code before this transcript can be read.",
        action: "read_sensitive",
      }),
      "/api/conversation-sessions/sess_1/transcript"
    );

    expect(isApiVerificationRequiredError(error)).toBe(true);
  });

  it("does NOT treat an authorization failure as step-up recoverable", async () => {
    // Same status, different meaning: AuthorizationFailedException omits the code on purpose.
    const error = await toApiError(
      response(403, { title: "Forbidden", detail: "Your role does not permit this." }),
      "/api/conversation-sessions/sess_1/transcript"
    );

    expect(isApiVerificationRequiredError(error)).toBe(false);
  });

  it("matches on the code, never on the human-facing title", async () => {
    // Title is prose for a person; a client that branches on copy breaks when copy changes.
    const error = await toApiError(
      response(403, { title: "Verification required" }),
      "/api/Bookings/bk_1"
    );

    expect(isApiVerificationRequiredError(error)).toBe(false);
  });

  it("is false for every other failure shape", async () => {
    const serverError = await toApiError(response(500, { title: "Server error" }), "/api/x");
    expect(isApiVerificationRequiredError(serverError)).toBe(false);
    expect(isApiVerificationRequiredError(new Error("network"))).toBe(false);
    expect(isApiVerificationRequiredError(null)).toBe(false);
  });
});

describe("transcript query key", () => {
  it("changes when a grant is obtained so the cached failure is not re-served", () => {
    const before = queryKeys.conversations.transcript("sess_1");
    const after = queryKeys.conversations.transcript("sess_1", "grant_1");

    expect(before).not.toEqual(after);
    expect(before.at(-1)).toBe("no-grant");
    expect(after.at(-1)).toBe("grant_1");
  });

  it("keeps different sessions on different keys", () => {
    expect(queryKeys.conversations.transcript("sess_1", "g")).not.toEqual(
      queryKeys.conversations.transcript("sess_2", "g")
    );
  });
});
