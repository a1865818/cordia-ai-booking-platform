/**
 * Menu catalog lifecycle calls (`POST /api/menu/catalogs` + transitions, docs §10).
 *
 * Every action here is an **owner** action server-side: `X-Actor`, `X-Actor-Role` and
 * `X-Reason-Code` are all mandatory and fail-closed, and the required role is exactly
 * `owner` — `operator` does not satisfy it. The reason codes are fixed per action (the
 * same convention as the cancel flow's `operator_request`): they describe the action for
 * the audit trail, they are not operator prose.
 *
 * The edit call is now wired. It requires the draft's namespaced `{catalogId}:{rawId}` item
 * ids, which `GET /api/menu/catalogs/{id}/items` returns — the draft-scoped read added to close
 * docs §12 gap 4. `/api/catalog/items` remains published-only and cannot serve this.
 *
 * No idempotency keys exist on this family; repeats are guarded only by the catalog state
 * machine (a wrong-state call is a 409). Nothing here is ever retried automatically.
 */

import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

/**
 * Registers a menu source for ingestion.
 *
 * `sourceArtifactRef` is a *reference* to an already-stored artifact — there is no
 * file-upload endpoint, so the value is the operator's own statement of where the menu
 * lives. The platform scans and extracts it asynchronously.
 */
export function uploadMenuCatalog(sourceArtifactRef: string, versionLabel?: string) {
  const body: Record<string, string> = { source_artifact_ref: sourceArtifactRef };
  if (versionLabel) body.version_label = versionLabel;

  return fetchApi<unknown>("/api/menu/catalogs", {
    method: "POST",
    actor: true,
    reasonCode: "menu_upload",
    body: JSON.stringify(body),
  });
}

/**
 * Moves a `draft_ready` catalog into review (`draft_ready → in_review`).
 *
 * `{}` body: edits are legal here, but the console cannot see draft items to edit
 * (backend gap), so the transition travels bare. A 409 means the draft is not ready —
 * with no status read, that is only discoverable by asking.
 */
export function sendCatalogForReview(catalogId: string) {
  return fetchApi<unknown>(
    `/api/menu/catalogs/${encodeURIComponent(catalogId)}/review`,
    { method: "POST", actor: true, reasonCode: "menu_review", body: JSON.stringify({}) }
  );
}

/** Approves an `in_review` catalog (`in_review → approved`). */
export function approveCatalog(catalogId: string) {
  return fetchApi<unknown>(
    `/api/menu/catalogs/${encodeURIComponent(catalogId)}/approve`,
    { method: "POST", actor: true, reasonCode: "menu_approve" }
  );
}

/**
 * Publishes an `approved` catalog (`approved → published`).
 *
 * This is the action with reach: the previously published catalog is superseded in the
 * same transaction, and the published items are what bookings can be made against — the
 * create drawer's service list changes the moment this lands.
 */
export function publishCatalog(catalogId: string) {
  return fetchApi<unknown>(
    `/api/menu/catalogs/${encodeURIComponent(catalogId)}/publish`,
    { method: "POST", actor: true, reasonCode: "menu_publish" }
  );
}

/**
 * The `{catalog_id, status}` a lifecycle response carried, if it did. The bodies are
 * anonymous objects the generated contract cannot type, so this is runtime-checked —
 * `null` means the server did not say, and nothing may be recorded from it.
 */
export function catalogFromResponse(body: unknown): { catalogId: string; status: string } | null {
  if (typeof body !== "object" || body === null) return null;
  const record = body as Record<string, unknown>;
  const catalogId = record.catalog_id;
  const status = record.status;
  if (typeof catalogId !== "string" || catalogId.length === 0) return null;
  if (typeof status !== "string" || status.length === 0) return null;
  return { catalogId, status };
}

// ---------------------------------------------------------------------------
// Draft items — the read that makes editing possible (docs §12 gap 4 / §13 B1).
// ---------------------------------------------------------------------------

export type CatalogDraftItemsResponse =
  paths["/api/menu/catalogs/{catalogId}/items"]["get"]["responses"][200]["content"]["application/json"];

export type CatalogDraftItem = NonNullable<CatalogDraftItemsResponse["items"]>[number];

/**
 * Reads a catalog's items at ANY status, plus the catalog's own status.
 *
 * Two things this unlocks:
 *  • the namespaced `item_id`s `editCatalogItems` must send — a raw id 404s;
 *  • the catalog's REAL status, so the panel no longer has to track only what its own writes
 *    confirmed (docs §12 gap 3).
 *
 * Owner-gated and audited server-side, like every other step of the D4 lifecycle: the draft is
 * un-reviewed content extracted from an untrusted document.
 */
export function getCatalogDraftItems(catalogId: string, signal?: AbortSignal) {
  return fetchApi<CatalogDraftItemsResponse>(
    `/api/menu/catalogs/${encodeURIComponent(catalogId)}/items`,
    { actor: true, reasonCode: "menu_review", signal }
  );
}

export interface CatalogItemEdit {
  /** MUST be the namespaced `{catalogId}:{rawId}` form returned by `getCatalogDraftItems`. */
  itemId: string;
  name?: string;
  description?: string;
  priceMinor?: number;
}

/**
 * Applies owner edits to a catalog in `in_review`. Repeatable and status-preserving.
 *
 * Only changed fields are sent: an unchanged field omitted is not the same as one cleared, and
 * sending everything back would make an accidental overwrite indistinguishable from an edit.
 */
export function editCatalogItems(catalogId: string, edits: readonly CatalogItemEdit[]) {
  return fetchApi<unknown>(
    `/api/menu/catalogs/${encodeURIComponent(catalogId)}/edit`,
    {
      method: "POST",
      actor: true,
      reasonCode: "menu_edit",
      body: JSON.stringify({
        edits: edits.map(e => ({
          item_id: e.itemId,
          name: e.name,
          description: e.description,
          price_minor: e.priceMinor,
        })),
      }),
    }
  );
}

// ---------------------------------------------------------------------------
// Catalog discovery (docs §12 gap 3).
// ---------------------------------------------------------------------------

export type CatalogListResponse =
  paths["/api/menu/catalogs"]["get"]["responses"][200]["content"]["application/json"];

export type CatalogSummary = NonNullable<CatalogListResponse["catalogs"]>[number];

/**
 * Lists the tenant's catalogs at any status, newest first.
 *
 * This is what makes a catalog started on another machine — or before a refresh — visible at
 * all. Each row carries `age_seconds`, which is still worth showing while a catalog is genuinely
 * in progress — but a stalled one is no longer inferred from age: D4 was amended (2026-08-06) to
 * add `extract_failed`, so the platform now SAYS an extraction stopped instead of leaving the
 * console to guess from a clock.
 *
 * Owner-gated and audited, like the rest of the D4 lifecycle.
 */
export function listCatalogs(limit?: number, signal?: AbortSignal) {
  const query = limit === undefined ? "" : `?limit=${encodeURIComponent(String(limit))}`;
  return fetchApi<CatalogListResponse>(`/api/menu/catalogs${query}`, {
    actor: true,
    reasonCode: "menu_review",
    signal,
  });
}

/**
 * How long is "long enough to be worth mentioning".
 *
 * Kept in sync with the server's `MenuIngestionMonitor` staleness window on purpose: the server
 * settles a catalog as `extract_failed` at this age, so a console using a different number would
 * disagree with the platform about the same catalog for the gap between them.
 */
export const STALE_INGESTION_SECONDS = 15 * 60;

/**
 * The platform's own verdict that extraction stopped.
 *
 * This is a **fact the server recorded**, not a client inference. Before the D4 amendment there
 * was no such status and this had to be guessed from `age_seconds` — which could only ever say
 * "this looks stuck", never "this failed".
 */
export function hasFailed(catalog: CatalogSummary): boolean {
  return catalog.status === "extract_failed";
}

/**
 * Mid-ingestion and past the window, but not yet settled by the server.
 *
 * Still useful: the monitor runs on an interval, so there is a window where a catalog is over the
 * threshold and the sweep has not reached it. Showing it as "taking longer than expected" during
 * that gap is honest; calling it failed before the platform has would not be.
 */
export function looksStuck(catalog: CatalogSummary): boolean {
  const inProgress = catalog.status === "scanning" || catalog.status === "extracting";
  return inProgress && (catalog.age_seconds ?? 0) >= STALE_INGESTION_SECONDS;
}
