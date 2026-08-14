/**
 * The page-level regression test.
 *
 * Every defect the last three audits found — the frozen `9-15 June` week, the invented queue
 * wait times, the silently-empty transcript, the fabricated shell identity, the mock service
 * ids written into durable bookings — was invisible to CI for the same reason: **nothing
 * rendered `ConsolePage`**. 300+ tests covered adapters, hashing, mutations and three isolated
 * components, and not one composed them.
 *
 * So this file pins the two rules `docs/frontend-api-wiring.md` §11 sets for the console, at
 * the level where they are actually violated:
 *
 *   1. A successful-but-empty response is a real answer.
 *   2. Nothing is asserted that the API does not record.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router";

vi.mock("../features/bookings/api", () => ({
  getBookingSchedule: vi.fn(),
  getBookingDetails: vi.fn(),
  getOperationStatus: vi.fn(),
  recordAttendance: vi.fn(),
}));
vi.mock("../features/operations/api", async (importOriginal) => ({
  // `statusFromResponse` stays real: the follow-up toasts echo what it reads from the body.
  ...(await importOriginal<typeof import("../features/operations/api")>()),
  listOperations: vi.fn(),
  listOpenWorkItems: vi.fn(),
  requestOperationCancellation: vi.fn(),
  resumeOperation: vi.fn(),
  resolveOperation: vi.fn(),
  respondToAlternatives: vi.fn(),
}));
// Both spread the original: `preflight` re-exports `refusalMessage`, and `submit` still owns
// `submitCancellation` for the cancel path — stubbing the whole module would break either.
vi.mock("../features/bookings/preflight", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/bookings/preflight")>()),
  preflightReschedule: vi.fn(),
}));
vi.mock("../features/bookings/submit", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/bookings/submit")>()),
  submitBooking: vi.fn(),
}));
vi.mock("../features/conversations/api", () => ({ listSessions: vi.fn(), getTranscript: vi.fn() }));
vi.mock("../features/customers/api", () => ({ listCustomers: vi.fn(), getCustomer: vi.fn(), setCustomerNotes: vi.fn() }));
vi.mock("../features/waitlist/api", () => ({
  listEnrollments: vi.fn(), cancelEnrollment: vi.fn(), acceptOffer: vi.fn(), declineOffer: vi.fn(),
  enrollGuest: vi.fn(), listWaitlistLocations: vi.fn(),
}));
vi.mock("../features/settings/api", () => ({ listCatalogItems: vi.fn(), getConsentPolicy: vi.fn() }));
vi.mock("../features/reporting/api", async (importOriginal) => ({
  // `rangeToWindow` is pure date maths the screen needs; only the fetchers are stubbed.
  ...(await importOriginal<typeof import("../features/reporting/api")>()),
  getBookingFunnel: vi.fn(), getBookingOverview: vi.fn(), getKpis: vi.fn(),
}));
vi.mock("../features/settings/profile", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/settings/profile")>()),
  getTenantProfile: vi.fn(), setTenantProfile: vi.fn(),
}));
vi.mock("../features/settings/hours", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/settings/hours")>()),
  getOperatingHours: vi.fn(), setOperatingHours: vi.fn(),
}));
vi.mock("../features/resources/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/resources/api")>()),
  listStaffResources: vi.fn(), createStaffResource: vi.fn(),
}));
vi.mock("../features/campaigns/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/campaigns/api")>()),
  listCampaigns: vi.fn(),
}));
vi.mock("../features/deposits/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/deposits/api")>()),
  listDeposits: vi.fn(), getPayoutSummary: vi.fn(),
}));
vi.mock("../features/settings/guardrails", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/settings/guardrails")>()),
  getGuardrails: vi.fn(),
}));
vi.mock("../features/settings/knowledge", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/settings/knowledge")>()),
  listKnowledge: vi.fn(),
}));
vi.mock("../features/settings/depositPolicy", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../features/settings/depositPolicy")>()),
  getDepositPolicy: vi.fn(),
}));

import { ConsolePage } from "./ConsolePage";
import { getBookingDetails, getBookingSchedule, getOperationStatus } from "../features/bookings/api";
import { preflightReschedule } from "../features/bookings/preflight";
import { submitBooking } from "../features/bookings/submit";
import {
  listOpenWorkItems, listOperations, requestOperationCancellation, resolveOperation,
  respondToAlternatives,
} from "../features/operations/api";
import { listSessions } from "../features/conversations/api";
import { listCustomers } from "../features/customers/api";
import { listEnrollments, listWaitlistLocations } from "../features/waitlist/api";
import { listCatalogItems } from "../features/settings/api";
import { getBookingFunnel, getBookingOverview, getKpis } from "../features/reporting/api";
import { getTenantProfile } from "../features/settings/profile";
import { getOperatingHours } from "../features/settings/hours";
import { listStaffResources } from "../features/resources/api";
import { listCampaigns } from "../features/campaigns/api";
import { getPayoutSummary, listDeposits } from "../features/deposits/api";
import { getGuardrails } from "../features/settings/guardrails";
import { listKnowledge } from "../features/settings/knowledge";
import { getDepositPolicy } from "../features/settings/depositPolicy";

function Providers({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

/** Every list resolves successfully and empty — the "fresh tenant" case. */
function allEmpty() {
  vi.mocked(getBookingSchedule).mockResolvedValue({ bookings: [], truncated: false } as never);
  vi.mocked(listOperations).mockResolvedValue({ operations: [] } as never);
  // The board's SECOND request. Left unmocked it fails in jsdom, and the queue then reports
  // incomplete recovery data on every test — which is the point of wiring it into `loadFailures`.
  vi.mocked(listOpenWorkItems).mockResolvedValue({ work_items: [] } as never);
  vi.mocked(listSessions).mockResolvedValue({ sessions: [] } as never);
  vi.mocked(listCustomers).mockResolvedValue({ customers: [], truncated: false } as never);
  vi.mocked(listEnrollments).mockResolvedValue({ enrollments: [] } as never);
  vi.mocked(listWaitlistLocations).mockResolvedValue({ locations: [] } as never);
  vi.mocked(listCatalogItems).mockResolvedValue([] as never);
  vi.mocked(getBookingFunnel).mockResolvedValue({
    accepted: 0, confirmed: 0, notified: 0,
  } as never);
  vi.mocked(getBookingOverview).mockResolvedValue({
    per_day: [], channels: [], top_services: [], breakdown_truncated: false,
  } as never);
  vi.mocked(getKpis).mockResolvedValue({
    kpis: [
      { key: "bookings", value: null, unit: "count", delta: null, up: null, available: false },
      { key: "noshow", value: null, unit: "percent", delta: null, up: null, available: false },
      { key: "ai", value: null, unit: "percent", delta: null, up: null, available: false },
      { key: "rev", value: null, unit: "minor", delta: null, up: null, available: false },
    ],
    handled: null,
  } as never);
  vi.mocked(getTenantProfile).mockResolvedValue({
    tenant_id: "t1", display_name: "", business_type: null, waitlist_auto_book: true,
  } as never);
  vi.mocked(getOperatingHours).mockResolvedValue({
    hours: ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map(d => ({
      d, open: "09:00", close: "18:00", on: true,
    })),
  } as never);
  vi.mocked(listStaffResources).mockResolvedValue({ resources: [] } as never);
  vi.mocked(listCampaigns).mockResolvedValue({ campaigns: [] } as never);
  vi.mocked(listDeposits).mockResolvedValue({ deposits: [] } as never);
  vi.mocked(getPayoutSummary).mockResolvedValue({
    held_minor: 0, available_minor: 0, currency: "AUD",
    next_date: null, next_amount_minor: null, account: "Not connected",
  } as never);
  vi.mocked(getGuardrails).mockResolvedValue({ guardrails: [] } as never);
  vi.mocked(listKnowledge).mockResolvedValue({ entries: [] } as never);
  vi.mocked(getDepositPolicy).mockResolvedValue({
    require: false, amount_policy: "", no_show_only: false, refund_window: "",
  } as never);
}

function renderConsole() {
  return render(<ConsolePage />, { wrapper: Providers });
}

/**
 * Navigates to a screen and waits past `go()`'s 540 ms skeleton.
 *
 * That skeleton is a deliberate fake latency on screen transitions (unrelated to any request),
 * so a naive assertion right after the click sees only `LoadingSkeletons`.
 */
async function goToScreen(name: RegExp) {
  fireEvent.click(screen.getByRole("button", { name }));
  await waitFor(
    () => expect(screen.queryByRole("button", { name })?.getAttribute("aria-current")).toBe("page"),
    { timeout: 3000 }
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  window.APP_CONFIG = undefined;
  // jsdom implements no media queries, and `useDevice` / the sidebar collapse both read one.
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  }));
  allEmpty();
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.APP_CONFIG = undefined;
});

describe("ConsolePage — shell identity", () => {
  it("shows the actor and tenant actually sent, not a hard-coded person or venue", async () => {
    window.APP_CONFIG = { ACTOR: "front_desk", ACTOR_ROLE: "manager", TENANT_ID: "bella" };
    renderConsole();

    // Three independent copies of these literals used to live in Sidebar, Header and
    // MoreScreen. No endpoint has ever returned either.
    expect(screen.queryByText("Maya Chen")).not.toBeInTheDocument();
    expect(screen.queryByText(/Bella Nails/)).not.toBeInTheDocument();

    await waitFor(() => expect(screen.getByText("front_desk")).toBeInTheDocument());
    expect(screen.getByText("bella · manager")).toBeInTheDocument();
    // The workspace label shows the tenant and is no longer a button: there is no switcher,
    // and an affordance that opens nothing claims a capability the console does not have.
    expect(screen.queryByRole("button", { name: /switch workspace/i })).not.toBeInTheDocument();
  });
});

describe("ConsolePage — rule 1: a successful-but-empty response is a real answer", () => {
  it("renders an empty week rather than demo bookings when the schedule is genuinely empty", async () => {
    renderConsole();
    await waitFor(() => expect(getBookingSchedule).toHaveBeenCalled());

    // The mock `BOOKINGS` rows carry these guest names. None may appear for an empty tenant.
    expect(screen.queryByText(/Sarah Kim/)).not.toBeInTheDocument();
    expect(screen.queryByText("Demo data")).not.toBeInTheDocument();
  });

  it("does not claim partial results for a complete empty answer", async () => {
    renderConsole();
    await waitFor(() => expect(getBookingSchedule).toHaveBeenCalled());
    expect(screen.queryByText("Partial results")).not.toBeInTheDocument();
  });

  it("marks a truncated schedule as partial", async () => {
    vi.mocked(getBookingSchedule).mockResolvedValue({ bookings: [], truncated: true } as never);
    renderConsole();
    await waitFor(() => expect(screen.getByText("Partial results")).toBeInTheDocument());
  });
});

describe("ConsolePage — a failed load is named, never rendered as an empty screen", () => {
  it("names the failing list instead of showing a confident empty week", async () => {
    vi.mocked(getBookingSchedule).mockRejectedValue(new Error("boom"));
    renderConsole();

    await waitFor(() =>
      expect(screen.getByText(/Couldn’t load today’s bookings/)).toBeInTheDocument()
    );
  });

  it("names every failing list, not just the first", async () => {
    vi.mocked(getBookingSchedule).mockRejectedValue(new Error("boom"));
    vi.mocked(listCustomers).mockRejectedValue(new Error("boom"));
    renderConsole();

    await waitFor(() =>
      expect(screen.getByText(/Couldn’t load today’s bookings and customers/)).toBeInTheDocument()
    );
  });

  /**
   * The work items are a SECOND request behind the same board.
   *
   * When it alone failed the queue rendered as fully loaded while every escalation quietly lost its
   * reason, SLA deadline and assignee — the `workItem` block simply did not appear, with no error
   * and no retry path. A partially-answered queue has to say so, exactly like a wholly
   * unanswered one.
   */
  it("names the recovery-queue details when only that request fails", async () => {
    vi.mocked(listOpenWorkItems).mockRejectedValue(new Error("boom"));
    renderConsole();

    await waitFor(() =>
      expect(screen.getByText(/Couldn’t load the recovery queue’s details/)).toBeInTheDocument()
    );
  });
});

describe("ConsolePage — rule 2: nothing is asserted that the API does not record", () => {
  it("paints no campaign badge when nothing is awaiting approval", async () => {
    renderConsole();
    await waitFor(() => expect(listOperations).toHaveBeenCalled());

    const campaigns = screen.getByRole("button", { name: /campaigns/i });
    expect(campaigns.textContent).not.toMatch(/\b1\b/);
  });

  it("names no resource on the schedule — the platform models none", async () => {
    function middayToday() {
      const d = new Date();
      d.setHours(12, 0, 0, 0);
      return d.toISOString();
    }

    vi.mocked(getBookingSchedule).mockResolvedValue({
      bookings: [{
        operation_id: "op_1", booking_id: "bk_1", booking_reference: "REF1",
        // Local midday today, NOT now+1h: after 23:00 local, now+1h lands on TOMORROW and the
        // booking falls outside today's lane, so this test failed purely by the hour it ran at.
        starts_at: middayToday(),
        service_id: "itm_gel", customer_ref: "cust_1", party_size: 2,
        duration_minutes: 45, lifecycle_status: "confirmed", operation_status: "booked",
      }],
      truncated: false,
    } as never);
    renderConsole();

    // Positive assertion first, so the absences below cannot pass because nothing rendered.
    await waitFor(() => expect(screen.getAllByText(/cust_1/).length).toBeGreaterThan(0));

    // The grid drew four invented technicians and stacked every live booking into lane one.
    for (const invented of ["Linh Pham", "Trang Vo", "Mai Dang", "Tony Bui"]) {
      expect(screen.queryByText(new RegExp(invented))).not.toBeInTheDocument();
    }
  });

  it("does not say the queue is clear before the queue has answered", async () => {
    // "Nothing needs you right now" is styled as good news and tells the operator the agent has
    // everything in hand. Both lists were seeded `[]` and only filled from a `data` effect, so
    // that reassurance rendered before the first fetch — and stayed rendered after a FAILED one,
    // directly under the page's own "Couldn't load the queue" banner.
    let resolveOps: (v: unknown) => void = () => {};
    vi.mocked(listOperations).mockReturnValue(
      new Promise(resolve => { resolveOps = resolve; }) as never
    );
    renderConsole();
    await goToScreen(/needs action/i);

    await waitFor(() => expect(screen.getByText("The queue hasn’t loaded")).toBeInTheDocument(),
      { timeout: 3000 });
    expect(screen.queryByText("Nothing needs you right now")).not.toBeInTheDocument();

    resolveOps({ operations: [] });
    await waitFor(() =>
      expect(screen.getByText("Nothing needs you right now")).toBeInTheDocument(),
      { timeout: 3000 }
    );
  });

  it("does not claim the queue is clear when the queue failed to load", async () => {
    vi.mocked(listOperations).mockRejectedValue(new Error("boom"));
    renderConsole();
    await goToScreen(/needs action/i);

    await waitFor(() => expect(screen.getByText(/Couldn’t load the queue/)).toBeInTheDocument(),
      { timeout: 3000 });
    // Positive assertion: the queue screen really is on screen (past `go()`'s 540 ms skeleton,
    // which renders BELOW the error banner), so the absence below is real and not vacuous.
    await waitFor(() => expect(screen.getByText("The queue hasn’t loaded")).toBeInTheDocument(),
      { timeout: 3000 });
    // The reassurance must never sit underneath the banner that contradicts it.
    expect(screen.queryByText("Nothing needs you right now")).not.toBeInTheDocument();
  });

  it("labels the analytics header with the window actually selected", async () => {
    renderConsole();
    await goToScreen(/analytics/i);

    // Wait for the screen CONTENT (past `go()`'s 540 ms skeleton), not just the header eyebrow —
    // the eyebrow renders outside the loading branch and would resolve immediately.
    await waitFor(() => expect(screen.getByRole("button", { name: "90 days" })).toBeInTheDocument(),
      { timeout: 3000 });
    expect(screen.getByText("Last 7 days")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "90 days" }));
    // The eyebrow was the static literal "Last 7 days", so it contradicted the live figures
    // underneath it the moment the operator changed the window.
    await waitFor(() => expect(screen.getByText("Last 90 days")).toBeInTheDocument(),
      { timeout: 3000 });
    expect(screen.queryByText("Last 7 days")).not.toBeInTheDocument();
  });

  it("does not say 'No bookings' before the schedule has answered", async () => {
    // Same rule Queue and Waitlist hold. `bookings` is seeded to an empty array per week key and
    // only replaced once `scheduleQuery.data` arrives, so the grid confidently reported an empty
    // day for the whole cold-load window — and after a failed load, under the error banner.
    let resolveSchedule: (v: unknown) => void = () => {};
    vi.mocked(getBookingSchedule).mockReturnValue(
      new Promise(resolve => { resolveSchedule = resolve; }) as never
    );
    renderConsole();

    await waitFor(() => expect(screen.getAllByText("Not loaded yet").length).toBeGreaterThan(0));
    expect(screen.queryByText("No bookings")).not.toBeInTheDocument();

    resolveSchedule({ bookings: [], truncated: false });
    await waitFor(() => expect(screen.getAllByText("No bookings").length).toBeGreaterThan(0));
  });

  it("does not label a day Closed when operating hours have the day open", async () => {
    renderConsole();
    await waitFor(() => expect(getBookingSchedule).toHaveBeenCalled());
    expect(screen.queryByText("Closed")).not.toBeInTheDocument();
  });

  it("labels the selected day Closed when operating hours say it is", async () => {
    vi.mocked(getOperatingHours).mockResolvedValue({
      hours: ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map(d => ({
        d, open: "09:00", close: "18:00", on: false,
      })),
    } as never);
    renderConsole();
    await waitFor(() => expect(screen.getByText("Closed")).toBeInTheDocument());
  });

  it("does not report a 'clients' count it never computed", async () => {
    renderConsole();
    await waitFor(() => expect(getBookingSchedule).toHaveBeenCalled());
    // The tile rendered the booking count under a second name, asserting a distinct fact
    // (how many separate guests) that was never derived.
    expect(screen.queryByText("clients")).not.toBeInTheDocument();
  });

  it("shows a booking that falls outside the mock operating hours", async () => {
    // The grid framed its columns from `dayBounds()` (9am-6pm from a hand-written constant), so a
    // real 20:30 booking was positioned outside the rendered window and could not be seen at all.
    // A schedule that silently omits a booking is how the double-booking defect happened before.
    // Today's column, because that is the one the grid opens on.
    const lateToday = new Date();
    lateToday.setHours(20, 30, 0, 0);

    vi.mocked(getBookingSchedule).mockResolvedValue({
      bookings: [{
        operation_id: "op_late", booking_id: "bk_late", booking_reference: "LATE1",
        starts_at: lateToday.toISOString(), service_id: "itm_gel", customer_ref: "cust_late",
        party_size: 2, duration_minutes: 45,
        lifecycle_status: "confirmed", operation_status: "booked",
      }],
      truncated: false,
    } as never);

    renderConsole();
    await waitFor(() => expect(screen.getAllByText(/cust_late/).length).toBeGreaterThan(0));
  });

  it("states no amount of money anywhere on the default screen", async () => {
    renderConsole();
    await waitFor(() => expect(getBookingSchedule).toHaveBeenCalled());
    // spec 09 has shipped no payment surface; the platform has never held or requested a cent.
    expect(screen.queryByText(/A\$/)).not.toBeInTheDocument();
  });
});

describe("ConsolePage — operator follow-ups are wired end to end", () => {
  const operation = (overrides: Record<string, unknown>) => ({
    operation_id: "op_1", intent_id: "int_1", intent_revision: 1, is_preorder: false,
    operation_type: "create", status: "manual_review", status_reason: "Provider timed out",
    booking_id: null, correlation_id: "corr_1",
    created_at: new Date().toISOString(), updated_at: new Date().toISOString(),
    ...overrides,
  });

  it("resolves a manual_review row and reports exactly what the server answered", async () => {
    vi.mocked(listOperations).mockResolvedValue({
      operations: [operation({ operation_id: "op_mr" })],
    } as never);
    vi.mocked(resolveOperation).mockResolvedValue({ operation_id: "op_mr", status: "booked" });
    renderConsole();
    await goToScreen(/needs action/i);

    await waitFor(() => expect(screen.getByRole("button", { name: "Resolve…" })).toBeInTheDocument(),
      { timeout: 3000 });
    fireEvent.click(screen.getByRole("button", { name: "Resolve…" }));
    fireEvent.change(screen.getByLabelText("Provider booking reference"), {
      target: { value: "prov_9" },
    });
    fireEvent.change(screen.getByLabelText("Reason for resolving"), {
      target: { value: "confirmed by phone" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Record outcome" }));

    await waitFor(() => expect(resolveOperation).toHaveBeenCalledWith(
      "op_mr", { resolution: "booked", providerRef: "prov_9" }, "confirmed by phone"
    ));
    // The toast echoes the server's status, not the operator's intent.
    await waitFor(() =>
      expect(screen.getByText(/Recorded as booked/)).toBeInTheDocument(), { timeout: 3000 });
  });

  it("offers a pre-order alternatives row decline only, and the decline round-trips", async () => {
    vi.mocked(listOperations).mockResolvedValue({
      operations: [operation({
        operation_id: "op_pre", status: "alternatives_required", is_preorder: true,
        status_reason: "No slot available",
      })],
    } as never);
    vi.mocked(respondToAlternatives).mockResolvedValue({ operation_id: "op_pre", status: "failed_final" });
    renderConsole();
    await goToScreen(/needs action/i);

    // The server refuses to waitlist a pre-order — the button must not exist, and the row says why.
    await waitFor(() =>
      expect(screen.getByText("Pre-order — can’t be waitlisted")).toBeInTheDocument(),
      { timeout: 3000 });
    expect(screen.queryByRole("button", { name: /Waitlist this guest/ })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Decline request" }));
    await waitFor(() => expect(respondToAlternatives).toHaveBeenCalledWith("op_pre", "decline"));
    await waitFor(() =>
      expect(screen.getByText(/Closed — the request was declined/)).toBeInTheDocument(),
      { timeout: 3000 });
  });

  it("reports a cancellation request as a request, and a failed one by name", async () => {
    vi.mocked(listOperations).mockResolvedValue({
      operations: [operation({ operation_id: "op_rc", status: "reconciling" })],
    } as never);
    vi.mocked(requestOperationCancellation)
      .mockResolvedValueOnce({ operation_id: "op_rc", cancellation_pending: true })
      .mockRejectedValueOnce(Object.assign(new Error("conflict"), { status: 409, name: "ApiError" }));
    renderConsole();
    await goToScreen(/needs action/i);

    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Request cancellation" })).toBeInTheDocument(),
      { timeout: 3000 });
    fireEvent.click(screen.getByRole("button", { name: "Request cancellation" }));

    // 202 `cancellation_pending` is a request accepted, not an outcome — the toast says so.
    await waitFor(() =>
      expect(screen.getByText(/Cancellation requested/)).toBeInTheDocument(), { timeout: 3000 });
  });

  it("opens the enrol drawer from the waitlist screen's primary action", async () => {
    renderConsole();
    // Exact match: once the screen opens, the header's "Add to waitlist" also contains
    // the word — a loose /waitlist/i would match both buttons.
    await goToScreen(/^waitlist$/i);

    await waitFor(() =>
      expect(screen.getByRole("button", { name: /Add to waitlist/ })).toBeInTheDocument(),
      { timeout: 3000 });
    fireEvent.click(screen.getByRole("button", { name: /Add to waitlist/ }));

    // The drawer is the real enrolment surface; its title proves the wiring, not a toast.
    await waitFor(() => expect(screen.getByText("Add a guest")).toBeInTheDocument());
  });
});

describe("ConsolePage — a refused reschedule puts the booking back", () => {
  /**
   * The diagram bar. Its `aria-label` carries the start time, which is exactly what a move
   * changes — so the label is the assertion for "which slot is this booking drawn in".
   */
  const bar = () => screen.getAllByRole("button", { name: /^cust_move — / })[0];

  it("reverts the optimistic move when the platform terminally declines it", async () => {
    // Noon, so a one-hour move stays well inside the grid's rendered window.
    const noon = new Date();
    noon.setHours(12, 0, 0, 0);

    // The FIRST schedule read answers; every refetch after it hangs.
    //
    // That is the window the defect lived in. `invalidateBookingViews()` is not a revert — it
    // marks the query stale, and local state changes only when the refetch lands. Pinning the
    // refetch open is what makes this test able to tell a real revert from a refetch that
    // happens to put the booking back, and it is also the real failure mode: an operator on a
    // flaky connection would have been left staring at the booking in the refused slot.
    vi.mocked(getBookingSchedule)
      .mockResolvedValueOnce({
        bookings: [{
          operation_id: "op_move", booking_id: "bk_move", booking_reference: "MOVE1",
          starts_at: noon.toISOString(), service_id: "itm_gel", customer_ref: "cust_move",
          party_size: 2, duration_minutes: 60,
          lifecycle_status: "confirmed", operation_status: "booked",
        }],
        truncated: false,
      } as never)
      .mockImplementation(() => new Promise(() => {}) as never);
    vi.mocked(getBookingDetails).mockResolvedValue({
      booking: { version: 4, party_size: 2 },
    } as never);
    vi.mocked(preflightReschedule).mockResolvedValue(null);
    vi.mocked(submitBooking).mockResolvedValue({ operation_id: "op_rs" } as never);
    // Accepted (202), then terminally refused — the case the operator sees as "Change: declined".
    vi.mocked(getOperationStatus).mockResolvedValue({
      operation_id: "op_rs", status: "rejected", terminal: true,
    } as never);

    renderConsole();
    await waitFor(() => expect(bar()).toBeInTheDocument());
    const original = bar().getAttribute("aria-label");

    // Drag it an hour later: the grid is 68 px per 15 minutes.
    fireEvent.mouseDown(bar(), { clientX: 100, clientY: 20 });
    fireEvent.mouseMove(window, { clientX: 100 + 4 * 68, clientY: 20 });
    fireEvent.mouseUp(window);

    // The optimistic move lands immediately — that is what makes dragging feel direct.
    expect(bar().getAttribute("aria-label")).not.toBe(original);

    await waitFor(() => expect(screen.getByText(/Change: declined/)).toBeInTheDocument(),
      { timeout: 3000 });
    // And the refusal puts it back, without waiting on a refetch that is never going to land.
    await waitFor(() => expect(bar().getAttribute("aria-label")).toBe(original));
  });
});
