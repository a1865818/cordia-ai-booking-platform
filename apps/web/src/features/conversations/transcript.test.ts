/**
 * The audited transcript read (spec 14 §2 purpose-restricted access, §3.1 step 4).
 *
 * The property under test is not "a reason was sent" — one always was. It is that the reason is
 * something the operator chose for *this* reading, so the audit trail can tell a dispute
 * investigation from idle browsing.
 */

import { describe, expect, it, vi, beforeEach } from "vitest";
import { SENSITIVE_READ_PURPOSES, type SensitiveReadPurpose } from "./purposes";

vi.mock("../../api/client", () => ({ fetchApi: vi.fn() }));

import { fetchApi } from "../../api/client";
import { getTranscript } from "./api";

beforeEach(() => vi.clearAllMocks());

describe("transcript read purposes", () => {
  it("offers only purposes the server ratifies", () => {
    // Mirrors SensitiveReadPurpose in apps/api. Adding one here without adding it there is a 400.
    expect(SENSITIVE_READ_PURPOSES.map(p => p.value)).toEqual([
      "quality_review",
      "guest_complaint",
      "dispute_review",
      "guest_request",
      "training_review",
      "security_investigation",
      "legal_hold",
    ]);
  });

  it("never offers the constant that made the trail meaningless", () => {
    const values = SENSITIVE_READ_PURPOSES.map(p => p.value as string);
    expect(values).not.toContain("operator_review");
  });

  it("gives every purpose an operator-facing label, not a raw enum", () => {
    for (const purpose of SENSITIVE_READ_PURPOSES) {
      expect(purpose.label).not.toBe(purpose.value);
      expect(purpose.label).not.toContain("_");
    }
  });

  it("sends the chosen purpose as the reason code, with the actor", () => {
    getTranscript("sess_1", "dispute_review");

    const [, options] = vi.mocked(fetchApi).mock.calls[0];
    expect(options).toMatchObject({ actor: true, reasonCode: "dispute_review" });
  });

  it("sends whichever purpose it is given — no hard-coded default survives", () => {
    // The regression this pins: `reasonCode: options.reasonCode ?? "operator_review"` meant every
    // caller that omitted one silently reported the same purpose.
    for (const purpose of SENSITIVE_READ_PURPOSES) {
      vi.mocked(fetchApi).mockClear();
      getTranscript("sess_1", purpose.value);
      const [, options] = vi.mocked(fetchApi).mock.calls[0];
      expect((options as { reasonCode?: string }).reasonCode).toBe(purpose.value);
    }
  });

  it("requires a purpose at the type level", () => {
    // @ts-expect-error — a transcript read without a stated purpose must not compile.
    void (() => getTranscript("sess_1"));

    const purpose: SensitiveReadPurpose = "legal_hold";
    expect(purpose).toBe("legal_hold");
  });
});
