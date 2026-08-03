import { useState, useEffect, useMemo, useRef } from "react";
import {
  Booking, THREADS, MESSAGES,
  QueueItem, WaitlistItem,
  Service, SERVICE_LIST, TenantConfig, TENANT,
} from "../data/console";

import {
  useBookingSchedule, useCustomers, useOperations, useSessions, useTranscript,
  useWaitlistEnrollments,
} from "../features/hooks";
import { toBookings, weekWindow } from "../features/bookings/adapters";
import { buildWeek, dayLabel, shiftWeekWindow } from "../features/bookings/week";
import { buildSubmission, toIsoInstant } from "../features/bookings/buildSubmission";
import { getOrCreateAttempt, resetAttempt } from "../features/bookings/identity";
import { toQueueItems } from "../features/operations/adapters";
import { toMessages, toThreads } from "../features/conversations/adapters";
import { toCustomers } from "../features/customers/adapters";
import { toWaitlistItems } from "../features/waitlist/adapters";
import { useQueryClient } from "@tanstack/react-query";
import { getBookingDetails } from "../features/bookings/api";
import { RESCHEDULE_FULFILMENT_SUPPORTED, submitBooking } from "../features/bookings/submit";
import {
  isRetryableWithNewAttempt, pollToTerminal, useCancelBooking, useRemoveFromWaitlist,
  writeFailureMessage,
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
import { AnalyticsScreen } from "../components/platform/screens/AnalyticsScreen";
import { SettingsScreen } from "../components/platform/screens/SettingsScreen";
import { MoreScreen } from "../components/platform/screens/MoreScreen";

import { BookingDetailDrawer } from "../components/platform/drawers/BookingDetailDrawer";
import { CreateBookingDrawer } from "../components/platform/drawers/CreateBookingDrawer";
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
  // Every list below comes from the API and falls back to demo data only when the API
  // genuinely has nothing to show (a fresh tenant) — never to paper over an error, which
  // surfaces on the screen instead.
  const queryClient = useQueryClient();
  // The displayed week is state, not a constant: the grid can move week to week, and every
  // date on screen is derived from THIS window rather than from a hard-coded literal. The
  // query key already includes from/to, so each week caches independently.
  const [scheduleWindow, setScheduleWindow] = useState(() => weekWindow());
  const week = useMemo(() => buildWeek(scheduleWindow.from), [scheduleWindow.from]);
  const scheduleQuery = useBookingSchedule(scheduleWindow);
  const operationsQuery = useOperations({ needsAttention: true, limit: 100 });
  const sessionsQuery = useSessions(50);
  const waitlistQuery = useWaitlistEnrollments(true, 100);
  const customersQuery = useCustomers("", 50);

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
  const [transcriptPurpose, setTranscriptPurpose] = useState<SensitiveReadPurpose | null>(null);
  const transcriptQuery = useTranscript(
    openSessionIsLive ? convOpen : null,
    transcriptPurpose
  );

  // A purpose is stated for one reading of one conversation; it must not carry over to the next.
  useEffect(() => { setTranscriptPurpose(null); }, [convOpen]);

  // Local overlays: optimistic removals applied on top of server data until the next
  // refetch. Server truth always wins on reload (Inv-16).
  const [dismissedQueueIds, setDismissedQueueIds] = useState<Set<string>>(new Set());

  // Data.
  //
  // Seeded EMPTY, not from the mock constants: seeding from mocks flashed fabricated
  // bookings and queue items — with no Demo badge — for as long as the first fetch took,
  // which an operator would read as their own data. An empty list plus the query's own
  // pending state is honest.
  const [bookings, setBookings] = useState<Booking[]>([]);
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
    kind: 'detail' | 'create' | 'reschedule' | null;
    bookingId: string | null;
  }>({ kind: null, bookingId: null });

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
  const campCount = 1;
  // A thread only counts as "live" until it's taken over by a human.
  const hasLive = (sessionsQuery.data === undefined ? THREADS : liveThreads)
    .some(t => t.live && !takenOver.has(t.id));

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
  const openCreate = () => setDrawer({ kind: 'create', bookingId: null });
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
  function onRescheduleDone(id: string, patch: { day: number; start: number; staff: string; dur?: number }) {
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

    if (!RESCHEDULE_FULFILMENT_SUPPORTED) {
      // Do not move the row either: the booking has not changed, and showing it in the new
      // slot would be exactly the fabricated success this guard exists to prevent.
      toast({
        message: 'Rescheduling is not available yet — the platform cannot move an existing booking.',
        duration: 6000,
      });
      return;
    }

    // Two drags of the same booking must not overlap: the second would reuse the first's
    // still-live attempt identity against a different time (Inv-3).
    if (rescheduleInFlight.current.has(id)) {
      toast({ message: 'That booking is already being changed — wait for it to settle.', duration: 4000 });
      return;
    }

    setBookings(bs => bs.map(b => b.id === id ? { ...b, ...patch } : b));
    void submitReschedule(id, patch);
  }

  async function submitReschedule(
    id: string,
    patch: { day: number; start: number; staff: string; dur?: number }
  ) {
    // Anchored to the DISPLAYED week, not the current one: dragging a booking while viewing
    // next week must submit next week's date.
    const startsAt = toIsoInstant(week.start, patch.day, patch.start);
    // Scoped by the payload it will be bound to, not just the booking: an idempotency key
    // must never carry two different canonical payloads (Inv-3).
    const attemptKey = `modify:${id}|${startsAt}|${patch.dur ?? ''}`;
    rescheduleInFlight.current.add(id);
    const controller = new AbortController();
    pollAbort.current = controller;
    try {
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

      const identity = getOrCreateAttempt(attemptKey);
      // The booking's real party size — sending 0 would durably record a reschedule for
      // nobody, and the canonical payload is the persisted intent.
      const partySize = details.booking?.party_size ?? 1;

      // Reschedule rides the acceptance front door so the new time is actually carried
      // (D7 / Inv-1). `expected_booking_version` travels on the canonical payload as
      // `target_booking_version`, keeping optimistic concurrency intact.
      const request = await buildSubmission(
        {
          operation: 'reschedule',
          serviceId: bookings.find(b => b.id === id)?.service ?? '',
          startsAt,
          partySize,
          durationMinutes: patch.dur,
          targetBookingId: id,
          targetBookingVersion: version,
        },
        {
          identity,
          sessionId: `console:${identity.intentId}`,
          identityRef: getActor(),
          readBackVersion: `reschedule|${id}|${startsAt}`,
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
      // Drop the optimistic move either way: refetch so the grid shows the server's placement.
      invalidateBookingViews();
    } finally {
      rescheduleInFlight.current.delete(id);
    }
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
    setQueue(q => q.filter(x => x.id !== id));
    setDismissedQueueIds(prev => new Set([...prev, id]));
    toast({
      message: 'Dismissed',
      action: {
        label: 'Undo',
        onClick: () => {
          setDismissedQueueIds(prev => {
            const next = new Set(prev);
            next.delete(id);
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
  const primaryLabel = screen === 'today' ? 'New booking' : undefined;
  const onPrimary = screen === 'today' ? openCreate : undefined;

  // Dynamic header subtitle for Today — the real date of the selected column.
  const todaySub = screen === 'today' ? dayLabel(week, selDay) : undefined;

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
            />
            </>
          );
        case 'queue':
          return <QueueScreen device={device} queue={queue} busy={busy} onFinish={finishQueue} onDismiss={dismissQueue} />;
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
                  // Named, with a retry — never an empty thread that reads as "nothing was said".
                  openSessionIsLive && transcriptQuery.isError ? (
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
              onBookNow={() => openCreate()}
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
          return <AnalyticsScreen device={device} />;
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
          return <MoreScreen go={go} autoText={autoBook ? 'on' : 'off'} onSignOut={handleSignOut} />;
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
          sub={todaySub}
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
        services={services}
      />
      <RescheduleDrawer
        open={drawer.kind === 'reschedule'}
        device={device}
        week={week}
        booking={activeBooking}
        onClose={closeDrawer}
        onDone={onRescheduleDone}
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
