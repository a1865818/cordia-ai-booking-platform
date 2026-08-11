import React, { useEffect, useRef, useState } from "react";
import { Channel } from "@cordia/shared-schemas";
import type { BookableService } from "../../../features/bookings/services";
import type { EnrollGuestInput } from "../../../features/waitlist/api";
import { useEnrollGuest } from "../../../features/bookings/mutations";
import { mintIdempotencyKey } from "../../../features/bookings/identity";
import { fmtDur } from "../../../data/console";
import { Drawer } from "../../ui/Drawer";
import { PrimaryButton } from "../../ui/Button";
import { Icon } from "../../ui/Icon";
import { ErrorState } from "../../ui/ErrorState";

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

function SelectField({ value, onChange, options }: {
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string }[];
}) {
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

/**
 * The channel the guest asked to be offered slots on — the ratified wire vocabulary
 * (`Channel`, `@cordia/shared-schemas`), never an invented string. There is no default:
 * a preference is the guest's statement, so the operator must pick it deliberately.
 *
 * **Only the channels offers can actually be SENT on.** `Channel` is the conversation-origin
 * vocabulary (voice/sms/web/email) and is wider than the notification vocabulary
 * (`NotificationChannel` = channel/email/sms). `NotificationRecipientResolver.ChannelFor` maps
 * only `sms`/`email`/`channel` and silently falls back to the channel implied by the address for
 * anything else — so an operator recording "Phone call" because that is how the guest rang in got
 * an SMS instead, with nothing anywhere reporting that their explicit choice had been discarded.
 * Offering a control that drops its own input is the defect the staff "With" picker was removed
 * for; this is the same one.
 */
const CHANNEL_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'Choose a channel…' },
  { value: Channel.SMS, label: 'Text message (SMS)' },
  { value: Channel.Email, label: 'Email' },
];

/**
 * Classifies the guest reference the same way the server's `AsDeliverable` does, so the drawer
 * cannot accept an enrolment whose offers the platform will be unable to deliver.
 *
 * The waitlist has no contact field of its own: `NotificationRecipientResolver` derives the
 * destination from `enrollment.customer_ref` alone. A reference that is only a name therefore
 * produces an enrolment that looks completely normal in the console while every offer and expiry
 * notification settles `no_destination` — the guest is never told, the offer expires unanswered,
 * and the slot is released.
 */
function classifyGuestRef(raw: string): 'email' | 'sms' | 'none' {
  const trimmed = raw.trim();
  if (trimmed === '') return 'none';

  const at = trimmed.indexOf('@');
  if (at > 0 && at === trimmed.lastIndexOf('@') && at < trimmed.length - 1
      && trimmed.slice(at + 1).includes('.')) {
    return 'email';
  }

  // International form only, matching `PhoneMatching.Normalise`: a bare national number cannot be
  // dialled without a country, and the platform records none.
  //
  // The server looks for a `+` ANYWHERE BEFORE THE FIRST DIGIT, not only at index 0. This kept a
  // leading `+` only, so `"(+61) 400 111 222"` — which the server normalises to `+61400111222`
  // and accepts as SMS-deliverable — was classified undeliverable here and the enrolment was
  // blocked outright. A client-side guard that is stricter than the server rejects real guests.
  const digits = trimmed.replace(/\D/g, '');
  const plusBeforeFirstDigit = /^[^\d]*\+/.test(trimmed);
  if (plusBeforeFirstDigit && digits.length >= 7) return 'sms';

  return 'none';
}

/** How long an offer stays open before it expires — a parameter of the enrolment itself. */
const LIFETIME_OPTIONS = [
  { value: String(15 * 60), label: '15 minutes' },
  { value: String(30 * 60), label: '30 minutes' },
  { value: String(60 * 60), label: '1 hour' },
  { value: String(4 * 60 * 60), label: '4 hours' },
  { value: String(24 * 60 * 60), label: '24 hours' },
];

interface EnrollWaitlistDrawerProps {
  open: boolean;
  device: 'phone' | 'desktop';
  onClose: () => void;
  /** Called once the server has recorded the enrolment (201) — never before. */
  onEnrolled: (guestRef: string) => void;
  /** The venue's bookable services from the live catalog, or `undefined` while never loaded. */
  services?: BookableService[];
  servicesFailed?: boolean;
  onRetryServices?: () => void;
  /**
   * `location_id`s this venue has actually enrolled against (`GET /api/waitlist/locations`).
   *
   * A **usage list, not a registry** — the platform has no locations table — so this is a
   * suggestion list, not a closed picker, and an unrecognised value warns rather than blocks:
   * a new venue legitimately has none, so its first enrolment can never be in the list.
   */
  knownLocations: string[];
}

/**
 * Enrols a guest on the waitlist (`POST /api/waitlist/enrollments`).
 *
 * Every field is either the guest's own statement, relayed by the operator (reference,
 * window, party size, channel), a live platform value (the service), or an enrolment
 * parameter the operator sets (offer lifetime, location). Auto-book is deliberately not
 * offered: it is a consent grant requiring recorded evidence (spec 11 §20), and the console
 * has no way to capture that evidence — so enrolments made here are offer-only.
 */
export function EnrollWaitlistDrawer({
  open, device, onClose, onEnrolled, services, servicesFailed, onRetryServices, knownLocations,
}: EnrollWaitlistDrawerProps) {
  const [guestRef, setGuestRef] = useState('');
  const [svc, setSvc] = useState('');
  const [location, setLocation] = useState('');
  const [partySize, setPartySize] = useState('2');
  const [channel, setChannel] = useState('');
  const [lifetime, setLifetime] = useState(String(60 * 60));
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');

  const enroll = useEnrollGuest();
  const busy = enroll.isPending;

  // Readable from the reset effect without depending on the mutation object, whose identity
  // changes every render — the same `servicesRef` idiom the create drawer uses.
  const resetMutationRef = useRef(enroll.reset);
  resetMutationRef.current = enroll.reset;

  /**
   * One idempotency key per attempt, reused across every retry of that attempt.
   *
   * The key is rebuilt whenever ANY field this drawer sends changes, and held constant when none
   * does. That is what makes a retry a retry and an edit a new enrolment — and it is precisely
   * the distinction the server's derived fallback key cannot draw, since two byte-identical
   * requests look the same to it whether or not the operator meant them to be.
   *
   * "Any field it sends" is deliberate rather than "the fields the server hashes": if the two
   * sets ever diverge again, a field the drawer can edit but the signature ignores becomes a
   * change the operator makes, sees confirmed, and never actually gets.
   *
   * Rebuilding on edit is not optional: reusing one key for a changed request is an idempotency
   * conflict (Inv-3), so a fixed key would turn "correct the party size and resubmit" into a 409.
   */
  const attemptRef = useRef<{ signature: string; key: string } | null>(null);

  function attemptKeyFor(material: Record<string, string | number>): string {
    const signature = JSON.stringify(material);
    if (attemptRef.current?.signature !== signature) {
      attemptRef.current = { signature, key: mintIdempotencyKey() };
    }
    return attemptRef.current.key;
  }

  // Reset on open — a drawer reopened for a new guest must not carry the previous one.
  useEffect(() => {
    if (open) {
      setGuestRef(''); setSvc(''); setLocation('');
      setPartySize('2'); setChannel(''); setLifetime(String(60 * 60));
      setFrom(''); setTo('');
      // A reopened drawer is a new enrolment, never a retry of the last one.
      attemptRef.current = null;
      resetMutationRef.current();
    }
  }, [open]);

  const serviceOptions = services ?? [];
  const servicesLoaded = services !== undefined;
  const hasServices = serviceOptions.length > 0;

  // Choose a service once options exist and none is chosen — same catch-up rule as the
  // create drawer, so a later refetch cannot move the operator's pick.
  useEffect(() => {
    if (!open || svc !== '' || !services || services.length === 0) return;
    setSvc(services[0].id);
  }, [open, svc, services]);

  const fromDate = from ? new Date(from) : null;
  const toDate = to ? new Date(to) : null;
  const windowValid =
    fromDate !== null && toDate !== null &&
    !Number.isNaN(fromDate.getTime()) && !Number.isNaN(toDate.getTime()) &&
    toDate.getTime() > fromDate.getTime();
  const windowStated = from !== '' && to !== '';

  const partySizeNumber = Number(partySize);
  // `some(id === svc)`: a catalog refetch can retire the selected item while the drawer is
  // open, and the server accepts any non-empty service_id — so a stale pick would durably
  // enrol against a service the venue no longer publishes. The operator re-picks; nothing
  // is silently substituted (that substitution was a confirmed defect once already).
  // The enrolment's only possible destination. Blocking rather than warning: an enrolment with
  // nowhere to send is not a degraded enrolment, it is one whose entire purpose — telling the
  // guest a slot opened — cannot happen, and nothing downstream would ever surface that.
  const guestRefKind = classifyGuestRef(guestRef);
  const guestRefUndeliverable = guestRef.trim() !== '' && guestRefKind === 'none';
  // A stated channel the reference cannot carry is also a silent discard: the server falls back
  // to the address-implied channel, so "Email" against a phone number sends an SMS.
  const channelMismatch =
    channel !== '' && guestRefKind !== 'none' &&
    ((channel === Channel.SMS && guestRefKind !== 'sms') ||
     (channel === Channel.Email && guestRefKind !== 'email'));

  const canSubmit =
    guestRef.trim() !== '' && !guestRefUndeliverable && !channelMismatch &&
    svc !== '' && serviceOptions.some(s => s.id === svc) &&
    location.trim() !== '' &&
    Number.isInteger(partySizeNumber) && partySizeNumber > 0 &&
    channel !== '' && windowValid && !busy;

  // Only warn once the tenant's real location list has loaded — an empty list cannot tell a
  // typo apart from a venue that has simply never enrolled anyone yet.
  const locationTrimmed = location.trim();
  const locationUnrecognised =
    locationTrimmed !== '' &&
    knownLocations.length > 0 &&
    !knownLocations.includes(locationTrimmed);

  function submit() {
    if (!canSubmit || !fromDate || !toDate) return;

    const input: EnrollGuestInput = {
      customerRef: guestRef.trim(),
      serviceId: svc,
      locationId: locationTrimmed,
      partySize: partySizeNumber,
      notificationChannel: channel,
      offerLifetimeSeconds: Number(lifetime),
      acceptableFrom: fromDate.toISOString(),
      acceptableTo: toDate.toISOString(),
      idempotencyKey: attemptKeyFor({
        customerRef: guestRef.trim(),
        serviceId: svc,
        locationId: locationTrimmed,
        partySize: partySizeNumber,
        acceptableFrom: fromDate.toISOString(),
        acceptableTo: toDate.toISOString(),
        // Channel and lifetime are part of the signature because they are part of the request.
        // Leaving them out made "the response was lost, correct the channel, press retry" reuse
        // the key for a DIFFERENT enrolment — which the server then answered as a replay,
        // returning the original id while the guest kept the old channel.
        notificationChannel: channel,
        offerLifetimeSeconds: Number(lifetime),
      }),
    };

    enroll.mutate(input, {
      // The parent toasts and closes; the refetched list is what actually shows the guest.
      onSuccess: () => onEnrolled(input.customerRef),
    });
  }

  const footer = hasServices
    ? (
      <PrimaryButton full busy={busy} disabled={!canSubmit} onClick={submit}>
        {busy ? 'Adding' : 'Add to waitlist'}
      </PrimaryButton>
    )
    : undefined;

  return (
    <Drawer open={open} onClose={onClose} device={device} eyebrow="Waitlist" title="Add a guest" footer={footer}>
      {!hasServices ? (
        // Same three states as the create drawer: failed, never loaded, genuinely empty.
        // An enrolment names a service, so without one there is nothing to enrol against.
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
                This venue’s catalog has no published items, so there is nothing to join a
                waitlist for. Publish a menu catalog first.
              </div>
            </>
          )}
        </div>
      ) : (
        <fieldset className="border-none p-0 m-0" disabled={busy}>
          <Field
            label="Guest contact"
            hint="A mobile number in international form (+61…) or an email address. This is the only thing an offer can be sent to — the waitlist has no separate contact field, so a name here would mean the guest is never told when a slot opens."
          >
            <>
              <input
                value={guestRef}
                onChange={e => setGuestRef(e.target.value)}
                placeholder="+61400111222 or guest@example.com"
                aria-describedby={
                  guestRefUndeliverable ? 'enroll-guest-undeliverable'
                  : channelMismatch ? 'enroll-channel-mismatch'
                  : undefined
                }
                style={inputStyle}
              />
              {guestRefUndeliverable && (
                <div id="enroll-guest-undeliverable" className="text-[11.5px] mt-[5px] leading-[1.45]" style={{ color: '#B4553E' }}>
                  Offers are sent to this value, so it has to be a destination. Use a mobile number
                  starting with a country code (+61…) or an email address.
                </div>
              )}
              {channelMismatch && (
                <div id="enroll-channel-mismatch" className="text-[11.5px] mt-[5px] leading-[1.45]" style={{ color: '#B4553E' }}>
                  {channel === Channel.SMS
                    ? 'That is an email address, so an SMS offer could not be delivered to it. Pick Email, or enter a mobile number.'
                    : 'That is a phone number, so an email offer could not be delivered to it. Pick Text message, or enter an email address.'}
                </div>
              )}
            </>
          </Field>

          <Field label="Service" hint="From this venue’s published catalog.">
            <SelectField
              value={svc}
              onChange={setSvc}
              options={serviceOptions.map(s => ({
                value: s.id,
                label: s.durationMinutes === null ? s.name : `${s.name} · ${fmtDur(s.durationMinutes)}`,
              }))}
            />
          </Field>

          <Field
            label="Location"
            hint="The location handle your booking flow uses. Suggestions are the locations this venue has already enrolled against — a usage list, not a registry, so anything you type is accepted."
          >
            <>
              <input
                value={location}
                onChange={e => setLocation(e.target.value)}
                placeholder="e.g. loc_001"
                list="enroll-known-locations"
                aria-describedby={locationUnrecognised ? 'enroll-location-unknown' : undefined}
                style={inputStyle}
              />
              {knownLocations.length > 0 && (
                <datalist id="enroll-known-locations">
                  {knownLocations.map(l => <option key={l} value={l} />)}
                </datalist>
              )}
              {/* Warns, never blocks. The matcher compares `location_id` by exact string, so a
                  typo produces an enrolment nothing can ever match and raises no error anywhere
                  — but a venue's FIRST enrolment at a new location is legitimately unrecognised,
                  so refusing it would break the only case that has to work on day one. */}
              {locationUnrecognised && (
                <div
                  id="enroll-location-unknown"
                  role="status"
                  className="mt-[6px] text-[11.5px] leading-[1.5]"
                  style={{ color: '#7E5606' }}
                >
                  This venue has no enrolments at <strong>{location.trim()}</strong> yet. That is
                  normal for a new location — but locations are matched by exact text, so a typo
                  here produces an enrolment no opening will ever match.
                </div>
              )}
            </>
          </Field>

          <div className="flex gap-[10px]">
            <div className="flex-1">
              <Field label="Party size">
                <input
                  type="number"
                  min={1}
                  max={50}
                  value={partySize}
                  onChange={e => setPartySize(e.target.value)}
                  style={inputStyle}
                />
              </Field>
            </div>
            <div className="flex-1">
              <Field label="Offer lifetime" hint="How long each offer stays open.">
                <SelectField value={lifetime} onChange={setLifetime} options={LIFETIME_OPTIONS} />
              </Field>
            </div>
          </div>

          <Field label="Earliest acceptable time" hint="The window the guest said they can make.">
            <input
              type="datetime-local"
              value={from}
              onChange={e => setFrom(e.target.value)}
              style={inputStyle}
              aria-label="Earliest acceptable time"
            />
          </Field>
          <Field label="Latest acceptable time">
            <input
              type="datetime-local"
              value={to}
              onChange={e => setTo(e.target.value)}
              style={inputStyle}
              aria-label="Latest acceptable time"
            />
          </Field>
          {windowStated && !windowValid && (
            <div className="mb-[13px] text-[12px]" style={{ color: '#B82C22' }} role="status">
              The latest acceptable time must be after the earliest.
            </div>
          )}

          <Field label="Offer channel" hint="Where the guest asked to receive offers.">
            <SelectField value={channel} onChange={setChannel} options={CHANNEL_OPTIONS} />
          </Field>

          {/* Auto-book is a consent grant, not a form field. Saying so beats silently lacking it. */}
          <div
            className="mb-[13px] rounded-[10px] p-[10px_12px] text-[12px] leading-[1.5]"
            style={{ background: '#FBFBFA', border: '1px solid #EFEFEF', color: '#55554F' }}
          >
            Enrolments made here are <strong>offer-only</strong>: auto-book requires the guest’s
            recorded consent (spec 11 §20), which the console cannot capture. The guest will be
            offered matching slots and must accept.
          </div>

          {enroll.isError && (
            <div className="mb-[13px]">
              <ErrorState
                inline
                error={enroll.error}
                title="Couldn’t add this guest to the waitlist."
                onRetry={submit}
              />
              {/* The drawer now sends a key minted for THIS attempt, so a retry is recognised as
                  the same request by identity rather than by looking alike. That removes the
                  caveat the derived-key version needed: a deliberate second entry with identical
                  details is a new attempt, so it no longer has to be disguised by editing a
                  field to get through. */}
              <div className="text-[11.5px] text-t3 mt-[6px] leading-[1.5]">
                Retrying is safe — this attempt carries its own key, so pressing retry cannot add
                the guest twice. To add a genuinely separate entry with the same details, close
                and reopen this drawer: that starts a new attempt.
              </div>
            </div>
          )}
        </fieldset>
      )}
    </Drawer>
  );
}
