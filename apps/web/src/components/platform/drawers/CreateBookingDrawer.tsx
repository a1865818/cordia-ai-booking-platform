import React, { useEffect, useRef, useState } from "react";
import { Service, STAFF, CUSTOMERS, fmtT, fmtDur, money, TIME_SLOTS, Booking, type Customer } from "../../../data/console";
import { dayOptions, shortDayLabel, type ConsoleWeek } from "../../../features/bookings/week";
import type { CreatePrefill } from "../../../features/waitlist/prefill";
import { Drawer } from "../../ui/Drawer";
import { PrimaryButton } from "../../ui/Button";
import { Icon } from "../../ui/Icon";
import { Spinner } from "../../ui/Spinner";
import { ErrorState } from "../../ui/ErrorState";
import { buildSubmission, toIsoInstant } from "../../../features/bookings/buildSubmission";
import { getOrCreateAttempt, resetAttempt } from "../../../features/bookings/identity";
import { submitBooking } from "../../../features/bookings/submit";
import {
  isBooked, normalizedStatusLabel, useOperationStatus,
} from "../../../features/bookings/useOperationStatus";
import { isApiIdempotencyConflictError } from "../../../api/problem";
import { getActor } from "../../../api/config";

const inputStyle: React.CSSProperties = {
  width: '100%', height: 42, border: '1px solid #EAEAEA', borderRadius: 10,
  padding: '0 12px', fontFamily: "'Inter Tight', system-ui, sans-serif",
  fontSize: 14, color: '#0A0A0A', background: '#fff', outline: 'none',
};
const selectStyle: React.CSSProperties = { ...inputStyle, appearance: 'none', cursor: 'pointer', paddingRight: 34 };

function Field({ label, children, hint }: { label: string; children: React.ReactNode; hint?: string }) {
  return (
    <label className="block mb-[13px]">
      <div className="text-[12px] font-semibold mb-[6px]" style={{ color: '#55554F' }}>{label}</div>
      {children}
      {hint && <div className="text-[11.5px] text-t3 mt-[5px]">{hint}</div>}
    </label>
  );
}

function SelectField({ value, onChange, options }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[] }) {
  return (
    <div className="relative">
      <select value={value} onChange={e => onChange(e.target.value)} style={selectStyle}>
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
      <span className="absolute right-3 top-1/2 -translate-y-1/2 pointer-events-none">
        <Icon name="chevD" size={14} strokeWidth={2.2} color="#9A9A9A" />
      </span>
    </div>
  );
}

interface CreateBookingDrawerProps {
  open: boolean;
  device: 'phone' | 'desktop';
  /**
   * The displayed week. The day picker is dated from it AND the submitted instant is anchored
   * to it — anchoring to `startOfWeek(new Date())` instead meant a booking made while viewing
   * next week was silently submitted for this week.
   */
  week: ConsoleWeek;
  onClose: () => void;
  onCreated: (b: Omit<Booking, 'id'>) => void;
  services: Service[];
  /** The tenant's customers, or `undefined` while the list has never loaded. */
  customers?: Customer[];
  /**
   * Defaults to open with, e.g. when booking a specific waitlisted guest. Applied on open
   * only — the operator can change anything before submitting.
   */
  prefill?: CreatePrefill;
}

export function CreateBookingDrawer({ open, device, week, onClose, onCreated, services, customers, prefill }: CreateBookingDrawerProps) {
  const [name, setName] = useState('');
  const [customerSearch, setCustomerSearch] = useState('');
  const [showDropdown, setShowDropdown] = useState(false);
  const [svc, setSvc] = useState('');
  const [staff, setStaff] = useState('s1');
  // Defaults to today's column when the displayed week is the current one, else Monday.
  const [day, setDay] = useState(String(week.todayIndex ?? 0));
  const [time, setTime] = useState('600');
  // Party size is required by the API's BookingRequest; the mock form never collected it.
  const [partySize, setPartySize] = useState('2');
  const [phase, setPhase] = useState<'form' | 'confirming' | 'done'>('form');
  const [submitError, setSubmitError] = useState<unknown>(null);
  /** A prefilled service id absent from this venue's list; surfaced rather than swapped. */
  const [unmatchedService, setUnmatchedService] = useState<string | null>(null);
  const [operationId, setOperationId] = useState<string | null>(null);
  const [startedAt, setStartedAt] = useState<number | null>(null);

  // One attempt identity per open drawer: the operation id and idempotency key are minted
  // before the first call and reused on every retry (Inv-2). `attemptKey` changes only
  // when the drawer is reopened for a genuinely new booking.
  const [attemptKey, setAttemptKey] = useState(() => `create:${Date.now()}`);
  // Guards the one-shot 'booking confirmed' announcement; see the effect below.
  const announcedRef = useRef<string | null>(null);

  const poll = useOperationStatus(operationId, startedAt);
  const booked = isBooked(poll.status);

  useEffect(() => {
    if (open) {
      setName(prefill?.name ?? ''); setCustomerSearch('');
      // A prefilled service that is not in the list is REPORTED, never silently swapped. The
      // services list is the hand-written mock catalogue, so a real enrolment's service_id
      // almost never matches — quietly falling back to services[0] meant an operator who
      // clicked a row reading "SNS dipping powder" submitted a durable booking for "Gel
      // manicure", with the rest of the form prefilled and equally trustworthy-looking.
      const prefilledService = services.find(s => s.key === prefill?.serviceId)?.key;
      setUnmatchedService(
        prefill?.serviceId && !prefilledService ? prefill.serviceId : null
      );
      setSvc(prefilledService ?? services[0]?.key ?? ''); setStaff('s1');
      setDay(String(prefill?.day ?? week.todayIndex ?? 0));
      setTime(String(prefill?.start ?? 600));
      setPartySize(String(prefill?.partySize ?? 2));
      setPhase('form'); setShowDropdown(false);
      setSubmitError(null); setOperationId(null); setStartedAt(null);
      announcedRef.current = null;
      setAttemptKey(`create:${Date.now()}`);
    }
  }, [open, services, week.todayIndex, prefill]);

  const hasServices = services.length > 0;
  const service = services.find(s => s.key === svc) ?? services[0];
  const eligible = STAFF; // all staff eligible across business types

  // The booking only counts as made when the server says so (Inv-16). Until then the
  // drawer stays in its submitting state, however long that takes.
  //
  // Guarded by a ref, not by `phase`: `onCreated` is a fresh function reference on every
  // parent render, and StrictMode double-invokes effects against the same state snapshot,
  // so a `phase === 'done'` check alone would let the booking be appended twice.
  useEffect(() => {
    if (!booked || announcedRef.current === attemptKey) return;
    announcedRef.current = attemptKey;
    setPhase('done');
    resetAttempt(attemptKey);
    onCreated({
      day: Number(day), staff, start: Number(time), service: svc,
      serviceName: service?.name ?? svc,
      // `prov: 'unknown'` even though this operator just made it: the schedule projection
      // carries no provenance, so the server's row will claim none. Saying "Booked by you"
      // here would make the row change its story the moment the refetch lands.
      name: name.trim(), prov: 'unknown', status: 'confirmed', channel: null,
      dep: service?.dep ?? 0, transcript: null, dur: service?.dur ?? 60,
    });
  }, [booked, phase, attemptKey, day, staff, time, svc, service, name, onCreated]);


  useEffect(() => {
    if (!eligible.find(s => s.id === staff) && eligible[0]) setStaff(eligible[0].id);
  }, [svc]);

  // The tenant's real customers once loaded; the mock array only while the list has never
  // loaded, and then labelled — the command palette was fixed the same way. Offering six
  // invented people as a venue's customer list is the same false claim wherever it appears.
  const showingDemoCustomers = customers === undefined;
  const suggestions = (customers ?? CUSTOMERS).filter(c =>
    c.name.toLowerCase().includes(customerSearch.toLowerCase()) && customerSearch.length > 0
  ).slice(0, 6);

  async function submit() {
    if (phase !== 'form' || !name.trim() || !hasServices) return;

    setPhase('confirming');
    setSubmitError(null);

    // Reusing the stored identity is what makes a retry safe: the same key + same payload
    // replays the original acceptance rather than creating a second operation (§13.4).
    const identity = getOrCreateAttempt(attemptKey);

    try {
      const request = await buildSubmission(
        {
          operation: 'create',
          serviceId: svc,
          startsAt: toIsoInstant(week.start, Number(day), Number(time)),
          partySize: Number(partySize),
          durationMinutes: service?.dur,
        },
        {
          identity,
          sessionId: `console:${identity.intentId}`,
          // The OPERATOR, not the typed guest name.
          //
          // `Confirmation.identity` is documented as "verified identity, or the
          // `anonymous_session` sentinel" (spec 04). A staff booking has no verified guest, and
          // the operator is the confirming party — which is what `verification_level:
          // "staff_confirmed"` already claims, and what cancel and reschedule already send.
          //
          // Sending the typed name here pushed plain-text PII into `identity_ref`, which the
          // platform surfaces as the opaque `customer_ref` on `/api/customers` — a field spec 14
          // minimises precisely so it cannot carry a person's name.
          identityRef: getActor(),
          readBackVersion: `${svc}|${day}|${time}|${partySize}`,
        }
      );

      const accepted = await submitBooking(request);
      setOperationId(accepted.operation_id ?? null);
      setStartedAt(Date.now());
    } catch (error) {
      // A conflict means this key is already bound to a different request — retrying can
      // never succeed, so the operator must start a new booking rather than resubmit.
      if (isApiIdempotencyConflictError(error)) resetAttempt(attemptKey);
      setSubmitError(error);
      setPhase('form');
    }
  }

  const footer = phase === 'done'
    ? <PrimaryButton full onClick={onClose}>Done</PrimaryButton>
    : hasServices
      ? <PrimaryButton
          full
          busy={phase === 'confirming'}
          disabled={!name.trim() || !partySize || phase !== 'form'}
          onClick={() => { void submit(); }}
        >
          {phase === 'confirming' ? 'Submitting' : 'Confirm booking'}
        </PrimaryButton>
      : undefined;

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Manual booking" title="New booking" footer={footer}>
      {!hasServices ? (
        <div className="py-10 text-center">
          <div className="text-[14px] font-semibold text-ink">No services yet</div>
          <div className="text-[12.5px] text-t3 mt-[5px] leading-[1.5]">
            Add a service in Settings before creating a booking.
          </div>
        </div>
      ) : phase === 'confirming' ? (
        <div className="text-center py-8 px-2">
          <span className="w-14 h-14 rounded-full inline-flex items-center justify-center mb-[14px]" style={{ background: '#F2F2F0' }}>
            <Spinner size={22} color="#0A0A0A" track="rgba(10,10,10,0.18)" />
          </span>
          <div className="text-[17px] font-bold tracking-[-0.02em] text-ink" aria-live="polite">
            {normalizedStatusLabel(poll.status?.status)}
          </div>
          <div className="text-[12.5px] text-t2 mt-[5px] leading-[1.5]">
            {poll.status?.customer_message
              ?? 'The booking has been accepted and is being confirmed with the venue.'}
          </div>
          {poll.timedOut && (
            <div className="text-[12px] text-t3 mt-[10px] leading-[1.5]">
              Still working. You can close this — the booking will appear in the queue when it settles.
            </div>
          )}
          {poll.status?.terminal && !booked && (
            <div className="mt-[14px]">
              <ErrorState
                inline
                error={new Error(poll.status?.customer_message ?? 'This booking could not be completed.')}
                title={normalizedStatusLabel(poll.status?.status)}
              />
            </div>
          )}
        </div>
      ) : phase === 'done' ? (
        <div className="text-center py-6 px-2">
          <span className="w-14 h-14 rounded-full inline-flex items-center justify-center mb-[14px]" style={{ background: '#E7F6EC' }}>
            <Icon name="check" size={26} strokeWidth={2.4} color="#15924B" />
          </span>
          <div className="text-[18px] font-bold tracking-[-0.02em] text-ink">Booking confirmed</div>
          <div className="text-[13px] text-t2 mt-[5px] leading-[1.5]">
            {name} · {service?.name}<br />
            {shortDayLabel(week, Number(day))} at {fmtT(Number(time))}
          </div>
          {/* No deposit line. This panel renders only after a terminal `booked` status, and the
              deposit here comes from the hand-written mock catalog — the platform defers
              payments (spec 09) and never requested or collected anything. Telling the
              operator a hold is pending would be a fabricated fact about money. */}
        </div>
      ) : (
        <fieldset className="border-none p-0 m-0">
          {/*
            Customer autocomplete.
            The name is a LABEL FOR THIS SCREEN. There is no customer field on `BookingRequest`
            and `Confirmation.identity` carries the operator, so nothing here reaches the
            platform as guest identity. The hint says so rather than letting the operator
            believe they have recorded who the booking is for.
          */}
          {unmatchedService && (
            <div
              className="mb-[13px] rounded-[10px] p-[10px_12px] text-[12px] leading-[1.5]"
              style={{ background: '#FBF3DF', border: '1px solid #F0DCA8', color: '#7E5606' }}
              role="status"
            >
              This guest asked for <strong>{unmatchedService}</strong>, which is not in the service
              list. Pick the right service below — nothing has been chosen for you.
            </div>
          )}
          <Field
            label="Customer"
            hint="Used on this screen only. Until guest identity capture exists, the platform records this booking against you."
          >
            <div className="relative">
              <input
                value={customerSearch || name}
                onChange={e => {
                  const v = e.target.value;
                  setCustomerSearch(v);
                  setName(v);
                  setShowDropdown(true);
                }}
                onFocus={() => setShowDropdown(true)}
                onBlur={() => setTimeout(() => setShowDropdown(false), 150)}
                placeholder="Search or add a customer"
                className="w-full h-[42px] border border-cd-border rounded-[10px] px-3 text-[14px] text-ink outline-none focus:border-[#2F6BEB] transition-colors"
                style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
              />
              {showDropdown && suggestions.length > 0 && (
                <div className="absolute left-0 right-0 top-[46px] bg-white border border-cd-border rounded-[11px] shadow-[0_8px_24px_-8px_rgba(0,0,0,0.15)] z-10 overflow-hidden">
                  {suggestions.map(c => (
                    <button
                      key={c.id}
                      type="button"
                      onMouseDown={() => { setName(c.name); setCustomerSearch(''); setShowDropdown(false); }}
                      className="flex items-center gap-3 w-full px-3 py-2.5 hover:bg-wash text-left border-none bg-white cursor-pointer transition-colors"
                    >
                      <span className="w-8 h-8 rounded-[9px] bg-wash2 flex items-center justify-center text-[12px] font-semibold text-ink shrink-0">
                        {c.name.split(' ').map(w => w[0]).slice(0, 2).join('').toUpperCase()}
                      </span>
                      <div>
                        <div className="text-[13.5px] font-semibold text-ink">{c.name}</div>
                        <div className="text-[11px] text-t3">
                          {showingDemoCustomers ? 'Demo data' : (c.fav || `${c.visits} bookings`)}
                        </div>
                      </div>
                    </button>
                  ))}
                  <button
                    type="button"
                    onMouseDown={() => setShowDropdown(false)}
                    className="flex items-center gap-2 w-full px-3 py-2.5 hover:bg-wash text-left border-none border-t border-hair bg-white cursor-pointer text-[13px] text-t2 transition-colors"
                  >
                    + Use "{customerSearch}" as the label
                  </button>
                </div>
              )}
            </div>
          </Field>

          <Field label="Service">
            <SelectField value={svc} onChange={setSvc} options={services.map(s => ({ value: s.key, label: `${s.name} · ${fmtDur(s.dur)}` }))} />
          </Field>
          <Field label="With">
            <SelectField value={staff} onChange={setStaff} options={eligible.map(s => ({ value: s.id, label: `${s.name} — ${s.role}` }))} />
          </Field>
          <div className="flex gap-[10px]">
            <div className="flex-1">
              <Field label="Day">
                <SelectField value={day} onChange={setDay} options={dayOptions(week)} />
              </Field>
            </div>
            <div className="flex-1">
              <Field label="Time">
                <SelectField value={time} onChange={setTime} options={TIME_SLOTS} />
              </Field>
            </div>
          </div>
          <Field label="Party size" hint="How many people the booking is for.">
            <input
              type="number"
              min={1}
              max={50}
              value={partySize}
              onChange={e => setPartySize(e.target.value)}
              className="w-full h-[42px] border border-cd-border rounded-[10px] px-3 text-[14px] text-ink outline-none focus:border-[#2F6BEB] transition-colors"
              style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
            />
          </Field>
          {submitError != null && (
            <div className="mb-[13px]">
              <ErrorState inline error={submitError} onRetry={() => { void submit(); }} />
            </div>
          )}
          <div className="flex items-center gap-[10px] p-[12px_13px] rounded-[11px] mt-[3px]"
            style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}>
            <Icon name="card" size={17} strokeWidth={1.9} color="#6B6B6B" />
            <div className="flex-1">
              <div className="text-[12.5px] font-semibold text-ink">{money(service.dep)} deposit</div>
              <div className="text-[11px] text-t3">Mirrors your deposit policy</div>
            </div>
            <span className="inline-flex items-center gap-1 text-[10.5px] font-semibold px-2 py-[3px] rounded-[7px] bg-wash2 text-ink70">Auto</span>
          </div>
        </fieldset>
      )}
    </Drawer>
  );
}
