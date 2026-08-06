import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { CUSTOMERS, fmtT, fmtDur, TIME_SLOTS, Booking, type Customer } from "../../../data/console";
import type { BookableService } from "../../../features/bookings/services";
import { UNASSIGNED_RESOURCE_ID } from "../../../features/bookings/resources";
import { dayOptions, shortDayLabel, type ConsoleWeek } from "../../../features/bookings/week";
import type { AvailableSlot } from "../../../features/bookings/availability";
import type { CreatePrefill } from "../../../features/waitlist/prefill";
import { Drawer } from "../../ui/Drawer";
import { PrimaryButton } from "../../ui/Button";
import { Icon } from "../../ui/Icon";
import { Spinner } from "../../ui/Spinner";
import { ErrorState } from "../../ui/ErrorState";
import { buildSubmission, toIsoInstant } from "../../../features/bookings/buildSubmission";
import { PhoneInput } from "../../ui/PhoneInput";
import { getOrCreateAttempt, resetAttempt } from "../../../features/bookings/identity";
import { submitBooking } from "../../../features/bookings/submit";
import { createGuestContact } from "../../../features/bookings/guestContact";
import { isRetryableWithNewAttempt } from "../../../features/bookings/mutations";
import {
  isBooked, normalizedStatusLabel, useOperationStatus,
} from "../../../features/bookings/useOperationStatus";
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
  /**
   * The venue's bookable services, from the **live** catalog — or `undefined` while it has
   * never loaded. There is deliberately no fall back to the mock `SERVICE_LIST`: its keys
   * exist on no server, and submitting one writes a durable booking against a service the
   * venue does not have. A catalog that loaded and is empty is a real answer ("nothing is
   * bookable yet"), and the drawer says so instead of inventing options.
   */
  services?: BookableService[];
  /** True when the catalog request failed, so "no services" can be told apart from "we don't know". */
  servicesFailed?: boolean;
  onRetryServices?: () => void;
  /** The tenant's customers, or `undefined` while the list has never loaded. */
  customers?: Customer[];
  /**
   * Slots the venue has declared for the displayed week, or `undefined` while never loaded.
   *
   * **Advisory, never a constraint.** The read is spec 07 `informational`: it takes no hold, so
   * a slot it shows as free can be taken before this booking is submitted. It also cannot be
   * made authoritative by wishing — a venue that has declared no slots returns an empty list,
   * and treating that as "fully booked" would report a confident falsehood for every tenant
   * that has not used the declaration surface. So it informs the operator and nothing else:
   * the day and time pickers stay open, and the booking learns its real outcome from the
   * operation.
   */
  availability?: AvailableSlot[];
  /**
   * Defaults to open with, e.g. when booking a specific waitlisted guest. Applied on open
   * only — the operator can change anything before submitting.
   */
  prefill?: CreatePrefill;
}

/**
 * What the venue has said it can take, on the day being booked.
 *
 * The wording is spec 07's `informational` register throughout — "appears available", never
 * "available". This read holds nothing, so anything stronger would promise a slot the platform
 * has not reserved and may hand to someone else before this booking is submitted.
 *
 * The empty case is the one that matters most. `capacity_slots` is filled only by the owner
 * declaration surface, so a venue that has never used it returns `[]` — which means *nothing
 * has been declared*, not *nothing is free*. Rendering that as "fully booked" would be a
 * confident falsehood for every such tenant, so it is stated as the absence of a declaration
 * and the pickers stay open.
 */
function AvailabilityNote({ slots, week, dayIndex }: {
  slots: AvailableSlot[] | undefined;
  week: ConsoleWeek;
  dayIndex: number;
}) {
  // Never loaded: say nothing rather than implying an answer.
  if (slots === undefined) return null;

  const box = "mb-[13px] rounded-[10px] p-[10px_12px] text-[11.5px] leading-[1.5]";
  const style = { background: '#FBFBFA', border: '1px solid #EFEFEF', color: '#55554F' };

  if (slots.length === 0) {
    return (
      <div className={box} style={style}>
        This venue has <strong>declared no capacity</strong>, so nothing here is checked against
        it. That is not the same as being fully booked — it means no slots have been set up.
        Declare them in Settings if you want this to mean something.
      </div>
    );
  }

  const day = week.dates[dayIndex];
  const onThisDay = day
    ? slots.filter(s => sameLocalDay(new Date(s.startsAt), day))
    : [];

  if (onThisDay.length === 0) {
    return (
      <div className={box} style={style}>
        No slots are declared for this day. You can still book it — the venue simply has not
        described its capacity for that date.
      </div>
    );
  }

  const free = onThisDay.filter(s => s.freeCapacity > 0);

  return (
    <div className={box} style={style}>
      {free.length === 0 ? (
        <>Every slot declared for this day <strong>appears full</strong>. You can still submit —
        the check happens when the booking is fulfilled.</>
      ) : (
        <>
          <strong>Appears available</strong> on this day:{' '}
          {free.map(s => `${localTimeLabel(s.startsAt)} (${s.freeCapacity} left)`).join(' · ')}.
          {' '}Times are not held, so one can be taken before you submit.
        </>
      )}
    </div>
  );
}

function sameLocalDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear()
    && a.getMonth() === b.getMonth()
    && a.getDate() === b.getDate();
}

function localTimeLabel(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

export function CreateBookingDrawer({
  open, device, week, onClose, onCreated, services, servicesFailed, onRetryServices, customers,
  availability, prefill,
}: CreateBookingDrawerProps) {
  const [name, setName] = useState('');
  // The guest's phone, unlike their name, DOES reach the platform — but NOT through the payload.
  // It is exchanged for an opaque `contact_ref` via `POST /api/guest-contacts` (see `submit()`),
  // and only that ref enters the hashed, immutable canonical payload. That is what gives the
  // booking a notification destination and makes it findable by phone while keeping the number
  // erasable (owner retention ruling 2026-08-06).
  const [phone, setPhone] = useState('');
  const [customerSearch, setCustomerSearch] = useState('');
  const [showDropdown, setShowDropdown] = useState(false);
  const [svc, setSvc] = useState('');
  // The grid lane the optimistic row is drawn on — a coordinate, not an assignment. There is
  // no resource dimension server-side and nothing here is submitted (see ./resources.ts).
  const staff = UNASSIGNED_RESOURCE_ID;
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

  // One attempt identity per open drawer PER PAYLOAD. The operation id and idempotency key are
  // minted before the first call and reused on every retry (Inv-2), so a retry of the same
  // booking replays the original acceptance instead of creating a second operation.
  //
  // Scoped by the payload material, not by the drawer alone — the same discipline
  // `submitReschedule` already applies with its `modify:${id}|${startsAt}|${dur}` key. An
  // idempotency key must never carry two different canonical payloads (Inv-3), and this form can
  // produce two: the operator submits, the call fails, they notice the phone was wrong, correct
  // it, and press the drawer's own Retry. A different phone mints a different `contact_ref`, so
  // `revision.payload_hash` changes while the key would not — the server answers 409
  // IdempotencyConflict and the correction can only be made by abandoning the booking entirely.
  //
  // The nonce keeps two genuinely separate bookings distinct even when every field matches; the
  // material makes an edited form a new attempt. `name` is deliberately absent: it never enters
  // the canonical payload, so changing it must NOT invalidate a live attempt.
  const [attemptNonce, setAttemptNonce] = useState(() => String(Date.now()));
  const attemptKey = useMemo(
    () => `create:${attemptNonce}|${svc}|${day}|${time}|${partySize}|${phone.trim()}`,
    [attemptNonce, svc, day, time, partySize, phone]
  );
  // Guards the one-shot 'booking confirmed' announcement; see the effect below.
  const announcedRef = useRef<string | null>(null);

  const poll = useOperationStatus(operationId, startedAt);
  const booked = isBooked(poll.status);

  /**
   * The current options, readable from the reset effect without making it depend on them.
   *
   * `services` is now the live catalog, so its array identity changes on every refetch — and
   * `useCatalogItems` inherits `refetchOnWindowFocus`. Listing it as a dependency meant that
   * alt-tabbing away and back **reset the open drawer**: the typed guest name, the chosen day,
   * time and party size were all wiped, and `attemptKey` was re-minted, which throws away the
   * operation identity and idempotency key the form was going to submit under (Inv-2/3).
   * It was a stable `useState` array before, so the dependency was previously inert.
   */
  const servicesRef = useRef(services);
  servicesRef.current = services;

  /** Picks the initial service: the prefilled one when the catalog has it, else the first. */
  const chooseInitialService = useCallback((options: readonly BookableService[]) => {
    // A prefilled service that is not in the venue's catalog is REPORTED, never silently
    // swapped. Quietly falling back to options[0] meant an operator who clicked a waitlist row
    // reading "SNS dipping powder" submitted a durable booking for "Gel manicure", with the
    // rest of the form prefilled and equally trustworthy-looking.
    const prefilledService = options.find(s => s.id === prefill?.serviceId)?.id;
    setUnmatchedService(prefill?.serviceId && !prefilledService ? prefill.serviceId : null);
    setSvc(prefilledService ?? options[0]?.id ?? '');
  }, [prefill]);

  // Reset on open. Deliberately NOT keyed on `services` — see `servicesRef` above.
  useEffect(() => {
    if (open) {
      setName(prefill?.name ?? ''); setPhone(''); setCustomerSearch('');
      chooseInitialService(servicesRef.current ?? []);
      setDay(String(prefill?.day ?? week.todayIndex ?? 0));
      setTime(String(prefill?.start ?? 600));
      setPartySize(String(prefill?.partySize ?? 2));
      setPhase('form'); setShowDropdown(false);
      setSubmitError(null); setOperationId(null); setStartedAt(null);
      announcedRef.current = null;
      setAttemptNonce(String(Date.now()));
    }
  }, [open, week.todayIndex, prefill, chooseInitialService]);

  // Catch-up for the drawer opened before the catalog answered: choose a service once options
  // exist, and only while none is chosen, so a later refetch cannot move the operator's pick.
  useEffect(() => {
    if (!open || svc !== '' || !services || services.length === 0) return;
    chooseInitialService(services);
  }, [open, svc, services, chooseInitialService]);

  const serviceOptions = services ?? [];
  const servicesLoaded = services !== undefined;
  const hasServices = serviceOptions.length > 0;
  // NO fallback to serviceOptions[0].
  //
  // `submit()` sends `svc` but reads `durationMinutes` from `service`, so falling back would
  // durably record a payload naming one service and reserving another's duration — and the
  // confirm panel would show the substitute's name, so the operator could not see it happen.
  // A refetch (a publish landing, or refetchOnWindowFocus) can retire the selected item at any
  // moment, so this is reachable without anyone doing anything wrong.
  const service = serviceOptions.find(s => s.id === svc);
  // The pick is gone from the catalog. Not the same as "still loading" (services undefined) or
  // "venue has none" (hasServices false) — those have their own branches.
  const selectionRetired = servicesLoaded && hasServices && svc !== '' && service === undefined;

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
      // `dep: 0` — the catalog models no deposit and the platform takes none (spec 09 deferred).
      // `dur` falls back to the grid's default block only for LAYOUT; nothing was submitted.
      dep: 0, transcript: null, dur: service?.durationMinutes ?? 60,
    });
  }, [booked, phase, attemptKey, day, staff, time, svc, service, name, onCreated]);


  // The tenant's real customers once loaded; the mock array only while the list has never
  // loaded, and then labelled — the command palette was fixed the same way. Offering six
  // invented people as a venue's customer list is the same false claim wherever it appears.
  const showingDemoCustomers = customers === undefined;
  const suggestions = (customers ?? CUSTOMERS).filter(c =>
    c.name.toLowerCase().includes(customerSearch.toLowerCase()) && customerSearch.length > 0
  ).slice(0, 6);

  // Advisory only — never blocks submission.
  //
  // Tested against the NORMALISED value, not the raw one: separators are stripped before the
  // payload is hashed, and the server normalises both sides at lookup, so "+61 400 111 222" is
  // perfectly findable and must not be warned about. What genuinely cannot be matched is a
  // number with no country code, because neither side guesses one.

  async function submit() {
    // `!service` is the load-bearing half: without it a retired pick would be submitted with
    // another service's duration.
    if (phase !== 'form' || !name.trim() || !hasServices || !service) return;

    setPhase('confirming');
    setSubmitError(null);

    // Reusing the stored identity is what makes a retry safe: the same key + same payload
    // replays the original acceptance rather than creating a second operation (§13.4).
    const identity = getOrCreateAttempt(attemptKey);

    try {
      // Minted BEFORE the payload is built, because the payload is hashed here and the ref has to
      // be inside that hash. A failure here fails the booking rather than silently falling back to
      // the inline shape: quietly writing an un-erasable phone number would defeat the point.
      const contactRef = phone.trim()
        ? (await createGuestContact({ phoneE164: phone.trim() })).contact_ref
        : undefined;

      const request = await buildSubmission(
        {
          operation: 'create',
          serviceId: svc,
          startsAt: toIsoInstant(week.start, Number(day), Number(time)),
          partySize: Number(partySize),
          // Omitted entirely when the catalog states no duration. Sending a default would
          // durably record how long the venue is booked for on the strength of a guess.
          durationMinutes: service?.durationMinutes ?? undefined,
          // The guest, so the booking has somewhere to notify and can be found by phone.
          //
          // Submitted as an opaque REF, not as the number: the phone reaches the platform on the
          // `POST /api/guest-contacts` call above and never enters this payload, which is hashed
          // and immutable. That is what makes the guest erasable later (owner retention ruling
          // 2026-08-06). Name deliberately excluded — see BookingDraft.customer.
          customer: contactRef ? { contactRef } : undefined,
        },
        {
          identity,
          sessionId: `console:${identity.intentId}`,
          // The OPERATOR, not the typed guest name.
          //
          // `Confirmation.identity` is documented as "verified identity, or the
          // `anonymous_session` sentinel" (spec 04). A staff booking has no verified guest, and
          // the operator is the confirming party — which is what `verification_level:
          // `verification_level` already claims, and what cancel and reschedule already send.
          //
          // Sending the typed name here pushed plain-text PII into `identity_ref`, which the
          // platform surfaces as the opaque `customer_ref` on `/api/customers` — a field spec 14
          // minimises precisely so it cannot carry a person's name.
          identityRef: getActor(),
          readBackVersion: `${svc}|${day}|${time}|${partySize}`,
          // Minted WITH the attempt, not per call — this is the whole reason
          // `AttemptIdentity.confirmedAt` exists (Inv-2/3).
          //
          // Omitted, `buildSubmission` fell back to `new Date().toISOString()`, so the retry this
          // drawer offers on failure claimed the operator had confirmed at the moment of the
          // RETRY. `confirmed_at` and `created_at` are the recorded time of confirmation, so when
          // a first attempt fails and a later one lands, the stored instant described something
          // that never happened (Inv-16).
          //
          // Not a 409 on THIS door: `SubmitConfirmedIntent` hashes only
          // `revision.canonical_payload`, so this field sits outside its idempotency hash. It IS a
          // 409 on the sibling doors — `SubmitBookingCancellation` and `SubmitBookingModification`
          // hash `SerializeToNode(request)`, whole body included — and the console must not depend
          // on which door happens to be forgiving.
          confirmedAt: identity.confirmedAt,
        }
      );

      const accepted = await submitBooking(request);
      setOperationId(accepted.operation_id ?? null);
      setStartedAt(Date.now());
    } catch (error) {
      // A conflict means this key is already bound to a different request — retrying with it can
      // never succeed, so the attempt is freed and the next submit mints a fresh identity.
      //
      // `isRetryableWithNewAttempt`, not a bare idempotency-conflict check: a plain 409 spends
      // the attempt just as surely as a typed one, and the helper also knows the single 409 a new
      // attempt CANNOT fix — an already-cancelled booking, where minting a new key just produces
      // another operation that 409s the same way forever.
      if (isRetryableWithNewAttempt(error)) resetAttempt(attemptKey);
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
          disabled={!name.trim() || !partySize || !service || phase !== 'form'}
          onClick={() => { void submit(); }}
        >
          {phase === 'confirming' ? 'Submitting' : 'Confirm booking'}
        </PrimaryButton>
      : undefined;

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Manual booking" title="New booking" footer={footer}>
      {!hasServices ? (
        /*
          Three distinct situations, told apart rather than collapsed into one "no services".
          The old drawer fell back to the mock catalogue here, which meant a failed or empty
          catalog still offered five bookable-looking services whose ids exist on no server.
        */
        <div className="py-10 text-center">
          {servicesFailed ? (
            <ErrorState
              inline
              error={new Error('The service catalog could not be loaded.')}
              title="Couldn’t load this venue’s services."
              onRetry={onRetryServices}
            />
          ) : !servicesLoaded ? (
            <>
              <div className="text-[14px] font-semibold text-ink">Loading services…</div>
              <div className="text-[12.5px] text-t3 mt-[5px] leading-[1.5]">
                Reading this venue’s catalog.
              </div>
            </>
          ) : (
            <>
              <div className="text-[14px] font-semibold text-ink">No bookable services</div>
              <div className="text-[12.5px] text-t3 mt-[5px] leading-[1.5]">
                This venue’s catalog has no published items, so there is nothing to book against
                yet. Publish a menu catalog first — a booking must name a service the venue
                actually has.
              </div>
            </>
          )}
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
            The NAME is still a label for this screen only: `Confirmation.identity` carries the
            operator, and the name is deliberately kept out of the canonical payload because that
            payload is immutable, replayed, and out of reach of the retention sweep.
            The PHONE is different — it is exchanged for an opaque `contact_ref`
            (`POST /api/guest-contacts`) and only that ref is submitted, which gives the booking a
            notification destination while keeping the number erasable. Each hint says which is
            which, so the operator is never misled about what has actually been recorded.
          */}
          {selectionRetired && (
            <div
              className="mb-[13px] rounded-[10px] p-[10px_12px] text-[12px] leading-[1.5]"
              style={{ background: '#FBF3DF', border: '1px solid #F0DCA8', color: '#7E5606' }}
              role="alert"
            >
              The service you picked is no longer in the published catalog — it was probably
              republished while this drawer was open. Choose one below. Nothing has been
              submitted, and nothing has been chosen for you.
            </div>
          )}
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
            hint="Name is used on this screen only — the booking is recorded as confirmed by you. Add a phone below to give the guest a real identity."
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

          {/*
            Unlike the name above, this IS recorded — as an opaque `contact_ref` minted by
            `POST /api/guest-contacts` before the payload is hashed, never as the number itself.
            Optional: a booking with no phone is still accepted, it simply has no notification
            destination and cannot be found by phone.

            E.164 still matters, but not for the reason once written here. Separators ARE
            normalised on both sides, so "+61 400 111 222" is perfectly findable. What neither
            side guesses is a COUNTRY CODE: a bare "0400 111 222" has no international form to
            normalise to, so it can never match a "+61400111222" search.
          */}
          <Field
            label="Guest phone"
            hint="Optional, E.164 (e.g. +61400111222). This is what the guest gets notified on and what the booking is found by — it must be exact."
          >
            <PhoneInput
              value={phone}
              onChange={setPhone}
              warningId="guest-phone-format"
              placeholder="+61400111222"
              className="w-full h-[42px] border border-cd-border rounded-[10px] px-3 text-[14px] text-ink outline-none focus:border-[#2F6BEB] transition-colors"
              style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
              warning={
                <>
                  Spacing and punctuation are fine — they are tidied automatically. But without a
                  leading <strong>+</strong> and country code this will not be found by a later
                  phone search, because neither side guesses a country.
                </>
              }
            />
          </Field>

          <Field label="Service" hint="From this venue’s published catalog.">
            <SelectField
              value={svc}
              onChange={setSvc}
              options={serviceOptions.map(s => ({
                value: s.id,
                // The duration is appended only when the catalog states one — an item with no
                // duration must not be labelled "· 1h", which reads as the venue's own figure.
                label: s.durationMinutes === null ? s.name : `${s.name} · ${fmtDur(s.durationMinutes)}`,
              }))}
            />
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
          <AvailabilityNote slots={availability} week={week} dayIndex={Number(day)} />
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
          {/* No deposit panel. It read `{money(service.dep)} deposit — "Mirrors your deposit
              policy"` with an "Auto" chip, from the hand-written mock catalogue. Spec 09 has
              not shipped a payment surface, so the platform has never requested or held a
              cent — and the confirmation panel below already omits it for exactly that reason.
              A fabricated fact about money is no more acceptable before the operator commits
              than after. */}
        </fieldset>
      )}
    </Drawer>
  );
}
