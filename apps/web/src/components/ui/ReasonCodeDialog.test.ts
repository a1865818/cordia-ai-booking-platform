/**
 * `composeReason` builds the `X-Reason-Code` header value an audited operator action carries.
 *
 * It had no tests at all, and the one behaviour its own comments promised — "flattened to single
 * spaces" — was the one it did not do.
 */

import { describe, expect, it } from "../../test/vitest-compat";
import { composeReason } from "./ReasonCodeDialog";

describe("composeReason", () => {
  it("flattens a multi-line note into spaces, not into question marks", () => {
    // The Latin-1 substitution `[^ -ÿ]` matches everything outside U+0020-U+00FF, which includes
    // LF, CR and TAB. Running it before the whitespace collapse turned every line break into a
    // literal `?`, so a two-line note reached the audit trail as
    // `guest_requested_change: Called the venue?They said closed`.
    expect(composeReason("guest_requested_change", "Called the venue\nThey said closed"))
      .toBe("guest_requested_change: Called the venue They said closed");
  });

  it("handles a CRLF as one space rather than two question marks", () => {
    expect(composeReason("rc", "one\r\ntwo")).toBe("rc: one two");
  });

  it("collapses tabs and runs of spaces", () => {
    expect(composeReason("rc", "a\t\tb   c")).toBe("rc: a b c");
  });

  it("still substitutes genuinely un-encodable characters", () => {
    // CJK cannot be a header value at all, so it remains `?` — that part was always correct.
    expect(composeReason("rc", "café 予約")).toBe("rc: café ??");
    // An emoji is a surrogate PAIR, so the per-code-unit substitution yields two `?`. Cosmetic
    // only — the point is that it never reaches the header, and the count is asserted here so the
    // behaviour is recorded rather than rediscovered.
    expect(composeReason("rc", "café 🎉")).toBe("rc: café ??");
  });

  it("normalises smart punctuation rather than replacing it", () => {
    expect(composeReason("rc", "the guest’s “note” — fine…"))
      .toBe("rc: the guest's \"note\" - fine...");
  });

  it("returns the bare code when the note is only whitespace", () => {
    expect(composeReason("rc", "  \n\t ")).toBe("rc");
  });
});
