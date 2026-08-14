// Cordia console — types and helpers. List screens read live API rows; the empty
// collections below exist only so existing imports keep compiling until those screens switch.

export type BookingStatus = 'confirmed' | 'pending' | 'confirming' | 'retrying' | 'failed' | 'cancelled' | 'waitlisted';
export type Channel = 'phone' | 'sms' | 'email' | 'web' | 'instagram' | 'whatsapp';

export interface Booking {
  id: string;
  day: number;
  staff: string;
  start: number;
  dur: number;
  service: string;
  serviceName: string;
  name: string;
  /**
   * The projection's raw `customer_ref`, unrendered. Needed when submitting a change against
   * this booking: the new revision must carry the guest forward or the resulting notification
   * has no destination. Null on rows the API returned without one, and absent on demo rows.
   */
  customerRef?: string | null;
  /** Who made the booking. `unknown` when the source does not record it (the API does not). */
  prov: 'ai' | 'you' | 'unknown';
  status: BookingStatus;
  channel: Channel | null;
  dep: number;
  transcript: string | null;
  /**
   * False when the row is an operation the platform has accepted but not yet finalised, so there
   * is no booking to act on: `id` holds the operation id, and cancel/modify against it would 404.
   * Absent on demo rows, which are all treated as real.
   */
  isBooking?: boolean;
}

export interface QueueItem {
  id: string;
  name: string;
  service: string;
  slot: string;
  reason: string;
  /**
   * Attempt count. **Optional** — `OperationSummary` does not project it, so a live row omits
   * it rather than reporting 0, which read as "this has never been retried".
   */
  retries?: number;
  /** Optional for the same reason: the API models no channel on an operation. */
  channel?: Channel;
  transcript: string | null;
  /**
   * The server's operation status. Carried so a local dismissal can be tied to the state it
   * acknowledged: an operation that escalates afterwards must come back, not stay hidden.
   * Absent on demo rows.
   */
  status?: string;
  /**
   * True when the operation carries a pre-order. Carried because the follow-up surface
   * needs it: the server refuses to route a pre-order to the waitlist (spec 11 §21.4), so
   * that answer must not be offered on such a row. Absent on demo rows.
   */
  isPreorder?: boolean;
  /**
   * The open manual-recovery work item for this operation, when one exists.
   *
   * A separate record from the status, with a separate lifetime: it is raised when the
   * platform gives up and stays open until a person closes it, so it says *why* someone was
   * called and *by when* — which the status alone cannot. Absent on demo rows and on
   * operations nobody has been called for.
   */
  workItem?: {
    /** Operator-facing wording; the raw reason is a machine tag. */
    reason: string;
    slaDeadline: string;
    /** Past its deadline. Never inferred from an unparseable date. */
    overdue: boolean;
    assignee: string | null;
  };
}

export interface Thread {
  id: string;
  name: string;
  channel: Channel;
  preview: string;
  /**
   * **Optional.** `ConversationSessionSummary` carries no outcome, so a live thread sets
   * nothing rather than claiming `pending` for every session that has ended.
   */
  status?: BookingStatus;
  /** Optional for the same reason: nothing records whether the agent or a human handled it. */
  prov?: string;
  ago: string;
  live?: boolean;
  /**
   * `consent_state` as the session read returned it — the ratified `ConsentState` vocabulary,
   * or `null` when the platform recorded none. Consent is the guest's and is captured
   * in-channel; the console only ever displays what was recorded.
   */
  consentState?: string | null;
  /** `recording_allowed`; meaningful only alongside a granted consent. */
  recordingAllowed?: boolean | null;
}

export interface Message {
  from: 'agent' | 'cust' | 'system' | 'readback' | 'escalate';
  text: string;
}

export interface WaitlistItem {
  id: string;
  name: string;
  service: string;
  want: string;
  /**
   * Whether the guest authorized auto-book. This is a **consent grant recorded at enrollment**
   * (spec 11 §20, Inv-1), not a venue preference — and no endpoint changes it, so it is
   * read-only on any row that came from the API.
   */
  auto: boolean;
  /** True for a row from `/api/waitlist/enrollments`; absent on demo rows. */
  isLive?: boolean;
  /**
   * The latest offer's id, raw. Whether it can be **answered** is decided at render against a
   * ticking clock, not here — an offer's expiry passes while the row sits on screen.
   */
  offerId?: string;
  /** Raw latest-offer status, for display. */
  offerStatus?: string;
  /** Raw ISO expiry of the latest offer. */
  offerExpiresAt?: string;
  /** Raw enrollment fields, carried so "Book now" can prefill rather than open a blank form. */
  serviceId?: string;
  partySize?: number;
  /** ISO instant of the start of the guest's acceptable window. */
  acceptableFrom?: string;
}

export interface Customer {
  id: string;
  name: string;
  /**
   * True when this row is the console operator rather than a guest — see
   * `features/customers/adapters.ts`. Absent on demo rows.
   */
  isOperator?: boolean;
  phone: string;
  channel: Channel;
  visits: number;
  noShows: number;
  lastVisit: string;
  spend: number;
  tags: string[];
  fav: string;
  note: string;
}

export interface Deposit {
  id: string;
  name: string;
  amount: number;
  status: 'captured' | 'held' | 'refunded' | 'failed';
  when: string;
  service: string;
}

export interface Campaign {
  id: string;
  title: string;
  status: 'awaiting' | 'scheduled' | 'sent' | 'rejected';
  audience: string;
  channel: Channel;
  recipients: number;
  when: string;
  message: string;
}

export interface KpiEntry {
  key: string;
  label: string;
  value: string;
  delta: string;
  up: boolean;
  sub: string;
}

export interface DayEntry { d: string; v: number; }
export interface ChannelEntry { label: string; v: number; color: string; }
export interface ServiceEntry { name: string; v: number; }

export interface AnalyticsData {
  range: string;
  kpis: KpiEntry[];
  perDay: DayEntry[];
  channels: ChannelEntry[];
  handled: { ai: number; you: number };
  topServices: ServiceEntry[];
}

export interface PayoutInfo {
  held: number;
  available: number;
  nextDate: string;
  nextAmount: number;
  currency: string;
  account: string;
}

export interface Service {
  key: string;
  name: string;
  dur: number;
  dep: number;
}

export interface SettingsGuardrail {
  id: string;
  label: string;
  on: boolean;
  locked?: boolean;
}

export interface SettingsHour {
  d: string;
  open: string;
  close: string;
  on: boolean;
}

export interface SettingsKnowledge {
  q: string;
  a: string;
  status: 'answered' | 'needs-answer';
}

// ---- helpers ----

export function statusMeta(s: BookingStatus) {
  const m: Record<string, { label: string; fill: string; text: string; accent: string; spin?: boolean }> = {
    confirmed:   { label: 'Confirmed',  fill: '#E7F6EC', text: '#0B6431', accent: '#15924B' },
    pending:     { label: 'Pending',    fill: '#FBF3DF', text: '#7E5606', accent: '#C98A09' },
    confirming:  { label: 'Confirming', fill: '#FBF3DF', text: '#7E5606', accent: '#C98A09', spin: true },
    retrying:    { label: 'Retrying',   fill: '#FBF3DF', text: '#7E5606', accent: '#C98A09', spin: true },
    failed:      { label: 'Failed',     fill: '#FBEBEA', text: '#93231B', accent: '#D23B30' },
    cancelled:   { label: 'Cancelled',  fill: '#F5F5F5', text: '#6B6B6B', accent: '#9A9A9A' },
    waitlisted:  { label: 'Waitlisted', fill: '#FBF3DF', text: '#7E5606', accent: '#C98A09' },
  };
  return m[s] || m.confirmed;
}

export function chMeta(ch: string) {
  const m: Record<string, { label: string; short: string; color: string }> = {
    phone:     { label: 'Phone call', short: 'Phone',     color: '#15924B' },
    sms:       { label: 'SMS',        short: 'SMS',       color: '#2F6BEB' },
    email:     { label: 'Email',      short: 'Email',     color: '#6B5BD2' },
    web:       { label: 'Web chat',   short: 'Web',       color: '#C98A09' },
    instagram: { label: 'Instagram',  short: 'Instagram', color: '#BC2A8D' },
    whatsapp:  { label: 'WhatsApp',   short: 'WhatsApp',  color: '#1FA855' },
  };
  return m[ch] || { label: ch, short: ch, color: '#6B6B6B' };
}

export const depStatusMeta: Record<string, { label: string; fill: string; text: string; accent: string }> = {
  captured: { label: 'Captured', fill: '#E7F6EC', text: '#0B6431', accent: '#15924B' },
  held:     { label: 'Held',     fill: '#F2F2F0', text: '#55554F', accent: '#9A9A9A' },
  refunded: { label: 'Refunded', fill: '#E9F0FE', text: '#2056CC', accent: '#2F6BEB' },
  failed:   { label: 'Failed',   fill: '#FBEBEA', text: '#93231B', accent: '#D23B30' },
};

export const campStatusMeta: Record<string, { label: string; fill: string; text: string; accent: string }> = {
  awaiting:  { label: 'Awaiting approval', fill: '#FBF3DF', text: '#7E5606', accent: '#C98A09' },
  scheduled: { label: 'Scheduled',         fill: '#E9F0FE', text: '#2056CC', accent: '#2F6BEB' },
  sent:      { label: 'Sent',              fill: '#E7F6EC', text: '#0B6431', accent: '#15924B' },
  rejected:  { label: 'Rejected',          fill: '#F5F5F5', text: '#6B6B6B', accent: '#9A9A9A' },
};

export const money = (n: number | string) => 'A$' + n;

export function fmtT(m: number): string {
  const h = Math.floor(m / 60), mm = m % 60, ap = h >= 12 ? 'PM' : 'AM';
  let hh = h % 12;
  if (hh === 0) hh = 12;
  return hh + (mm ? ':' + (mm < 10 ? '0' : '') + mm : '') + ' ' + ap;
}

export function fmtRange(start: number, dur: number): string {
  return fmtT(start) + ' – ' + fmtT(start + dur);
}

export function fmtDur(m: number): string {
  const h = Math.floor(m / 60), mm = m % 60;
  return (h ? h + 'h' : '') + (mm ? (h ? ' ' : '') + mm + 'm' : '');
}

export function initials(name: string): string {
  return name.replace('@', '').split(/[ .]/).filter(Boolean).map(w => w[0]).slice(0, 2).join('').toUpperCase();
}

export function idemKey(id: string): string {
  const sum = (id || '').split('').reduce((a, c) => a + c.charCodeAt(0), 0);
  return 'idem_' + (id || 'x').padEnd(3, '0').slice(0, 3) + '…' + ((sum % 900) + 100);
}

// ---- TENANT ----

export interface TenantConfig {
  name: string;
  businessType: string;
  /** What a single row in the schedule grid represents — e.g. "Technician", "Table", "Crew", "Room" */
  resourceLabel: string;
  resourceLabelPlural: string;
}

export interface BusinessType {
  key: string;
  label: string;
  resourceLabel: string;
  resourceLabelPlural: string;
  icon: string;
  hint: string;
}

export const BUSINESS_TYPES: BusinessType[] = [
  { key: 'salon',      label: 'Salon & beauty',   resourceLabel: 'Technician',   resourceLabelPlural: 'Technicians',    icon: 'scissors', hint: 'Manicure, Haircut, Facial…'    },
  { key: 'restaurant', label: 'Restaurant',        resourceLabel: 'Table',        resourceLabelPlural: 'Tables',         icon: 'book',     hint: 'Breakfast, Lunch, Dinner…'     },
  { key: 'medical',    label: 'Health & clinic',   resourceLabel: 'Practitioner', resourceLabelPlural: 'Practitioners',  icon: 'shield',   hint: 'Consultation, Treatment…'      },
  { key: 'fitness',    label: 'Fitness & gym',     resourceLabel: 'Trainer',      resourceLabelPlural: 'Trainers',       icon: 'zap',      hint: 'Personal training, Class…'     },
  { key: 'cleaning',   label: 'Cleaning',          resourceLabel: 'Crew',         resourceLabelPlural: 'Crews',          icon: 'home',     hint: 'Home clean, Office clean…'     },
  { key: 'lawn',       label: 'Lawn & outdoor',    resourceLabel: 'Crew',         resourceLabelPlural: 'Crews',          icon: 'sun',      hint: 'Lawn mow, Hedge trim…'         },
  { key: 'repair',     label: 'Repair & trades',   resourceLabel: 'Technician',   resourceLabelPlural: 'Technicians',    icon: 'tool',     hint: 'Roof repair, Plumbing fix…'    },
  { key: 'other',      label: 'Other',             resourceLabel: 'Staff',        resourceLabelPlural: 'Staff',          icon: 'dots',     hint: 'Add your own services…'        },
];

const OTHER_BUSINESS = BUSINESS_TYPES.find((t) => t.key === "other")!;

/** Empty venue config until the shell reads `GET /api/tenant-profile`. */
export const DEFAULT_TENANT: TenantConfig = {
  name: "",
  businessType: OTHER_BUSINESS.key,
  resourceLabel: OTHER_BUSINESS.resourceLabel,
  resourceLabelPlural: OTHER_BUSINESS.resourceLabelPlural,
};

/** Alias so ConsolePage can keep importing `TENANT` until it switches to the profile query. */
export const TENANT: TenantConfig = { ...DEFAULT_TENANT };

export const SERVICES: Record<string, { name: string; dur: number; dep: number }> = {};

export const SERVICE_LIST: Service[] = [];

export const BOOKINGS: Booking[] = [];
export const QUEUE: QueueItem[] = [];
export const THREADS: Thread[] = [];
export const MESSAGES: Record<string, Message[]> = {};
export const WAITLIST: WaitlistItem[] = [];
export const CUSTOMERS: Customer[] = [];

export const PAYOUT: PayoutInfo = {
  held: 0, available: 0, nextDate: "", nextAmount: 0, currency: "AUD", account: "",
};

export const DEPOSITS: Deposit[] = [];
export const CAMPAIGNS: Campaign[] = [];

const EMPTY_ANALYTICS: AnalyticsData = {
  range: "",
  kpis: [],
  perDay: [],
  channels: [],
  handled: { ai: 0, you: 0 },
  topServices: [],
};

export const ANALYTICS: AnalyticsData = { ...EMPTY_ANALYTICS };
export const ANALYTICS_30D: AnalyticsData = { ...EMPTY_ANALYTICS };
export const ANALYTICS_90D: AnalyticsData = { ...EMPTY_ANALYTICS };

export const ANALYTICS_BY_RANGE: Record<string, AnalyticsData> = {
  "7d": ANALYTICS,
  "30d": ANALYTICS_30D,
  "90d": ANALYTICS_90D,
};

export const SETTINGS = {
  hours: [] as SettingsHour[],
  deposit: { require: false, amountPolicy: "", noShowOnly: false, refundWindow: "" },
  guardrails: [] as SettingsGuardrail[],
  knowledge: [] as SettingsKnowledge[],
};

// ---- time helpers ----

/** Convert a display time string ("9:00 AM", "8:00 PM") or 24h "09:00" to minutes since midnight. */
export function parseTime(s: string): number {
  const [time, ampm] = s.split(" ");
  const [hStr, mStr] = time.split(":");
  let h = parseInt(hStr, 10);
  const m = parseInt(mStr || "0", 10);
  if (ampm === "PM" && h !== 12) h += 12;
  if (ampm === "AM" && h === 12) h = 0;
  return h * 60 + m;
}

/**
 * Open/close bounds (minutes since midnight) for a day index (0=Mon…6=Sun).
 * When `hours` is omitted or the day is missing, falls back to 09:00–18:00, not closed.
 */
export function dayBounds(
  dayIdx: number,
  hours?: SettingsHour[],
): { start: number; end: number; closed: boolean } {
  const h = hours?.[dayIdx];
  if (!h) return { start: 540, end: 1080, closed: false };
  return { start: parseTime(h.open), end: parseTime(h.close), closed: !h.on };
}

export type TimeSlotOption = { value: string; label: string };

function slotsBetween(start: number, end: number): TimeSlotOption[] {
  const o: TimeSlotOption[] = [];
  const aligned = Math.floor(start / 30) * 30;
  for (let m = aligned; m <= end; m += 30) o.push({ value: String(m), label: fmtT(m) });
  return o;
}

/**
 * Bookable picker times from operating hours: earliest open through latest close
 * among days that are on, in half-hour steps. Same shape as `TIME_SLOTS`.
 */
export function timeSlotsFromHours(hours: SettingsHour[]): TimeSlotOption[] {
  const openDays = hours.filter((h) => h.on);
  if (openDays.length === 0) return [];
  let start = Infinity;
  let end = -Infinity;
  for (const h of openDays) {
    start = Math.min(start, parseTime(h.open));
    end = Math.max(end, parseTime(h.close));
  }
  return slotsBetween(start, end);
}

/**
 * Fallback picker times, 08:00–20:00 in half-hour steps, used only while hours have not loaded.
 */
export const TIME_SLOTS: TimeSlotOption[] = slotsBetween(480, 1200);
