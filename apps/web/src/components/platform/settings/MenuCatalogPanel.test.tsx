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
}));

import { sessionIdentity } from "../../../features/session/identity";
import {
  approveCatalog, publishCatalog, sendCatalogForReview, uploadMenuCatalog,
} from "../../../features/menu/api";

function identity(role: string) {
  return {
    actor: "console-operator", role, tenant: "demo",
    actorInitials: "CO", tenantInitial: "D",
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
    expect(screen.getByText("No catalogs started from this session yet.")).toBeInTheDocument();
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
