/**
 * EnrollWaitlistDrawer — the console surface for `POST /api/waitlist/enrollments`.
 *
 * Pins: the submit gate (nothing is sent until every validator-required field is stated),
 * the honest failure path (named error + the duplicate-risk warning, no success claim),
 * the offer-only consent stance (spec 11 §20), and that success is only announced after
 * the server's 201.
 */

import { describe, expect, it, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { EnrollWaitlistDrawer } from "./EnrollWaitlistDrawer";
import type { BookableService } from "../../../features/bookings/services";

vi.mock("../../../features/waitlist/api", () => ({
  enrollGuest: vi.fn(),
}));
import { enrollGuest } from "../../../features/waitlist/api";

const SERVICES: BookableService[] = [
  { id: "cat_1:gel", name: "Gel manicure", durationMinutes: 60 },
  { id: "cat_1:sns", name: "SNS dipping", durationMinutes: null },
];

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function renderDrawer(props: Partial<Parameters<typeof EnrollWaitlistDrawer>[0]> = {}) {
  return render(
    <EnrollWaitlistDrawer
      open
      device="desktop"
      onClose={() => {}}
      onEnrolled={() => {}}
      services={SERVICES}
      knownLocations={["loc_001"]}
      {...props}
    />,
    { wrapper }
  );
}

/**
 * Fills every required field with a coherent enrolment.
 *
 * The guest contact is a real DESTINATION, not a name. It is the only thing an offer can be sent
 * to — `NotificationRecipientResolver` derives the address from `customer_ref` alone — so the
 * drawer now refuses a name-only enrolment rather than creating one whose every offer settles
 * `no_destination`.
 */
function fillForm() {
  fireEvent.change(screen.getByPlaceholderText("+61400111222 or guest@example.com"), {
    target: { value: "  +61400111222  " },
  });
  fireEvent.change(screen.getByPlaceholderText("e.g. loc_001"), {
    target: { value: "loc_001" },
  });
  fireEvent.change(screen.getByLabelText("Earliest acceptable time"), {
    target: { value: "2026-08-10T10:00" },
  });
  fireEvent.change(screen.getByLabelText("Latest acceptable time"), {
    target: { value: "2026-08-10T14:00" },
  });
  fireEvent.change(screen.getByLabelText(/Offer channel/), { target: { value: "sms" } });
}

beforeEach(() => vi.clearAllMocks());

describe("EnrollWaitlistDrawer", () => {
  it("does not allow submission until every required field is stated", () => {
    renderDrawer();
    const button = screen.getByRole("button", { name: "Add to waitlist" });
    // The guest reference, location, window and channel all start unstated.
    expect(button).toBeDisabled();

    fillForm();
    expect(button).toBeEnabled();
  });

  it("rejects a window whose end is not after its start, and says why", () => {
    renderDrawer();
    fillForm();
    fireEvent.change(screen.getByLabelText("Latest acceptable time"), {
      target: { value: "2026-08-10T09:00" },
    });

    expect(screen.getByText("The latest acceptable time must be after the earliest.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add to waitlist" })).toBeDisabled();
  });

  it("submits exactly what the operator stated, trimmed, as ISO instants", async () => {
    vi.mocked(enrollGuest).mockResolvedValueOnce({});
    const onEnrolled = vi.fn();
    renderDrawer({ onEnrolled });

    fillForm();
    fireEvent.click(screen.getByRole("button", { name: "Add to waitlist" }));

    await waitFor(() => expect(onEnrolled).toHaveBeenCalledWith("+61400111222"));
    expect(enrollGuest).toHaveBeenCalledWith({
      customerRef: "+61400111222",
      serviceId: "cat_1:gel",
      locationId: "loc_001",
      partySize: 2,
      notificationChannel: "sms",
      offerLifetimeSeconds: 3600,
      acceptableFrom: new Date("2026-08-10T10:00").toISOString(),
      acceptableTo: new Date("2026-08-10T14:00").toISOString(),
      // Minted per attempt, so its value is necessarily random — what matters is that one is
      // sent at all, which is what stops the server falling back to hashing the guest's details.
      idempotencyKey: expect.stringMatching(/^idem_/) as unknown as string,
    });
  });

  it("reuses one key across retries of an attempt, and mints a new one when a field changes", async () => {
    // This is the whole reason the console mints the key rather than letting the server derive
    // one. A retry must look like the same request; an edit must not — and a fixed key would
    // turn "correct the party size and resubmit" into an idempotency conflict (Inv-3).
    vi.mocked(enrollGuest).mockRejectedValue(new Error("network dropped"));
    renderDrawer({});

    fillForm();
    fireEvent.click(screen.getByRole("button", { name: "Add to waitlist" }));
    await waitFor(() => expect(enrollGuest).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole("button", { name: "Add to waitlist" }));
    await waitFor(() => expect(enrollGuest).toHaveBeenCalledTimes(2));

    const keyOf = (call: number) =>
      vi.mocked(enrollGuest).mock.calls[call][0].idempotencyKey;
    expect(keyOf(1)).toBe(keyOf(0));

    // Change a material field: a different request, so a different key.
    fireEvent.change(screen.getByLabelText("Party size"), { target: { value: "5" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to waitlist" }));
    await waitFor(() => expect(enrollGuest).toHaveBeenCalledTimes(3));

    expect(keyOf(2)).not.toBe(keyOf(0));
  });

  it("mints a new key when the offer channel changes, not just the six hashed fields", async () => {
    // The signature originally covered only the fields the server hashed, and the channel was in
    // neither. So: enrol on SMS, the response is lost, the operator realises the guest wanted
    // email and corrects it, presses retry — same key, server sees a matching hash, answers it as
    // a replay and returns the ORIGINAL enrolment. The drawer toasts success while the guest is
    // still on SMS, and nothing anywhere reports the discarded choice.
    //
    // Channel and contact now move TOGETHER: the drawer refuses a stated channel the contact
    // cannot carry, so "same guest, different channel" is no longer a reachable state to test in
    // isolation. Switching to email means supplying an email, which is what an operator making
    // this correction would actually do.
    vi.mocked(enrollGuest).mockRejectedValue(new Error("network dropped"));
    renderDrawer({});

    fillForm();
    fireEvent.click(screen.getByRole("button", { name: "Add to waitlist" }));
    await waitFor(() => expect(enrollGuest).toHaveBeenCalledTimes(1));

    fireEvent.change(screen.getByLabelText(/Offer channel/), { target: { value: "email" } });
    fireEvent.change(screen.getByPlaceholderText("+61400111222 or guest@example.com"), {
      target: { value: "riley@example.com" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add to waitlist" }));
    await waitFor(() => expect(enrollGuest).toHaveBeenCalledTimes(2));

    const calls = vi.mocked(enrollGuest).mock.calls;
    expect(calls[1][0].notificationChannel).toBe("email");
    expect(calls[1][0].idempotencyKey).not.toBe(calls[0][0].idempotencyKey);
  });

  it("refuses a guest reference that is not a destination, and says why", () => {
    // The waitlist carries no contact field of its own, so a name here produces an enrolment that
    // looks entirely normal in the console while every offer and expiry notification settles
    // `no_destination`: the guest is never told, the offer expires unanswered, and the slot is
    // released. Blocked rather than warned — there is no degraded version of this that works.
    renderDrawer();
    fillForm();
    expect(screen.getByRole("button", { name: "Add to waitlist" })).toBeEnabled();

    fireEvent.change(screen.getByPlaceholderText("+61400111222 or guest@example.com"), {
      target: { value: "Riley P" },
    });

    expect(screen.getByText(/it has to be a destination/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add to waitlist" })).toBeDisabled();
  });

  it("refuses a channel the stated contact cannot carry", () => {
    // `ChannelFor` returns the STATED channel whenever it names a real one, so "Email" against a
    // phone number does not fall back — it pairs an email channel with a phone destination and
    // the send fails. The operator's two statements have to agree before either is honoured.
    renderDrawer();
    fillForm();
    fireEvent.change(screen.getByLabelText(/Offer channel/), { target: { value: "email" } });

    expect(screen.getByText(/could not be delivered to it/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add to waitlist" })).toBeDisabled();
  });

  it("offers only the channels an offer can actually be sent on", () => {
    // `Channel` is the conversation-origin vocabulary (voice/sms/web/email) and is wider than the
    // notification vocabulary. Offering "Phone call" or "Web" let the operator record a
    // preference the notification layer silently discarded — the same defect the staff "With"
    // picker was removed for.
    renderDrawer();
    const options = Array.from(
      screen.getByLabelText(/Offer channel/).querySelectorAll("option")
    ).map(o => (o as HTMLOptionElement).value);

    expect(options).toContain("sms");
    expect(options).toContain("email");
    expect(options).not.toContain("voice");
    expect(options).not.toContain("web");
  });

  it("reports a failure by name, says a retry is safe, and claims no success", async () => {
    vi.mocked(enrollGuest).mockRejectedValueOnce(new Error("location_id must not be empty"));
    const onEnrolled = vi.fn();
    renderDrawer({ onEnrolled });

    fillForm();
    fireEvent.click(screen.getByRole("button", { name: "Add to waitlist" }));

    // The server's own words plus a retry — never calm emptiness.
    expect(await screen.findByText("Couldn’t add this guest to the waitlist.")).toBeInTheDocument();
    expect(screen.getByText("location_id must not be empty")).toBeInTheDocument();
    // The guidance changed with the server: retrying an identical enrolment can no longer
    // duplicate the guest, so warning about a duplicate would now point at the wrong risk.
    expect(screen.getByText(/Retrying is safe/)).toBeInTheDocument();
    expect(screen.queryByText(/may already be on the list/)).toBeNull();
    expect(screen.getByRole("button", { name: /Try again/ })).toBeInTheDocument();
    expect(onEnrolled).not.toHaveBeenCalled();
  });

  it("states the offer-only consent rule instead of silently lacking auto-book", () => {
    renderDrawer();
    // Spec 11 §20: auto-book is a consent grant needing recorded evidence the console
    // cannot capture. The drawer says so rather than omitting the concept.
    expect(screen.getByText(/offer-only/)).toBeInTheDocument();
  });

  it("distinguishes an empty catalog from a missing one, and offers no submit for either", () => {
    renderDrawer({ services: [] });
    expect(screen.getByText("No bookable services")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add to waitlist" })).not.toBeInTheDocument();

    renderDrawer({ services: undefined });
    expect(screen.getByText("Loading services…")).toBeInTheDocument();
  });

  it("blocks submission when the chosen service vanishes from a refetched catalog", () => {
    // A publish can supersede items while the drawer is open, and the server accepts any
    // non-empty service_id — a stale pick would durably enrol against a retired service.
    // Nothing is silently substituted; the operator re-picks.
    const { rerender } = renderDrawer();
    fillForm();
    expect(screen.getByRole("button", { name: "Add to waitlist" })).toBeEnabled();

    rerender(
      <EnrollWaitlistDrawer
        open
        device="desktop"
        onClose={() => {}}
        onEnrolled={() => {}}
        services={[{ id: "cat_2:new", name: "New service", durationMinutes: null }]}
        knownLocations={["loc_001"]}
      />
    );

    expect(screen.getByRole("button", { name: "Add to waitlist" })).toBeDisabled();
  });

  it("shows a named, retryable error when the catalog itself failed to load", () => {
    const onRetryServices = vi.fn();
    renderDrawer({ services: undefined, servicesFailed: true, onRetryServices });

    expect(screen.getByText("Couldn’t load this venue’s services.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Try again/ }));
    expect(onRetryServices).toHaveBeenCalled();
  });
});
