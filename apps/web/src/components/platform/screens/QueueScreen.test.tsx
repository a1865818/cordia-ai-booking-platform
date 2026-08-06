/**
 * QueueScreen — server follow-ups are offered by the row's CURRENT status only, the
 * resolve/resume forms demand what the server demands (a reason; a provider ref for
 * booked), and the local clear/dismiss row stays a plainly-labelled local acknowledgement.
 */

import { describe, expect, it, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { QueueScreen } from "./QueueScreen";
import type { QueueItem } from "../../../data/console";

function row(id: string, status: string | undefined, isPreorder = false): QueueItem {
  return {
    id,
    name: id,
    service: "New booking",
    slot: "2m ago",
    reason: "Needs your review",
    status,
    isPreorder,
    transcript: null,
  } as QueueItem;
}

const serverHandlers = () => ({
  onRequestCancellation: vi.fn(),
  onResume: vi.fn(),
  onResolve: vi.fn(),
  onRespondAlternatives: vi.fn(),
});

function renderQueue(items: QueueItem[], extra: Partial<Parameters<typeof QueueScreen>[0]> = {}) {
  return render(
    <QueueScreen
      device="desktop"
      queue={items}
      busy={null}
      onFinish={() => {}}
      onDismiss={() => {}}
      loaded
      {...extra}
    />
  );
}

describe("per-status server actions", () => {
  it("manual_review offers resume + resolve + cancellation request, and no alternatives answer", () => {
    renderQueue([row("op_mr", "manual_review")], serverHandlers());

    expect(screen.getByRole("button", { name: "Send back for checks" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resolve…" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Request cancellation" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Waitlist this guest" })).not.toBeInTheDocument();
  });

  it("alternatives_required offers the two answers and no resume/resolve", () => {
    renderQueue([row("op_alt", "alternatives_required")], serverHandlers());

    expect(screen.getByRole("button", { name: "Waitlist this guest" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Decline request" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Resolve…" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Send back for checks" })).not.toBeInTheDocument();
  });

  it("failed_final is terminal: only the local acknowledgements remain", () => {
    renderQueue([row("op_ff", "failed_final")], serverHandlers());

    // No server action can move a terminal operation — offering one would be a certain 409.
    expect(screen.queryByText("Platform actions")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear from queue" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeInTheDocument();
  });

  it("a row with no status offers no server action — no status, no claim", () => {
    renderQueue([row("op_unknown", undefined)], serverHandlers());
    expect(screen.queryByText("Platform actions")).not.toBeInTheDocument();
  });

  it("a pre-order alternatives row offers decline only, and says why", () => {
    // The server unconditionally 409s waitlisting a pre-order (spec 11 §21.4) — offering
    // the button would be a certain failure for this row.
    renderQueue([row("op_pre", "alternatives_required", true)], serverHandlers());

    expect(screen.queryByRole("button", { name: /Waitlist this guest/ })).not.toBeInTheDocument();
    expect(screen.getByText("Pre-order — can’t be waitlisted")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Decline request" })).toBeInTheDocument();
  });

  it("closes an open form when a refetch moves the row's status out from under it", () => {
    // The board refetches beneath an open form (follow-ups invalidate the query; focus
    // refetches). A resolve form kept open after manual_review -> reconciling would keep an
    // enabled submit for a call that is now certain to 409.
    const handlers = serverHandlers();
    const { rerender } = renderQueue([row("op_mr", "manual_review")], handlers);

    fireEvent.click(screen.getByRole("button", { name: "Resolve…" }));
    expect(screen.getByRole("button", { name: "Record outcome" })).toBeInTheDocument();

    rerender(
      <QueueScreen
        device="desktop"
        queue={[row("op_mr", "reconciling")]}
        busy={null}
        onFinish={() => {}}
        onDismiss={() => {}}
        loaded
        {...handlers}
      />
    );

    // The form is gone and no resolve affordance remains; the still-legal action shows instead.
    expect(screen.queryByRole("button", { name: "Record outcome" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Resolve…" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Request cancellation" })).toBeInTheDocument();
  });

  it("offers no server actions at all when the callbacks are not wired", () => {
    renderQueue([row("op_mr", "manual_review")]);
    expect(screen.queryByText("Platform actions")).not.toBeInTheDocument();
  });
});

describe("the resolve form", () => {
  it("requires a reason, and a provider reference when resolving as booked", () => {
    const handlers = serverHandlers();
    renderQueue([row("op_mr", "manual_review")], handlers);

    fireEvent.click(screen.getByRole("button", { name: "Resolve…" }));
    const record = screen.getByRole("button", { name: "Record outcome" });
    // Booked is selected but nothing is stated yet.
    expect(record).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Reason for resolving"), {
      target: { value: "checked with the venue" },
    });
    // Still disabled: booked needs the provider's own reference (the server 400s without it).
    expect(record).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Provider booking reference"), {
      target: { value: "prov_99" },
    });
    expect(record).toBeEnabled();

    fireEvent.click(record);
    expect(handlers.onResolve).toHaveBeenCalledWith("op_mr", "booked", "prov_99", "checked with the venue");
  });

  it("failed_final needs no provider reference", () => {
    const handlers = serverHandlers();
    renderQueue([row("op_mr", "manual_review")], handlers);

    fireEvent.click(screen.getByRole("button", { name: "Resolve…" }));
    fireEvent.click(screen.getByLabelText(/It failed/));
    fireEvent.change(screen.getByLabelText("Reason for resolving"), {
      target: { value: "venue refused the booking" },
    });

    fireEvent.click(screen.getByRole("button", { name: "Record outcome" }));
    expect(handlers.onResolve).toHaveBeenCalledWith("op_mr", "failed_final", undefined, "venue refused the booking");
  });
});

describe("the resume form", () => {
  it("requires the operator's reason — never defaulted", () => {
    const handlers = serverHandlers();
    renderQueue([row("op_mr", "manual_review")], handlers);

    fireEvent.click(screen.getByRole("button", { name: "Send back for checks" }));
    const confirm = screen.getByRole("button", { name: "Send back for checks" });
    expect(confirm).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Reason for sending back"), {
      target: { value: "provider line was busy, retry" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Send back for checks" }));
    expect(handlers.onResume).toHaveBeenCalledWith("op_mr", "provider line was busy, retry");
  });
});

describe("busy discipline and the local row", () => {
  it("disables every server action while one is in flight, leaving local actions alone", () => {
    const handlers = serverHandlers();
    renderQueue(
      [row("op_a", "alternatives_required"), row("op_b", "manual_review")],
      { ...handlers, serverBusyId: "op_a" }
    );

    expect(screen.getByRole("button", { name: /Waitlist this guest|Working…/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Decline request" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Resolve…" })).toBeDisabled();
    // The local acknowledgements are not server calls and stay usable.
    expect(screen.getAllByRole("button", { name: "Clear from queue" })[0]).toBeEnabled();
  });

  it("labels the local clear as local — the platform keeps working", () => {
    renderQueue([row("op_mr", "manual_review")], serverHandlers());
    expect(screen.getByTitle(/Removes this from your queue only/)).toBeInTheDocument();
  });

  it("fires the alternatives answers with the row and the answer", () => {
    const handlers = serverHandlers();
    renderQueue([row("op_alt", "alternatives_required")], handlers);

    fireEvent.click(screen.getByRole("button", { name: "Waitlist this guest" }));
    expect(handlers.onRespondAlternatives).toHaveBeenCalledWith("op_alt", "waitlist");

    fireEvent.click(screen.getByRole("button", { name: "Decline request" }));
    expect(handlers.onRespondAlternatives).toHaveBeenCalledWith("op_alt", "decline");
  });

  it("fires the cancellation request for a non-terminal row", () => {
    const handlers = serverHandlers();
    renderQueue([row("op_rc", "reconciling")], handlers);

    fireEvent.click(screen.getByRole("button", { name: "Request cancellation" }));
    expect(handlers.onRequestCancellation).toHaveBeenCalledWith("op_rc");
  });
});
