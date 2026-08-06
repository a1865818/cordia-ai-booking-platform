/**
 * Design-system components (docs §13 phase 1).
 *
 * These are consumed by the operator surfaces, where every one of them is load-bearing for
 * whether the operator is told the truth. So the tests pin the *rules*, not the markup:
 * nothing preselected on an audited reason, illegal actions absent rather than disabled, a
 * halted lifecycle never drawn as in-progress, and absent values never rendered as blank.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ReasonCodeDialog } from "./ReasonCodeDialog";
import { CorrelationIdChip } from "./CorrelationIdChip";
import { Timeline } from "./Timeline";
import { MaskedField, maskValue } from "./MaskedField";
import { ActionBar } from "./ActionBar";
import { DiffView, summarise } from "./DiffView";
import { Stepper } from "./Stepper";

// ---------------------------------------------------------------------------
// ReasonCodeDialog
// ---------------------------------------------------------------------------

describe("ReasonCodeDialog", () => {
  function open(onConfirm = vi.fn(), onCancel = vi.fn(), props = {}) {
    render(
      <ReasonCodeDialog
        open title="Cancel" confirmLabel="Confirm"
        onConfirm={onConfirm} onCancel={onCancel} {...props}
      />
    );
    return { onConfirm, onCancel };
  }

  it("offers the canonical app-wide vocabulary plus Other", () => {
    open();
    // A per-screen vocabulary would make the audit trail unqueryable.
    for (const label of [
      "Provider unavailable", "Guest requested change", "Duplicate booking",
      "Payment issue", "Capacity conflict", "Other",
    ]) {
      expect(screen.getByRole("radio", { name: label })).toBeInTheDocument();
    }
  });

  it("preselects nothing and refuses to confirm until a reason is chosen", () => {
    open();
    // A default reason would put the same sentence on every audit row, recording nothing.
    for (const radio of screen.getAllByRole("radio")) {
      expect(radio).toHaveAttribute("aria-checked", "false");
    }
    expect(screen.getByRole("button", { name: "Confirm" })).toBeDisabled();
  });

  it("says where the reason goes, in the design's words", () => {
    open();
    expect(screen.getByText("Pick a reason — it's recorded on the audit trail.")).toBeInTheDocument();
  });

  it("passes the chosen CODE and the note through separately", () => {
    const { onConfirm } = open();
    fireEvent.click(screen.getByRole("radio", { name: "Payment issue" }));
    fireEvent.change(screen.getByPlaceholderText("Add context for your team…"), {
      target: { value: "card declined twice" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    // The code is the queryable dimension; the note is the context it cannot carry.
    expect(onConfirm).toHaveBeenCalledWith("payment_issue", "card declined twice");
  });

  it("requires a note when the reason is Other — 'other' alone records nothing", () => {
    const { onConfirm } = open();
    fireEvent.click(screen.getByRole("radio", { name: "Other" }));
    // Reaching for Other means the named codes did not fit; the note IS the reason.
    expect(screen.getByRole("button", { name: "Confirm" })).toBeDisabled();

    fireEvent.change(screen.getByPlaceholderText(/Required — describe what happened/), {
      target: { value: "consumer redeployed with the mapping fix" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    expect(onConfirm).toHaveBeenCalledWith("other", "consumer redeployed with the mapping fix");
  });

  it("keeps the note optional for the NAMED codes", () => {
    const { onConfirm } = open();
    fireEvent.click(screen.getByRole("radio", { name: "Duplicate booking" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    expect(onConfirm).toHaveBeenCalledWith("duplicate_booking", "");
  });

  it("marks the reason group required for assistive tech", () => {
    open();
    expect(screen.getByRole("radiogroup")).toHaveAttribute("aria-required", "true");
  });

  it("is a labelled modal dialog and cancels on Escape", () => {
    const { onCancel } = open();
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveAccessibleName("Cancel");
    expect(dialog).toHaveAttribute("aria-modal", "true");
    fireEvent.keyDown(window, { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
  });

  it("cannot be dismissed while the write is in flight", () => {
    const onCancel = vi.fn();
    render(
      <ReasonCodeDialog open busy title="Cancel" confirmLabel="Confirm"
        onConfirm={vi.fn()} onCancel={onCancel} />
    );
    fireEvent.keyDown(window, { key: "Escape" });
    // Dismissing mid-flight strands a write whose outcome the operator never sees.
    expect(onCancel).not.toHaveBeenCalled();
  });

  it("forgets the previous action's reason when reopened", () => {
    const { rerender } = render(
      <ReasonCodeDialog open title="Cancel" confirmLabel="Confirm"
        onConfirm={vi.fn()} onCancel={vi.fn()} />
    );
    fireEvent.click(screen.getByRole("radio", { name: "Payment issue" }));
    rerender(<ReasonCodeDialog open={false} title="Cancel" confirmLabel="Confirm"
      onConfirm={vi.fn()} onCancel={vi.fn()} />);
    rerender(<ReasonCodeDialog open title="Cancel" confirmLabel="Confirm"
      onConfirm={vi.fn()} onCancel={vi.fn()} />);
    // Carrying a reason across actions would attribute it to the wrong one.
    expect(screen.getByRole("button", { name: "Confirm" })).toBeDisabled();
  });
});

// ---------------------------------------------------------------------------
// CorrelationIdChip
// ---------------------------------------------------------------------------

describe("CorrelationIdChip", () => {
  const LONG = "corr_0123456789abcdef0123456789abcdef";

  beforeEach(() => {
    vi.stubGlobal("navigator", {
      clipboard: { writeText: vi.fn().mockResolvedValue(undefined) },
    });
  });
  afterEach(() => vi.unstubAllGlobals());

  it("elides for display but copies the WHOLE id", async () => {
    render(<CorrelationIdChip value={LONG} label="correlation" />);
    // Truncated on screen...
    expect(screen.getByTitle(LONG).textContent).not.toBe(LONG);
    expect(screen.getByTitle(LONG).textContent).toContain("…");

    fireEvent.click(screen.getByRole("button", { name: /copy/i }));
    // ...but a partial id is useless to whoever reads the logs.
    await waitFor(() => {
      expect(navigator.clipboard.writeText).toHaveBeenCalledWith(LONG);
    });
  });

  it("shows a short id in full rather than eliding it pointlessly", () => {
    render(<CorrelationIdChip value="op_12" label="op" />);
    expect(screen.getByTitle("op_12").textContent).toBe("op_12");
  });

  it("announces the copy without stealing focus", async () => {
    render(<CorrelationIdChip value={LONG} label="correlation" />);
    fireEvent.click(screen.getByRole("button", { name: /copy/i }));
    await waitFor(() => {
      expect(screen.getByRole("status")).toHaveTextContent(/copied to clipboard/i);
    });
  });

  it("does not claim success when the clipboard is unavailable", async () => {
    vi.stubGlobal("navigator", {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) },
    });
    render(<CorrelationIdChip value={LONG} label="correlation" />);
    fireEvent.click(screen.getByRole("button", { name: /copy/i }));
    await waitFor(() => {
      expect(screen.getByRole("status")).toHaveTextContent("");
    });
    // The full value stays reachable by hand.
    expect(screen.getByTitle(LONG)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Timeline
// ---------------------------------------------------------------------------

describe("Timeline", () => {
  it("renders only what it was given — no invented pending tail", () => {
    render(
      <Timeline
        entries={[
          { id: "a", title: "Accepted", at: "2026-08-01T10:00:00Z", tone: "done" },
          { id: "b", title: "Reserving capacity", at: "2026-08-01T10:00:05Z", tone: "active" },
        ]}
      />
    );
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
    expect(screen.queryByText(/pending/i)).not.toBeInTheDocument();
  });

  it("shows an event with no timestamp rather than inventing one", () => {
    render(<Timeline entries={[{ id: "a", title: "Quarantined", at: null }]} />);
    expect(screen.getByText("Quarantined")).toBeInTheDocument();
    expect(document.querySelector("time")).toBeNull();
  });

  it("renders an unparseable instant verbatim, never as Invalid Date", () => {
    render(<Timeline entries={[{ id: "a", title: "X", at: "not-a-date" }]} />);
    expect(screen.getByText("not-a-date")).toBeInTheDocument();
    expect(screen.queryByText(/Invalid Date/)).not.toBeInTheDocument();
  });

  it("treats an empty history as a real answer", () => {
    render(<Timeline entries={[]} emptyLabel="No events recorded." />);
    expect(screen.getByText("No events recorded.")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// MaskedField
// ---------------------------------------------------------------------------

describe("MaskedField", () => {
  it("masks by default and reveals only when explicitly allowed", () => {
    const { rerender } = render(<MaskedField label="Phone" value="+61400111222" />);
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.queryByText("+61400111222")).not.toBeInTheDocument();

    rerender(<MaskedField label="Phone" value="+61400111222" revealable />);
    fireEvent.click(screen.getByRole("button", { name: /reveal/i }));
    expect(screen.getByText("+61400111222")).toBeInTheDocument();
  });

  it("distinguishes absent from empty instead of rendering blank", () => {
    render(<MaskedField label="Email" value={null} emptyLabel="Not recorded" />);
    expect(screen.getByText("Not recorded")).toBeInTheDocument();
  });

  it("shows the server's redaction verbatim — it cannot un-redact", () => {
    render(<MaskedField label="Note" value="[REDACTED]" revealable />);
    fireEvent.click(screen.getByRole("button", { name: /reveal/i }));
    expect(screen.getByText("[REDACTED]")).toBeInTheDocument();
  });

  it("exposes reveal state to assistive tech", () => {
    render(<MaskedField label="Phone" value="+61400111222" revealable />);
    const toggle = screen.getByRole("button", { name: /reveal/i });
    expect(toggle).toHaveAttribute("aria-pressed", "false");
    fireEvent.click(toggle);
    expect(screen.getByRole("button", { name: /hide/i })).toHaveAttribute("aria-pressed", "true");
  });

  it("masks a short value entirely rather than leaking most of it", () => {
    // Keeping the last 4 of a 5-char value would expose it.
    expect(maskValue("12345")).toBe("•••••");
    expect(maskValue("+61400111222")).toBe("••••••••1222");
  });
});

// ---------------------------------------------------------------------------
// ActionBar
// ---------------------------------------------------------------------------

describe("ActionBar", () => {
  const noop = () => {};

  it("omits illegal actions entirely — it does not disable them", () => {
    render(
      <ActionBar
        actions={[
          { id: "cancel", label: "Cancel saga", legal: true, onClick: noop, kind: "danger" as const },
          { id: "convert", label: "Convert to standalone", legal: false, onClick: noop },
        ]}
      />
    );
    expect(screen.getByRole("button", { name: "Cancel saga" })).toBeInTheDocument();
    // A disabled button advertises a capability and is indistinguishable from "would 409".
    expect(screen.queryByRole("button", { name: "Convert to standalone" })).toBeNull();
  });

  it("explains a withheld action only when asked to", () => {
    const actions = [
      { id: "convert", label: "Convert", legal: false, onClick: noop, whenIllegal: "Only a parked saga can convert." },
    ];
    const { rerender } = render(<ActionBar actions={actions} emptyLabel="Nothing to do." />);
    expect(screen.queryByText(/Only a parked saga/)).toBeNull();

    rerender(<ActionBar actions={actions} explainIllegal emptyLabel="Nothing to do." />);
    expect(screen.getByText(/Only a parked saga can convert\./)).toBeInTheDocument();
  });

  it("says so when nothing is legal", () => {
    render(
      <ActionBar
        actions={[{ id: "a", label: "A", legal: false, onClick: noop }]}
        emptyLabel="No actions are available in this state."
      />
    );
    expect(screen.getByText("No actions are available in this state.")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// DiffView
// ---------------------------------------------------------------------------

describe("DiffView", () => {
  const ITEMS = [
    { id: "1", name: "Gel manicure", kind: "changed" as const, meta: "45 min", before: "A$45", after: "A$50" },
    { id: "2", name: "Chrome finish add-on", kind: "added" as const, after: "A$12" },
    { id: "3", name: "Paraffin hand dip", kind: "removed" as const, before: "A$15" },
    { id: "4", name: "Classic pedicure", kind: "unchanged" as const, after: "A$60" },
  ];

  it("hides unchanged items so the ones that moved are not buried", () => {
    render(<DiffView items={ITEMS} />);
    expect(screen.getByText("Gel manicure")).toBeInTheDocument();
    expect(screen.queryByText("Classic pedicure")).toBeNull();
  });

  it("names the change kind in words, not only colour", () => {
    render(<DiffView items={ITEMS} />);
    expect(screen.getByText("Changed")).toBeInTheDocument();
    expect(screen.getByText("New")).toBeInTheDocument();
    expect(screen.getByText("Removed")).toBeInTheDocument();
  });

  it("rolls the change set up before a single row is read", () => {
    render(<DiffView items={ITEMS} />);
    expect(screen.getByText("1 new · 1 changed · 1 removed")).toBeInTheDocument();
  });

  it("shows the old value struck through beside the new one", () => {
    render(<DiffView items={ITEMS} />);
    expect(screen.getByText("A$45")).toBeInTheDocument();
    expect(screen.getByText("A$50")).toBeInTheDocument();
  });

  it("does not show a stale 'before' on an added item", () => {
    render(<DiffView items={[{ id: "x", name: "New thing", kind: "added", before: "A$1", after: "A$2" }]} />);
    expect(screen.queryByText("A$1")).toBeNull();
  });

  it("treats an all-unchanged set as a real answer", () => {
    render(<DiffView items={[ITEMS[3]]} emptyLabel="No changes." />);
    expect(screen.getByText("No changes.")).toBeInTheDocument();
  });

  it("summarises an empty change set without inventing counts", () => {
    expect(summarise([])).toBe("Nothing changed");
  });
});

// ---------------------------------------------------------------------------
// Stepper — the D4 catalog lifecycle is the motivating case
// ---------------------------------------------------------------------------

describe("Stepper", () => {
  const D4 = [
    { id: "uploaded", label: "Uploaded" },
    { id: "scanning", label: "Scanning" },
    { id: "extracting", label: "Extracting" },
    { id: "draft_ready", label: "Draft ready" },
    { id: "in_review", label: "In review" },
    { id: "approved", label: "Approved" },
    { id: "published", label: "Published" },
  ];

  it("maps position to a state word, not to colour alone", () => {
    render(<Stepper steps={D4} currentId="extracting" />);
    expect(screen.getByText("Uploaded").parentElement).toHaveTextContent("complete");
    expect(screen.getByText("Extracting").parentElement).toHaveTextContent("in progress");
    expect(screen.getByText("Published").parentElement).toHaveTextContent("not started");
  });

  it("marks the current step for assistive tech", () => {
    render(<Stepper steps={D4} currentId="in_review" />);
    const current = document.querySelectorAll('[aria-current="step"]');
    // Exactly one step is current — two would be a lie about where the subject is.
    expect(current).toHaveLength(1);
    expect(current[0]).toHaveTextContent("In review");
  });

  it("draws a halted lifecycle as stopped, never as in progress", () => {
    // A quarantined catalog that renders as "in progress" tells the operator to keep waiting
    // for something that will never arrive.
    render(<Stepper steps={D4} currentId="scanning" haltedIds={["scanning"]} />);
    expect(screen.getByText("Scanning").parentElement).toHaveTextContent("stopped");
    expect(screen.getByText("Scanning").parentElement).not.toHaveTextContent("in progress");
  });

  it("does not throw on a status this console has not been rebuilt for", () => {
    render(<Stepper steps={D4} currentId="extract_failed" />);
    // Every step reads as not-started rather than the panel crashing.
    expect(screen.getByText("Uploaded").parentElement).toHaveTextContent("not started");
  });
});
