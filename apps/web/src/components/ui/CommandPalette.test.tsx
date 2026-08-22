import { describe, expect, it, vi } from "../../test/vitest-compat";
import { render, screen, fireEvent } from "@testing-library/react";
import { CommandPalette } from "./CommandPalette";
import type { Customer } from "../../data/console";

function customer(overrides: Partial<Customer> = {}): Customer {
  return {
    id: "cust_9f2a",
    name: "cust_9f2a",
    phone: "",
    channel: "web",
    visits: 3,
    noShows: 0,
    lastVisit: "Yesterday",
    spend: 0,
    tags: [],
    fav: "",
    note: "",
    ...overrides,
  };
}

function open(props: Partial<Parameters<typeof CommandPalette>[0]> = {}) {
  const onOpenCustomer = vi.fn();
  render(
    <CommandPalette
      open
      onClose={() => {}}
      onNavigate={() => {}}
      onCreateBooking={() => {}}
      onOpenCustomer={onOpenCustomer}
      {...props}
    />
  );
  return { onOpenCustomer };
}

function search(term: string) {
  const input = screen.getByRole("textbox");
  fireEvent.change(input, { target: { value: term } });
}

describe("CommandPalette customer search", () => {
  it("searches the tenant's real customers once they have loaded", () => {
    const { onOpenCustomer } = open({ customers: [customer({ name: "cust_9f2a" })] });

    search("9f2");

    const hit = screen.getByText("cust_9f2a");
    expect(hit).toBeInTheDocument();
    // A live row must not be labelled as demo data.
    expect(screen.queryByText("Demo data")).not.toBeInTheDocument();

    // The palette selects on mouseDown, which fires before the input's blur.
    fireEvent.mouseDown(hit);
    expect(onOpenCustomer).toHaveBeenCalledWith("cust_9f2a");
  });

  it("does not offer a live customer who does not match", () => {
    open({ customers: [customer({ name: "cust_9f2a" })] });
    search("zzz");
    expect(screen.queryByText("cust_9f2a")).not.toBeInTheDocument();
  });

  it("does not offer fabricated names when the customer list has never loaded", () => {
    open({ customers: undefined });
    search("sarah");
    expect(screen.queryByText("Sarah Nguyen")).not.toBeInTheDocument();
  });

  it("offers no customers at all when the tenant genuinely has none", () => {
    // An empty array is a real answer, not "not loaded" — it must not fall back to demo rows.
    open({ customers: [] });
    search("sarah");
    expect(screen.queryByText("Sarah Nguyen")).not.toBeInTheDocument();
  });
});
