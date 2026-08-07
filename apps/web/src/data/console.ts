// Cordia console — types and mock data

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

export const TENANT: TenantConfig = {
  name: 'Bella Nails',
  businessType: 'salon',
  resourceLabel: 'Technician',
  resourceLabelPlural: 'Technicians',
};

export const SERVICES: Record<string, { name: string; dur: number; dep: number }> = {
  gel:     { name: 'Gel manicure',       dur: 45,  dep: 20 },
  sns:     { name: 'SNS dipping powder', dur: 60,  dep: 25 },
  acrylic: { name: 'Acrylic full set',   dur: 90,  dep: 30 },
  pedi:    { name: 'Spa pedicure',       dur: 60,  dep: 25 },
  lash:    { name: 'Eyelash extensions', dur: 120, dep: 20 },
};

export const SERVICE_LIST: Service[] = Object.entries(SERVICES).map(([key, s]) => ({ key, ...s }));

let _bid = 0;
function bk(day: number, staff: string, start: number, svc: string, name: string, prov: 'ai'|'you', status: BookingStatus, channel: Channel|null, dep: number|null, transcript?: string): Booking {
  _bid++;
  const s = SERVICES[svc];
  return {
    id: 'b' + _bid, day, staff, start, dur: s.dur, service: svc, serviceName: s.name,
    name, prov, status, channel: channel || (prov === 'ai' ? 'phone' : null),
    dep: dep == null ? (status === 'pending' || status === 'waitlisted' ? 0 : s.dep) : dep,
    transcript: transcript || null,
  };
}

export const BOOKINGS: Booking[] = [
  bk(0,'s1',570,'gel','Chloe Martin','ai','confirmed','sms',20),
  bk(0,'s2',600,'acrylic','Bianca Tran','ai','confirmed','phone',30,'c1'),
  bk(0,'s3',630,'lash','Sophie Allen','you','confirmed',null,20),
  bk(0,'s4',690,'gel','Ngoc Le','ai','confirmed','web',20),
  bk(0,'s1',720,'pedi','Rachel Kim','ai','confirmed','instagram',0),
  bk(1,'s2',600,'acrylic','Tony Le','ai','confirmed','sms',30,'c2'),
  bk(1,'s1',585,'sns','Hong Vu','ai','confirmed','phone',25),
  bk(1,'s3',660,'lash','Amelia Ross','you','confirmed',null,20),
  bk(1,'s4',780,'acrylic','Jade Nguyen','ai','retrying','web',30),
  bk(1,'s2',810,'gel','Lucy Pham','ai','confirmed','sms',20),
  bk(2,'s1',570,'gel','Hannah Pham','ai','confirmed','phone',20),
  bk(2,'s3',600,'pedi','Mai Tran','ai','confirmed','sms',25),
  bk(2,'s2',660,'sns','Trinh Do','you','confirmed',null,25),
  bk(2,'s4',720,'lash','Zoe Walker','ai','waitlisted','instagram',0),
  bk(2,'s1',810,'acrylic','Phuong Ly','ai','confirmed','web',30),
  bk(2,'s2',840,'gel','Ella Brooks','ai','confirmed','web',0),
  bk(3,'s1',570,'gel','Sarah Nguyen','ai','confirmed','phone',20,'c1'),
  bk(3,'s1',660,'sns','Mai Tran','you','confirmed',null,25),
  bk(3,'s1',810,'pedi','Emily Carter','ai','confirmed','web',0,'c4'),
  bk(3,'s2',600,'acrylic','Tony Le','ai','confirmed','sms',30,'c2'),
  bk(3,'s2',720,'lash','Hannah Pham','ai','retrying','phone',20),
  bk(3,'s2',870,'gel','Olivia Smith','you','confirmed',null,20),
  bk(3,'s3',585,'pedi','Grace Tran','ai','confirmed','sms',25),
  bk(3,'s3',690,'sns','Linh Vo','ai','waitlisted','web',0),
  bk(3,'s4',630,'acrylic','Jessica Brown','ai','confirmed','phone',30),
  bk(3,'s4',780,'lash','Kim Nguyen','ai','failed','instagram',20),
  bk(4,'s1',600,'acrylic','David Chen','ai','retrying','sms',30),
  bk(4,'s2',570,'gel','Mia Chen','ai','confirmed','phone',20),
  bk(4,'s3',630,'lash','Isabella Ng','ai','confirmed','web',20),
  bk(4,'s4',690,'gel','Quynh Tran','you','confirmed',null,20),
  bk(4,'s1',780,'pedi','Holly Evans','ai','confirmed','instagram',0),
  bk(4,'s2',810,'sns','Vivian Le','ai','confirmed','sms',25),
  bk(5,'s1',540,'gel','Renee Lim','you','confirmed',null,20),
  bk(5,'s2',570,'acrylic','Bao Nguyen','ai','confirmed','phone',30),
  bk(5,'s3',600,'lash','Daisy Ho','ai','confirmed','web',20),
  bk(5,'s4',660,'gel','Tara Singh','ai','confirmed','instagram',0),
  bk(5,'s1',690,'pedi','Priya Sharma','ai','failed','web',0),
  bk(5,'s2',780,'sns','Kelly Vo','ai','confirmed','sms',25),
  bk(5,'s3',810,'lash','Ngan Bui','you','confirmed',null,20),
  bk(6,'s1',600,'gel','Anh Pham','ai','confirmed','phone',20),
  bk(6,'s2',660,'pedi','Sienna Cole','ai','confirmed','web',25),
  bk(6,'s4',720,'acrylic','Linh Dao','ai','confirmed','sms',0),
];

export const QUEUE: QueueItem[] = [
  { id:'q1', name:'Kim Nguyen',  service:'Eyelash extensions', slot:'Today, 1:00 PM',         reason:'Provider slot no longer available',    retries:4, channel:'instagram', transcript:null },
  { id:'q2', name:'David Chen',  service:'Acrylic full set',   slot:'Fri 13 Jun, 10:00 AM',   reason:'Deposit payment failed twice',          retries:3, channel:'sms',       transcript:null },
  { id:'q3', name:'Priya Sharma',service:'Spa pedicure x2',    slot:'Sat 14 Jun, 11:30 AM',   reason:'No chair fits a party of 2 at that time', retries:5, channel:'web',     transcript:null },
];

export const THREADS: Thread[] = [
  { id:'c3', name:'@bella.nails', channel:'instagram', preview:"Do you do ombre on toes? Saw it on your reel", status:'pending', prov:'ai', ago:'1 min', live:true },
  { id:'c1', name:'Sarah Nguyen', channel:'phone',     preview:'Booked gel manicure with Linh, Thu 9:30 AM. Deposit captured.', status:'confirmed', prov:'ai', ago:'8 min' },
  { id:'c5', name:'Trang Bui',    channel:'whatsapp',  preview:'Can I move my acrylic to Saturday morning instead?', status:'pending', prov:'ai', ago:'19 min' },
  { id:'c4', name:'Emily Carter', channel:'web',       preview:'Looking for a pedicure Thursday afternoon', status:'pending', prov:'ai', ago:'35 min' },
  { id:'c2', name:'Tony Le',      channel:'sms',       preview:'Confirmed acrylic full set, Tue 10:00 AM with Trang', status:'confirmed', prov:'ai', ago:'1 hr' },
];

export const MESSAGES: Record<string, Message[]> = {
  c1: [
    { from:'agent',   text:'Hi Sarah, thanks for calling Bella Nails. How can I help?' },
    { from:'cust',    text:'Hi! Can I get a gel manicure with Linh on Thursday morning?' },
    { from:'agent',   text:"Linh has 9:30 AM free on Thursday. Shall I book that for you?" },
    { from:'cust',    text:'Yes please.' },
    { from:'readback',text:'Reading that back: gel manicure with Linh, Thursday 12 June at 9:30 AM. A A$20 deposit holds the spot. Is that correct?' },
    { from:'cust',    text:'Correct, thank you!' },
    { from:'system',  text:'BOOKING CONFIRMED · DEPOSIT A$20 CAPTURED' },
  ],
  c3: [
    { from:'cust',    text:'Do you do ombre on toes? Saw it on your reel' },
    { from:'agent',   text:'We do ombre on fingernails. Let me check whether we offer it for toes.' },
    { from:'escalate',text:'Agent was not confident this service exists. Escalated to you to answer.' },
  ],
  c4: [
    { from:'cust',  text:'Looking for a pedicure Thursday afternoon' },
    { from:'agent', text:'We have a spa pedicure at 1:30 PM with Linh, or 3:00 PM with Mai. Which suits you?' },
    { from:'system',text:'WAITING ON CUSTOMER REPLY · HOLD EXPIRES IN 10 MIN' },
  ],
  c2: [
    { from:'cust',    text:'Hi, need to confirm my acrylic set tomorrow' },
    { from:'readback',text:'Reading that back: acrylic full set, Tuesday at 10:00 AM with Trang. Correct?' },
    { from:'cust',    text:'Yep that works' },
    { from:'system',  text:'BOOKING CONFIRMED · DEPOSIT A$30 CAPTURED' },
  ],
  c5: [
    { from:'cust',  text:'Can I move my acrylic to Saturday morning instead?' },
    { from:'agent', text:'Your acrylic full set is Friday 10:00 AM with Trang. Saturday has 11:00 AM free with Trang — want me to move it?' },
    { from:'system',text:'WAITING ON CUSTOMER REPLY' },
  ],
};

export const WAITLIST: WaitlistItem[] = [
  { id:'w1', name:'Olivia Smith', service:'Gel manicure',       want:'Any Friday afternoon',   auto:true  },
  { id:'w2', name:'Grace Tran',   service:'Spa pedicure',       want:'Saturday morning',       auto:true  },
  { id:'w3', name:'Daniel Vo',    service:'Acrylic full set',   want:'This week, evenings',    auto:false },
  { id:'w4', name:'Mia Chen',     service:'Eyelash extensions', want:'Thursday or Friday',     auto:true  },
];

export const CUSTOMERS: Customer[] = [
  { id:'cu1', name:'Sarah Nguyen', phone:'+61 412 220 113', channel:'phone',    visits:14, noShows:0, lastVisit:'2 weeks ago', spend:680,  tags:['Regular'],        fav:'Gel manicure with Linh',      note:'Prefers morning slots. Allergic to acetone-heavy removers.' },
  { id:'cu2', name:'Kim Nguyen',   phone:'+61 423 887 540', channel:'instagram',visits:6,  noShows:3, lastVisit:'5 days ago',  spend:240,  tags:['No-show risk'],   fav:'Eyelash extensions',          note:'Three no-shows in a row. Deposit now required on every booking.' },
  { id:'cu3', name:'Tony Le',      phone:'+61 401 556 902', channel:'sms',      visits:9,  noShows:1, lastVisit:'yesterday',   spend:430,  tags:['Regular'],        fav:'Acrylic full set with Trang', note:'' },
  { id:'cu4', name:'Emily Carter', phone:'+61 438 109 776', channel:'web',      visits:2,  noShows:0, lastVisit:'1 month ago', spend:90,   tags:['New'],            fav:'Spa pedicure',               note:'Found us through Instagram.' },
  { id:'cu5', name:'Mai Tran',     phone:'+61 410 332 188', channel:'phone',    visits:22, noShows:0, lastVisit:'today',       spend:1120, tags:['VIP'],            fav:'SNS dipping powder',         note:'Long-time regular. Books monthly. Always tips.' },
  { id:'cu6', name:'Olivia Smith', phone:'+61 422 770 651', channel:'sms',      visits:5,  noShows:1, lastVisit:'1 week ago',  spend:210,  tags:[],                 fav:'Gel manicure',               note:'' },
  { id:'cu7', name:'Grace Tran',   phone:'+61 433 901 224', channel:'whatsapp', visits:8,  noShows:0, lastVisit:'3 days ago',  spend:360,  tags:['Regular'],        fav:'Spa pedicure with Mai',      note:'' },
  { id:'cu8', name:'David Chen',   phone:'+61 415 668 030', channel:'sms',      visits:4,  noShows:2, lastVisit:'2 weeks ago', spend:160,  tags:['No-show risk'],   fav:'Acrylic full set',           note:'Deposit failed twice on last booking.' },
];

export const PAYOUT: PayoutInfo = {
  held: 240, available: 1180, nextDate: 'Friday 13 June', nextAmount: 1180,
  currency: 'AUD', account: 'Stripe Connect · NAB ••4471',
};

export const DEPOSITS: Deposit[] = [
  { id:'d1', name:'Sarah Nguyen', amount:20, status:'captured', when:'Today, 9:32 AM',       service:'Gel manicure' },
  { id:'d2', name:'Tony Le',      amount:30, status:'held',     when:'Today, 8:14 AM',       service:'Acrylic full set' },
  { id:'d3', name:'Mai Tran',     amount:25, status:'captured', when:'Today, 8:02 AM',       service:'SNS dipping powder' },
  { id:'d4', name:'David Chen',   amount:30, status:'failed',   when:'Today, 7:48 AM',       service:'Acrylic full set' },
  { id:'d5', name:'Jessica Brown',amount:30, status:'held',     when:'Yesterday, 4:20 PM',   service:'Acrylic full set' },
  { id:'d6', name:'Hannah Pham',  amount:20, status:'held',     when:'Yesterday, 2:10 PM',   service:'Eyelash extensions' },
  { id:'d7', name:'Lucy Pham',    amount:20, status:'refunded', when:'Mon, 11:00 AM',        service:'Gel manicure' },
];

export const CAMPAIGNS: Campaign[] = [
  { id:'cp1', title:'Win back no-shows',     status:'awaiting',  audience:'8 customers with a no-show in the last 60 days', channel:'sms', recipients:8,  when:'Holding for your approval',    message:"Hi {name}, we miss you at Bella Nails. Book any service this week and your deposit is on us. Reply BOOK and our assistant will sort a time that suits you." },
  { id:'cp2', title:'Quiet Tuesday fill',    status:'scheduled', audience:'Regulars who usually book weekdays',             channel:'sms', recipients:34, when:'Sends Mon 9 Jun, 10:00 AM',    message:'Hi {name}, Tuesdays are quiet this week. 15% off any gel or SNS set. Reply to grab a spot.' },
  { id:'cp3', title:'Lash refill reminder',  status:'sent',      audience:'Lash clients due for a refill',                  channel:'sms', recipients:21, when:'Sent Fri 6 Jun · 19 booked',   message:'Hi {name}, your lashes are due for a refill. Want me to find you a slot this week?' },
];

export const ANALYTICS: AnalyticsData = {
  range: 'Last 7 days',
  kpis: [
    { key:'bookings', label:'Bookings',          value:'86',       delta:'+12%',    up:true, sub:'vs prior week' },
    { key:'noshow',   label:'No-show rate',      value:'3.4%',     delta:'-1.8pt',  up:true, sub:'deposits working' },
    { key:'ai',       label:'AI-handled',        value:'91%',      delta:'+4pt',    up:true, sub:'of all bookings' },
    { key:'rev',      label:'Deposits captured', value:'A$1,740',  delta:'+A$210',  up:true, sub:'this week' },
  ],
  perDay: [
    { d:'Mon', v:11 }, { d:'Tue', v:9 }, { d:'Wed', v:13 },
    { d:'Thu', v:14 }, { d:'Fri', v:15 }, { d:'Sat', v:18 }, { d:'Sun', v:6 },
  ],
  channels: [
    { label:'Phone',     v:38, color:'#15924B' },
    { label:'SMS',       v:24, color:'#2F6BEB' },
    { label:'Instagram', v:16, color:'#BC2A8D' },
    { label:'Web',       v:14, color:'#C98A09' },
    { label:'WhatsApp',  v:8,  color:'#1FA855' },
  ],
  handled: { ai:91, you:9 },
  topServices: [
    { name:'Gel manicure',       v:28 },
    { name:'Acrylic full set',   v:21 },
    { name:'Eyelash extensions', v:17 },
    { name:'Spa pedicure',       v:12 },
    { name:'SNS dipping powder', v:8  },
  ],
};

export const ANALYTICS_30D: AnalyticsData = {
  range: 'Last 30 days',
  kpis: [
    { key:'bookings', label:'Bookings',          value:'347',      delta:'+18%',     up:true, sub:'vs prior period' },
    { key:'noshow',   label:'No-show rate',      value:'3.2%',     delta:'-0.6pt',   up:true, sub:'deposits working' },
    { key:'ai',       label:'AI-handled',        value:'92%',      delta:'+2pt',     up:true, sub:'of all bookings' },
    { key:'rev',      label:'Deposits captured', value:'A$6,820',  delta:'+A$940',   up:true, sub:'this period' },
  ],
  perDay: [
    { d:'Wk 1', v:71 }, { d:'Wk 2', v:78 }, { d:'Wk 3', v:89 },
    { d:'Wk 4', v:86 }, { d:'Wk 5', v:23 },
  ],
  channels: [
    { label:'Phone',     v:152, color:'#15924B' },
    { label:'SMS',       v:96,  color:'#2F6BEB' },
    { label:'Instagram', v:62,  color:'#BC2A8D' },
    { label:'Web',       v:55,  color:'#C98A09' },
    { label:'WhatsApp',  v:32,  color:'#1FA855' },
  ],
  handled: { ai:92, you:8 },
  topServices: [
    { name:'Gel manicure',       v:114 },
    { name:'Acrylic full set',   v:86  },
    { name:'Eyelash extensions', v:71  },
    { name:'Spa pedicure',       v:48  },
    { name:'SNS dipping powder', v:28  },
  ],
};

export const ANALYTICS_90D: AnalyticsData = {
  range: 'Last 90 days',
  kpis: [
    { key:'bookings', label:'Bookings',          value:'1,098',    delta:'+22%',     up:true, sub:'vs prior period' },
    { key:'noshow',   label:'No-show rate',      value:'2.9%',     delta:'-1.1pt',   up:true, sub:'deposits working' },
    { key:'ai',       label:'AI-handled',        value:'93%',      delta:'+6pt',     up:true, sub:'of all bookings' },
    { key:'rev',      label:'Deposits captured', value:'A$22,140', delta:'+A$5,200', up:true, sub:'this period' },
  ],
  perDay: [
    { d:'Apr', v:312 }, { d:'May', v:389 }, { d:'Jun', v:397 },
  ],
  channels: [
    { label:'Phone',     v:480, color:'#15924B' },
    { label:'SMS',       v:302, color:'#2F6BEB' },
    { label:'Instagram', v:198, color:'#BC2A8D' },
    { label:'Web',       v:178, color:'#C98A09' },
    { label:'WhatsApp',  v:94,  color:'#1FA855' },
  ],
  handled: { ai:93, you:7 },
  topServices: [
    { name:'Gel manicure',       v:356 },
    { name:'Acrylic full set',   v:278 },
    { name:'Eyelash extensions', v:231 },
    { name:'Spa pedicure',       v:152 },
    { name:'SNS dipping powder', v:81  },
  ],
};

export const ANALYTICS_BY_RANGE: Record<string, AnalyticsData> = {
  '7d': ANALYTICS, '30d': ANALYTICS_30D, '90d': ANALYTICS_90D,
};

export const SETTINGS = {
  hours: [
    { d:'Monday',    open:'9:00 AM', close:'6:00 PM', on:true  },
    { d:'Tuesday',   open:'9:00 AM', close:'6:00 PM', on:true  },
    { d:'Wednesday', open:'9:00 AM', close:'6:00 PM', on:true  },
    { d:'Thursday',  open:'9:00 AM', close:'8:00 PM', on:true  },
    { d:'Friday',    open:'9:00 AM', close:'8:00 PM', on:true  },
    { d:'Saturday',  open:'9:00 AM', close:'5:00 PM', on:true  },
    { d:'Sunday',    open:'10:00 AM',close:'4:00 PM', on:false },
  ] as SettingsHour[],
  deposit: { require:true, amountPolicy:'Per service', noShowOnly:false, refundWindow:'24 hours' },
  guardrails: [
    { id:'g1', label:'Never book without reading the details back',                      on:true,  locked:true },
    { id:'g2', label:'Require a deposit from customers with a past no-show',             on:true  },
    { id:'g3', label:'Escalate to a human when unsure about a service',                  on:true  },
    { id:'g4', label:'Never send marketing without owner approval',                      on:true,  locked:true },
    { id:'g5', label:'Auto-book from waitlist when a slot opens',                        on:true  },
    { id:'g6', label:'Offer discounts on its own',                                       on:false },
  ] as SettingsGuardrail[],
  knowledge: [
    { q:'Do you do ombre on toes?',  a:'Not currently — ombre is fingernails only.',                                          status:'needs-answer' },
    { q:'Where do I park?',          a:'Free 2-hour parking on Gouger St, rear entrance.',                                    status:'answered' },
    { q:'Do you take walk-ins?',     a:'Yes, subject to chair availability. The agent always offers the next open slot first.',status:'answered' },
  ] as SettingsKnowledge[],
};

// ---- time helpers ----

/** Convert a display time string ("9:00 AM", "8:00 PM") to minutes since midnight. */
export function parseTime(s: string): number {
  const [time, ampm] = s.split(' ');
  const [hStr, mStr] = time.split(':');
  let h = parseInt(hStr, 10);
  const m = parseInt(mStr || '0', 10);
  if (ampm === 'PM' && h !== 12) h += 12;
  if (ampm === 'AM' && h === 12) h = 0;
  return h * 60 + m;
}

/**
 * Return the open/close bounds (minutes since midnight) for a given day index
 * (0=Mon…6=Sun), plus whether the business is closed that day. Closed days still
 * report their configured hours so the grid can frame any existing bookings, but
 * `closed` lets the UI flag the day instead of silently rendering a normal schedule.
 */
export function dayBounds(dayIdx: number): { start: number; end: number; closed: boolean } {
  const h = SETTINGS.hours[dayIdx];
  if (!h) return { start: 540, end: 1080, closed: false }; // fallback 9 AM – 6 PM
  return { start: parseTime(h.open), end: parseTime(h.close), closed: !h.on };
}

export const TIME_SLOTS = (() => {
  const o: { value: string; label: string }[] = [];
  for (let m = 540; m <= 1050; m += 30) o.push({ value: String(m), label: fmtT(m) });
  return o;
})();
