/**
 * The console's per-session record of catalog lifecycles — last server-confirmed status only.
 *
 * This module keeps that record (per tenant, in sessionStorage, updated exclusively from server
 * responses) and derives which owner actions the D4 state machine permits from it.
 *
 * <b>It is no longer the console's only source.</b> `GET /api/menu/catalogs` now exists
 * (docs §12 gap 3), so `MenuCatalogPanel` merges this session record with a live list and can
 * finally see catalogs started on another machine, and statuses reached by the system between
 * this session's own writes. What this record still adds is recency: a status this session just
 * confirmed in a write response is newer than a list fetched before that write, and must not be
 * overwritten by it.
 *
 * What is still genuinely unobservable: `uploaded → scanning → extracting → draft_ready`
 * progress BETWEEN list fetches, since those transitions are system-driven and nothing pushes.
 */

import { getTenantId } from "../../api/config";

const STORAGE_PREFIX = "cordia.catalogs.";

/** Operator wording for the ratified `catalog_status` values (D4). */
export const CATALOG_STATUS_COPY: Record<string, string> = {
  uploaded: "Uploaded — queued for scanning",
  scanning: "Being scanned",
  quarantined: "Quarantined — failed the safety scan",
  extracting: "Being read",
  draft_ready: "Draft ready for review",
  in_review: "In review",
  approved: "Approved — ready to publish",
  published: "Published — live for bookings",
  superseded: "Superseded by a newer version",
  // The tenth value, added by the D4 amendment (owner ruling 2026-08-06) and written by the
  // server's ingestion monitor. Terminal: the wording says what to DO, because there is no
  // retry — recovery is a fresh upload, which supersedes this one.
  extract_failed: "Could not be read — upload it again",
};

export function catalogStatusLabel(status: string): string {
  return CATALOG_STATUS_COPY[status] ?? status.split("_").join(" ");
}

export interface TrackedCatalog {
  catalogId: string;
  /** As last confirmed by a server response — never a guess. */
  lastConfirmedStatus: string;
  versionLabel: string | null;
  /** ISO instant of the confirming response. */
  confirmedAt: string;
}

export interface CatalogConsoleActions {
  canSendForReview: boolean;
  /**
   * True when the last confirmed status precedes `draft_ready`: extraction runs
   * unobserved, so review is offered but may be refused (409) until the draft exists.
   * The UI must say so — this is the honest limit of a write-only surface.
   */
  reviewMayBePremature: boolean;
  canApprove: boolean;
  canPublish: boolean;
  /**
   * Editing draft items. Legal only in `in_review` — the server's own guard
   * (`CatalogNotEditableException`) — and now reachable, because
   * `GET /menu/catalogs/{id}/items` returns the namespaced ids the edit action keys on.
   */
  canEdit: boolean;
}

const NO_ACTIONS: CatalogConsoleActions = {
  canSendForReview: false,
  reviewMayBePremature: false,
  canApprove: false,
  canPublish: false,
  canEdit: false,
};

/**
 * The owner actions worth offering for a catalog whose status was last confirmed as
 * `lastConfirmedStatus` (D4 machine). Terminal statuses (`quarantined`, `superseded`) and
 * `published` offer nothing; the in-between system statuses offer review with the
 * premature caveat, because the platform may already have advanced past them.
 */
export function catalogConsoleActions(lastConfirmedStatus: string): CatalogConsoleActions {
  switch (lastConfirmedStatus) {
    case "uploaded":
    case "scanning":
    case "extracting":
      return { ...NO_ACTIONS, canSendForReview: true, reviewMayBePremature: true };
    case "draft_ready":
      return { ...NO_ACTIONS, canSendForReview: true };
    case "in_review":
      return { ...NO_ACTIONS, canApprove: true, canEdit: true };
    case "approved":
      return { ...NO_ACTIONS, canPublish: true };
    default:
      return NO_ACTIONS;
  }
}

function storageKey(): string {
  return STORAGE_PREFIX + getTenantId();
}

/** Catalogs started from this console session for the current tenant, newest first. */
export function readTrackedCatalogs(): TrackedCatalog[] {
  try {
    const raw = sessionStorage.getItem(storageKey());
    if (!raw) return [];
    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((entry): entry is TrackedCatalog =>
      typeof entry === "object" && entry !== null &&
      typeof (entry as TrackedCatalog).catalogId === "string" &&
      typeof (entry as TrackedCatalog).lastConfirmedStatus === "string"
    );
  } catch {
    // A blocked or corrupted sessionStorage must not break the settings screen.
    return [];
  }
}

/**
 * Records a status the server just confirmed for a catalog, upserting its row (newest
 * first) and returning the new list. Callers must pass only values read from a server
 * response body (`catalogFromResponse`) — this is the whole honesty contract of the list.
 */
export function recordCatalogStatus(
  catalogId: string,
  confirmedStatus: string,
  versionLabel?: string | null,
  now: string = new Date().toISOString()
): TrackedCatalog[] {
  const existing = readTrackedCatalogs();
  const others = existing.filter((entry) => entry.catalogId !== catalogId);
  const previous = existing.find((entry) => entry.catalogId === catalogId);

  const updated: TrackedCatalog = {
    catalogId,
    lastConfirmedStatus: confirmedStatus,
    versionLabel: versionLabel !== undefined ? versionLabel : previous?.versionLabel ?? null,
    confirmedAt: now,
  };

  const next = [updated, ...others];
  try {
    sessionStorage.setItem(storageKey(), JSON.stringify(next));
  } catch {
    /* best effort — the in-memory list is still consistent for this render */
  }
  return next;
}
