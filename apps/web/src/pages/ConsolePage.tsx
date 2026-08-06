import { useState, useEffect, useMemo, useRef, useCallback } from "react";
import {
  Booking, THREADS, MESSAGES,
  QueueItem, WaitlistItem,
  Service, SERVICE_LIST, TenantConfig, TENANT,
} from "../data/console";

import {
  useAvailability, useBookingSchedule, useCatalogItems, useCustomers, useOperations, useSessions,
  useTranscript, useWaitlistEnrollments,
} from "../features/hooks";
import { toAvailableSlots } from "../features/bookings/availability";
import { toBookableServices } from "../features/bookings/services";
import { UNASSIGNED_RESOURCES } from "../features/bookings/resources";
import { toBookings, weekWindow } from "../features/bookings/adapters";
import { updateForKey, valueForKey, type ScopedTo } from "../features/bookings/scoped";
import { buildWeek, dayLabel, shiftWeekWindow } from "../features/bookings/week";
import { buildSubmission, customerFromRef, toIsoInstant } from "../features/bookings/buildSubmission";
import { getOrCreateAttempt, resetAttempt } from "../features/bookings/identity";
import { toQueueItems } from "../features/operations/adapters";
import { statusFromResponse } from "../features/operations/api";
import {
  operationActionFailureMessage, useRequestOperationCancellation, useResolveOperation,
  useRespondToAlternatives, useResumeOperation,
} from "../features/operations/mutations";
import { toMessages, toThreads } from "../features/conversations/adapters";
import { toCustomers } from "../features/customers/adapters";
import { toWaitlistItems } from "../features/waitlist/adapters";
import { prefillFromWaitlist, type CreatePrefill } from "../features/waitlist/prefill";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { getBookingDetails } from "../features/bookings/api";
import { submitBooking } from "../features/bookings/submit";
import { preflightReschedule } from "../features/bookings/preflight";
import { listWaitlistLocations } from "../features/waitlist/api";
import {
  isRetryableWithNewAttempt, offerFailureMessage, pollToTerminal, useAcceptOffer,
  useCancelBooking, useDeclineOffer, useRemoveFromWaitlist, writeFailureMessage,
} from "../features/bookings/mutations";
import { normalizedStatusLabel } from "../features/bookings/useOperationStatus";
import { VerificationGate } from "../features/verification/VerificationGate";
import { SENSITIVE_READ_PURPOSES, type SensitiveReadPurpose } from "../features/conversations/purposes";
import { getActor } from "../api/config";
import { queryKeys } from "../api/queryKeys";
import { isApiVerificationRequiredError } from "../api/problem";

import { Skeleton } from "../components/ui/Skeleton";
import { DemoBadge, PartialBadge } from "../components/ui/DemoBadge";
import { ErrorState } from "../components/ui/ErrorState";

import { StatusBar } from "../components/platform/console/StatusBar";
import { Sidebar } from "../components/platform/console/Sidebar";
import { BottomNav } from "../components/platform/console/BottomNav";
import { Header } from "../components/platform/console/Header";

import { TodayScreen } from "../components/platform/screens/TodayScreen";
import { QueueScreen } from "../components/platform/screens/QueueScreen";
import { ConversationsScreen } from "../components/platform/screens/ConversationsScreen";
import { WaitlistScreen } from "../components/platform/screens/WaitlistScreen";
import { CustomersScreen } from "../components/platform/screens/CustomersScreen";
import { DepositsScreen } from "../components/platform/screens/DepositsScreen";
import { CampaignsScreen } from "../components/platform/screens/CampaignsScreen";
import { AnalyticsScreen, type AnalyticsRange } from "../components/platform/screens/AnalyticsScreen";
import { SettingsScreen } from "../components/platform/screens/SettingsScreen";
import { MoreScreen } from "../components/platform/screens/MoreScreen";

import { BookingDetailDrawer } from "../components/platform/drawers/BookingDetailDrawer";
import { CreateBookingDrawer } from "../components/platform/drawers/CreateBookingDrawer";
import { EnrollWaitlistDrawer } from "../components/platform/drawers/EnrollWaitlistDrawer";
import { RescheduleDrawer } from "../components/platform/drawers/RescheduleDrawer";

import { ToastContainer } from "../components/ui/Toast";
import { CommandPalette } from "../components/ui/CommandPalette";
import { useToast } from "../hooks/useToast";

type Screen =
  | 'today' | 'queue' | 'conversations' | 'waitlist'
  | 'customers' | 'deposits' | 'campaigns' | 'analytics'
  | 'settings' | 'more';

const DESKTOP_BP = 768;
const COLLAPSE_BP = 1100;

function useDevice(): 'phone' | 'desktop' {
  const [device, setDevice] = useState<'phone' | 'desktop'>(
    () => window.innerWidth >= DESKTOP_BP ? 'desktop' : 'phone'
  );
  useEffect(() => {
    const mq = window.matchMedia(`(min-width: ${DESKTOP_BP}px)`);
    const handler = (e: MediaQueryListEvent) => setDevice(e.matches ? 'desktop' : 'phone');
    mq.addEventListener('change', handler);
    return () => mq.removeEventListener('change', handler);
  }, []);
  return device;
}

function LoadingSkeletons() {
  return (
    <div className="flex flex-col gap-3">
      <Skeleton h={72} />
      <Skeleton h={14} w={120} r={6} style={{ marginTop: 6 }} />
      <Skeleton h={86} /><Skeleton h={86} /><Skeleton h={86} />
    </div>
  );
}

/**
 * Identity of a queue dismissal: the operation AND the state it was in.
 *
 * Keying on the id alone hides the row forever, so an operation that escalates after being
 * cleared (manual_review -> compensation_required) never comes back and the operator never sees it.
 */
function dismissalKey(item: QueueItem): string {
  return `${item.id}|${item.status ?? item.reason}`;
}

/** Stable empty identities, so a scoped miss does not produce a new array/object each render. */
const EMPTY_BOOKINGS: Booking[] = [];

interface TranscriptAuth {
  purpose: SensitiveReadPurpose | null;
  grant: string | null;
}
const NO_TRANSCRIPT_AUTH: TranscriptAuth = { purpose: null, grant: null };

/** What the header eyebrow says while Analytics is open — must match the selected window. */
const ANALYTICS_RANGE_LABEL: Record<AnalyticsRange, string> = {
  '7d': 'Last 7 days', '30d': 'Last 30 days', '90d': 'Last 90 days',
};

/** "a", "a and b", "a, b and c" — so the banner names exactly what failed. */
function listLabels(labels: string[]): string {
  if (labels.length <= 1) return labels[0] ?? '';
  return `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`;
}

export function ConsolePage() {
  const device = useDevice();
  const isPhone = device === 'phone';
  const isDesktop = !isPhone;

  const [screen, setScreen] = useState<Screen>('today');
  const [loading, setLoading] = useState(false);
  const ltRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Sidebar collapse
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${COLLAPSE_BP}px)`);
    setSidebarCollapsed(mq.matches);
    const h = (e: MediaQueryListEvent) => setSidebarCollapsed(e.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);

  // Scroll shadow
  const [scrolled, setScrolled] = useState(false);

  // Command palette
  const [cmdOpen, setCmdOpen] = useState(false);

  /** The analytics window, held here so the page header can label it truthfully. */
  const [analyticsRange, setAnalyticsRange] = useState<AnalyticsRange>('7d');

  // When viewport grows to desktop, 'more' isn't a real screen — reset to today
  useEffect(() => {
    if (isDesktop && screen === 'more') setScreen('today');
  }, [isDesktop, screen]);

  // Sign-out — respect the Vite base path (import.meta.env.BASE_URL) so we land
  // on the app root even when the SPA is served under a prefix (VITE_BASE_PATH).
  function handleSignOut() {
    window.location.href = import.meta.env.BASE_URL;
  }

  // Waitlist actions
  function toggleWaitlistAuto(id: string) {
    setWaitlist(wl => wl.map(w => w.id === id ? { ...w, auto: !w.auto } : w));
  }
  // Customer focus (from CommandPalette)
  function handleOpenCustomer(id: string) {
    setCustomerFocus(id);
    go('customers');
  }

  // Calendar. Opens on the real current weekday (0 = Monday), not a fixed column.
  const [selDay, setSelDay] = useState(() => (new Date().getDay() + 6) % 7);

  // Conversations
  const [convOpen, setConvOpen] = useState('c3');
  const [convView, setConvView] = useState<'list' | 'thread'>('list');
  const [takenOver, setTakenOver] = useState<Set<string>>(new Set());

  // Waitlist
  const [autoBook, setAutoBook] = useState(true);
  const [waitlist, setWaitlist] = useState<WaitlistItem[]>([]);

  /**
   * Removes a guest from the waitlist.
   *
   * Optimistic with rollback: the row disappears immediately, and if the API refuses it comes
   * back with the server's message — the operator is never told something happened that did
   * not (Inv-16). The API audits the removal against `X-Actor`, which `fetchApi` attaches.
   */
  const removeWaitlistMutation = useRemoveFromWaitlist(setWaitlist);

  /**
   * Answering a live waitlist offer.
   *
   * Not optimistic: accepting finalizes a capacity hold and confirms a booking server-side,
   * and both actions can legitimately 409 when the guest answered first or the offer expired.
   * The row is left alone until the server has decided, then the list refetches.
   */
  const acceptOfferMutation = useAcceptOffer();
  const declineOfferMutation = useDeclineOffer();
  const offerBusyId =
    acceptOfferMutation.isPending ? acceptOfferMutation.variables?.offerId ?? null
    : declineOfferMutation.isPending ? declineOfferMutation.variables?.offerId ?? null
    : null;

  function answerOffer(kind: 'accept' | 'decline', offerId: string, name: string) {
    const mutation = kind === 'accept' ? acceptOfferMutation : declineOfferMutation;
    if (mutation.isPending) return;

    mutation.mutate({ offerId, name }, {
      onSuccess: () => toast({
        message: kind === 'accept'
          ? `Offer accepted for ${name}`
          : `Offer declined for ${name} — the next guest will be matched`,
        duration: 4000,
      }),
      onError: (error) => toast({ message: offerFailureMessage(error, kind), duration: 6000 }),
    });
  }

  function removeFromWaitlist(id: string) {
    const item = waitlist.find(w => w.id === id);
    if (!item) return;

    removeWaitlistMutation.mutate(
      { enrollmentId: id, current: waitlist },
      {
        onSuccess: () => toast({ message: `${item.name} removed from waitlist`, duration: 4000 }),
        onError: (error) => toast({
          message: error instanceof Error ? error.message : 'Could not remove this guest',
          duration: 6000,
        }),
      }
    );
  }

  // Customers — focus a specific customer from CommandPalette
  const [customerFocus, setCustomerFocus] = useState<string | null>(null);

  // Business config
  const [services, setServices] = useState<Service[]>(() => [...SERVICE_LIST]);
  const [tenantCfg, setTenantCfg] = useState<TenantConfig>(() => ({ ...TENANT }));

  // ---- Live server state -------------------------------------------------
  // Bookings, the queue and the waitlist are seeded EMPTY and only ever show server rows.
  // Conversations and customers fall back to LABELLED demo rows while their query has not
  // produced data — which includes a failure, so the fallback always arrives with the named
  // load error and a DemoBadge beside it, never presented as the venue's own data.
  const queryClient = useQueryClient();
  // The displayed week is state, not a constant: the grid can move week to week, and every
  // date on screen is derived from THIS window rather than from a hard-coded literal. The
  // query key already includes from/to, so each week caches independently.
  const [scheduleWindow, setScheduleWindow] = useState(() => weekWindow());
  const week = useMemo(() => buildWeek(scheduleWindow.from), [scheduleWindow.from]);
  // Read by `setBookings`, which must tag writes with the CURRENT week without being recreated
  // (and thus re-running every effect that depends on it) each time the week changes.
  const scheduleWindowRef = useRef(scheduleWindow.from);
  scheduleWindowRef.current = scheduleWindow.from;
  const scheduleQuery = useBookingSchedule(scheduleWindow);

  /**
   * The capacity the venue has declared for the displayed week (docs §12 gap 10 — closed
   * 2026-08-06). Advisory only: it holds nothing, so it informs the create drawer rather than
   * gating it, and an empty answer means "no slots declared", never "fully booked".
   */
  const availabilityQuery = useAvailability(scheduleWindow.from, scheduleWindow.to);
  const availableSlots = useMemo(
    () => (availabilityQuery.data ? toAvailableSlots(availabilityQuery.data) : undefined),
    [availabilityQuery.data]
  );
  const operationsQuery = useOperations({ needsAttention: true, limit: 100 });
  const sessionsQuery = useSessions(50);
  const waitlistQuery = useWaitlistEnrollments(true, 100);
  const customersQuery = useCustomers("", 50);
  /**
   * The venue's bookable services.
   *
   * Shares its query key with the Settings screen's catalog list, so this is the same cached
   * request rather than a second one. The create drawer submits these ids verbatim as
   * `service_id`, which is the point: it previously submitted keys from the hand-written
   * `SERVICE_LIST`, none of which exists on the server.
   */
  const catalogQuery = useCatalogItems();
  const bookableServices = useMemo(
    () => (catalogQuery.data ? toBookableServices(catalogQuery.data) : undefined),
    [catalogQuery.data]
  );

  const liveBookings = useMemo(
    () => toBookings(scheduleQuery.data?.bookings, { weekStart: week.start }),
    [scheduleQuery.data, week.start]
  );

  /** Moves the grid a whole week; the rows follow because the window is the query key. */
  function goToWeek(deltaWeeks: number) {
    setScheduleWindow(current => shiftWeekWindow(current, deltaWeeks));
  }

  /** Back to the current week AND to today's column — "Today" must mean today. */
  function goToToday() {
    setScheduleWindow(weekWindow());
    setSelDay((new Date().getDay() + 6) % 7);
  }
  const liveQueue = useMemo(() => toQueueItems(operationsQuery.data?.operations), [operationsQuery.data]);
  const liveThreads = useMemo(() => toThreads(sessionsQuery.data?.sessions), [sessionsQuery.data]);
  const liveWaitlist = useMemo(() => toWaitlistItems(waitlistQuery.data?.enrollments), [waitlistQuery.data]);
  const liveCustomers = useMemo(() => toCustomers(customersQuery.data?.customers), [customersQuery.data]);

  // Only fetch a transcript for a session that came from the API — the demo thread ids
  // are not real sessions and would 404.
  const openSessionIsLive = liveThreads.some(t => t.id === convOpen);
  /**
   * Why this operator is reading a transcript. Chosen per conversation and never defaulted: it is
   * recorded verbatim in `audit_events`, and a constant leaves the trail unable to tell a dispute
   * investigation from idle browsing (spec 14 §2, purpose-restricted access).
   */
  /**
   * The purpose and step-up grant for reading a transcript, **bound to the session they were
   * given for** and held in memory only (Inv-11).
   *
   * Bound rather than reset in an effect, and read at render, because an effect is too late:
   * `useTranscript` subscribes during this render, so clearing in a `useEffect` keyed on
   * `convOpen` let one render escape with the NEW session id and the PREVIOUS conversation's
   * reason code and grant — firing an audited sensitive read the operator never authorised for
   * that subject, with a grant issued for someone else (spec 14 §2, Inv-11). Comparing the
   * stored session id makes that frame impossible.
   */
  const [transcriptAuth, setTranscriptAuth] = useState<ScopedTo<string, TranscriptAuth>>(
    () => ({ key: "", value: NO_TRANSCRIPT_AUTH })
  );

  const openAuth = valueForKey(transcriptAuth, convOpen, NO_TRANSCRIPT_AUTH);
  const transcriptPurpose = openAuth.purpose;
  const transcriptGrant = openAuth.grant;

  const setTranscriptPurpose = (purpose: SensitiveReadPurpose) =>
    // A new purpose drops any grant with it: both are stated for one reading of one conversation.
    setTranscriptAuth({ key: convOpen, value: { purpose, grant: null } });

  const setTranscriptGrant = (grant: string) =>
    setTranscriptAuth(current =>
      // Only ever attach a grant to the session it was obtained for.
      updateForKey(current, convOpen, NO_TRANSCRIPT_AUTH, existing => ({ ...existing, grant }))
    );

  const transcriptQuery = useTranscript(
    openSessionIsLive ? convOpen : null,
    transcriptPurpose,
    transcriptGrant ?? undefined
  );

  /** True when the platform says this transcript needs step-up, not that it refused outright. */
  const transcriptNeedsVerification =
    openSessionIsLive && isApiVerificationRequiredError(transcriptQuery.error);

  // Local overlays: optimistic removals applied on top of server data until the next
  // refetch. Server truth always wins on reload (Inv-16).
  const [dismissedQueueIds, setDismissedQueueIds] = useState<Set<string>>(new Set());

  // Data.
  //
  // Seeded EMPTY, not from the mock constants: seeding from mocks flashed fabricated
  // bookings and queue items — with no Demo badge — for as long as the first fetch took,
  // which an operator would read as their own data. An empty list plus the query's own
  // pending state is honest.
  /**
   * The grid's rows, **tagged with the week they belong to**.
   *
   * Tagging rather than a bare array, and read at render rather than reset in an effect: moving
   * to another week changes `week` immediately, but the new window's query starts at
   * `data === undefined`, so a plain array would keep the previous week's bookings on screen
   * under the new week's dates — the exact "the label disagrees with the data underneath it"
   * defect this whole change exists to remove. A stale tag renders as empty, with no
   * intermediate frame in which the two disagree.
   */
  const [bookingsState, setBookingsState] = useState<ScopedTo<string, Booking[]>>(
    () => ({ key: scheduleWindow.from, value: [] })
  );
  const bookings = valueForKey(bookingsState, scheduleWindow.from, EMPTY_BOOKINGS);

  const setBookings = useCallback((update: React.SetStateAction<Booking[]>) => {
    // Re-bases onto an empty list when the stored rows belong to another week, so a
    // late-arriving optimistic write cannot resurrect the previous week's bookings.
    setBookingsState(current =>
      updateForKey(current, scheduleWindowRef.current, EMPTY_BOOKINGS, update)
    );
  }, []);
  const [queue, setQueue] = useState<QueueItem[]>([]);
  // Queue actions are local and instant now that 'finish' is an acknowledgement rather
  // than a server call, so there is no pending state to show.
  const busy: string | null = null;

  // Server data replaces the seeded demo rows as soon as it arrives.
  useEffect(() => {
    if (scheduleQuery.data) setBookings(liveBookings);
  }, [scheduleQuery.data, liveBookings]);

  useEffect(() => {
    if (!operationsQuery.data) return;

    // A dismissal acknowledges the operation AS IT STOOD. If the platform moves it afterwards —
    // a manual_review escalating to compensation_required — that is new information, and keeping
    // it hidden means the operator never learns their queue is stale. The dismissal is therefore
    // keyed to the row's state, not just its id, so an escalation resurfaces it.
    setQueue(liveQueue.filter(q => !dismissedQueueIds.has(dismissalKey(q))));

    // Drop dismissals whose operation the server no longer reports at all, so the set cannot
    // grow without bound across a long shift.
    const liveKeys = new Set(liveQueue.map(dismissalKey));
    setDismissedQueueIds(prev => {
      const next = new Set([...prev].filter(k => liveKeys.has(k)));
      return next.size === prev.size ? prev : next;
    });
  }, [operationsQuery.data, liveQueue, dismissedQueueIds]);

  useEffect(() => {
    if (waitlistQuery.data) setWaitlist(liveWaitlist);
  }, [waitlistQuery.data, liveWaitlist]);

  // Drawers
  const [drawer, setDrawer] = useState<{
    kind: 'detail' | 'create' | 'reschedule' | 'enroll' | null;
    bookingId: string | null;
  }>({ kind: null, bookingId: null });

  /**
   * What the create drawer opens with. Empty for a plain "New booking"; populated when the
   * operator books a specific waitlisted guest, so they do not retype what the enrollment
   * already records.
   */
  const [createPrefill, setCreatePrefill] = useState<CreatePrefill | undefined>(undefined);

  // Toasts
  const { toasts, toast, dismiss } = useToast();

  // ⌘K shortcut → open command palette
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && (e.key === 'k' || e.key === 'K')) {
        e.preventDefault();
        setCmdOpen(true);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
      if (ltRef.current) clearTimeout(ltRef.current);
    };
  }, []);

  const queueCount = queue.length;
  // No campaign badge. A nav badge is a claim that something needs the operator's attention, and
  // campaigns are entirely demo data — no endpoint and no spec owns them, so nothing is awaiting
  // approval. This was a hard-coded `1`, painted amber on the sidebar and duplicated as a second
  // literal inside MoreScreen.
  const campCount = 0;
  // A thread only counts as "live" until it's taken over by a human.
  //
  // Computed from SERVER threads only — never from the demo fallback. This drives a dot in the
  // persistent nav (Sidebar and BottomNav), which is on screen for every screen, and a dot has
  // nowhere to put a DemoBadge. The fallback condition here is `sessionsQuery.data === undefined`,
  // which covers the initial load AND every failure, and `THREADS[0]` is hard-coded `live: true` —
  // so the console asserted "an AI conversation is happening right now" to an operator who had
  // never opened Conversations, on the strength of a mock row, most confidently at exactly the
  // moment it had failed to reach the server.
  //
  // The Conversations screen may still show demo threads, because there the fallback is labelled
  // and badged. Unbadgeable chrome gets the truthful answer instead: we do not know, so we do not
  // claim.
  const hasLive = liveThreads.some(t => t.live && !takenOver.has(t.id));

  function go(next: string) {
    const n = next as Screen;
    if (n === screen) {
      if (n === 'conversations' && isPhone) setConvView('list');
      return;
    }
    setScreen(n);
    if (n === 'conversations' && isPhone) setConvView('list');
    setLoading(true);
    if (ltRef.current) clearTimeout(ltRef.current);
    ltRef.current = setTimeout(() => setLoading(false), 540);
  }

  // Drawer actions
  const openBooking = (id: string) => setDrawer({ kind: 'detail', bookingId: id });
  const openCreate = () => {
    setCreatePrefill(undefined);
    setDrawer({ kind: 'create', bookingId: null });
  };
  const openEnroll = () => setDrawer({ kind: 'enroll', bookingId: null });

  /**
   * Books a waitlisted guest.
   *
   * Opens the create drawer already carrying what the enrollment records — guest, service,
   * party size and the start of their acceptable window — instead of a blank form the
   * operator has to re-derive from the row they just clicked. When that window falls in a
   * different week, the grid moves there first so the prefilled day index means what it says.
   */
  function bookFromWaitlist(id: string) {
    const entry = waitlist.find(w => w.id === id);
    if (!entry) { openCreate(); return; }

    let targetWeek = week;
    if (entry.acceptableFrom) {
      const from = new Date(entry.acceptableFrom);
      if (!Number.isNaN(from.getTime())) {
        const targetWindow = weekWindow(from);
        if (targetWindow.from !== scheduleWindow.from) {
          setScheduleWindow(targetWindow);
          targetWeek = buildWeek(targetWindow.from);
        }
      }
    }

    setCreatePrefill(prefillFromWaitlist(entry, targetWeek));
    setDrawer({ kind: 'create', bookingId: null });
  }
  const closeDrawer = () => {
    // Drop any pending step-up: it belonged to the booking being closed, and leaving it set
    // would make the gate reappear on whichever booking is opened next.
    setCancelAwaitingGrant(null);
    setDrawer(d => ({ ...d, kind: null }));
  };
  const startReschedule = (id: string) => setDrawer({ kind: 'reschedule', bookingId: id });

  /** Refreshes every list a booking mutation can affect. */
  function invalidateBookingViews() {
    void queryClient.invalidateQueries({ queryKey: queryKeys.bookings.all() });
    void queryClient.invalidateQueries({ queryKey: queryKeys.operations.all() });
  }

  /**
   * Cancels a booking.
   *
   * The whole submit→poll sequence is the mutation, so `isPending` covers the real duration
   * and the outcome reported is the server's terminal status, phrased for a cancellation —
   * `booked` means "cancelled" here, not "confirmed" (D1 has no cancelled operation status).
   */
  const cancelMutation = useCancelBooking();
  const cancelBusy = cancelMutation.isPending;

  /** Bookings whose reschedule is mid-flight, so a second drag cannot race the first. */
  const rescheduleInFlight = useRef<Set<string>>(new Set());
  /**
   * Stops reschedule polling when the console unmounts — the server keeps working.
   *
   * Created per submission, never up-front: StrictMode double-invokes effects, so a controller
   * allocated here would be aborted by the first cleanup and its signal would be dead for the
   * rest of the session — every later poll would reject instantly.
   */
  const pollAbort = useRef<AbortController | null>(null);
  useEffect(() => () => pollAbort.current?.abort(), []);

  /**
   * The booking awaiting step-up before its cancellation can be submitted.
   *
   * The platform requires a `cancel` grant and rejects an empty one *structurally* (400), so there
   * is no submit-then-recover path — the grant must exist before the first request.
   */
  const [cancelAwaitingGrant, setCancelAwaitingGrant] = useState<string | null>(null);

  function cancelBooking(id: string) {
    if (cancelMutation.isPending) return;
    // Ask for the grant first; the submission happens in submitCancelWithGrant below.
    setCancelAwaitingGrant(id);
  }

  function submitCancelWithGrant(id: string, grantId: string) {
    setCancelAwaitingGrant(null);
    if (cancelMutation.isPending) return;

    cancelMutation.mutate({ bookingId: id, grantId }, {
      onSuccess: ({ status, settled, cancelled }) => {
        // Only dismiss the booking when it is actually gone. A settled *refusal* (declined,
        // failed) or an unfinished poll leaves the drawer open, because the operator still
        // has something to do with this booking.
        if (cancelled) closeDrawer();
        toast({
          message: settled
            ? normalizedStatusLabel(status?.status, 'cancel')
            : 'Still cancelling — check the queue shortly',
          duration: 5000,
        });
      },
      onError: (error) => {
        // A grant can expire between issue and use. That 403 IS resolvable, so put the gate back
        // rather than leaving the operator holding a message they cannot act on.
        if (isApiVerificationRequiredError(error)) setCancelAwaitingGrant(id);
        toast({ message: writeFailureMessage(error, 'cancel this booking'), duration: 6000 });
      },
    });
  }


  function onCreated(nb: Omit<Booking, 'id'>) {
    // Shown immediately so the grid reacts, then replaced by the server's row — the
    // placeholder id is not a real booking and would 404 if acted upon.
    // `isBooking: false` — the id is client-fabricated and addresses nothing on the server, so
    // nothing may offer to cancel or move it in the window before the refetch replaces it.
    setBookings(bs => [...bs, { ...nb, id: 'new' + Date.now(), isBooking: false }]);
    invalidateBookingViews();
  }

  /**
   * Reschedules a booking.
   *
   * The grid updates immediately so drag-to-reschedule still feels direct, but the change
   * is submitted as a durable modification and the grid is refreshed from the server when
   * the operation settles — server truth always wins (Inv-16).
   */
  // `dur` is REQUIRED, not optional. Both callers (the drag handler and the reschedule drawer)
  // have the booking's duration in hand, and an absent one is not "unchanged" — it omits
  // `duration_minutes` from the canonical payload and durably resets the booking to the grid's
  // 60-minute default.
  function onRescheduleDone(id: string, patch: { day: number; start: number; staff: string; dur: number }) {
    // `id` is an OPERATION id while the platform is still finalising: there is no booking to
    // move, and `submitReschedule` would only discover that after a network round trip — moving
    // the row optimistically and snapping it back. Refuse before touching the grid.
    if (bookings.find(b => b.id === id)?.isBooking === false) {
      toast({
        message: 'That booking is still being confirmed — it can be moved once the platform finishes.',
        duration: 5000,
      });
      return;
    }

    // Two drags of the same booking must not overlap: the second would reuse the first's
    // still-live attempt identity against a different time (Inv-3).
    if (rescheduleInFlight.current.has(id)) {
      toast({ message: 'That booking is already being changed — wait for it to settle.', duration: 4000 });
      return;
    }

    // Snapshot BEFORE the optimistic move, so a refusal can put the row back where it was.
    // `invalidateBookingViews()` alone is not a revert: it marks the query stale and the refetch
    // replaces local state only once it lands, so a refused move stayed on the grid until then —
    // and stayed indefinitely if the refetch failed. The operator would be looking at a booking
    // in a slot the platform had just refused to move it to.
    const before = bookings.find(b => b.id === id);
    setBookings(bs => bs.map(b => b.id === id ? { ...b, ...patch } : b));
    void submitReschedule(id, patch, before);
  }

  async function submitReschedule(
    id: string,
    patch: { day: number; start: number; staff: string; dur: number },
    /** The row as it was before the optimistic move, for putting back on a refusal. */
    before?: Booking
  ) {
    // Anchored to the DISPLAYED week, not the current one: dragging a booking while viewing
    // next week must submit next week's date.
    const startsAt = toIsoInstant(week.start, patch.day, patch.start);
    // Scoped by the payload it will be bound to, not just the booking: an idempotency key
    // must never carry two different canonical payloads (Inv-3).
    const attemptKey = `modify:${id}|${startsAt}|${patch.dur}`;
    rescheduleInFlight.current.add(id);
    const controller = new AbortController();
    pollAbort.current = controller;
    try {
      // No VerificationGate here, and that is now deliberate rather than a gap.
      //
      // Spec 08 §12 authorizes a mutation by a verification grant OR an authenticated staff
      // role. Cancel takes the grant path because `POST .../cancellations` consumes one
      // structurally. A reschedule cannot use that door — `SubmitModificationInput` carries no
      // field for the new time — so it rides the acceptance door, which as of this change
      // requires one of the two and 403s without either (`submitBooking` sends X-Actor and
      // X-Actor-Role). The operator IS the authorization, so asking them to step up a guest
      // would be a gate that authorizes nothing.
      //
      // What this leans on: the console's staff identity is the documented X-Actor shim, which
      // is only trustworthy in Development. That is the known console-authentication exception,
      // not a second one.
      const details = await getBookingDetails(id);
      const version = details.booking?.version;
      if (version === null || version === undefined) {
        toast({
          message: 'This booking has no confirmed record yet, so it cannot be changed.',
          duration: 6000,
        });
        invalidateBookingViews();
        return;
      }

      // The booking's real party size — sending 0 would durably record a reschedule for
      // nobody, and the canonical payload is the persisted intent.
      const partySize = details.booking?.party_size ?? 1;

      // Pre-flight (N5 — prevent the error rather than report it).
      //
      // Asked BEFORE an attempt identity is minted, so a refused change costs no operation id and
      // no idempotency key: the operator corrects and tries again as a genuinely new attempt
      // rather than being told to abandon a booking.
      //
      // What it actually catches TODAY is the stale version — the booking moved underneath the
      // grid between the read and the submit. `ValidateBookingChangeQueryHandler` takes only a
      // booking repository and a grant service, so `capacity_unavailable` is a code it can return
      // in principle and never does, and `policy_failed` only fires on a field the server does not
      // model. Handling all three is deliberate: the console must not silently proceed past a
      // refusal it does not recognise, and the handler is where capacity and policy would be added.
      //
      // A pre-flight that FAILS TO ANSWER is not a refusal. If this endpoint is unreachable the
      // reschedule proceeds: blocking on it would let an outage on an advisory read take down a
      // working mutation, and the acceptance door re-checks everything authoritatively anyway.
      const refusal = await preflightReschedule(id, version, startsAt, patch.dur, partySize);
      if (refusal) {
        toast({ message: refusal, duration: 7000 });
        // Put the row back immediately, then reconcile with the server. Leaving the optimistic
        // move on screen would show the booking in a slot the platform just declined.
        if (before) setBookings(bs => bs.map(b => b.id === id ? before : b));
        invalidateBookingViews();
        return;
      }

      const identity = getOrCreateAttempt(attemptKey);

      // Reschedule rides the acceptance front door so the new time is actually carried
      // (D7 / Inv-1). `expected_booking_version` travels on the canonical payload as
      // `target_booking_version`, keeping optimistic concurrency intact.
      const target = bookings.find(b => b.id === id);

      const request = await buildSubmission(
        {
          operation: 'reschedule',
          serviceId: target?.service ?? '',
          startsAt,
          partySize,
          durationMinutes: patch.dur,
          targetBookingId: id,
          targetBookingVersion: version,
          // The guest has to travel with the change. `NotificationRecipientResolver` reads the
          // destination from the revision THIS operation writes, not from the booking being
          // moved, so a reschedule submitted without a customer node emits a
          // `cancel_confirmed` notification that can never resolve anywhere — the guest is
          // simply never told their booking moved.
          customer: customerFromRef(target?.customerRef),
        },
        {
          identity,
          sessionId: `console:${identity.intentId}`,
          identityRef: getActor(),
          readBackVersion: `reschedule|${id}|${startsAt}`,
          // Same reasoning as the create drawer and `submitCancellation`: taken from the stored
          // attempt, so every re-attempt of this reschedule describes the same confirmation
          // rather than re-stamping the clock (Inv-2/3).
          confirmedAt: identity.confirmedAt,
        }
      );

      const accepted = await submitBooking(request);

      const operationId = accepted.operation_id;
      if (operationId) {
        toast({ message: 'Change submitted — confirming…', duration: 3000 });
        const status = await pollToTerminal(operationId, controller.signal);
        toast({
          message: status.terminal
            ? `Change: ${normalizedStatusLabel(status.status, 'reschedule').toLowerCase()}`
            : 'Still confirming the change — check the queue shortly',
          duration: 5000,
        });
      }
      resetAttempt(attemptKey);
      invalidateBookingViews();
    } catch (error) {
      // Abandoned polling is not a failure: the screen went away, the operation did not.
      // Keep the attempt identity so a retry replays the same key against the same payload.
      if (controller.signal.aborted) return;

      toast({ message: writeFailureMessage(error, 'change this booking'), duration: 6000 });
      if (isRetryableWithNewAttempt(error)) resetAttempt(attemptKey);
      // Put the row back, THEN refetch — the same revert the pre-flight refusal does, and for the
      // same reason. `invalidateBookingViews()` is not a revert: it marks the query stale and
      // local state changes only when the refetch lands, so on the commonest failures (network
      // drop, 409, 403, poll timeout) the bar sat in its new slot while the toast said the change
      // had failed — and stayed there indefinitely if the refetch failed too. This path is far
      // more likely than the refusal path, so it needed the revert more, not less.
      if (before) setBookings(bs => bs.map(b => b.id === id ? before : b));
      invalidateBookingViews();
    } finally {
      rescheduleInFlight.current.delete(id);
    }
  }

  // ---- Server follow-ups on operations (docs §10) --------------------------
  // Distinct from the local clear/dismiss below: these round-trip the API, report only
  // what the server answered, and let the refetch move the row (Inv-16).
  const cancelOperationMutation = useRequestOperationCancellation();
  const resumeOperationMutation = useResumeOperation();
  const resolveOperationMutation = useResolveOperation();
  const alternativesMutation = useRespondToAlternatives();

  const serverBusyId =
    cancelOperationMutation.isPending ? cancelOperationMutation.variables?.operationId ?? null
    : resumeOperationMutation.isPending ? resumeOperationMutation.variables?.operationId ?? null
    : resolveOperationMutation.isPending ? resolveOperationMutation.variables?.operationId ?? null
    : alternativesMutation.isPending ? alternativesMutation.variables?.operationId ?? null
    : null;

  const operationActionFailed = (action: string) => (error: unknown) =>
    toast({ message: operationActionFailureMessage(error, action), duration: 6000 });

  function requestOperationCancel(id: string) {
    if (cancelOperationMutation.isPending) return;
    cancelOperationMutation.mutate({ operationId: id }, {
      // 202 `cancellation_pending`: a request, not a result — say exactly that.
      onSuccess: () => toast({
        message: 'Cancellation requested — the platform will stop this operation at its next safe point.',
        duration: 5000,
      }),
      onError: operationActionFailed('request cancellation'),
    });
  }

  function resumeOperationAction(id: string, reason: string) {
    if (resumeOperationMutation.isPending) return;
    resumeOperationMutation.mutate({ operationId: id, reasonCode: reason }, {
      onSuccess: () => toast({
        message: 'Sent back to the platform — it will re-check what happened.',
        duration: 5000,
      }),
      onError: operationActionFailed('send this back for checks'),
    });
  }

  function resolveOperationAction(
    id: string,
    resolution: 'booked' | 'failed_final',
    providerRef: string | undefined,
    reason: string
  ) {
    if (resolveOperationMutation.isPending) return;
    resolveOperationMutation.mutate(
      { operationId: id, resolution, providerRef, reasonCode: reason },
      {
        onSuccess: (body) => {
          // Echo the server's answer, not the operator's intent, when the body carries one.
          const settled = statusFromResponse(body) ?? resolution;
          toast({
            message: settled === 'booked'
              ? 'Recorded as booked — the provider reference is now the booking id.'
              : 'Recorded as failed — the operation is closed.',
            duration: 5000,
          });
        },
        onError: operationActionFailed('record this outcome'),
      }
    );
  }

  function respondAlternativesAction(id: string, response: 'waitlist' | 'decline') {
    if (alternativesMutation.isPending) return;
    alternativesMutation.mutate({ operationId: id, response }, {
      onSuccess: (body) => {
        const settled = statusFromResponse(body) ?? (response === 'waitlist' ? 'waitlisted' : 'failed_final');
        toast({
          message: settled === 'waitlisted'
            ? 'The guest is on the waitlist — they’ll be offered the next matching slot.'
            : 'Closed — the request was declined.',
          duration: 5000,
        });
      },
      onError: operationActionFailed('answer this request'),
    });
  }

  /**
   * Clears an item from the operator's own queue view.
   *
   * This is deliberately a local acknowledgement, not a server resolution: the only
   * resolve endpoint (`POST /api/booking-operations/{id}/resolve`) applies solely to
   * `manual_review` operations and requires a reason code, so claiming "resolved" here
   * would assert an outcome the platform never produced (Inv-16). The id is recorded so a
   * refetch cannot resurrect the row and contradict what the operator was just told.
   */
  function finishQueue(id: string) {
    const item = queue.find(x => x.id === id);
    setQueue(q => q.filter(x => x.id !== id));
    if (item) setDismissedQueueIds(prev => new Set([...prev, dismissalKey(item)]));
    toast({ message: 'Cleared from your queue', duration: 3000 });
  }

  const dismissQueue = (id: string) => {
    const item = queue.find(x => x.id === id);
    if (!item) return;
    // The SAME key the refetch filter matches on. Storing a bare id here meant the filter
    // (`dismissalKey(q)` = `id|status`) could never match it, so the row reappeared on the very
    // next render — and the orphan id was then pruned, leaving Undo nothing to undo either.
    const key = dismissalKey(item);
    setQueue(q => q.filter(x => x.id !== id));
    setDismissedQueueIds(prev => new Set([...prev, key]));
    toast({
      message: 'Dismissed',
      action: {
        label: 'Undo',
        onClick: () => {
          setDismissedQueueIds(prev => {
            const next = new Set(prev);
            next.delete(key);
            return next;
          });
          setQueue(q => [item, ...q]);
        },
      },
      duration: 5000,
    });
  };

  const openConv = (id: string) => { setConvOpen(id); setConvView('thread'); };

  function takeOver() {
    setTakenOver(prev => new Set([...prev, convOpen]));
    toast({ message: "You're now handling this conversation", duration: 4000 });
  }

  function handleCustomerMessage(customerName: string) {
    const thread = (sessionsQuery.data === undefined ? THREADS : liveThreads)
      .find(t => t.name === customerName);
    if (thread) {
      setConvOpen(thread.id);
      setConvView('thread');
    }
    go('conversations');
  }

  const onViewTranscript = (tid: string) => {
    closeDrawer();
    setConvOpen(tid);
    setConvView('thread');
    setScreen('conversations');
  };

  const activeBooking = bookings.find(b => b.id === drawer.bookingId) ?? null;
  const primaryLabel =
    screen === 'today' ? 'New booking'
    : screen === 'waitlist' ? 'Add to waitlist'
    : undefined;
  const onPrimary =
    screen === 'today' ? openCreate
    : screen === 'waitlist' ? openEnroll
    : undefined;

  /**
   * The `location_id`s this venue has actually enrolled against, from the dedicated endpoint
   * (`GET /api/waitlist/locations`, docs §12 gap 7 — closed 2026-08-06) rather than scraped out
   * of the enrollments list: that list is capped, so a location used only by older enrolments
   * would never be suggested, which is exactly how a typo'd location gets created.
   *
   * It is a usage list, not a registry — the platform has no locations table — so the drawer
   * warns on an unrecognised value rather than refusing it.
   */
  const locationsQuery = useQuery({
    queryKey: queryKeys.waitlist.locations(),
    queryFn: ({ signal }) => listWaitlistLocations(signal),
    staleTime: 5 * 60_000,
  });

  const knownLocations = useMemo(() => {
    const declared = locationsQuery.data?.locations ?? [];
    if (declared.length > 0) return declared;
    // Fallback while the read is loading or unavailable: better a partial list than none.
    const ids = (waitlistQuery.data?.enrollments ?? [])
      .map(e => e.location_id)
      .filter((id): id is string => typeof id === 'string' && id.length > 0);
    return [...new Set(ids)].sort();
  }, [locationsQuery.data, waitlistQuery.data]);

  // Dynamic header subtitle for Today — the real date of the selected column.
  //
  // Analytics gets one too: its eyebrow was the static literal "Last 7 days", so selecting
  // 30 or 90 days left the chrome above the (live) figures disagreeing with them — the same
  // label-contradicts-the-data-underneath defect as the frozen `WEEK` literal.
  const todaySub = screen === 'today' ? dayLabel(week, selDay) : undefined;
  const headerSub = screen === 'analytics' ? ANALYTICS_RANGE_LABEL[analyticsRange] : todaySub;

  /**
   * Load failures, named.
   *
   * Without this a failed fetch is indistinguishable from a real empty result: the Today grid
   * shows a clean, confident, completely empty week and the operator concludes there are no
   * bookings today. §11's rule is that demo data never papers over an error — the error
   * surfaces on the screen instead.
   */
  const loadFailures = [
    { label: 'today’s bookings', query: scheduleQuery },
    { label: 'the queue', query: operationsQuery },
    { label: 'conversations', query: sessionsQuery },
    { label: 'the waitlist', query: waitlistQuery },
    { label: 'customers', query: customersQuery },
  ].filter(entry => entry.query.isError);

  function retryFailedLoads() {
    for (const entry of loadFailures) void entry.query.refetch();
  }

  function renderScreen() {
    const content = (() => {
      if (loading) return <LoadingSkeletons />;
      switch (screen) {
        case 'today':
          return (
            <>
              {/* The server reports a partial answer when its bounded scan hits a ceiling or the
                  page limit. Rendering the list unmarked would read as "this is the whole day". */}
              {scheduleQuery.data?.truncated && (
                <div className="mb-3">
                  <PartialBadge reason="too many bookings in this window to list them all — some are not shown" />
                </div>
              )}
            <TodayScreen
              device={device}
              week={week}
              onPrevWeek={() => goToWeek(-1)}
              onNextWeek={() => goToWeek(1)}
              onGoToToday={goToToday}
              selDay={selDay}
              setSelDay={setSelDay}
              openBooking={openBooking}
              goQueue={() => go('queue')}
              queueCount={queueCount}
              bookings={bookings}
              onDragReschedule={onRescheduleDone}
              resourceLabel={tenantCfg.resourceLabel}
              // One unnamed lane: the platform models no resource, so the grid names none.
              resources={UNASSIGNED_RESOURCES}
              // "No bookings" is a claim about the day; it may only be made once the schedule
              // has answered for THIS week.
              loaded={scheduleQuery.data !== undefined}
            />
            </>
          );
        case 'queue':
          return (
            <QueueScreen
              device={device} queue={queue} busy={busy}
              onFinish={finishQueue} onDismiss={dismissQueue}
              // "Nothing needs you right now" may only be said once the platform has answered.
              loaded={operationsQuery.data !== undefined}
              onRequestCancellation={requestOperationCancel}
              onResume={resumeOperationAction}
              onResolve={resolveOperationAction}
              onRespondAlternatives={respondAlternativesAction}
              serverBusyId={serverBusyId}
            />
          );
        case 'conversations': {
          // Fall back to demo threads only while the session list has never loaded — a
          // successful-but-empty result is a real answer ("no conversations yet") and must
          // not be replaced with fabricated rows.
          const showingDemoThreads = sessionsQuery.data === undefined;
          const sourceThreads = showingDemoThreads ? THREADS : liveThreads;
          const displayThreads = sourceThreads.map(t =>
            takenOver.has(t.id) ? { ...t, live: false } : t
          );
          // Transcript for the open thread is fetched on demand; it is a sensitive read
          // and carries actor + reason-code headers (spec 14 §4.2).
          //
          // A live session that has NOT loaded gets an explicit empty entry rather than
          // falling through to MESSAGES: the mock map has no key for a real session id, so
          // the pane rendered a clean empty conversation for a failed or forbidden read of
          // the most sensitive data in the product.
          const liveMessages = transcriptQuery.data
            ? { ...MESSAGES, [convOpen]: toMessages(transcriptQuery.data) }
            : openSessionIsLive
              ? { ...MESSAGES, [convOpen]: [] }
              : MESSAGES;
          // The read does not happen until the operator says why. Rendered only for a real
          // session — the demo threads are not transcripts and nothing is disclosed by them.
          const needsPurpose = openSessionIsLive && transcriptPurpose === null;
          return (
            <>
              {showingDemoThreads && (
                <div className="mb-3"><DemoBadge reason="conversations have not loaded yet" /></div>
              )}
              {needsPurpose && (
                <div
                  className="mb-3 rounded-[13px] p-[14px]"
                  style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}
                >
                  <div className="text-[13px] font-semibold text-ink">Why are you opening this?</div>
                  <div className="text-[12px] text-t2 mt-[3px] leading-[1.5]">
                    Reading a transcript discloses the guest&rsquo;s personal data, so the reason is
                    recorded against your name.
                  </div>
                  <div className="flex flex-wrap gap-[7px] mt-[11px]">
                    {SENSITIVE_READ_PURPOSES.map(p => (
                      <button
                        key={p.value}
                        type="button"
                        onClick={() => setTranscriptPurpose(p.value)}
                        className="px-[11px] py-[6px] rounded-[9px] text-[12px] font-medium text-ink bg-white transition-colors hover:border-[#2F6BEB]"
                        style={{ border: '1px solid #E4E4E1', fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                      >
                        {p.label}
                      </button>
                    ))}
                  </div>
                </div>
              )}
              <ConversationsScreen device={device} threads={displayThreads} messages={liveMessages}
                convOpen={convOpen} convView={convView} openConv={openConv}
                backConv={() => setConvView('list')} takeOver={takeOver}
                transcriptPending={openSessionIsLive && transcriptPurpose !== null && transcriptQuery.isPending}
                transcriptError={
                  // A `verification_required` 403 is resolvable, so it gets the gate rather than
                  // an error the operator can only stare at. Anything else is named, with a retry
                  // — never an empty thread that reads as "nothing was said".
                  transcriptNeedsVerification ? (
                    <VerificationGate
                      action="read_sensitive"
                      customerId={sourceThreads.find(t => t.id === convOpen)?.name}
                      purpose="read this transcript"
                      onGranted={setTranscriptGrant}
                    />
                  ) : openSessionIsLive && transcriptQuery.isError ? (
                    <ErrorState
                      inline
                      error={transcriptQuery.error}
                      title="Couldn’t load this transcript."
                      onRetry={() => void transcriptQuery.refetch()}
                    />
                  ) : undefined
                } />
            </>
          );
        }
        case 'waitlist':
          return (
            <WaitlistScreen
              device={device}
              autoBook={autoBook}
              toggleAutoBook={() => setAutoBook(v => !v)}
              waitlist={waitlist}
              onToggleAuto={toggleWaitlistAuto}
              onRemove={removeFromWaitlist}
              onBookNow={bookFromWaitlist}
              onAcceptOffer={(offerId, name) => answerOffer('accept', offerId, name)}
              onDeclineOffer={(offerId, name) => answerOffer('decline', offerId, name)}
              offerBusyId={offerBusyId}
              loaded={waitlistQuery.data !== undefined}
            />
          );
        case 'customers':
          return (
            <>
              {customersQuery.data?.truncated && (
                <div className="mb-3">
                  <PartialBadge reason="too many customers matched to list them all — refine the search" />
                </div>
              )}
              <CustomersScreen
              device={device}
              onNewBooking={openCreate}
              onMessage={handleCustomerMessage}
              focusCustomerId={customerFocus}
              onClearFocus={() => setCustomerFocus(null)}
              customers={customersQuery.data ? liveCustomers : undefined}
              />
            </>
          );
        case 'deposits':
          return <DepositsScreen />;
        case 'campaigns':
          return <CampaignsScreen />;
        case 'analytics':
          return <AnalyticsScreen device={device} range={analyticsRange} onRangeChange={setAnalyticsRange} />;
        case 'settings':
          return (
            <SettingsScreen
              services={services}
              tenantCfg={tenantCfg}
              onUpdateServices={setServices}
              onUpdateTenant={setTenantCfg}
              device={device}
            />
          );
        case 'more':
          return (
            <MoreScreen
              go={go}
              autoText={autoBook ? 'on' : 'off'}
              onSignOut={handleSignOut}
              // Undefined until the waitlist has loaded, so the row says "guests waiting" rather
              // than claiming a count it does not have (a successful-but-empty answer is 0).
              waitlistCount={waitlistQuery.data ? waitlist.length : undefined}
            />
          );
        default:
          return null;
      }
    })();

    return (
      <div key={screen} style={{ animation: 'fadeIn 120ms ease-out' }}>
        {loadFailures.length > 0 && (
          <div style={{ marginBottom: 12 }}>
            <ErrorState
              inline
              error={loadFailures[0].query.error}
              title={`Couldn’t load ${listLabels(loadFailures.map(f => f.label))}. What you see below may be incomplete.`}
              onRetry={retryFailedLoads}
            />
          </div>
        )}
        {content}
      </div>
    );
  }

  return (
    <div
      className="h-screen w-screen flex overflow-hidden"
      style={{
        position: 'relative', zIndex: 1, background: '#fff',
        fontFamily: "'Inter Tight', system-ui, sans-serif", WebkitFontSmoothing: 'antialiased',
      }}
    >
      {/* Sidebar — desktop only */}
      {isDesktop && (
        <Sidebar
          screen={screen}
          go={go}
          queueCount={queueCount}
          campCount={campCount}
          hasLive={hasLive}
          isCollapsed={sidebarCollapsed}
          onToggle={() => setSidebarCollapsed(v => !v)}
          onSignOut={handleSignOut}
        />
      )}

      {/* Main area */}
      <div className="flex-1 min-w-0 flex flex-col bg-white overflow-hidden">
        {isPhone && <StatusBar />}

        <Header
          device={device}
          screen={screen}
          primaryLabel={primaryLabel}
          onPrimary={onPrimary}
          scrolled={scrolled}
          sub={headerSub}
          onOpenSearch={() => setCmdOpen(true)}
        />

        {/* Scrollable content — dot pattern shows in padding gaps, bg-white panels cover it */}
        <div
          className="cd-scroll flex-1 min-h-0 overflow-y-auto"
          style={{
            padding: isPhone ? '14px 16px 16px' : '22px 28px 28px',
            backgroundImage: 'radial-gradient(#E0E0DC 1.1px, transparent 1.1px)',
            backgroundSize: '24px 24px',
            backgroundColor: '#F9F9F7',
          }}
          onScroll={(e) => setScrolled((e.target as HTMLElement).scrollTop > 4)}
        >
          <div style={{ width: '100%', maxWidth: isDesktop ? 1440 : undefined, margin: '0 auto' }}>
            {renderScreen()}
          </div>
        </div>

        {isPhone && (
          <BottomNav screen={screen} go={go} queueCount={queueCount} hasLive={hasLive} />
        )}
      </div>

      {/* Drawers — fixed overlay on the full viewport */}
      <BookingDetailDrawer
        open={drawer.kind === 'detail'}
        device={device}
        week={week}
        booking={activeBooking}
        onClose={closeDrawer}
        onStartReschedule={startReschedule}
        onCancelBooking={cancelBooking}
        cancelBusy={cancelBusy}
        onViewTranscript={onViewTranscript}
        verificationGate={
          cancelAwaitingGrant && activeBooking?.id === cancelAwaitingGrant ? (
            <VerificationGate
              action="cancel"
              targetBookingId={cancelAwaitingGrant}
              purpose="cancel this booking"
              onGranted={(grantId) => submitCancelWithGrant(cancelAwaitingGrant, grantId)}
              onCancel={() => setCancelAwaitingGrant(null)}
            />
          ) : undefined
        }
      />
      <CreateBookingDrawer
        open={drawer.kind === 'create'}
        device={device}
        week={week}
        onClose={closeDrawer}
        onCreated={onCreated}
        services={bookableServices}
        servicesFailed={catalogQuery.isError}
        onRetryServices={() => void catalogQuery.refetch()}
        customers={customersQuery.data ? liveCustomers : undefined}
        availability={availableSlots}
        prefill={createPrefill}
      />
      <RescheduleDrawer
        open={drawer.kind === 'reschedule'}
        device={device}
        week={week}
        booking={activeBooking}
        onClose={closeDrawer}
        onDone={onRescheduleDone}
      />
      <EnrollWaitlistDrawer
        open={drawer.kind === 'enroll'}
        device={device}
        onClose={closeDrawer}
        onEnrolled={(guestRef) => {
          closeDrawer();
          // The toast reports what the server did (201); the refetched list shows the row.
          toast({ message: `${guestRef} added to the waitlist`, duration: 4000 });
        }}
        services={bookableServices}
        servicesFailed={catalogQuery.isError}
        onRetryServices={() => void catalogQuery.refetch()}
        knownLocations={knownLocations}
      />

      {/* Toast notifications */}
      <ToastContainer toasts={toasts} onDismiss={dismiss} device={device} />

      {/* Command palette */}
      <CommandPalette
        open={cmdOpen}
        onClose={() => setCmdOpen(false)}
        onNavigate={go}
        onCreateBooking={openCreate}
        onOpenCustomer={handleOpenCustomer}
        // Undefined until the list has loaded — the palette then labels its demo rows rather
        // than offering fabricated people as if they were the venue's customers.
        customers={customersQuery.data ? liveCustomers : undefined}
      />
    </div>
  );
}
