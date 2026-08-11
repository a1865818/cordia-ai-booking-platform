/**
 * Identity-first booking lookup (docs §13 item 4).
 *
 * The rules worth pinning here are all about not over-claiming: a search is deliberate rather
 * than typed-ahead, an empty result names BOTH of its causes, and a candidate is rendered with
 * exactly the fields the contract returns — no invented lifecycle status, no contact details.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { BookingLookupPanel } from "./BookingLookupPanel";

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const CANDIDATE = {
  booking_id: "bk_1",
  booking_reference: "CORD-1234",
  business: "Serenity Spa",
  date: "2026-09-01",
  approx_time: "19:00",
  party_size: 2,
};

function lastUrl() {
  const calls = vi.mocked(fetch).mock.calls;
  return String(calls[calls.length - 1][0]);
}

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(
    jsonResponse({ status: "found", candidates: [CANDIDATE] })
  ));
});
afterEach(() => vi.unstubAllGlobals());

function search(value: string) {
  fireEvent.change(screen.getByPlaceholderText("+61400111222"), { target: { value } });
  fireEvent.click(screen.getByRole("button", { name: "Search" }));
}

describe("BookingLookupPanel", () => {
  it("does not search as you type — discovery reads another person's booking", () => {
    render(<BookingLookupPanel />);
    fireEvent.change(screen.getByPlaceholderText("+61400111222"), {
      target: { value: "+61400111222" },
    });
    expect(fetch).not.toHaveBeenCalled();
  });

  it("normalises a formatted phone with the SAME function the submission path uses", async () => {
    render(<BookingLookupPanel />);
    search("  +61 400 111 222  ");

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    // A booking stored without separators must be findable by a search typed with them; the
    // stored value lives in an immutable hashed payload and can never be tidied afterwards.
    expect(lastUrl()).toContain(`phone_e164=${encodeURIComponent("+61400111222")}`);
  });

  it("warns that a national number will not match, without refusing to search it", async () => {
    render(<BookingLookupPanel />);
    fireEvent.change(screen.getByPlaceholderText("+61400111222"), {
      target: { value: "0400 111 222" },
    });

    expect(screen.getByText(/will not match one saved with it/)).toBeInTheDocument();
    // Still searchable — the console does not know the country either, so refusing would be
    // presumptuous rather than helpful.
    expect(screen.getByRole("button", { name: "Search" })).not.toBeDisabled();
  });

  it("renders only the fields the contract returns", async () => {
    render(<BookingLookupPanel />);
    search("+61400111222");

    expect(await screen.findByText(/Serenity Spa · CORD-1234/)).toBeInTheDocument();
    expect(screen.getByText(/2026-09-01 · 19:00 · party of 2/)).toBeInTheDocument();
    // The minimised candidate carries no lifecycle status; inventing "Confirmed" would assert
    // something the API never said.
    expect(screen.queryByText(/confirmed/i)).toBeNull();
  });

  it("names BOTH causes of an empty result", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ status: "none", candidates: [] }));
    render(<BookingLookupPanel />);
    search("+61400999999");

    const status = await screen.findByRole("status");
    // "no such booking" and "this venue has no name set, so candidates are withheld entirely"
    // are different problems, and only one of them is the operator's to fix.
    expect(status).toHaveTextContent(/No booking matches that/);
    expect(status).toHaveTextContent(/venue name is set in Settings/);
  });

  it("tells the operator to disambiguate when more than one matches", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({
      status: "multiple",
      candidates: [CANDIDATE, { ...CANDIDATE, booking_id: "bk_2", booking_reference: "CORD-5678" }],
    }));
    render(<BookingLookupPanel />);
    search("+61400111222");

    expect(await screen.findByText(/do not assume the first/i)).toBeInTheDocument();
  });

  it("searches by reference and by email on the right query parameter", async () => {
    render(<BookingLookupPanel />);

    fireEvent.click(screen.getByRole("button", { name: "Reference" }));
    fireEvent.change(screen.getByPlaceholderText("CORD-1234"), { target: { value: "CORD-9" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await waitFor(() => expect(lastUrl()).toContain("booking_reference=CORD-9"));

    fireEvent.click(screen.getByRole("button", { name: "Email" }));
    fireEvent.change(screen.getByPlaceholderText("guest@example.com"), {
      target: { value: "a@b.com" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await waitFor(() => expect(lastUrl()).toContain("email=a%40b.com"));
  });

  it("names a failed search with a retry rather than showing an empty result", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse(
      { title: "Bad Request", detail: "At least one hint is required.", status: 400 }, 400
    ));
    render(<BookingLookupPanel />);
    search("+61400111222");

    expect(await screen.findByRole("alert")).toHaveTextContent(/Could not search/);
    expect(screen.queryByText(/No booking matches that/)).toBeNull();
  });

  it("says that finding a booking is not permission to change it", () => {
    render(<BookingLookupPanel />);
    // Discovery ids are not authorization (spec 08 §9.4.8) — the panel must not imply otherwise.
    expect(screen.getByText(/not permission to change it/)).toBeInTheDocument();
  });
});
