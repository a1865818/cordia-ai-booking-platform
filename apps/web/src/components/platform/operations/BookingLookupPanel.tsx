import { useRef, useState } from "react";
import { Panel } from "../../ui/Panel";
import { Icon } from "../../ui/Icon";
import { ErrorState } from "../../ui/ErrorState";
import { PrimaryButton } from "../../ui/Button";
import { Segmented } from "../../ui/Segmented";
import { CorrelationIdChip } from "../../ui/CorrelationIdChip";
import {
  EMPTY_LOOKUP_HINT, findBookings, lookupStatus,
  type BookingCandidate, type FindBookingsResponse, type LookupKind,
} from "../../../features/bookings/lookup";
import { looksUnnormalisedPhone } from "../../ui/PhoneInput";

const KINDS: { value: LookupKind; label: string; placeholder: string }[] = [
  { value: "phone", label: "Phone", placeholder: "+61400111222" },
  { value: "reference", label: "Reference", placeholder: "CORD-1234" },
  { value: "email", label: "Email", placeholder: "guest@example.com" },
];

/**
 * Identity-first booking lookup (spec 08 §9.4.1 — docs §13 item 4).
 *
 * A guest rings back with a number and nothing else; this is how an operator gets from that to
 * their booking. The endpoint has existed and worked all along — no console screen called it.
 *
 * Three things this deliberately does NOT do:
 *
 *  • **It does not search as you type.** Discovery reads another person's booking; it is an
 *    operator action, not an autocomplete.
 *  • **It does not treat a result as authorization.** Finding a booking says nothing about
 *    whether the caller may change it — every mutation still needs its own verification grant.
 *    The panel says so rather than offering actions it cannot back.
 *  • **It does not present the candidate as a customer record.** The server minimises it to
 *    disambiguation material on purpose (spec 08 §4).
 */
export function BookingLookupPanel() {
  const [kind, setKind] = useState<LookupKind>("phone");
  const [value, setValue] = useState("");
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<FindBookingsResponse | null>(null);
  const [searched, setSearched] = useState<string | null>(null);
  const [failure, setFailure] = useState<{ error: unknown; retry: () => void } | null>(null);

  const active = KINDS.find(k => k.value === kind)!;
  const trimmed = value.trim();

  // Advisory only. A national number is still searched — it simply will not match a booking
  // stored in international form, because neither side guesses a country (see PhoneMatching).
  //
  // Shares the create drawer's rule rather than keeping a looser one: this previously accepted
  // anything beginning with "+", so `+0…` and `+1` passed here while the drawer flagged them —
  // the same number could be warned about on one screen and not the other.
  const phoneLooksLocal = kind === "phone" && looksUnnormalisedPhone(value);

  /**
   * One in-flight lookup at a time, and only the LATEST one may write to the screen.
   *
   * Two lookups race independently, so whichever resolved last won — which is not necessarily the
   * one the operator is looking at. A slow answer for a previous phone number could overwrite a
   * fast answer for the current one, and the panel would show candidates for an identifier that is
   * no longer in the box. On a surface whose entire job is deciding WHICH booking a caller means,
   * that is the worst thing it can do.
   *
   * Every state setter is guarded by the token check, including the `finally` — a superseded
   * request must not clear the spinner belonging to the request that replaced it.
   */
  const inFlight = useRef<AbortController | null>(null);

  async function run() {
    inFlight.current?.abort();
    const controller = new AbortController();
    inFlight.current = controller;

    setLoading(true);
    setFailure(null);
    setResult(null);
    setSearched(trimmed);
    try {
      const response = await findBookings({ kind, value: trimmed }, controller.signal);
      if (inFlight.current === controller) setResult(response);
    } catch (error) {
      // An abort is this component superseding itself, not a failure to report.
      if (controller.signal.aborted) return;
      if (inFlight.current === controller) setFailure({ error, retry: () => { void run(); } });
    } finally {
      if (inFlight.current === controller) setLoading(false);
    }
  }

  const candidates: readonly BookingCandidate[] = result?.candidates ?? [];
  const status = lookupStatus(result ?? undefined);

  return (
    <Panel style={{ marginBottom: 18, padding: '15px' }}>
      <div className="flex items-start gap-[11px] mb-[13px]">
        <span className="w-[34px] h-[34px] rounded-[10px] flex items-center justify-center shrink-0"
          style={{ background: '#F1F1EE' }}>
          <Icon name="search" size={17} strokeWidth={2.1} color="#0A0A0A" />
        </span>
        <div className="min-w-0">
          <div className="text-[13.5px] font-semibold text-ink">Find a booking</div>
          <div className="text-[12px] text-t2 mt-[3px] leading-[1.5]">
            For a guest who rings back. Finding a booking is not permission to change it — a
            modification or cancellation still needs the guest verified.
          </div>
        </div>
      </div>

      <div className="mb-[10px]">
        <Segmented
          value={kind}
          onChange={v => { setKind(v as LookupKind); setResult(null); setFailure(null); }}
          options={KINDS.map(k => ({ value: k.value, label: k.label }))}
        />
      </div>

      <label className="block mb-[12px]">
        <div className="text-[12px] font-semibold mb-[5px]" style={{ color: '#55554F' }}>
          {active.label}
        </div>
        <input
          value={value}
          onChange={e => setValue(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && trimmed) void run(); }}
          placeholder={active.placeholder}
          inputMode={kind === "phone" ? "tel" : "text"}
          className="w-full h-[38px] border border-cd-border rounded-[9px] px-3 text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors"
        />
        {phoneLooksLocal && (
          <div className="text-[11.5px] mt-[5px] leading-[1.5]" style={{ color: '#7E5606' }}>
            Bookings are stored as typed, and neither side guesses a country — a number without
            <strong> +country</strong> will not match one saved with it.
          </div>
        )}
      </label>

      <PrimaryButton disabled={trimmed.length === 0 || loading} busy={loading} onClick={() => { void run(); }}>
        Search
      </PrimaryButton>

      {failure && (
        <div className="mt-[12px]">
          <ErrorState inline error={failure.error} title="Could not search" onRetry={failure.retry} />
        </div>
      )}

      {result && candidates.length === 0 && (
        <div role="status" className="mt-[12px] text-[12.5px] text-t2 leading-[1.5]">
          {/* Empty is a real answer — but it has two causes, and the operator needs both. */}
          {EMPTY_LOOKUP_HINT}
        </div>
      )}

      {candidates.length > 0 && (
        <div className="mt-[13px]">
          {status === "multiple" && (
            <div className="text-[12px] mb-[8px] leading-[1.5]" style={{ color: '#7E5606' }}>
              More than one booking matches {searched}. Confirm which one with the guest before
              acting — do not assume the first.
            </div>
          )}
          <ul className="list-none p-0 m-0 flex flex-col gap-[7px]">
            {candidates.map(c => (
              <li
                key={c.booking_id ?? c.booking_reference}
                className="rounded-[10px] border border-cd-border bg-white p-[10px]"
              >
                <div className="text-[13px] font-semibold text-ink">
                  {c.business} · {c.booking_reference}
                </div>
                {/* Exactly the fields the contract returns — no lifecycle status, because the
                    minimised candidate does not carry one and inventing "confirmed" here would
                    be asserting something the API never said. */}
                <div className="text-[12px] text-t2 mt-[3px]">
                  {c.date} · {c.approx_time} · party of {c.party_size}
                </div>
                {c.booking_id && (
                  <div className="mt-[7px]">
                    <CorrelationIdChip label="booking" value={c.booking_id} />
                  </div>
                )}
              </li>
            ))}
          </ul>
          <div className="text-[11px] text-t3 mt-[9px] leading-[1.5]">
            This is everything the platform will disclose before the guest is verified — a
            reference, a venue, a time and a party size, and no contact details (spec 08 §4).
          </div>
        </div>
      )}
    </Panel>
  );
}
