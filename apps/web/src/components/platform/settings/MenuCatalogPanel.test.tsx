/**
 * MenuCatalogPanel — the owner review→approve→publish surface.
 *
 * Pins the role gate (no 403-certain buttons for a non-owner session), the honesty
 * contract of the tracked list (rows change only on a server-confirmed status), the
 * one-legal-step-at-a-time state machine, and the named failure path with retry.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MenuCatalogPanel } from "./MenuCatalogPanel";
import { recordCatalogStatus } from "../../../features/menu/catalogs";
import { ApiError } from "../../../api/problem";
import type { CatalogSummary } from "../../../features/menu/api";

vi.mock("../../../features/session/identity", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../../features/session/identity")>()),
  sessionIdentity: vi.fn(),
}));
vi.mock("../../../features/menu/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../../features/menu/api")>()),
  uploadMenuCatalog: vi.fn(),
  sendCatalogForReview: vi.fn(),
  approveCatalog: vi.fn(),
  publishCatalog: vi.fn(),
  // The discovery read was left unmocked, so every test in this file was quietly exercising a
  // real `fetch` that jsdom rejects — the panel rendered its error path and the assertions
  // happened to pass anyway because the empty state did not distinguish the two. It does now.
  listCatalogs: vi.fn(),
}));

import { sessionIdentity } from "../../../features/session/identity";
import {
  approveCatalog, listCatalogs, publishCatalog, sendCatalogForReview, uploadMenuCatalog,
} from "../../../features/menu/api";

function identity(role: string) {
  return {
    actor: "console-operator", role, tenant: "demo",
    actorInitials: "CO", tenantInitial: "D",
  };
}

/**
 * A discovery row in the shape the endpoint actually returns.
 *
 * Spelled out through the generated contract rather than as a two-field literal, so a test
 * cannot pass against a shape the API does not send — `age_seconds` in particular drives the
 * "taking longer than expected" panel.
 */
function catalogRow(
  catalogId: string,
  status: string,
  extra: Partial<CatalogSummary> = {}
): CatalogSummary {
  return {
    catalog_id: catalogId,
    status,
    version_label: null,
    created_at: "2026-08-09T00:00:00Z",
    age_seconds: 5,
    ...extra,
  };
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  vi.mocked(sessionIdentity).mockReturnValue(identity("owner"));
  // Default: the discovery read SUCCEEDS and the tenant has no catalogs. That is the only state
  // in which the panel may claim there are none.
  vi.mocked(listCatalogs).mockResolvedValue({ catalogs: [] });
});
afterEach(() => sessionStorage.clear());

describe("role gate", () => {
  it("explains instead of offering actions when the session is not the owner", () => {
    vi.mocked(sessionIdentity).mockReturnValue(identity("operator"));
    render(<MenuCatalogPanel />, { wrapper });

    // Every menu action 403s for a non-owner — offering one would be a certain failure.
    expect(screen.getByText(/requires the/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Register" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Publish" })).not.toBeInTheDocument();
  });

  it("matches the server's exact-case check — 'Owner' is refused there, so it is gated here", () => {
    // StaffRole.Satisfies is a strict string comparison and the header travels verbatim:
    // a session configured "Owner" WILL 403 on every action, so the gate must agree.
    vi.mocked(sessionIdentity).mockReturnValue(identity("Owner"));
    render(<MenuCatalogPanel />, { wrapper });

    expect(screen.getByText(/requires the/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Register" })).not.toBeInTheDocument();
  });
});

describe("registering a menu", () => {
  it("requires an artifact reference before anything is sent", () => {
    render(<MenuCatalogPanel />, { wrapper });
    expect(screen.getByRole("button", { name: "Register" })).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Menu artifact reference"), {
      target: { value: "s3://menus/spring.pdf" },
    });
    expect(screen.getByRole("button", { name: "Register" })).toBeEnabled();
  });

  it("records the row only from the server's confirmed response", async () => {
    vi.mocked(uploadMenuCatalog).mockResolvedValueOnce({ catalog_id: "cat_1", status: "uploaded" });
    render(<MenuCatalogPanel />, { wrapper });

    fireEvent.change(screen.getByLabelText("Menu artifact reference"), {
      target: { value: "s3://menus/spring.pdf" },
    });
    fireEvent.change(screen.getByLabelText("Version label"), { target: { value: "spring-v2" } });
    fireEvent.click(screen.getByRole("button", { name: "Register" }));

    expect(await screen.findByText("cat_1")).toBeInTheDocument();
    expect(uploadMenuCatalog).toHaveBeenCalledWith("s3://menus/spring.pdf", "spring-v2");
    // The premature caveat: extraction is unobservable, review may be refused for now.
    expect(screen.getByText(/no status endpoint to watch/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send for review" })).toBeInTheDocument();
  });

  it("reports a refused upload by name with a retry, and records no row", async () => {
    vi.mocked(uploadMenuCatalog).mockRejectedValueOnce(new ApiError(403, "forbidden"));
    render(<MenuCatalogPanel />, { wrapper });

    fireEvent.change(screen.getByLabelText("Menu artifact reference"), {
      target: { value: "s3://menus/spring.pdf" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Register" }));

    expect(await screen.findByText(/owner role/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Try again/ })).toBeInTheDocument();
    expect(await screen.findByText("No menu catalogs yet.")).toBeInTheDocument();
  });

  it("retries with what the operator NOW sees in the box, not the failed render's value", async () => {
    // The retry closure must not freeze the failing attempt's state: an operator who
    // corrects a typo'd reference and clicks Try again means the corrected one.
    vi.mocked(uploadMenuCatalog)
      .mockRejectedValueOnce(new ApiError(404, "artifact not found"))
      .mockResolvedValueOnce({ catalog_id: "cat_2", status: "uploaded" });
    render(<MenuCatalogPanel />, { wrapper });

    fireEvent.change(screen.getByLabelText("Menu artifact reference"), {
      target: { value: "s3://menus/typo.pdf" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Register" }));
    await screen.findByRole("button", { name: /Try again/ });

    fireEvent.change(screen.getByLabelText("Menu artifact reference"), {
      target: { value: "s3://menus/corrected.pdf" },
    });
    fireEvent.click(screen.getByRole("button", { name: /Try again/ }));

    await waitFor(() =>
      expect(uploadMenuCatalog).toHaveBeenLastCalledWith("s3://menus/corrected.pdf", undefined)
    );
  });
});

describe("walking the state machine", () => {
  it("offers exactly one legal next step per confirmed status, never one button for the flow", () => {
    recordCatalogStatus("cat_r", "in_review", "menu-v1");
    render(<MenuCatalogPanel />, { wrapper });

    expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Publish" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Send for review" })).not.toBeInTheDocument();
  });

  it("moves a row only when the server confirms the transition", async () => {
    recordCatalogStatus("cat_a", "approved", "menu-v1");
    vi.mocked(publishCatalog).mockResolvedValueOnce({ catalog_id: "cat_a", status: "published" });
    render(<MenuCatalogPanel />, { wrapper });

    fireEvent.click(screen.getByRole("button", { name: "Publish" }));

    expect(await screen.findByText(/Published — live for bookings/)).toBeInTheDocument();
    // Published is the end of this console's reach — nothing further is offered.
    expect(screen.queryByRole("button", { name: "Publish" })).not.toBeInTheDocument();
  });

  it("keeps the row's status when a premature review is refused, and names the race", async () => {
    recordCatalogStatus("cat_u", "uploaded", null);
    vi.mocked(sendCatalogForReview).mockRejectedValueOnce(new ApiError(409, "wrong state"));
    render(<MenuCatalogPanel />, { wrapper });

    fireEvent.click(screen.getByRole("button", { name: "Send for review" }));

    expect(await screen.findByText(/isn't in the right state/)).toBeInTheDocument();
    // The row still says what the server last confirmed — a refusal changes nothing.
    expect(screen.getByText(/Uploaded — queued for scanning/)).toBeInTheDocument();
    expect(sendCatalogForReview).toHaveBeenCalledTimes(1);
  });

  it("approve fires with the row's catalog id", async () => {
    recordCatalogStatus("cat_r", "in_review", null);
    vi.mocked(approveCatalog).mockResolvedValueOnce({ catalog_id: "cat_r", status: "approved" });
    render(<MenuCatalogPanel />, { wrapper });

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(approveCatalog).toHaveBeenCalledWith("cat_r"));
    expect(await screen.findByText(/Approved — ready to publish/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish" })).toBeInTheDocument();
  });
});

describe("the discovery read's own state", () => {
  it("does not present a failed catalog read as a tenant with no catalogs", async () => {
    // `discovered.data?.catalogs ?? []` collapses pending and error into an empty array, so a
    // failed GET looked exactly like an empty tenant: the operator was told there was nothing to
    // approve when the truth was that we could not find out, and no retry was offered.
    vi.mocked(listCatalogs).mockRejectedValue(new ApiError(503, "upstream down"));
    render(<MenuCatalogPanel />, { wrapper });

    expect(await screen.findByText("Could not load the catalog list")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload list" })).toBeInTheDocument();
    expect(screen.queryByText("No menu catalogs yet.")).not.toBeInTheDocument();
  });

  it("re-reads on the operator's retry", async () => {
    vi.mocked(listCatalogs)
      .mockRejectedValueOnce(new ApiError(503, "upstream down"))
      .mockResolvedValueOnce({ catalogs: [catalogRow("cat_back", "in_review")] });
    render(<MenuCatalogPanel />, { wrapper });

    fireEvent.click(await screen.findByRole("button", { name: "Reload list" }));

    expect(await screen.findByText("cat_back")).toBeInTheDocument();
    expect(screen.queryByText("Could not load the catalog list")).not.toBeInTheDocument();
  });

  it("says it is loading rather than claiming the tenant is empty", async () => {
    // Never resolves: the panel must not fill the gap with a claim it cannot support yet.
    vi.mocked(listCatalogs).mockReturnValue(new Promise(() => {}));
    render(<MenuCatalogPanel />, { wrapper });

    expect(await screen.findByText("Loading catalogs…")).toBeInTheDocument();
    expect(screen.queryByText("No menu catalogs yet.")).not.toBeInTheDocument();
  });

  it("keeps session-confirmed rows visible while the discovery read is down", async () => {
    // A status this session's own write response confirmed is still true when the list read
    // fails — dropping it would lose the one thing we do know.
    recordCatalogStatus("cat_mine", "approved", "menu-v1");
    vi.mocked(listCatalogs).mockRejectedValue(new ApiError(503, "upstream down"));
    render(<MenuCatalogPanel />, { wrapper });

    expect(await screen.findByText("Could not load the catalog list")).toBeInTheDocument();
    expect(screen.getByText("cat_mine")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish" })).toBeInTheDocument();
  });
});

describe("merge ordering", () => {
  it("preserves the discovery endpoint's newest-first order over local insertion order", async () => {
    // Seeding the map with session rows first pinned them at the top: Map.set on an existing key
    // does not move it, so a server row replacing a locally-tracked one kept the local position
    // and server-only rows were appended after. A catalog created seconds ago on another machine
    // therefore rendered BELOW rows from an old session, inverting the one ordering signal the
    // endpoint gives.
    recordCatalogStatus("cat_old", "uploaded", "old");
    vi.mocked(listCatalogs).mockResolvedValue({
      catalogs: [
        catalogRow("cat_newest", "in_review"),
        catalogRow("cat_middle", "draft_ready"),
        catalogRow("cat_old", "uploaded"),
      ],
    });
    render(<MenuCatalogPanel />, { wrapper });

    await screen.findByText("cat_newest");

    const ids = screen.getAllByTitle(/^cat_/).map(n => n.textContent);
    expect(ids).toEqual(["cat_newest", "cat_middle", "cat_old"]);
  });

  it("keeps a just-registered catalog the fetch has not seen yet, at the end", async () => {
    // It is genuinely the freshest thing here, but the server has never confirmed it and this
    // read cannot rank it against the list — so it is kept, not promoted.
    recordCatalogStatus("cat_local", "uploaded", null);
    vi.mocked(listCatalogs).mockResolvedValue({
      catalogs: [catalogRow("cat_server", "in_review")],
    });
    render(<MenuCatalogPanel />, { wrapper });

    await screen.findByText("cat_server");

    const ids = screen.getAllByTitle(/^cat_/).map(n => n.textContent);
    expect(ids).toEqual(["cat_server", "cat_local"]);
  });
});

describe("two failures at once", () => {
  it("keeps the write retry and the list retry tellable apart", async () => {
    // An Approve that 403s while the discovery read is also down puts two ErrorStates on screen.
    // Sharing the "Try again" label gave them identical accessible names for entirely different
    // actions — re-run the write, or refetch the list — with nothing to distinguish them.
    recordCatalogStatus("cat_r", "in_review", null);
    vi.mocked(listCatalogs).mockRejectedValue(new ApiError(503, "upstream down"));
    vi.mocked(approveCatalog).mockRejectedValueOnce(new ApiError(403, "forbidden"));
    render(<MenuCatalogPanel />, { wrapper });

    await screen.findByRole("button", { name: "Reload list" });
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    expect(await screen.findByRole("button", { name: /Try again/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload list" })).toBeInTheDocument();

    // The write retry re-runs the write, and only the write.
    fireEvent.click(screen.getByRole("button", { name: /Try again/ }));
    await waitFor(() => expect(approveCatalog).toHaveBeenCalledTimes(2));
  });
});

describe("a save and a transition on the same row", () => {
  it("disables Approve while the open draft editor has a save in flight", async () => {
    // `in_review` carries both canApprove and canEdit. Save and Approve are separate mutations
    // with separate pending flags, and the panel could not see the editor's — so an owner could
    // stage a price correction, click Save, and click Approve before it returned. If Approve
    // won, the catalog left in_review, the editor unmounted, the edit was refused and its error
    // went with the unmounted component: the operator saw Approved and had no reason to doubt
    // the corrections landed. Items cannot change after approval, so that is not recoverable.
    recordCatalogStatus("cat_r", "in_review", null);
    vi.mocked(listCatalogs).mockResolvedValue({ catalogs: [] });

    // The draft read resolves; the edit hangs, holding the save in flight.
    vi.stubGlobal("fetch", vi.fn().mockImplementation((url: string) =>
      String(url).includes("/edit")
        ? new Promise<Response>(() => {})
        : Promise.resolve(new Response(JSON.stringify({
            catalog_id: "cat_r",
            status: "in_review",
            version_label: null,
            items: [{
              item_id: "cat_r:itm_1", name: "Gel manicure", description: "", price_minor: 4500,
              currency: "AUD", category: null, allergen_info: null, extraction_confidence: 0.9,
            }],
          }), { status: 200, headers: { "Content-Type": "application/json" } }))
    ));

    try {
      render(<MenuCatalogPanel />, { wrapper });

      fireEvent.click(await screen.findByRole("button", { name: "Review items" }));
      await screen.findByText("cat_r:itm_1");

      const priceInput = screen.getAllByDisplayValue(/^\d+$/)[0];
      fireEvent.change(priceInput, { target: { value: "5000" } });
      fireEvent.click(await screen.findByRole("button", { name: /Save 1 change$/ }));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled()
      );
      expect(approveCatalog).not.toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
