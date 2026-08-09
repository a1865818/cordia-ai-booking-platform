/**
 * Draft-catalog review (docs §13 B1).
 *
 * The operator is correcting machine-extracted text against a physical menu, so what these pin
 * is that nothing is sent they did not change, that the namespaced id the edit action keys on
 * travels verbatim, and that the status shown is the server's rather than an inference.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { CatalogDraftEditor } from "./CatalogDraftEditor";

function Providers({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const DRAFT = {
  catalog_id: "cat_1",
  status: "in_review",
  version_label: "menu_v1",
  items: [
    {
      item_id: "cat_1:itm_gel",
      name: "Gel manicure",
      description: "45 minutes",
      price_minor: 4500,
      currency: "AUD",
      category: null,
      allergen_info: null,
      extraction_confidence: 0.62,
    },
    {
      item_id: "cat_1:itm_pedi",
      name: "Pedicure",
      description: "",
      price_minor: 6000,
      currency: "AUD",
      category: null,
      allergen_info: null,
      extraction_confidence: 0.98,
    },
  ],
};

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function calls() {
  return vi.mocked(fetch).mock.calls as [string, RequestInit][];
}

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn().mockImplementation(() => Promise.resolve(jsonResponse(DRAFT))));
});
afterEach(() => vi.unstubAllGlobals());

function open(props: Partial<Parameters<typeof CatalogDraftEditor>[0]> = {}) {
  render(
    <CatalogDraftEditor catalogId="cat_1" onClose={() => {}} {...props} />,
    { wrapper: Providers }
  );
}

describe("CatalogDraftEditor", () => {
  it("shows the catalog's REAL status, not one inferred from our own writes", async () => {
    open();
    // The panel elsewhere can only track write-confirmed statuses; this read carries the
    // server's (docs §12 gap 3).
    const step = await screen.findByText("In review");
    expect(step.parentElement).toHaveAttribute("aria-current", "step");
  });

  it("surfaces the namespaced item id the edit action keys on", async () => {
    open();
    // A raw `itm_gel` here would 404 on POST .../edit.
    expect(await screen.findByText("cat_1:itm_gel")).toBeInTheDocument();
  });

  it("sends nothing until something actually changes", async () => {
    open();
    await screen.findByText("cat_1:itm_gel");
    expect(screen.getByRole("button", { name: /No changes to save/ })).toBeDisabled();
    expect(screen.getByText("Nothing has been changed yet.")).toBeInTheDocument();
  });

  it("submits ONLY the fields that changed, with the namespaced id", async () => {
    open();
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "5000" } });

    fireEvent.click(await screen.findByRole("button", { name: /Save 1 change$/ }));

    await waitFor(() => {
      expect(calls().some(([u]) => String(u).includes("/edit"))).toBe(true);
    });
    const editCall = calls().find(([u]) => String(u).includes("/edit"))!;
    const body = JSON.parse(editCall[1].body as string);

    expect(body.edits).toHaveLength(1);
    expect(body.edits[0].item_id).toBe("cat_1:itm_gel");
    expect(body.edits[0].price_minor).toBe(5000);
    // The untouched name is omitted — resending it would make an accidental overwrite
    // indistinguishable from a deliberate edit.
    expect(body.edits[0].name).toBeUndefined();
    // The item that was not touched at all is absent entirely.
    expect(JSON.stringify(body)).not.toContain("itm_pedi");
  });

  it("previews the change as a diff before it is sent", async () => {
    open();
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "5000" } });

    // Only the row that moved, with both values.
    expect(await screen.findByText("1 changed")).toBeInTheDocument();
    expect(screen.getByText("AUD 45.00")).toBeInTheDocument();
    expect(screen.getByText("AUD 50.00")).toBeInTheDocument();
  });

  it("refuses to turn a non-numeric price into zero", async () => {
    open();
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "abc" } });

    // Number("abc") is NaN; letting it through would silently make the item free.
    expect(screen.getByRole("button", { name: /No changes to save/ })).toBeDisabled();
  });

  it("carries the owner spine on the read — the draft is audited too", async () => {
    open();
    await screen.findByText("cat_1:itm_gel");

    const readCall = calls().find(([u]) => String(u).includes("/items"))!;
    const headers = new Headers(readCall[1]?.headers);
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBeTruthy();
  });

  it("treats an empty catalog as a real answer, not a failure", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ ...DRAFT, items: [] }));
    open();
    expect(await screen.findByText(/Extraction produced none/)).toBeInTheDocument();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("names a failed read with a retry rather than showing an empty draft", async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse(
      { title: "Forbidden", detail: "Owner role required.", status: 403 }, 403
    ));
    open();
    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText(/Could not read the draft/)).toBeInTheDocument();
    expect(screen.queryByText(/Extraction produced none/)).toBeNull();
  });

  it("flags a low-confidence extraction so the shakiest rows get checked", async () => {
    open();
    await screen.findByText("cat_1:itm_gel");
    expect(screen.getByText(/Extractor confidence 62%/)).toBeInTheDocument();
  });
});

describe("racing a lifecycle transition on the same row", () => {
  it("reports a save in flight so the panel can gate Approve", async () => {
    // `in_review` carries both canApprove and canEdit, so Save and Approve are live on the same
    // row at once. They are separate mutations with separate pending flags, and the panel could
    // not see this one — so it left Approve enabled while a save was in flight.
    let release: (() => void) | undefined;
    vi.mocked(fetch).mockImplementation((url) =>
      String(url).includes("/edit")
        ? new Promise<Response>(resolve => { release = () => resolve(jsonResponse({ ok: true })); })
        : Promise.resolve(jsonResponse(DRAFT))
    );

    const saving: boolean[] = [];
    open({ onSavingChange: v => saving.push(v) });
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "5000" } });
    fireEvent.click(await screen.findByRole("button", { name: /Save 1 change$/ }));

    await waitFor(() => expect(saving.at(-1)).toBe(true));

    release!();
    await waitFor(() => expect(saving.at(-1)).toBe(false));
  });

  it("reports NOT saving when it unmounts mid-save, so the panel does not latch", async () => {
    // An Approve unmounts this editor the instant the row loses canEdit. A parent left holding
    // `true` would disable every action on every row for the rest of the session.
    vi.mocked(fetch).mockImplementation((url) =>
      String(url).includes("/edit")
        ? new Promise<Response>(() => {}) // never settles
        : Promise.resolve(jsonResponse(DRAFT))
    );

    const saving: boolean[] = [];
    const { unmount } = render(
      <CatalogDraftEditor catalogId="cat_1" onClose={() => {}} onSavingChange={v => saving.push(v)} />,
      { wrapper: Providers }
    );
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "5000" } });
    fireEvent.click(await screen.findByRole("button", { name: /Save 1 change$/ }));
    await waitFor(() => expect(saving.at(-1)).toBe(true));

    unmount();
    expect(saving.at(-1)).toBe(false);
  });

  it("refuses to start a save while a lifecycle transition is running", async () => {
    // The mirror of the above. `edit.isPending` alone left Save fully clickable during an
    // Approve, so the same race just ran the other way and the edit was refused by a catalog
    // that had already left in_review.
    open({ locked: true });
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "5000" } });

    expect(await screen.findByRole("button", { name: /Save 1 change$/ })).toBeDisabled();
    expect(calls().some(([u]) => String(u).includes("/edit"))).toBe(false);
  });
});

describe("closing mid-save", () => {
  it("cannot be closed while a save is in flight", async () => {
    // The panel gates its own "Hide draft" toggle, but Close lived here and did not. Clicking it
    // during a save unmounted this editor, the cleanup reported `saving: false`, and Approve went
    // live while the POST was still on the wire — React Query does not cancel on unmount.
    vi.mocked(fetch).mockImplementation((url) =>
      String(url).includes("/edit")
        ? new Promise<Response>(() => {})
        : Promise.resolve(jsonResponse(DRAFT))
    );

    const onClose = vi.fn();
    open({ onClose });
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "5000" } });
    fireEvent.click(await screen.findByRole("button", { name: /Save 1 change$/ }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Close" })).toBeDisabled());
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onClose).not.toHaveBeenCalled();
  });

  it("keeps the failed-save retry on screen while a transition runs, disabled", async () => {
    vi.mocked(fetch).mockImplementation((url) =>
      String(url).includes("/edit")
        ? Promise.resolve(jsonResponse({ title: "nope" }, 500))
        : Promise.resolve(jsonResponse(DRAFT))
    );

    const { rerender } = render(
      <CatalogDraftEditor catalogId="cat_1" onClose={() => {}} />, { wrapper: Providers }
    );
    await screen.findByText("cat_1:itm_gel");

    const priceInputs = screen.getAllByDisplayValue(/^\d+$/);
    fireEvent.change(priceInputs[0], { target: { value: "5000" } });
    fireEvent.click(await screen.findByRole("button", { name: /Save 1 change$/ }));
    await screen.findByRole("button", { name: /Try again/ });

    // A transition starts on the panel. The retry must stay put, not disappear from the tab order.
    rerender(<CatalogDraftEditor catalogId="cat_1" onClose={() => {}} locked />);

    const retry = screen.getByRole("button", { name: /Try again/ });
    expect(retry).toBeInTheDocument();
    expect(retry).toBeDisabled();
  });
});

describe("editing a description", () => {
  it("shows the extracted description and sends a correction", async () => {
    // `CatalogItemEdit` has carried `description` and `pending` has staged it since this editor
    // was written — there was no input, so the branch was unreachable. An extractor mistake in a
    // description could be neither seen nor corrected, and it is frozen in on approval.
    open();
    await screen.findByText("cat_1:itm_gel");

    const boxes = screen.getAllByLabelText(/^Description for /);
    expect(boxes[0]).toHaveValue("45 minutes");

    fireEvent.change(boxes[0], { target: { value: "45 minutes, includes soak-off" } });
    fireEvent.click(await screen.findByRole("button", { name: /Save 1 change$/ }));

    await waitFor(() =>
      expect(calls().some(([u]) => String(u).includes("/edit"))).toBe(true)
    );
    const editCall = calls().find(([u]) => String(u).includes("/edit"))!;
    const body = JSON.parse(String(editCall[1].body));
    expect(body.edits).toEqual([
      { item_id: "cat_1:itm_gel", name: undefined, description: "45 minutes, includes soak-off", price_minor: undefined },
    ]);
  });

  it("treats an emptied description as a deliberate clear", async () => {
    // The counterpart to the rule below: emptying REAL text is a change the operator meant, and
    // sending only changed fields is what makes it expressible at all.
    open();
    await screen.findByText("cat_1:itm_gel");

    fireEvent.change(screen.getAllByLabelText(/^Description for /)[0], { target: { value: "" } });

    expect(await screen.findByRole("button", { name: /Save 1 change$/ })).toBeEnabled();
    expect(screen.getByText(/description cleared \(was “45 minutes”\)/)).toBeInTheDocument();
  });

  it("does NOT stage a change when a NULL description is touched and left empty", async () => {
    // `description` is `string?` on the server DTO, so a stored `null` is a real case — and it
    // renders as `''` in the textarea, identically to a stored `''`. Clicking in and back out
    // staged `description: ''` against that `null`: a write that changes nothing, shown to the
    // operator as a change they are asked to approve. `''` alone would not have proved this,
    // since `'' !== ''` was already false.
    vi.mocked(fetch).mockImplementation(() =>
      Promise.resolve(jsonResponse({
        ...DRAFT,
        items: [{ ...DRAFT.items[0], description: null }],
      }))
    );

    open();
    await screen.findByText("cat_1:itm_gel");

    const box = screen.getByLabelText(/^Description for /);
    expect(box).toHaveValue("");

    fireEvent.change(box, { target: { value: "x" } });
    fireEvent.change(box, { target: { value: "" } });

    expect(screen.getByRole("button", { name: /No changes to save/ })).toBeDisabled();
    expect(screen.getByText("Nothing has been changed yet.")).toBeInTheDocument();
  });

  it("names a description-only edit in the diff, which the price columns cannot show", async () => {
    // Before this the row was badged "Changed" with two identical prices and no explanation.
    open();
    await screen.findByText("cat_1:itm_pedi");

    fireEvent.change(screen.getAllByLabelText(/^Description for /)[1], {
      target: { value: "Includes callus treatment" },
    });

    expect(await screen.findByText(/description added: “Includes callus treatment”/)).toBeInTheDocument();
  });
});

describe("a null name behaves like a null description", () => {
  it("does not stage a no-op write when an unnamed item's name is touched", async () => {
    // The generated contract types `name` as `string | null` too, so this is the same defect one
    // line above the description one — not a hypothetical. Fixing only description would have
    // left the pair inconsistent for the next reader.
    vi.mocked(fetch).mockImplementation(() =>
      Promise.resolve(jsonResponse({
        ...DRAFT,
        items: [{ ...DRAFT.items[0], name: null }],
      }))
    );

    open();
    await screen.findByText("cat_1:itm_gel");

    const name = screen.getByLabelText(/^Name for /);
    expect(name).toHaveValue("");

    fireEvent.change(name, { target: { value: "x" } });
    fireEvent.change(name, { target: { value: "" } });

    expect(screen.getByRole("button", { name: /No changes to save/ })).toBeDisabled();
  });

  it("still treats clearing a REAL name as a change, and says what it was", async () => {
    open();
    await screen.findByText("cat_1:itm_gel");

    fireEvent.change(screen.getAllByLabelText(/^Name for /)[0], { target: { value: "" } });

    expect(await screen.findByRole("button", { name: /Save 1 change$/ })).toBeEnabled();
    // The old meta used a truthiness test, so clearing a name to '' produced no explanation at
    // all — the most destructive edit was the one the diff described least.
    expect(screen.getByText(/name cleared \(was “Gel manicure”\)/)).toBeInTheDocument();
  });
});

describe("what the diff claims", () => {
  it("does not strike through a price nobody touched", async () => {
    // before/after were computed unconditionally, so a description-only edit rendered the old
    // price struck through beside an identical new one. In this component's vocabulary a
    // strikethrough means "value being replaced", so the diff asserted a price change nobody
    // made — on the screen whose whole job is making an unintended change impossible to miss.
    open();
    await screen.findByText("cat_1:itm_gel");

    fireEvent.change(screen.getAllByLabelText(/^Description for /)[0], {
      target: { value: "60 minutes" },
    });

    expect(await screen.findByText("1 changed")).toBeInTheDocument();
    expect(screen.queryByText("AUD 45.00")).toBeNull();
  });

  it("tells a description CLEAR apart from a description EDIT", async () => {
    // Both used to render `description was “45 minutes”`, so the approval surface could not
    // distinguish a destructive removal from a correction.
    open();
    await screen.findByText("cat_1:itm_gel");

    fireEvent.change(screen.getAllByLabelText(/^Description for /)[0], {
      target: { value: "60 minutes" },
    });

    expect(await screen.findByText(/description: “45 minutes” → “60 minutes”/)).toBeInTheDocument();
  });

  it("keeps a heading on the row whose name was cleared", async () => {
    // `p?.name ?? item.name` took the staged '' and left the row that needs the most scrutiny
    // with no title at all — just a "Changed" badge.
    open();
    await screen.findByText("cat_1:itm_gel");

    fireEvent.change(screen.getAllByLabelText(/^Name for /)[0], { target: { value: "" } });

    await screen.findByText(/name cleared/);
    // The previous name still identifies the row.
    expect(screen.getAllByText("Gel manicure").length).toBeGreaterThan(0);
  });
});

describe("an item the read could not identify", () => {
  it("cannot be edited, and never joins a save that would roll the whole batch back", async () => {
    // `item_id` is `string | null` on the generated contract. A null collapsed to '' here, which
    // then missed `pending.find(...)` in the diff — Save offered "1 change" the preview showed as
    // nothing — and server-side `ApplyItemEditsAsync` throws CatalogItemNotFoundException the
    // moment one edit affects zero rows, discarding every other correction in the batch.
    vi.mocked(fetch).mockImplementation(() =>
      Promise.resolve(jsonResponse({
        ...DRAFT,
        items: [{ ...DRAFT.items[0], item_id: null }],
      }))
    );

    open();
    expect(await screen.findByText(/no id and cannot be edited/)).toBeInTheDocument();

    const name = screen.getByLabelText(/^Name for /);
    expect(name).toBeDisabled();
    fireEvent.change(name, { target: { value: "Corrected" } });

    expect(screen.getByRole("button", { name: /No changes to save/ })).toBeDisabled();
  });
});
