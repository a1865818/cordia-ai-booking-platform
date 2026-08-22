/**
 * Menu catalog lifecycle — state gating, session tracking, transport and mutations.
 *
 * The gating tests pin the D4 machine as the console offers it; the tracking tests pin
 * the honesty contract (only server-confirmed statuses are ever recorded); the transport
 * tests pin the owner spine (X-Actor + X-Actor-Role + fixed X-Reason-Code on every call).
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "../../test/vitest-compat";
import { hasFailed, looksStuck, STALE_INGESTION_SECONDS } from "./api";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, act } from "@testing-library/react";
import type { ReactNode } from "react";
import {
  catalogConsoleActions, catalogStatusLabel, readTrackedCatalogs, recordCatalogStatus,
} from "./catalogs";
import { ApiError } from "../../api/problem";

const D4_STATUSES = [
  "uploaded", "scanning", "quarantined", "extracting", "draft_ready",
  "in_review", "approved", "published", "superseded",
];

describe("catalogConsoleActions (D4)", () => {
  it("offers review before and at draft_ready, flagged premature while extraction is unobservable", () => {
    for (const status of ["uploaded", "scanning", "extracting"]) {
      const actions = catalogConsoleActions(status);
      expect(actions.canSendForReview, status).toBe(true);
      expect(actions.reviewMayBePremature, status).toBe(true);
    }
    const ready = catalogConsoleActions("draft_ready");
    expect(ready.canSendForReview).toBe(true);
    expect(ready.reviewMayBePremature).toBe(false);
  });

  it("walks the owner path one legal step at a time — never one button for the whole machine", () => {
    // in_review is the ONLY status in which editing is legal — the server's own guard
    // (CatalogNotEditableException), now reachable because the draft read exists.
    expect(catalogConsoleActions("in_review")).toEqual({
      canSendForReview: false, reviewMayBePremature: false, canApprove: true, canPublish: false,
      canEdit: true,
    });
    expect(catalogConsoleActions("approved")).toEqual({
      canSendForReview: false, reviewMayBePremature: false, canApprove: false, canPublish: true,
      canEdit: false,
    });
  });

  it("offers nothing on published, terminal, or unknown statuses", () => {
    for (const status of ["published", "quarantined", "superseded", "nonsense", ""]) {
      expect(catalogConsoleActions(status), status).toEqual({
        canSendForReview: false, reviewMayBePremature: false, canApprove: false, canPublish: false,
        canEdit: false,
      });
    }
  });

  it("has operator wording for every ratified status", () => {
    for (const status of D4_STATUSES) {
      expect(catalogStatusLabel(status), status).not.toBe(status);
    }
  });
});

describe("tracked catalogs (sessionStorage, per tenant)", () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it("round-trips and upserts newest-first, keeping the version label across transitions", () => {
    recordCatalogStatus("cat_1", "uploaded", "menu-v1", "2026-08-05T10:00:00.000Z");
    recordCatalogStatus("cat_2", "uploaded", null, "2026-08-05T10:01:00.000Z");
    // The transition response carries no version label — the recorded one must survive.
    const list = recordCatalogStatus("cat_1", "in_review", undefined, "2026-08-05T10:02:00.000Z");

    expect(list.map(c => c.catalogId)).toEqual(["cat_1", "cat_2"]);
    expect(list[0]).toEqual({
      catalogId: "cat_1",
      lastConfirmedStatus: "in_review",
      versionLabel: "menu-v1",
      confirmedAt: "2026-08-05T10:02:00.000Z",
    });
    expect(readTrackedCatalogs()).toEqual(list);
  });

  it("returns an empty list for corrupt storage instead of crashing the screen", () => {
    sessionStorage.setItem("cordia.catalogs.demo", "{not json");
    expect(readTrackedCatalogs()).toEqual([]);
    sessionStorage.setItem("cordia.catalogs.demo", JSON.stringify([{ nope: true }, null]));
    expect(readTrackedCatalogs()).toEqual([]);
  });
});

describe("lifecycle transport", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn().mockImplementation(() => Promise.resolve(
      new Response(JSON.stringify({ catalog_id: "cat_1", status: "uploaded" }), { status: 202 })
    )));
  });
  afterEach(() => vi.unstubAllGlobals());

  function lastRequest() {
    const [url, init] = vi.mocked(fetch).mock.calls.at(-1) as [string, RequestInit];
    return {
      url, init,
      headers: new Headers(init.headers),
      body: init.body ? JSON.parse(init.body as string) : undefined,
    };
  }

  it("every action carries the mandatory owner spine with its fixed reason code", async () => {
    const api = __actual_api;

    await api.uploadMenuCatalog("s3://menus/spring.pdf", "spring-v2");
    let { url, headers, body } = lastRequest();
    expect(url).toBe("/api/menu/catalogs");
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Reason-Code")).toBe("menu_upload");
    expect(body).toEqual({ source_artifact_ref: "s3://menus/spring.pdf", version_label: "spring-v2" });

    await api.sendCatalogForReview("cat 1");
    ({ url, headers, body } = lastRequest());
    expect(url).toBe("/api/menu/catalogs/cat%201/review");
    expect(headers.get("X-Reason-Code")).toBe("menu_review");
    expect(body).toEqual({});

    await api.approveCatalog("cat_1");
    ({ url, headers, body } = lastRequest());
    expect(url).toBe("/api/menu/catalogs/cat_1/approve");
    expect(headers.get("X-Reason-Code")).toBe("menu_approve");
    expect(body).toBeUndefined();

    await api.publishCatalog("cat_1");
    ({ url, headers } = lastRequest());
    expect(url).toBe("/api/menu/catalogs/cat_1/publish");
    expect(headers.get("X-Reason-Code")).toBe("menu_publish");
  });

  it("omits the version label rather than sending an empty one", async () => {
    const api = __actual_api;
    await api.uploadMenuCatalog("ref-only");
    expect(lastRequest().body).toEqual({ source_artifact_ref: "ref-only" });
  });

  it("reads a confirmed {catalog_id, status} and refuses anything less", async () => {
    const api = __actual_api;
    expect(api.catalogFromResponse({ catalog_id: "cat_1", status: "approved" }))
      .toEqual({ catalogId: "cat_1", status: "approved" });
    expect(api.catalogFromResponse({ catalog_id: "cat_1" })).toBeNull();
    expect(api.catalogFromResponse({ status: "approved" })).toBeNull();
    expect(api.catalogFromResponse(null)).toBeNull();
  });
});

// ---------------------------------------------------------------------------

const __actual_api = { ...(await import("./api")) };
vi.mock("./api", () => ({
  ...__actual_api,
  uploadMenuCatalog: vi.fn(),
  sendCatalogForReview: vi.fn(),
  approveCatalog: vi.fn(),
  publishCatalog: vi.fn(),
}));

import { publishCatalog } from "./api";
import { menuActionFailureMessage, usePublishCatalog } from "./mutations";

describe("usePublishCatalog", () => {
  it("refreshes the settings domain on both outcomes — published items are what is bookable", async () => {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const invalidate = vi.spyOn(client, "invalidateQueries");
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );

    vi.mocked(publishCatalog).mockResolvedValueOnce({ catalog_id: "cat_1", status: "published" });
    const { result } = renderHook(() => usePublishCatalog(), { wrapper });
    await act(async () => { await result.current.mutateAsync({ catalogId: "cat_1" }); });

    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: expect.arrayContaining(["settings"]) })
    );
    expect(publishCatalog).toHaveBeenCalledTimes(1);
  });
});

describe("menuActionFailureMessage", () => {
  it("names the role refusal, the state race, and the disappearance", () => {
    expect(menuActionFailureMessage(new ApiError(403, "forbidden"), "x"))
      .toMatch(/owner role/);
    expect(menuActionFailureMessage(new ApiError(409, "conflict"), "x"))
      .toMatch(/isn't in the right state/);
    expect(menuActionFailureMessage(new ApiError(404, "missing"), "x"))
      .toMatch(/no longer exists/);
    expect(menuActionFailureMessage("nope", "publish this catalog"))
      .toBe("Could not publish this catalog");
  });
});

// ---------------------------------------------------------------------------
// Failed vs slow ingestion (docs §12 gap 8; D4 amended 2026-08-06 to add extract_failed)
// ---------------------------------------------------------------------------

describe("hasFailed", () => {
  const at = (status: string, ageSeconds = 0) =>
    ({ catalog_id: "cat_1", version_label: "v1", status, created_at: "", age_seconds: ageSeconds }) as never;

  it("reports the platform's own verdict, not an inference from the clock", () => {
    // Age is irrelevant here on purpose: `extract_failed` is a status the SERVER settled, so a
    // freshly-failed catalog is just as failed as an old one. Before the D4 amendment there was
    // no such status and this could only ever be guessed from age_seconds.
    expect(hasFailed(at("extract_failed", 1))).toBe(true);
    expect(hasFailed(at("extract_failed", 99_999))).toBe(true);
  });

  it("does not call an in-progress catalog failed however old it is", () => {
    // The distinction the two banners rest on: over the threshold but not yet settled is the
    // console noticing something, not the platform stating it.
    expect(hasFailed(at("extracting", STALE_INGESTION_SECONDS * 10))).toBe(false);
    expect(hasFailed(at("scanning", STALE_INGESTION_SECONDS * 10))).toBe(false);
  });

  it("does not call a resting status failed", () => {
    for (const status of ["draft_ready", "in_review", "approved", "published", "superseded"]) {
      expect(hasFailed(at(status)), status).toBe(false);
    }
  });
});

describe("looksStuck", () => {
  const at = (status: string, ageSeconds: number) =>
    ({ catalog_id: "cat_1", version_label: "v1", status, created_at: "", age_seconds: ageSeconds }) as never;

  it("flags an ingestion that has sat in scanning or extracting too long", () => {
    expect(looksStuck(at("extracting", STALE_INGESTION_SECONDS + 1))).toBe(true);
    expect(looksStuck(at("scanning", STALE_INGESTION_SECONDS + 1))).toBe(true);
  });

  it("does not flag one that is merely in progress", () => {
    expect(looksStuck(at("extracting", 60))).toBe(false);
  });

  it("never flags a status that is not mid-ingestion, however old", () => {
    // A published catalog is old by design; an in_review one is waiting on a HUMAN, not on the
    // platform. Calling either stuck would send the operator hunting for a problem that is not
    // there.
    for (const status of ["uploaded", "draft_ready", "in_review", "approved", "published", "quarantined"]) {
      expect(looksStuck(at(status, STALE_INGESTION_SECONDS * 10)), status).toBe(false);
    }
  });

  it("treats a missing age as not stuck rather than as zero-or-infinite", () => {
    expect(looksStuck({ catalog_id: "c", status: "extracting" } as never)).toBe(false);
  });

  it("stops claiming a catalog is merely slow once the platform has failed it", () => {
    // Otherwise the same catalog would appear in BOTH banners — once as a terminal failure and
    // once as "taking longer than expected", which reads as two separate problems.
    expect(looksStuck(at("extract_failed", STALE_INGESTION_SECONDS * 10))).toBe(false);
  });
});
