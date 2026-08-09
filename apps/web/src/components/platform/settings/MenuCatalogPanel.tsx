import React, { useRef, useState } from "react";
import { SESSION_IDENTITY_HINT, sessionIdentity } from "../../../features/session/identity";
import {
  catalogFromResponse, hasFailed, listCatalogs, looksStuck, STALE_INGESTION_SECONDS,
} from "../../../features/menu/api";
import {
  catalogConsoleActions, catalogStatusLabel, readTrackedCatalogs, recordCatalogStatus,
  type TrackedCatalog,
} from "../../../features/menu/catalogs";
import {
  menuActionFailureMessage, useApproveCatalog, usePublishCatalog, useSendCatalogForReview,
  useUploadMenuCatalog,
} from "../../../features/menu/mutations";
import { useQuery } from "@tanstack/react-query";
import { queryKeys } from "../../../api/queryKeys";
import { Panel } from "../../ui/Panel";
import { PermissionGate, satisfiesRole } from "../../ui/PermissionGate";
import { ErrorState } from "../../ui/ErrorState";
import { CatalogDraftEditor } from "./CatalogDraftEditor";

const inputStyle: React.CSSProperties = {
  width: '100%', height: 38, border: '1px solid #EAEAEA', borderRadius: 9,
  padding: '0 10px', fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 13, color: '#0A0A0A', background: '#fff', outline: 'none',
};

const actionButtonStyle: React.CSSProperties = {
  height: 30, padding: '0 10px', borderRadius: 8, fontSize: 11.5, fontWeight: 600,
  fontFamily: "'Inter Tight', system-ui, sans-serif", cursor: 'pointer',
  transition: 'all 120ms ease',
};

function ActionButton({ onClick, disabled, title, emphasis, children }: {
  onClick: () => void;
  disabled?: boolean;
  title: string;
  emphasis?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="disabled:opacity-40 disabled:cursor-not-allowed hover:brightness-105"
      style={{
        ...actionButtonStyle,
        background: emphasis ? '#0A0A0A' : '#fff',
        color: emphasis ? '#fff' : '#3A3A38',
        border: `1px solid ${emphasis ? '#0A0A0A' : '#E0E0DC'}`,
      }}
    >
      {children}
    </button>
  );
}

interface ActionFailure {
  /** What failed, for the heading. */
  action: string;
  error: unknown;
  /** Re-runs the exact action — the operator's deliberate retry, never automatic. */
  retry: () => void;
}

/**
 * The owner review→approve→publish flow for menu catalogs (spec 11 §21, D4).
 *
 * Two things shape this panel:
 *
 * - **The list is a MERGE** of `GET /api/menu/catalogs` (docs §12 gap 3, now closed) with the
 *   statuses this session confirmed in its own write responses. Neither alone is right: the
 *   fetch is the only way to see a catalog started elsewhere, and the session record is the only
 *   thing newer than a fetch taken before the operator's last action. The more recent statement
 *   wins. Progress BETWEEN fetches is still unobservable — nothing pushes.
 * - **All five lifecycle actions require the `owner` role**, exactly (`operator` is
 *   refused). When this session is not the owner, the panel explains instead of offering
 *   buttons that are certain to 403.
 */
export function MenuCatalogPanel() {
  const session = sessionIdentity();
  // One shared port of `StaffRole.Satisfies` — see `satisfiesRole` for why this is not
  // case-folded and why owner-required is an equality test while operator-required is not.
  const isOwner = satisfiesRole(session.role, 'owner');

  const [catalogs, setCatalogs] = useState<TrackedCatalog[]>(() => readTrackedCatalogs());
  const [artifactRef, setArtifactRef] = useState('');
  const [versionLabel, setVersionLabel] = useState('');
  const [failure, setFailure] = useState<ActionFailure | null>(null);
  // Only one draft open at a time — the read is owner-gated and audited, so it is opened
  // deliberately rather than eagerly for every row.
  const [openDraft, setOpenDraft] = useState<string | null>(null);

  // Discovery (docs §12 gap 3). Owner-gated and audited, so it is fetched only for an owner and
  // never refetched on window focus — an audited read must not fire because someone alt-tabbed.
  const discovered = useQuery({
    queryKey: queryKeys.settings.catalogList(),
    queryFn: ({ signal }) => listCatalogs(50, signal),
    enabled: isOwner,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  // Two different statements, kept apart on purpose. `failed` is the platform's own verdict
  // (`extract_failed`, added by the D4 amendment); `stuck` is the console noticing a catalog is
  // over the threshold in the window before the server's sweep reaches it. Merging them would
  // put the platform's authority behind a guess.
  const discoveredCatalogs = discovered.data?.catalogs ?? [];
  const failed = discoveredCatalogs.filter(hasFailed);
  const stuck = discoveredCatalogs.filter(looksStuck);

  // MERGE, not just count. Fetching the discovery read and then rendering the sessionStorage
  // list would leave gap 3 open: an ingestion started on another machine stays invisible, and a
  // locally-tracked catalog keeps showing its last write-confirmed status while the live one
  // sits unused. Server rows win on status; session-only rows are kept so a catalog registered
  // seconds ago does not vanish before the next fetch.
  const merged: TrackedCatalog[] = (() => {
    const server = discovered.data?.catalogs ?? [];
    // WHEN the server said it, not when we merged it. Stamping "now" made the snapshot look
    // freshly confirmed on every render, so it always beat the session record — including a
    // status a write response had confirmed seconds ago.
    const serverConfirmedAt = discovered.dataUpdatedAt;

    const byId = new Map<string, TrackedCatalog>();
    for (const t of catalogs) byId.set(t.catalogId, t);
    for (const s of server) {
      if (!s.catalog_id) continue;
      const local = byId.get(s.catalog_id);

      // The newer statement wins, and neither side is inherently authoritative — both are
      // server statements, just made at different times. The list is fetched once with
      // `staleTime: Infinity`, so a lifecycle action taken after it is STRICTLY newer: letting
      // the snapshot win there re-rendered an approved catalog as `in_review`, offered Approve
      // again, and 409'd. The mutations now invalidate this query, but the refetch lands a beat
      // later and serves the old data until it does — so recency has to be checked here too.
      const localConfirmedAt = local ? Date.parse(local.confirmedAt) : NaN;
      if (Number.isFinite(localConfirmedAt) && localConfirmedAt > serverConfirmedAt) continue;

      byId.set(s.catalog_id, {
        catalogId: s.catalog_id,
        versionLabel: s.version_label ?? local?.versionLabel ?? '',
        lastConfirmedStatus: s.status ?? local?.lastConfirmedStatus ?? '',
        confirmedAt: new Date(serverConfirmedAt).toISOString(),
      });
    }
    return [...byId.values()];
  })();

  const upload = useUploadMenuCatalog();
  const review = useSendCatalogForReview();
  const approve = useApproveCatalog();
  const publish = usePublishCatalog();
  const anyBusy = upload.isPending || review.isPending || approve.isPending || publish.isPending;

  /** Records only what a server response actually said (the list's honesty contract). */
  function confirmFromBody(body: unknown, label?: string | null) {
    const confirmed = catalogFromResponse(body);
    if (confirmed) {
      setCatalogs(recordCatalogStatus(confirmed.catalogId, confirmed.status, label));
    }
  }

  // Always the latest render's doUpload. The retry stored on a failure must submit what
  // the operator NOW sees in the inputs — a closure from the failing render would silently
  // resubmit the old reference after they corrected it.
  const doUploadRef = useRef<() => void>(() => {});

  function doUpload() {
    const ref = artifactRef.trim();
    if (anyBusy || ref === '') return;
    setFailure(null);
    const label = versionLabel.trim() || undefined;

    upload.mutate({ sourceArtifactRef: ref, versionLabel: label }, {
      onSuccess: (body) => {
        confirmFromBody(body, label ?? null);
        setArtifactRef('');
        setVersionLabel('');
      },
      onError: (error) => setFailure({
        action: 'register this menu',
        error,
        retry: () => doUploadRef.current(),
      }),
    });
  }
  doUploadRef.current = doUpload;

  function transition(
    mutation: typeof review | typeof approve | typeof publish,
    catalogId: string,
    action: string
  ) {
    if (anyBusy) return;
    setFailure(null);

    const run = () =>
      mutation.mutate({ catalogId }, {
        onSuccess: (body) => confirmFromBody(body),
        onError: (error) => setFailure({ action, error, retry: run }),
      });
    run();
  }

  if (!isOwner) {
    return (
      <Panel style={{ marginBottom: 18 }}>
        <div title={SESSION_IDENTITY_HINT}>
          <PermissionGate
            requiredRole="owner"
            role={session.role}
            capability="Managing menu catalogs (upload, review, approve, publish)"
          >
            {null}
          </PermissionGate>
        </div>
      </Panel>
    );
  }

  return (
    <Panel style={{ marginBottom: 18 }}>
      {/* Register a menu source. There is no file-upload endpoint: the reference names an
          already-stored artifact, and extraction happens asynchronously after the 202. */}
      <div className="text-[13.5px] font-medium text-ink">Register a menu for ingestion</div>
      <div className="text-[11.5px] text-t3 mt-[2px] mb-[9px] leading-[1.5]">
        The platform scans and reads the referenced artifact, then a draft becomes available
        to review, approve and publish. There is no file upload — the reference names a menu
        artifact that is already stored.
      </div>
      <div className="flex gap-[8px] mb-[6px]">
        <div className="flex-1">
          <input
            value={artifactRef}
            onChange={e => setArtifactRef(e.target.value)}
            placeholder="Menu artifact reference"
            aria-label="Menu artifact reference"
            style={inputStyle}
          />
        </div>
        <div style={{ width: 140 }}>
          <input
            value={versionLabel}
            onChange={e => setVersionLabel(e.target.value)}
            placeholder="Label (optional)"
            aria-label="Version label"
            style={inputStyle}
          />
        </div>
        <ActionButton
          emphasis
          disabled={anyBusy || artifactRef.trim() === ''}
          title="Registers the referenced menu for scanning and extraction."
          onClick={doUpload}
        >
          {upload.isPending ? 'Registering…' : 'Register'}
        </ActionButton>
      </div>

      {failure && (
        <div className="my-[9px]">
          <ErrorState
            inline
            error={failure.error}
            title={menuActionFailureMessage(failure.error, failure.action)}
            onRetry={failure.retry}
          />
        </div>
      )}

      {/* Catalogs this session has touched, at their last server-confirmed status. */}
      <div style={{ borderTop: '1px solid #EFEFEF', marginTop: 10, paddingTop: 10 }}>
        {merged.length === 0 ? (
          <div className="py-2 text-[12.5px] text-t3">
            No catalogs started from this session yet.
          </div>
        ) : (
          merged.map((catalog) => {
            const actions = catalogConsoleActions(catalog.lastConfirmedStatus);
            return (
              <div
                key={catalog.catalogId}
                className="py-[10px] flex items-start gap-[10px] flex-wrap"
                style={{ borderBottom: '1px solid #F4F4F2' }}
              >
                <div className="flex-1 min-w-[180px]">
                  <div className="font-mono text-[11.5px] text-ink truncate" title={catalog.catalogId}>
                    {catalog.catalogId}
                  </div>
                  <div className="text-[11.5px] text-t2 mt-[2px]">
                    {catalog.versionLabel ? `${catalog.versionLabel} · ` : ''}
                    {catalogStatusLabel(catalog.lastConfirmedStatus)}
                  </div>
                  {actions.reviewMayBePremature && (
                    <div className="text-[11px] text-t3 mt-[2px] leading-[1.45]">
                      Scanning and extraction run in the background and there is no status
                      endpoint to watch them — sending for review will be refused until the
                      draft is ready.
                    </div>
                  )}
                </div>
                <div className="flex gap-[7px] shrink-0 flex-wrap">
                  {actions.canSendForReview && (
                    <ActionButton
                      disabled={anyBusy}
                      title="Moves the extracted draft into review (draft_ready → in_review)."
                      onClick={() => transition(review, catalog.catalogId, 'send this catalog for review')}
                    >
                      Send for review
                    </ActionButton>
                  )}
                  {actions.canEdit && (
                    <ActionButton
                      disabled={anyBusy}
                      title="Reads the draft's items and lets you correct the extracted text and prices before approving."
                      onClick={() => setOpenDraft(
                        openDraft === catalog.catalogId ? null : catalog.catalogId
                      )}
                    >
                      {openDraft === catalog.catalogId ? 'Hide draft' : 'Review items'}
                    </ActionButton>
                  )}
                  {actions.canApprove && (
                    <ActionButton
                      disabled={anyBusy}
                      title="Approves the reviewed draft (in_review → approved). Items cannot change afterwards."
                      onClick={() => transition(approve, catalog.catalogId, 'approve this catalog')}
                    >
                      Approve
                    </ActionButton>
                  )}
                  {actions.canPublish && (
                    <ActionButton
                      emphasis
                      disabled={anyBusy}
                      title="Publishes this catalog (approved → published). The currently published catalog is superseded, and these items become what bookings can be made against."
                      onClick={() => transition(publish, catalog.catalogId, 'publish this catalog')}
                    >
                      Publish
                    </ActionButton>
                  )}
                </div>

                {/*
                  ANDed with `canEdit`, not keyed on `openDraft` alone.
                  `in_review` carries both `canApprove` and `canEdit`, so an operator could open
                  "Review items", stage a price edit, and click Approve on the same row: the status
                  moved to `approved`, the toggle disappeared, and the editor stayed mounted with
                  "Save N changes" still enabled — a call the server refuses with
                  `CatalogNotEditableException`. QueueScreen guards its row actions the same way.
                */}
                {actions.canEdit && openDraft === catalog.catalogId && (
                  <div className="w-full">
                    <CatalogDraftEditor
                      catalogId={catalog.catalogId}
                      onClose={() => setOpenDraft(null)}
                      // The server's own status is more trustworthy than what our writes
                      // implied. `recordCatalogStatus` RETURNS the updated list — discarding it
                      // would leave sessionStorage and the rendered row disagreeing, so the row
                      // would keep offering the stale action set (e.g. "Send for review" on a
                      // catalog the server says is quarantined). That is the very gap-3 problem
                      // this read was added to close.
                      onStatusObserved={status =>
                        setCatalogs(recordCatalogStatus(catalog.catalogId, status))}
                    />
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>

      {failed.length > 0 && (
        <div
          role="status"
          className="mt-[10px] rounded-[11px] p-[11px_12px]"
          style={{ background: '#FDECEC', border: '1px solid #F3C9C9' }}
        >
          <div className="text-[12.5px] font-semibold text-ink">
            {failed.length === 1
              ? '1 catalog failed to extract'
              : `${failed.length} catalogs failed to extract`}
          </div>
          <ul className="list-none p-0 m-0 mt-[6px]">
            {failed.map(c2 => (
              <li key={c2.catalog_id} className="font-mono text-[11px] text-t2">
                {c2.catalog_id}
              </li>
            ))}
          </ul>
          {/* Stated flatly, because this IS the platform's verdict — `extract_failed`, added to
              catalog_status by the D4 amendment. Before it, the console could only report an age
              and let the operator infer. */}
          <div className="text-[11px] mt-[7px] leading-[1.5]" style={{ color: '#8A2C2C' }}>
            Extraction stopped and will not resume — this is terminal, not slow. Register the menu
            again to replace it; the failed version is superseded, not edited.
          </div>
        </div>
      )}

      {stuck.length > 0 && (
        <div
          role="status"
          className="mt-[10px] rounded-[11px] p-[11px_12px]"
          style={{ background: '#FBF3DF', border: '1px solid #F0DCA8' }}
        >
          <div className="text-[12.5px] font-semibold text-ink">
            {stuck.length === 1
              ? '1 catalog is taking longer than expected'
              : `${stuck.length} catalogs are taking longer than expected`}
          </div>
          <ul className="list-none p-0 m-0 mt-[6px]">
            {stuck.map(c2 => (
              <li key={c2.catalog_id} className="font-mono text-[11px] text-t2">
                {c2.catalog_id} · {catalogStatusLabel(c2.status ?? '')} ·{' '}
                {Math.floor((c2.age_seconds ?? 0) / 60)}m
              </li>
            ))}
          </ul>
          {/* Deliberately NOT called failed. These are over the threshold but the server's sweep
              has not settled them yet, so the platform has made no claim about them. When it
              does, they move to the block above. */}
          <div className="text-[11px] mt-[7px] leading-[1.5]" style={{ color: '#7E5606' }}>
            Over {Math.floor(STALE_INGESTION_SECONDS / 60)} minutes is unusual. The platform marks
            an extraction failed once it is sure — until then this is an age, not a verdict.
          </div>
        </div>
      )}

      <div className="text-[11px] text-t3 mt-[9px] leading-[1.5]">
        Progress between
        actions — scanning, extraction, a quarantine — is not visible from here.
      </div>
    </Panel>
  );
}
