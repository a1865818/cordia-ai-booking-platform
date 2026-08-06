import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MoreScreen } from "./MoreScreen";

function renderScreen(props: Partial<Parameters<typeof MoreScreen>[0]> = {}) {
  render(
    <MoreScreen
      go={() => {}}
      autoText="on"
      onSignOut={() => {}}
      {...props}
    />
  );
}

describe("MoreScreen", () => {
  beforeEach(() => { window.APP_CONFIG = undefined; });
  afterEach(() => { window.APP_CONFIG = undefined; vi.restoreAllMocks(); });

  it("shows the configured actor and tenant, never a hard-coded person or venue", () => {
    // On phone this is the primary navigation surface. It asserted "Maya Chen" and
    // "Bella Nails · Owner" as plain fact, in a third independent copy of those literals,
    // with no endpoint anywhere returning either.
    window.APP_CONFIG = { ACTOR: "front_desk", ACTOR_ROLE: "manager", TENANT_ID: "bella" };
    renderScreen();

    expect(screen.queryByText("Maya Chen")).not.toBeInTheDocument();
    expect(screen.queryByText(/Bella Nails/)).not.toBeInTheDocument();
    expect(screen.getByText("front_desk")).toBeInTheDocument();
    expect(screen.getByText("bella · manager")).toBeInTheDocument();
  });

  it("states no waitlist count until the live list has loaded", () => {
    // A successful-but-empty answer is 0; "we have not asked yet" is not a number at all.
    // This row used to read `WAITLIST.length` — the mock array's size — beside a waitlist
    // screen showing the real one.
    renderScreen({ waitlistCount: undefined });
    expect(screen.getByText("Guests waiting for a slot")).toBeInTheDocument();
  });

  it("reports the live waitlist count once it has, including zero", () => {
    renderScreen({ waitlistCount: 0 });
    expect(screen.getByText("0 waiting · auto-book on")).toBeInTheDocument();
  });

  it("never states an amount of money held", () => {
    // `PAYOUT.held` is a hand-written constant and spec 09 has shipped no payment surface, so
    // the platform has never held a cent. This was the most credible fabrication in the product.
    renderScreen({ waitlistCount: 3 });
    expect(screen.queryByText(/A\$/)).not.toBeInTheDocument();
    expect(screen.queryByText(/held/)).not.toBeInTheDocument();
    expect(screen.queryByText(/next payout/)).not.toBeInTheDocument();
  });

  it("badges every row whose figures have no backend", () => {
    renderScreen();
    // Deposits and Campaigns both.
    expect(screen.getAllByText("Demo data")).toHaveLength(2);
  });

  it("claims nothing is awaiting approval", () => {
    // The campaigns row carried a hard-coded amber `1` badge — a claim that something needed
    // the operator's attention, on a screen no endpoint and no spec owns.
    renderScreen();
    expect(screen.queryByText("1 awaiting your approval")).not.toBeInTheDocument();
  });

  it("does not claim analytics reports no-shows or who handled a booking", () => {
    // Neither is recorded server-side (Inv-16).
    renderScreen();
    expect(screen.queryByText(/no-shows/)).not.toBeInTheDocument();
    expect(screen.queryByText(/AI handled/)).not.toBeInTheDocument();
  });
});
