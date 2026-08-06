import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { queryKeys } from "../../../api/queryKeys";
import { getCatalogDraftItems, type CatalogDraftItem, type CatalogItemEdit } from "../../../features/menu/api";
import { menuActionFailureMessage, useEditCatalogItems } from "../../../features/menu/mutations";
import { DiffView, type DiffItem } from "../../ui/DiffView";
import { formatMoney } from "../../ui/MoneyAmount";
import { Stepper } from "../../ui/Stepper";
import { ErrorState } from "../../ui/ErrorState";
import { Spinner } from "../../ui/Spinner";
import { PrimaryButton, GhostButton } from "../../ui/Button";

/** The D4 happy path, in order. */
const D4_HAPPY_PATH = [
  { id: 'uploaded', label: 'Uploaded' },
  { id: 'scanning', label: 'Scanning' },
  { id: 'extracting', label: 'Extracting' },
  { id: 'draft_ready', label: 'Draft ready' },
  { id: 'in_review', label: 'In review' },
  { id: 'approved', label: 'Approved' },
  { id: 'published', label: 'Published' },
];

/**
 * Terminal statuses that are NOT the happy path, and where each one interrupts it.
 *
 * A halted id has to be a real step or `Stepper` cannot mark it: `findIndex` returns -1 and
 * every step falls through to "not started", so a terminally-dead catalog would render as one
 * that has not begun — the exact confusion this read was added to remove.
 */
const D4_TERMINAL: Record<string, { label: string; after: string }> = {
  quarantined: { label: 'Quarantined', after: 'scanning' },
  superseded: { label: 'Superseded', after: 'published' },
  // Added with the status itself (D4 amendment, owner ruling 2026-08-06). Without an entry here
  // a failed catalog rendered as one that had not started — `findIndex` returns -1 and every step
  // falls through to "not started" — which is the opposite of what happened to it.
  extract_failed: { label: 'Could not be read', after: 'extracting' },
};

/** Splices the terminal status into the lifecycle so the stepper can point at it. */
function stepsFor(status: string) {
  const terminal = D4_TERMINAL[status];
  if (!terminal) return D4_HAPPY_PATH;
  const at = D4_HAPPY_PATH.findIndex(s => s.id === terminal.after);
  const head = D4_HAPPY_PATH.slice(0, at + 1);
  return [...head, { id: status, label: terminal.label }];
}

interface CatalogDraftEditorProps {
  catalogId: string;
  onClose: () => void;
  /** Lets the panel record the status the server actually reported. */
  onStatusObserved?: (status: string) => void;
}

/**
 * Owner review of a draft catalog's items (docs §13 B1).
 *
 * The read this is built on is what closed §12 gap 4: it returns the namespaced
 * `{catalogId}:{rawId}` ids `POST .../edit` keys on, which the published-only reads cannot
 * surface while a catalog is `in_review`. It also carries the catalog's REAL status, which is
 * why the stepper here shows observed progress rather than the write-confirmed guesses the rest
 * of the panel has to make (gap 3).
 *
 * Edits are staged locally and shown as a diff before anything is sent: the operator is
 * correcting machine-extracted text against a physical menu, and an accidental keystroke that
 * goes straight to the server is indistinguishable from a deliberate correction. Only the
 * fields that actually changed are submitted.
 */
export function CatalogDraftEditor({ catalogId, onClose, onStatusObserved }: CatalogDraftEditorProps) {
  const [drafts, setDrafts] = useState<Record<string, Partial<CatalogItemEdit>>>({});
  // The price field's raw text, kept separately from the staged edit.
  //
  // Without it the field cannot be cleared: emptying it stages `undefined`, and a controlled
  // `value={staged ?? item.price_minor}` immediately puts the ORIGINAL number back with the
  // caret at 0 — so backspacing 1200 to retype 950 yields 9501200, staged silently as a
  // deliberate change.
  const [priceText, setPriceText] = useState<Record<string, string>>({});

  const query = useQuery({
    queryKey: queryKeys.settings.catalogDraft(catalogId),
    queryFn: ({ signal }) => getCatalogDraftItems(catalogId, signal),
    // This GET is an IOperatorActionRequest: every call writes an audit row, and a refused one
    // writes a DENY. Under the shared defaults (refetchOnWindowFocus, staleTime 30s) simply
    // alt-tabbing back would file another `catalog.draft_items_read`, drowning deliberate
    // accesses in incidental ones. `useTranscript` opts out for exactly this reason, and the
    // model-trace panel avoids useQuery altogether — this read belongs in the same category.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount: false,
  });

  const edit = useEditCatalogItems();

  const status = query.data?.status ?? null;
  // From an effect, not from render — recording a status is a side effect and React may render
  // twice. Guarded by a ref rather than the callback identity: the parent passes an inline
  // arrow, so a dependency on it would re-fire on every parent render.
  const reportedRef = useRef<string | null>(null);
  const onStatusObservedRef = useRef(onStatusObserved);
  onStatusObservedRef.current = onStatusObserved;
  useEffect(() => {
    if (status && reportedRef.current !== status) {
      reportedRef.current = status;
      onStatusObservedRef.current?.(status);
    }
  }, [status]);

  const items: readonly CatalogDraftItem[] = query.data?.items ?? [];

  function change(itemId: string, patch: Partial<CatalogItemEdit>) {
    setDrafts(d => ({ ...d, [itemId]: { ...d[itemId], ...patch } }));
  }

  // Only genuinely-changed fields travel: an unchanged field omitted is not the same as one
  // cleared, and resending everything makes an accidental overwrite look like an edit.
  const pending: CatalogItemEdit[] = items.flatMap(item => {
    const d = drafts[item.item_id ?? ''];
    if (!d) return [];
    const patch: CatalogItemEdit = { itemId: item.item_id ?? '' };
    let changed = false;
    if (d.name !== undefined && d.name !== item.name) { patch.name = d.name; changed = true; }
    if (d.description !== undefined && d.description !== item.description) {
      patch.description = d.description; changed = true;
    }
    if (d.priceMinor !== undefined && d.priceMinor !== item.price_minor) {
      patch.priceMinor = d.priceMinor; changed = true;
    }
    return changed ? [patch] : [];
  });

  const diff: DiffItem[] = items.map(item => {
    const p = pending.find(e => e.itemId === item.item_id);
    return {
      id: item.item_id ?? '',
      name: p?.name ?? item.name ?? '',
      kind: p ? 'changed' : 'unchanged',
      meta: p?.name && p.name !== item.name ? `was “${item.name}”` : undefined,
      before: money(item.price_minor, item.currency),
      after: money(p?.priceMinor ?? item.price_minor, item.currency),
    };
  });

  return (
    <div className="mt-[12px] rounded-[12px] p-[13px]" style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
      <div className="flex items-center justify-between gap-[10px] mb-[11px]">
        <div className="text-[13px] font-semibold text-ink">Review draft items</div>
        <GhostButton onClick={onClose}>Close</GhostButton>
      </div>

      {status && (
        <div className="mb-[12px] overflow-x-auto">
          {/* The catalog's REAL status, read from the server — not inferred from our own writes. */}
          <Stepper
            steps={stepsFor(status)}
            currentId={status}
            haltedIds={Object.keys(D4_TERMINAL)}
          />
        </div>
      )}

      {query.isPending && (
        <div className="flex items-center gap-[8px] text-[12.5px] text-t2 py-[10px]">
          <Spinner size={13} /> Loading the draft…
        </div>
      )}

      {query.isError && (
        <ErrorState
          inline
          error={query.error}
          title="Could not read the draft"
          onRetry={() => { void query.refetch(); }}
        />
      )}

      {query.isSuccess && items.length === 0 && (
        <div className="text-[12.5px] text-t3 py-[10px]">
          {/* Empty is a real answer: extraction can legitimately find nothing. */}
          This catalog has no items. Extraction produced none — that is not the same as the
          read failing.
        </div>
      )}

      {items.length > 0 && (
        <>
          <ul className="list-none p-0 m-0 flex flex-col gap-[8px]">
            {items.map(item => {
              const id = item.item_id ?? '';
              const d = drafts[id] ?? {};
              return (
                <li key={id} className="rounded-[10px] border border-cd-border bg-white p-[10px]">
                  <div className="font-mono text-[10.5px] text-t3 mb-[6px] break-all">{id}</div>
                  <div className="flex flex-wrap gap-[8px]">
                    <label className="flex-1 min-w-[160px]">
                      <div className="text-[11px] text-t3 mb-[3px]">Name</div>
                      <input
                        value={d.name ?? item.name ?? ''}
                        onChange={e => change(id, { name: e.target.value })}
                        className="w-full h-[34px] border border-cd-border rounded-[8px] px-[9px] text-[12.5px] text-ink outline-none focus:border-[#2F6BEB]"
                      />
                    </label>
                    <label className="w-[120px]">
                      <div className="text-[11px] text-t3 mb-[3px]">Price (minor)</div>
                      <input
                        inputMode="numeric"
                        value={priceText[id] ?? String(item.price_minor ?? '')}
                        onChange={e => {
                          const raw = e.target.value;
                          setPriceText(t => ({ ...t, [id]: raw }));
                          // Minor units are a whole number in a C# `long?`.
                          //   • `Number("   ")` is 0 and `Number.isFinite(0)` is true, so a
                          //     whitespace-only field would stage a FREE item.
                          //   • `4.50` (dollars, typed by habit) is finite but fractional; it
                          //     stages as 4.5, shows as AUD 0.04, and 400s on the wire.
                          // Only a non-empty, whole, non-negative value is staged; anything
                          // else stages nothing, so the diff shows no change rather than a
                          // wrong one.
                          const n = Number(raw);
                          const usable =
                            raw.trim() !== '' && Number.isInteger(n) && n >= 0;
                          change(id, { priceMinor: usable ? n : undefined });
                        }}
                        aria-invalid={priceInvalid(priceText[id])}
                        className="w-full h-[34px] border border-cd-border rounded-[8px] px-[9px] text-[12.5px] text-ink outline-none focus:border-[#2F6BEB] font-mono"
                      />
                      {priceInvalid(priceText[id]) && (
                        <div className="text-[11px] mt-[3px] leading-[1.4]" style={{ color: '#93231B' }}>
                          Whole minor units only — 5000, not 50.00. Nothing is staged until this
                          is a whole number.
                        </div>
                      )}
                    </label>
                  </div>
                  {item.extraction_confidence !== undefined && item.extraction_confidence !== null && (
                    <div className="text-[11px] text-t3 mt-[5px]">
                      Extractor confidence {Math.round(item.extraction_confidence * 100)}% — check
                      the low ones against the physical menu.
                    </div>
                  )}
                </li>
              );
            })}
          </ul>

          <div className="mt-[13px]">
            <div className="text-[12px] font-semibold mb-[6px]" style={{ color: '#55554F' }}>
              What will be sent
            </div>
            <DiffView items={diff} emptyLabel="Nothing has been changed yet." />
          </div>

          {edit.isError && (
            <div className="mt-[10px]">
              <ErrorState
                inline
                error={edit.error}
                title={menuActionFailureMessage(edit.error, "save these edits")}
                onRetry={() => edit.mutate({ catalogId, edits: pending })}
              />
            </div>
          )}

          {edit.isSuccess && pending.length === 0 && (
            <div role="status" className="mt-[10px] text-[12px]" style={{ color: '#0B6431' }}>
              Edits saved. The catalog is still in review.
            </div>
          )}

          <div className="mt-[12px]">
            <PrimaryButton
              disabled={pending.length === 0 || edit.isPending}
              busy={edit.isPending}
              onClick={() => edit.mutate(
                { catalogId, edits: pending },
                { onSuccess: () => { setDrafts({}); setPriceText({}); } }
              )}
            >
              {pending.length === 0 ? 'No changes to save' : `Save ${pending.length} change${pending.length === 1 ? '' : 's'}`}
            </PrimaryButton>
          </div>
        </>
      )}
    </div>
  );
}

/** True when the operator has typed something that cannot be minor units. Empty is not invalid. */
function priceInvalid(raw: string | undefined): boolean {
  if (raw === undefined || raw.trim() === '') return false;
  const n = Number(raw);
  return !Number.isInteger(n) || n < 0;
}

/**
 * Minor units to a readable amount, from the shared formatter.
 *
 * `DiffView` compares `before`/`after` as strings, so this path needs text rather than the
 * `MoneyAmount` element — but it must be the *same* conversion, or a price could diff against
 * itself purely because two formatters disagreed on how to render it.
 */
const money = formatMoney;
