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
  /**
   * Reports whether a save is in flight, so the panel can gate the row's lifecycle actions.
   *
   * Without it the two halves of the same row could run at once: Save here and Approve there are
   * separate mutations with separate pending flags, so an owner could stage a price correction,
   * click Save, and click Approve before it returned.
   */
  onSavingChange?: (saving: boolean) => void;
  /**
   * True while the panel has a lifecycle transition in flight, which must block a save.
   *
   * The mirror of `onSavingChange`, and needed for the same reason: `edit.isPending` alone
   * leaves Save fully clickable while an Approve is running, so the same race just runs the
   * other way round and the edit is refused by a catalog that has already left `in_review`.
   */
  locked?: boolean;
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
export function CatalogDraftEditor({
  catalogId, onClose, onStatusObserved, onSavingChange, locked,
}: CatalogDraftEditorProps) {
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

  // Same ref-not-dependency reasoning as `onStatusObserved` above: the panel passes an inline
  // setter, so depending on the callback identity would re-fire on every parent render.
  const onSavingChangeRef = useRef(onSavingChange);
  onSavingChangeRef.current = onSavingChange;
  const saving = edit.isPending;
  useEffect(() => {
    onSavingChangeRef.current?.(saving);
    // Reports FALSE on unmount, and that is load-bearing rather than tidiness. This editor is
    // unmounted the moment its row loses `canEdit` — which is exactly what an Approve does — so
    // a save in flight at that instant would leave the panel holding `saving: true` with no
    // component left to ever clear it, disabling every action on every row for the rest of the
    // session. The cleanup also runs between renders, where the following effect immediately
    // re-reports the current value, so the steady state is unaffected.
    return () => onSavingChangeRef.current?.(false);
  }, [saving]);

  const items: readonly CatalogDraftItem[] = query.data?.items ?? [];

  function change(itemId: string, patch: Partial<CatalogItemEdit>) {
    setDrafts(d => ({ ...d, [itemId]: { ...d[itemId], ...patch } }));
  }

  // Only genuinely-changed fields travel: an unchanged field omitted is not the same as one
  // cleared, and resending everything makes an accidental overwrite look like an edit.
  const pending: CatalogItemEdit[] = items.flatMap(item => {
    const d = drafts[item.item_id ?? ''];
    if (!d) return [];
    // An item the read could not identify cannot be edited at all, so it must not reach the wire.
    // `item_id` is `string | null` on the generated contract, and a null collapsed to `''` here —
    // which then failed `pending.find(e => e.itemId === item.item_id)` in the diff below, so Save
    // offered "1 change" that the preview showed as nothing. Worse, `ApplyItemEditsAsync` throws
    // `CatalogItemNotFoundException` the moment one edit affects zero rows, and the whole batch
    // rolls back — one unidentifiable item would discard every other correction in the save.
    if (!item.item_id) return [];

    const patch: CatalogItemEdit = { itemId: item.item_id };
    let changed = false;
    // Normalised the same way as the description below, and for the same reason: the generated
    // contract types BOTH as `string | null`, and a null renders as `''` in a controlled input.
    // Clicking into an unnamed item and back out otherwise staged `name: ''` against a stored
    // `null` — a write that changes nothing, presented as a change. Clearing a REAL name still
    // stages `''`.
    if (d.name !== undefined && (d.name ?? '') !== (item.name ?? '')) {
      patch.name = d.name; changed = true;
    }
    // `null` and `''` both mean "no description", so moving between them is not an edit.
    // A stored `null` rendered as `''` in the field, so clicking into an empty description and
    // back out staged `description: ''` — a write that changes nothing, shown to the operator as
    // a change they are asked to approve. A genuine CLEAR (real text emptied) still stages `''`,
    // which is the point of sending only changed fields rather than everything.
    if (d.description !== undefined && (d.description ?? '') !== (item.description ?? '')) {
      patch.description = d.description; changed = true;
    }
    if (d.priceMinor !== undefined && d.priceMinor !== item.price_minor) {
      patch.priceMinor = d.priceMinor; changed = true;
    }
    return changed ? [patch] : [];
  });

  const diff: DiffItem[] = items.map(item => {
    const p = pending.find(e => e.itemId === item.item_id);
    const priceMoved = p?.priceMinor !== undefined;
    return {
      id: item.item_id ?? '',
      // `||`, not `??`. A cleared name stages `''`, and `??` would take it — leaving the row that
      // needs the MOST scrutiny with no heading at all, just a "Changed" badge. The previous name
      // keeps the row identifiable, and `describeEdit` says it was cleared.
      //
      // Flattened at each step, so the emptiness test here agrees with `describeEdit`'s. Raw
      // truthiness disagreed: a staged `" "` is truthy, so the heading became a blank space while
      // `describeEdit` — which flattens — called the same value empty. The row rendered with no
      // visible title at all and a badge claiming a name had been added.
      name: oneLine(p?.name ?? '') || oneLine(item.name ?? '') || (item.item_id ?? ''),
      kind: p ? 'changed' : 'unchanged',
      meta: describeEdit(p, item),
      // ONLY when the price actually moved. Computed unconditionally, a description-only edit
      // rendered the old price struck through beside an identical new one — and in this
      // component's own vocabulary a strikethrough means "value being replaced", so the diff
      // asserted a price change nobody made, on the screen whose entire job is to make an
      // unintended change impossible to miss.
      before: priceMoved ? money(item.price_minor, item.currency) : undefined,
      after: priceMoved ? money(p.priceMinor, item.currency) : undefined,
    };
  });

  return (
    <div className="mt-[12px] rounded-[12px] p-[13px]" style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
      <div className="flex items-center justify-between gap-[10px] mb-[11px]">
        <div className="text-[13px] font-semibold text-ink">Review draft items</div>
        {/*
          Gated on the save, and this is the counterexample to the comment on the effect above.
          The panel disables its own "Hide draft" toggle while anything is in flight, but Close
          lived here and did not: clicking it during a save unmounted this editor, the cleanup
          reported `saving: false`, and Approve went live again while the `POST .../edit` was
          still on the wire — React Query does not cancel a mutation on unmount, and this call
          carries no abort signal. The row's transitions would then race the very edit the panel
          thought had finished.
        */}
        <GhostButton
          onClick={onClose}
          disabled={edit.isPending}
          title={edit.isPending
            ? 'Waiting for the save to finish — closing now would let the catalog be approved before the edit lands.'
            : undefined}
        >
          Close
        </GhostButton>
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
              // `POST .../edit` keys on this id, so an item the read could not identify cannot be
              // corrected at all. `pending` already refuses to stage one (it would fail the whole
              // batch server-side); disabling the inputs stops the operator typing a correction
              // that silently goes nowhere.
              const editable = Boolean(item.item_id);
              return (
                <li key={id} className="rounded-[10px] border border-cd-border bg-white p-[10px]">
                  <div className="font-mono text-[10.5px] text-t3 mb-[6px] break-all">
                    {id || 'This item has no id and cannot be edited.'}
                  </div>
                  <div className="flex flex-wrap gap-[8px]">
                    <label className="flex-1 min-w-[160px]">
                      <div className="text-[11px] text-t3 mb-[3px]">Name</div>
                      <input
                        value={d.name ?? item.name ?? ''}
                        onChange={e => change(id, { name: e.target.value })}
                        // Per-item accessible names on all three controls. The visible labels are
                        // one word each, so a screen-reader user moving control-to-control through
                        // a 30-item draft heard "Name, edit text" thirty times with nothing saying
                        // which item they were on — the id above is a plain div, in no control's
                        // accessible name.
                        aria-label={`Name for ${item.name || id}`}
                        disabled={!editable}
                        className="w-full h-[34px] border border-cd-border rounded-[8px] px-[9px] text-[12.5px] text-ink outline-none focus:border-[#2F6BEB]"
                      />
                    </label>
                    <label className="w-[120px]">
                      <div className="text-[11px] text-t3 mb-[3px]">Price (minor)</div>
                      <input
                        inputMode="numeric"
                        aria-label={`Price in minor units for ${item.name || id}`}
                        disabled={!editable}
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
                  {/*
                    Full width and a textarea, unlike the two fields above: a description is the
                    sentence the extractor pulled off the menu ("45 minutes, includes soak-off"),
                    not a token. Sharing the row with the price would truncate exactly the text
                    the operator opened this editor to check.

                    `CatalogItemEdit` has carried `description` and `pending` has staged it since
                    this editor was written — there was simply no input, so that branch was
                    unreachable. An extractor mistake in a description could be neither seen nor
                    corrected here, and it is frozen into the catalog on approval along with the
                    item it belongs to.
                  */}
                  <label className="block mt-[8px]">
                    <div className="text-[11px] text-t3 mb-[3px]">Description</div>
                    <textarea
                      rows={2}
                      value={d.description ?? item.description ?? ''}
                      onChange={e => change(id, { description: e.target.value })}
                      aria-label={`Description for ${item.name || id}`}
                        disabled={!editable}
                      // Which of the two empty cases this is — and it is not always the same one.
                      // A blank box right after a deliberate clear was still reading "No
                      // description was extracted", which is the opposite of true: one WAS
                      // extracted and is about to be removed.
                      placeholder={item.description
                        ? 'Cleared — saving will remove this description.'
                        : 'No description was extracted.'}
                      className="w-full border border-cd-border rounded-[8px] px-[9px] py-[6px] text-[12.5px] text-ink outline-none focus:border-[#2F6BEB] resize-y leading-[1.45]"
                    />
                  </label>
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
                // DISABLED while a transition runs, not withdrawn. Removing `onRetry` deleted the
                // button from the DOM, so an operator tabbed onto it lost focus to `<body>` the
                // moment an unrelated Publish started, and the control reappeared elsewhere in
                // the tab order when that settled. Same reasoning as the Save button below.
                onRetry={() => edit.mutate({ catalogId, edits: pending })}
                retryDisabled={locked}
                retryTitle="A catalog action is still running — retry once it finishes."
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
              // `locked` closes the other half of the race the panel's `anyBusy` closes for the
              // row buttons: an Approve already in flight will take the catalog out of
              // `in_review`, so a save started now is refused by the time it lands.
              disabled={pending.length === 0 || edit.isPending || locked}
              busy={edit.isPending}
              // Says WHY. Under `locked` the button greys out with no spinner and its label
              // unchanged, which is indistinguishable from a broken control — and the staged
              // edits are still sitting in the form, so an operator who reloads to "fix" it
              // loses them.
              title={locked && pending.length > 0
                ? 'A catalog action is still running — this will be available when it finishes.'
                : undefined}
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

/**
 * Which fields moved, in words, for the diff row's secondary line.
 *
 * The row's before/after columns can only carry the PRICE, so a name-only or description-only
 * edit rendered as a row badged "Changed" whose two visible numbers were identical — a change
 * the operator is asked to approve with nothing on screen saying what it was. That was tolerable
 * while name was the only other editable field and its old value was shown here; with
 * descriptions editable it is not.
 *
 * Price is deliberately absent: the columns already show it, and repeating it in words would
 * bury the two changes they cannot show.
 */
function describeEdit(
  edit: CatalogItemEdit | undefined,
  item: CatalogDraftItem
): string | undefined {
  if (!edit) return undefined;

  const parts: string[] = [];

  // `!== undefined`, not truthiness. The old test was `p?.name && …`, so clearing a name to the
  // empty string — a real change, and a destructive one — produced no explanation at all.
  //
  // Every branch below tests the FLATTENED value rather than the raw one, because a whitespace-
  // only field is empty to a reader and to the diff line: `" "` is truthy, so a truthiness test
  // sent it down the "had a value" path and rendered `was “”` — an empty quotation.
  // ORDER MATTERS, and getting it wrong is how an earlier version of this line lied.
  //
  // `before === after` has to be tested FIRST. Both values are flattened, so two that differ only
  // in whitespace collapse to the same string — including to the same EMPTY string. Testing
  // `!before` ahead of it sent those down the "added" branch, so deleting an extractor-produced
  // "  " announced `description added: “”`: a destructive removal reported as an addition of
  // nothing, which is the exact removal-vs-correction distinction these lines exist to draw.
  if (edit.name !== undefined) {
    const before = oneLine(item.name ?? '');
    const after = oneLine(edit.name);
    if (before === after) parts.push('name: spacing only');
    else if (!before) parts.push('name added');
    else if (!after) parts.push(`name cleared (was “${clip(before)}”)`);
    // The NEW name is the diff row's heading, so only the old one needs restating here — but it
    // is windowed against the new one, so a correction late in a long name is not clipped away.
    else parts.push(`name was “${diffWindow(before, after).before}”`);
  }

  if (edit.description !== undefined) {
    const before = oneLine(item.description ?? '');
    const after = oneLine(edit.description);
    // Collapsing whitespace can make two genuinely different stored values render identically.
    // The write is real — the server keeps the raw text — so it is named rather than shown as a
    // pair of identical quotations.
    if (before === after) parts.push('description: spacing only');
    // Both sides below, because the description appears NOWHERE else in the row: `before`/`after`
    // carry the price and the heading carries the name. Saying only `description was “45 minutes”`
    // rendered a deletion and a correction as byte-identical text, so the approval surface could
    // not tell a destructive removal from a fix.
    else if (!before) parts.push(`description added: “${clip(after)}”`);
    else if (!after) parts.push(`description cleared (was “${clip(before)}”)`);
    else {
      const w = diffWindow(before, after);
      parts.push(`description: “${w.before}” → “${w.after}”`);
    }
  }

  return parts.length ? parts.join(' · ') : undefined;
}

/**
 * One line. A description can be several sentences and can carry newlines from the textarea; the
 * diff's secondary line is a single row, so an unflattened value would break the layout of the
 * very list that is meant to make a change obvious.
 */
function oneLine(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

/**
 * Grapheme clusters — what a reader would call "one character".
 *
 * Slicing by UTF-16 unit leaves a lone surrogate that renders as a replacement glyph. Code points
 * fix that much, but they are not the whole story and it would be easy to stop there believing
 * otherwise: `Array.from` still splits a ZWJ emoji sequence into its parts (👨‍👩‍👧 is five code
 * points), a flag into two regional indicators, and an accent off its letter (e + ́ ). A cut at any
 * of those boundaries strands a bare combining mark or turns one glyph into three.
 *
 * `Intl.Segmenter` is the only thing that matches what is actually on screen. The code-point
 * fallback is for a runtime without it, where the lone-surrogate case — the one that renders as
 * visibly broken text rather than merely wrong text — is still handled.
 */
/**
 * The slice of `Intl.Segmenter` used here, declared locally.
 *
 * `apps/web` compiles against `lib: ES2020`, which predates the built-in typing. Raising the
 * whole app's lib to reach one runtime API would change the emit target for every file — a real
 * blast radius for a diff-preview helper — so the shape it needs is stated here instead, and the
 * feature test below is what decides whether it is actually used.
 */
interface GraphemeSegmenter {
  segment(input: string): Iterable<{ segment: string }>;
}

type GraphemeSegmenterCtor =
  new (locale?: string, options?: { granularity: 'grapheme' }) => GraphemeSegmenter;

const graphemeSegmenter: GraphemeSegmenter | null = (() => {
  const ctor = (Intl as unknown as { Segmenter?: GraphemeSegmenterCtor }).Segmenter;
  // Built ONCE: constructing a Segmenter is expensive and this runs per keystroke, per item.
  return typeof ctor === 'function' ? new ctor(undefined, { granularity: 'grapheme' }) : null;
})();

function graphemes(text: string): string[] {
  if (!graphemeSegmenter) return Array.from(text);
  return Array.from(graphemeSegmenter.segment(text), s => s.segment);
}

/** How much of a value the diff line may show, and how much unchanged context to keep around the change. */
const WINDOW_MAX = 48;
const WINDOW_CONTEXT = 8;

/**
 * Two values windowed onto the span where they actually DIFFER.
 *
 * Truncating each from the start hid the change outright: two descriptions sharing a long opening
 * clause rendered as `“Gel manicure, includes a full soak-off and…” → “Gel manicure, includes a
 * full soak-off and…”` — identical text on both sides of an arrow, on the screen whose only job
 * is to show the operator what they are about to approve. The full replacement was saved
 * regardless, so the preview was not merely unhelpful, it was wrong.
 *
 * The window is computed from the SHARED prefix and suffix, so both sides are cut at the same
 * logical place and the surviving fragments line up for comparison. A little unchanged context is
 * kept on each side so the change reads in situ rather than as a floating fragment.
 */
function diffWindow(beforeText: string, afterText: string): { before: string; after: string } {
  const a = graphemes(beforeText);
  const b = graphemes(afterText);

  let prefix = 0;
  while (prefix < a.length && prefix < b.length && a[prefix] === b[prefix]) prefix++;

  // Bounded by what the prefix already consumed, so the two spans can never overlap on a value
  // like "aaa" → "aaaaa", where the same characters would otherwise be claimed twice.
  let suffix = 0;
  while (
    suffix < a.length - prefix &&
    suffix < b.length - prefix &&
    a[a.length - 1 - suffix] === b[b.length - 1 - suffix]
  ) {
    suffix++;
  }

  return { before: windowOf(a, prefix, suffix), after: windowOf(b, prefix, suffix) };
}

/** The visible slice of one value, with an ellipsis wherever text was elided. */
function windowOf(value: readonly string[], prefix: number, suffix: number): string {
  if (value.length <= WINDOW_MAX) return value.join('');

  // `end > start` is guaranteed, not defended: the suffix loop is bounded by what the prefix
  // already consumed, so `prefix + suffix <= value.length` for both values, which makes
  // `end >= prefix + WINDOW_CONTEXT > prefix - WINDOW_CONTEXT = start`. A dead `end <= start`
  // branch used to sit here reading as load-bearing; if that suffix bound is ever relaxed to
  // allow the two spans to overlap, THIS is the invariant that breaks and the slice inverts.
  const start = Math.max(0, prefix - WINDOW_CONTEXT);
  let end = Math.min(value.length, value.length - suffix + WINDOW_CONTEXT);

  // The differing span can itself be longer than the budget; keep its head rather than nothing.
  if (end - start > WINDOW_MAX) end = Math.min(value.length, start + WINDOW_MAX);

  return (start > 0 ? '…' : '')
    + value.slice(start, end).join('')
    + (end < value.length ? '…' : '');
}

/** A single value with no counterpart to align to — head-truncated. */
function clip(text: string): string {
  return windowOf(graphemes(text), 0, 0);
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
