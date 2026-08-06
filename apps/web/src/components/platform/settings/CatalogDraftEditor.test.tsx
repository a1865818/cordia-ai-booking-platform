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

function open() {
  render(<CatalogDraftEditor catalogId="cat_1" onClose={() => {}} />, { wrapper: Providers });
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
