/**
 * The five primitives added for surfaces that already reach a live endpoint.
 *
 * Same philosophy as `designSystem.test.tsx`: pin the *rules*, not the markup. Every rule here
 * exists because getting it wrong would state something the platform cannot back — a guessed
 * country code, a guessed currency, a role gate that disagrees with the server's matrix, a
 * consent state nobody recorded, or a countdown that outlives the action it describes.
 */

import { describe, expect, it } from "../../test/vitest-compat";
import { render, screen } from "@testing-library/react";
import { MoneyAmount, formatMoney } from "./MoneyAmount";
import { PermissionGate, satisfiesRole } from "./PermissionGate";
import { ConsentBadge } from "./ConsentBadge";
import { Countdown, remainingLabel } from "./Countdown";
import { looksUnnormalisedPhone } from "./PhoneInput";

// ---------------------------------------------------------------------------
// MoneyAmount
// ---------------------------------------------------------------------------

describe("MoneyAmount", () => {
  it("converts minor units once and never renders a float", () => {
    expect(formatMoney(4550, "AUD")).toBe("AUD 45.50");
    // The classic float trap: 0.1 + 0.2 arithmetic never happens because the value is integer
    // cents until the single divide at the render boundary.
    expect(formatMoney(1, "AUD")).toBe("AUD 0.01");
    expect(formatMoney(100000, "AUD")).toBe("AUD 1000.00");
  });

  it("renders the bare amount rather than guessing a currency", () => {
    expect(formatMoney(4550, null)).toBe("45.50");
    render(<MoneyAmount minor={4550} currency={null} />);
    expect(screen.getByText("45.50")).toBeInTheDocument();
  });

  it("treats zero as a real price and only null/undefined as absent", () => {
    // A complimentary item is priced, not unpriced — collapsing the two would hide a real
    // catalog value behind "No price".
    expect(formatMoney(0, "AUD")).toBe("AUD 0.00");
    expect(formatMoney(null, "AUD")).toBeNull();
    expect(formatMoney(undefined, "AUD")).toBeNull();
  });

  it("says an absent price is absent instead of leaving a blank", () => {
    render(<MoneyAmount minor={null} currency="AUD" />);
    expect(screen.getByText("No price")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// PermissionGate
// ---------------------------------------------------------------------------

describe("satisfiesRole", () => {
  it("implements the server's matrix, in which owner satisfies operator", () => {
    // `StaffRole.Satisfies`: owner ⊇ operator. The two hand-written checks this replaces were
    // equality tests, which would have refused an owner any operator-required action.
    expect(satisfiesRole("owner", "operator")).toBe(true);
    expect(satisfiesRole("operator", "operator")).toBe(true);
    expect(satisfiesRole("owner", "owner")).toBe(true);
    expect(satisfiesRole("operator", "owner")).toBe(false);
  });

  it("fails closed on unknown, blank and absent roles", () => {
    for (const role of ["", "   ", "admin", null, undefined]) {
      expect(satisfiesRole(role, "owner")).toBe(false);
      expect(satisfiesRole(role, "operator")).toBe(false);
    }
  });

  it("is case-sensitive, because the server compares the header verbatim", () => {
    // A session configured "Owner" genuinely 403s. A gate that accepted it would offer a
    // button the platform then refuses.
    expect(satisfiesRole("Owner", "owner")).toBe(false);
  });
});

describe("PermissionGate", () => {
  it("renders children only when the role satisfies the requirement", () => {
    render(
      <PermissionGate requiredRole="owner" role="owner" capability="Publishing">
        <button>Publish</button>
      </PermissionGate>
    );
    expect(screen.getByRole("button", { name: "Publish" })).toBeInTheDocument();
  });

  it("explains rather than rendering a disabled action that is certain to 403", () => {
    render(
      <PermissionGate requiredRole="owner" role="operator" capability="Publishing">
        <button>Publish</button>
      </PermissionGate>
    );
    expect(screen.queryByRole("button", { name: "Publish" })).not.toBeInTheDocument();
    expect(screen.getByText(/requires the/)).toBeInTheDocument();
    expect(screen.getByText("owner")).toBeInTheDocument();
  });

  it("names an unset role as unset instead of rendering an empty gap", () => {
    render(
      <PermissionGate requiredRole="owner" role="" capability="Publishing">
        <button>Publish</button>
      </PermissionGate>
    );
    expect(screen.getByText("unset")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// ConsentBadge
// ---------------------------------------------------------------------------

describe("ConsentBadge", () => {
  it("distinguishes 'no consent recorded' from 'consent declined'", () => {
    // These are completely different facts and collapsing them would misreport what the guest
    // did. `null` means the platform recorded nothing, not that the guest said no.
    const { unmount } = render(<ConsentBadge state={null} />);
    expect(screen.getByText("Consent not recorded")).toBeInTheDocument();
    unmount();

    render(<ConsentBadge state="declined" />);
    expect(screen.getByText("Consent declined")).toBeInTheDocument();
  });

  it("shows an unrecognised state verbatim rather than hiding it behind 'Unknown'", () => {
    // The vocabulary is a pg-enum that can gain members before this file knows them; showing
    // the server's own word is the only option that does not discard a real answer.
    render(<ConsentBadge state="withdrawn_by_request" />);
    expect(screen.getByText("withdrawn_by_request")).toBeInTheDocument();
  });

  it("mentions recording only alongside a granted consent", () => {
    const { unmount } = render(<ConsentBadge state="granted" recordingAllowed />);
    expect(screen.getByText(/recording/)).toBeInTheDocument();
    unmount();

    // "recording not allowed" beside "declined" says the same thing twice.
    render(<ConsentBadge state="declined" recordingAllowed={false} />);
    expect(screen.queryByText(/recording/)).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Countdown
// ---------------------------------------------------------------------------

describe("Countdown", () => {
  const NOW = Date.parse("2026-08-06T12:00:00Z");

  it("floors, so it can understate the time left but never overstate it", () => {
    // Rounding once put "expired" beside a live Accept button: an offer 25 s from expiry was
    // still actionable while its label had already rounded past the deadline.
    expect(remainingLabel("2026-08-06T12:07:59Z", NOW)).toBe("expires in 7 min");
    expect(remainingLabel("2026-08-06T14:59:00Z", NOW)).toBe("expires in 2 hr");
    expect(remainingLabel("2026-08-06T12:00:30Z", NOW)).toBe("expires in under a minute");
  });

  it("treats the deadline itself as expired, matching the actionability check", () => {
    expect(remainingLabel("2026-08-06T12:00:00Z", NOW)).toBe("expired");
    expect(remainingLabel("2026-08-06T11:00:00Z", NOW)).toBe("expired");
  });

  it("renders nothing at all — including no prefix — when there is no usable deadline", () => {
    // An absent expiry is a real state. Inventing "expired" for it would report an expiry the
    // server never stated, and a stray separator would look like a missing value.
    expect(remainingLabel(null, NOW)).toBe("");
    expect(remainingLabel("not-a-date", NOW)).toBe("");

    const { container } = render(<Countdown expiresAt={null} now={NOW} prefix=" · " />);
    expect(container).toBeEmptyDOMElement();
  });

  it("renders the prefix only alongside a label", () => {
    render(<Countdown expiresAt="2026-08-06T12:07:00Z" now={NOW} prefix=" · " />);
    expect(screen.getByRole("timer")).toHaveTextContent("· expires in 7 min");
  });
});

// ---------------------------------------------------------------------------
// PhoneInput
// ---------------------------------------------------------------------------

describe("looksUnnormalisedPhone", () => {
  it("accepts E.164 regardless of the spacing the operator typed", () => {
    // Formatting is tidied automatically, so punctuation is never what the warning is about.
    expect(looksUnnormalisedPhone("+61400111222")).toBe(false);
    expect(looksUnnormalisedPhone("+61 400 111 222")).toBe(false);
    expect(looksUnnormalisedPhone("+61-400-111-222")).toBe(false);
  });

  it("warns about a national number, which no search will ever match", () => {
    // Neither side guesses a country: `PhoneMatching` and `normalisePhone` are both
    // formatting-only, so `0400…` is stored as typed and never found by a `+61…` search.
    expect(looksUnnormalisedPhone("0400111222")).toBe(true);
    expect(looksUnnormalisedPhone("400 111 222")).toBe(true);
  });

  it("warns about the values the looser 'starts with +' rule used to accept", () => {
    // This is the drift the shared rule closes: these passed on the lookup panel and were
    // flagged in the create drawer, so the same number got two different answers.
    expect(looksUnnormalisedPhone("+0400111222")).toBe(true);
    expect(looksUnnormalisedPhone("+1")).toBe(true);
  });

  it("says nothing about an empty field", () => {
    // The guest phone is optional; nagging about an unfilled field trains operators to ignore
    // the warning that matters.
    expect(looksUnnormalisedPhone("")).toBe(false);
    expect(looksUnnormalisedPhone("   ")).toBe(false);
  });
});
