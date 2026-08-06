/**
 * Menu catalog lifecycle mutations.
 *
 * All inherit `retry: false` — no idempotency keys exist on this family, and a repeat is
 * guarded only by the state machine. Publishing invalidates the settings domain: the
 * published items ARE the create drawer's bookable services (they share the catalog-items
 * query), so what can be booked changes the moment a publish lands.
 */

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../../api/queryKeys";
import { isApiError } from "../../api/problem";
import { approveCatalog, publishCatalog, sendCatalogForReview, uploadMenuCatalog , editCatalogItems, type CatalogItemEdit } from "./api";

export interface UploadMenuVariables {
  sourceArtifactRef: string;
  versionLabel?: string;
}

export function useUploadMenuCatalog() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ sourceArtifactRef, versionLabel }: UploadMenuVariables) =>
      uploadMenuCatalog(sourceArtifactRef, versionLabel),
    // `onSettled`, not `onSuccess`. A new catalog exists and the discovery list predates it —
    // but the upload can COMMIT and still fail here, if the 202 is lost in transit. On that path
    // `onSuccess` never runs, the list is never refreshed, and the catalog the server did create
    // is invisible: the operator sees a failure, uploads again, and the venue ends up with two.
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.settings.catalogList() });
    },
  });
}

export interface CatalogTransitionVariables {
  catalogId: string;
}

/**
 * Every catalog LIFECYCLE transition invalidates the discovery list.
 *
 * The list is fetched with `staleTime: Infinity` and no focus/reconnect refetch (an audited,
 * owner-gated read must not fire because someone alt-tabbed), so nothing else would ever refresh
 * it. Without this, the panel's merge kept overwriting a status the server had just confirmed in
 * a write response with the one from the mount-time snapshot: an approved catalog re-rendered as
 * `in_review`, offering Approve again, which then 409'd with no way out but a page reload.
 *
 * `onSettled`, not `onSuccess`: a 409 means the catalog is not where this session thought it was,
 * which is exactly when the stale snapshot most needs replacing.
 */
function useCatalogLifecycleMutation(
  mutationFn: (variables: CatalogTransitionVariables) => Promise<unknown>
) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn,
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.settings.catalogList() });
    },
  });
}

export function useSendCatalogForReview() {
  return useCatalogLifecycleMutation(({ catalogId }) => sendCatalogForReview(catalogId));
}

export function useApproveCatalog() {
  return useCatalogLifecycleMutation(({ catalogId }) => approveCatalog(catalogId));
}

export function usePublishCatalog() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ catalogId }: CatalogTransitionVariables) => publishCatalog(catalogId),
    onSettled: () => {
      // The published-items list (Settings panel AND the create drawer's services) is
      // stale either way: a success changed it, and a 409 means someone else's action did.
      void queryClient.invalidateQueries({ queryKey: queryKeys.settings.all() });
    },
  });
}

export interface EditCatalogItemsVariables {
  catalogId: string;
  edits: readonly CatalogItemEdit[];
}

/**
 * Applies owner edits to an in_review catalog.
 *
 * `retry: false` is inherited: the edit family carries no idempotency key, so a silent re-send
 * could apply the same change twice. Invalidates the draft read on both outcomes — a success
 * changed it, and a failure may mean someone else did.
 */
export function useEditCatalogItems() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ catalogId, edits }: EditCatalogItemsVariables) =>
      editCatalogItems(catalogId, edits),
    onSettled: (_d, _e, variables) => {
      void queryClient.invalidateQueries({
        queryKey: queryKeys.settings.catalogDraft(variables.catalogId),
      });
    },
  });
}

/** Operator-facing message for a failed catalog action. */
export function menuActionFailureMessage(error: unknown, action: string): string {
  if (isApiError(error) && error.status === 403) {
    return "The platform refused this — menu management needs the owner role, and this session's role was not accepted.";
  }
  if (isApiError(error) && error.status === 409) {
    return "The catalog isn't in the right state for that yet — extraction may still be running, or another action already moved it. There is no status endpoint to check, so try again shortly.";
  }
  if (isApiError(error) && error.status === 404) {
    return "That catalog no longer exists on the platform.";
  }
  return error instanceof Error && error.message ? error.message : `Could not ${action}`;
}
