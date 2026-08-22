/**
 * The three operator recovery surfaces (docs §13 group A).
 *
 * Each one is a place the console could easily tell the operator something untrue, so these
 * pin exactly those points: a replay that dispatched nothing must not read as "replayed", a
 * trace must never be fetched without an explicit request (every fetch writes an audit row),
 * and a pre-order must not convert without the customer's grant.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "../../../test/vitest-compat";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { DeadLetterReplayPanel } from "./DeadLetterReplayPanel";
import { ModelTraceViewerPanel } from "./ModelTraceViewerPanel";
import { PreorderRecoveryPanel } from "./PreorderRecoveryPanel";

function Providers({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function lastCall() {
  const calls = vi.mocked(fetch).mock.calls;
  const [url, init] = calls[calls.length - 1] as [string, RequestInit];
  return {
    url,
    init,
    headers: new Headers(init?.headers),
    body: init?.body ? JSON.parse(init.body as string) : undefined,
  };
}

/** Fills the reason dialog and confirms. Nothing is preselected, so this must always run. */
function chooseReasonAndConfirm(confirmLabel: string, reason = "Payment issue") {
  // Scoped to the dialog: the trigger that opened it carries the same label.
  const dialog = within(screen.getByRole("dialog"));
  fireEvent.click(dialog.getByRole("radio", { name: reason }));
  fireEvent.click(dialog.getByRole("button", { name: confirmLabel }));
}

/** The outcome banner, distinguished from CorrelationIdChip's copy announcer. */
async function outcome(pattern: RegExp) {
  return await screen.findByText(pattern);
}

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn().mockImplementation(() => Promise.resolve(jsonResponse({}))));
});
afterEach(() => vi.unstubAllGlobals());

// ---------------------------------------------------------------------------
// A1 — DLQ replay
// ---------------------------------------------------------------------------

describe("DeadLetterReplayPanel", () => {
  function open() {
    render(<DeadLetterReplayPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("obx_…"), { target: { value: "obx_1" } });
    fireEvent.change(screen.getByPlaceholderText("booking-command-consumer"), {
      target: { value: "booking-consumer" },
    });
  }

  it("does not pretend to list quarantined messages", () => {
    render(<DeadLetterReplayPanel />, { wrapper: Providers });
    // An empty table would assert the queue is clear — which the console cannot know.
    expect(screen.queryByRole("table")).toBeNull();
    expect(screen.getByText(/exposes no way to list quarantined messages/i)).toBeInTheDocument();
  });

  it("will not submit without both the id and the consumer", () => {
    render(<DeadLetterReplayPanel />, { wrapper: Providers });
    expect(screen.getByRole("button", { name: "Replay" })).toBeDisabled();
  });

  it("sends the reason in the BODY, not as a header", async () => {
    open();
    fireEvent.click(screen.getByRole("button", { name: "Replay" }));
    chooseReasonAndConfirm("Replay");

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    const { url, body, headers } = lastCall();
    expect(url).toBe("/api/dead-letter-messages/replay");
    expect(body.reasonCode).toBe("payment_issue");
    // This endpoint reads the reason from the body — unlike the operation follow-ups.
    expect(headers.get("X-Reason-Code")).toBeNull();
    // The audited-write spine still travels.
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
  });

  it("keeps the operator's note with the code rather than dropping it", async () => {
    open();
    fireEvent.click(screen.getByRole("button", { name: "Replay" }));
    const dialog = within(screen.getByRole("dialog"));
    fireEvent.click(dialog.getByRole("radio", { name: "Provider unavailable" }));
    fireEvent.change(dialog.getByPlaceholderText("Add context for your team…"), {
      target: { value: "provider back up at 09:15" },
    });
    fireEvent.click(dialog.getByRole("button", { name: "Replay" }));

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(lastCall().body.reasonCode).toBe("provider_unavailable: provider back up at 09:15");
  });

  it("never says 'replayed' when nothing was dispatched", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({
      decision: "AckedWithoutReadmit",
      replayCommandId: "rpl_1",
      dispatchedMessageId: null,
    }));
    open();
    fireEvent.click(screen.getByRole("button", { name: "Replay" }));
    chooseReasonAndConfirm("Replay");

    // Inv-16: a 200 covers both outcomes, and these are opposite for the operator.
    expect(await outcome(/Acknowledged without re-admitting/i)).toBeInTheDocument();
    expect(screen.getByText(/Nothing was dispatched\./i)).toBeInTheDocument();
    expect(screen.queryByText(/handed back to its consumer/i)).toBeNull();
  });

  // The two values the server ACTUALLY emits for a re-admission. `ReplayDecision` has exactly
  // three members and `Readmitted` is not one of them — this fixture used to invent it, which is
  // why the panel's `decision === 'Readmitted'` check passed CI while reporting every real
  // successful replay as an unrecognised decision.
  it.each([
    ['PreservedOriginalMessageId', /under its original id/i],
    ['NewControlMessageId', /under a new control id/i],
  ])("reports a re-admission (%s) as one, with the dispatched id", async (decision, explain) => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({
      decision,
      replayCommandId: "rpl_2",
      dispatchedMessageId: "obx_new",
    }));
    open();
    fireEvent.click(screen.getByRole("button", { name: "Replay" }));
    chooseReasonAndConfirm("Replay");

    expect(await outcome(/Re-admitted to its consumer/i)).toBeInTheDocument();
    expect(screen.getByText(explain)).toBeInTheDocument();
    // And NOT the unrecognised-decision footnote, which is what it used to show.
    expect(screen.queryByText(/does not recognise that decision/i)).toBeNull();
  });

  it("shows an unrecognised decision verbatim instead of guessing", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ decision: "SomethingNew" }));
    open();
    fireEvent.click(screen.getByRole("button", { name: "Replay" }));
    chooseReasonAndConfirm("Replay");

    expect(await outcome(/The platform answered: SomethingNew/)).toBeInTheDocument();
    expect(screen.getByText(/does not recognise that decision/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// A2 — model-trace viewer
// ---------------------------------------------------------------------------

describe("ModelTraceViewerPanel", () => {
  it("fetches NOTHING until the operator explicitly asks", () => {
    render(<ModelTraceViewerPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("trc_…"), { target: { value: "trc_1" } });
    // Every read writes an audit row, so typing, hovering and mounting must all be silent.
    expect(fetch).not.toHaveBeenCalled();

    fireEvent.mouseOver(screen.getByRole("button", { name: "Open trace" }));
    expect(fetch).not.toHaveBeenCalled();
  });

  it("collects the reason BEFORE the read, because a refusal is audited too", () => {
    render(<ModelTraceViewerPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("trc_…"), { target: { value: "trc_1" } });
    fireEvent.click(screen.getByRole("button", { name: "Open trace" }));

    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it("sends actor, role and the chosen reason", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ trace_id: "trc_1", content: "hello" }));
    render(<ModelTraceViewerPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("trc_…"), { target: { value: "trc_1" } });
    fireEvent.click(screen.getByRole("button", { name: "Open trace" }));
    chooseReasonAndConfirm("Open trace", "Guest requested change");

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    const { url, headers } = lastCall();
    expect(url).toBe("/api/model-traces/trc_1");
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBe("guest_requested_change");
  });

  it("renders a 403 as a refusal, not as an empty trace", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse(
      { title: "Forbidden", detail: "Operator role may not read traces.", status: 403 },
      403
    ));
    render(<ModelTraceViewerPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("trc_…"), { target: { value: "trc_1" } });
    fireEvent.click(screen.getByRole("button", { name: "Open trace" }));
    chooseReasonAndConfirm("Open trace");

    // An empty view would imply the trace does not exist. It is a decision about access.
    expect(await outcome(/Not shown/)).toBeInTheDocument();
    expect(screen.getByText(/Operator role may not read traces\./)).toBeInTheDocument();
    expect(screen.getByText(/audit trail either way/i)).toBeInTheDocument();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  // The server stamps [JsonPropertyName] on every member of ModelTraceView and `fetchApi` does a
  // plain JSON.parse with no key transformation, so a camelCase client type reads properties that
  // are never present. It did: `traceId`, `sessionId` and `occurredAt` were permanently undefined,
  // the session chip never rendered, the timestamp was always null, and the trace-id chip fell
  // back to the id the operator had typed — which made the read failure look like a read.
  it("renders the trace fields the server actually sends (snake_case wire shape)", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({
      trace_id: "trc_from_server", session_id: "sess_9", ts: "2026-08-06T10:00:00Z",
      kind: "tool_call", content: "body",
    }));
    render(<ModelTraceViewerPanel />, { wrapper: Providers });
    // A DIFFERENT id than the server returns, so a fallback to the typed value cannot pass.
    fireEvent.change(screen.getByPlaceholderText("trc_…"), { target: { value: "trc_typed" } });
    fireEvent.click(screen.getByRole("button", { name: "Open trace" }));
    chooseReasonAndConfirm("Open trace");

    expect(await screen.findByText("trc_from_server")).toBeInTheDocument();
    expect(screen.getByText("sess_9")).toBeInTheDocument();
  });

  it("masks the trace body until it is deliberately revealed, without refetching", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({
      trace_id: "trc_1", session_id: "sess_9", ts: "2026-08-06T10:00:00Z",
      kind: "tool_call", content: "secret-model-output",
    }));
    render(<ModelTraceViewerPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("trc_…"), { target: { value: "trc_1" } });
    fireEvent.click(screen.getByRole("button", { name: "Open trace" }));
    chooseReasonAndConfirm("Open trace");

    await screen.findByText(/Content \(as the platform stored it\)/);
    expect(screen.queryByText("secret-model-output")).not.toBeInTheDocument();

    const callsBefore = vi.mocked(fetch).mock.calls.length;
    fireEvent.click(screen.getByRole("button", { name: /reveal/i }));
    expect(screen.getByText("secret-model-output")).toBeInTheDocument();
    // Revealing is local — it must never write a second audit row.
    expect(vi.mocked(fetch).mock.calls).toHaveLength(callsBefore);
  });
});

// ---------------------------------------------------------------------------
// A3 — pre-order recovery
// ---------------------------------------------------------------------------

describe("PreorderRecoveryPanel", () => {
  it("offers nothing until a saga id is entered", () => {
    render(<PreorderRecoveryPanel />, { wrapper: Providers });
    expect(screen.getByText(/Enter a saga id to see what can be done/i)).toBeInTheDocument();
  });

  it("offers cancel on a saga id alone — cancel needs no customer agreement", () => {
    render(<PreorderRecoveryPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("pos_…"), { target: { value: "pos_1" } });
    expect(screen.getByRole("button", { name: "Cancel the pre-order" })).toBeInTheDocument();
  });

  it("will not convert straight away — it asks for the customer's agreement first", () => {
    render(<PreorderRecoveryPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("pos_…"), { target: { value: "pos_1" } });
    fireEvent.change(screen.getByPlaceholderText("cust_…"), { target: { value: "cust_1" } });

    // §21.4: the pre-order surviving a dead booking is conditional on the CUSTOMER agreeing,
    // so the button starts the verification rather than performing the conversion.
    expect(screen.getByRole("button", { name: /Get the customer’s agreement/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Convert to standalone" })).toBeNull();
  });

  it("cancel sends the reason and the operator spine, and no grant", async () => {
    render(<PreorderRecoveryPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("pos_…"), { target: { value: "pos 1" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel the pre-order" }));
    chooseReasonAndConfirm("Cancel pre-order", "Capacity conflict");

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    const { url, headers, body } = lastCall();
    expect(url).toBe("/api/preorder/sagas/pos%201/cancel");
    expect(headers.get("X-Reason-Code")).toBe("capacity_conflict");
    expect(headers.get("X-Actor")).toBeTruthy();
    // Nothing is being kept on the customer's behalf, so there is nothing to agree to.
    expect(Object.keys(body as object)).not.toContain("grant_id");
  });

  it("echoes the server's own word for what happened", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ saga_id: "pos_1", status: "cancelled" }));
    render(<PreorderRecoveryPanel />, { wrapper: Providers });
    fireEvent.change(screen.getByPlaceholderText("pos_…"), { target: { value: "pos_1" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel the pre-order" }));
    chooseReasonAndConfirm("Cancel pre-order");

    expect(await outcome(/The platform recorded: cancelled/)).toBeInTheDocument();
  });
});
