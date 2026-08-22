import { describe, expect, it, vi } from "../../../test/vitest-compat";
import { render, screen, fireEvent } from "@testing-library/react";
import { WaitlistScreen } from "./WaitlistScreen";
import type { WaitlistItem } from "../../../data/console";
import { toWaitlistItem } from "../../../features/waitlist/adapters";

function item(overrides: Partial<WaitlistItem> = {}): WaitlistItem {
  return {
    id: "enr_1",
    name: "cust_9f2a",
    service: "Gel manicure",
    want: "Thu 6 Aug from 10:00",
    auto: true,
    ...overrides,
  };
}

function renderScreen(waitlist: WaitlistItem[], onToggleAuto = vi.fn()) {
  render(
    <WaitlistScreen
      device="desktop"
      autoBook
      toggleAutoBook={() => {}}
      waitlist={waitlist}
      onToggleAuto={onToggleAuto}
      onRemove={() => {}}
      onBookNow={() => {}}
    />
  );
  return { onToggleAuto };
}

describe("waitlist auto-book consent", () => {
  it("shows a live guest's auto-book state read-only", () => {
    // `auto_book_authorized` is the GUEST's consent, recorded at enrollment (spec 11 §20,
    // Inv-1), and no endpoint changes it. A clickable chip flipped it and wrote nothing.
    const { onToggleAuto } = renderScreen([item({ isLive: true })]);

    const chip = screen.getByText("Auto");
    expect(chip.closest("button")).toBeNull();

    fireEvent.click(chip);
    expect(onToggleAuto).not.toHaveBeenCalled();
  });

  it("explains whose decision it was", () => {
    renderScreen([item({ isLive: true })]);
    expect(screen.getByTitle(/guest agreed to auto-book/i)).toBeInTheDocument();
  });

  it("says an unauthorized guest gets an offer instead", () => {
    renderScreen([item({ isLive: true, auto: false })]);
    expect(screen.getByText("Manual")).toBeInTheDocument();
    expect(screen.getByTitle(/did not agree to auto-book/i)).toBeInTheDocument();
  });

  it("keeps the demo rows interactive so the mock screen still works", () => {
    const { onToggleAuto } = renderScreen([item({ isLive: undefined })]);

    fireEvent.click(screen.getByText("Auto"));
    expect(onToggleAuto).toHaveBeenCalledWith("enr_1");
  });

  it("does not badge the venue-level auto-book switch — it is saved", () => {
    renderScreen([item({ isLive: true })]);
    expect(screen.queryByText("Demo data")).not.toBeInTheDocument();
  });
});

describe("toWaitlistItem", () => {
  it("marks API rows as live so their consent renders read-only", () => {
    const mapped = toWaitlistItem({
      enrollment_id: "enr_1",
      customer_ref: "cust_1",
      service_id: "gel",
      location_id: "loc_1",
      party_size: 2,
      acceptable_from: "2026-08-06T00:00:00Z",
      acceptable_to: "2026-08-06T04:00:00Z",
      auto_book_authorized: false,
      cancelled: false,
      created_at: "2026-08-03T09:00:00Z",
      latest_offer_status: null,
      latest_offer_expires_at: null,
    } as Parameters<typeof toWaitlistItem>[0]);

    expect(mapped.isLive).toBe(true);
    // Never assumed true: silent auto-book is prohibited by default.
    expect(mapped.auto).toBe(false);
  });
});
