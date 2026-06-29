import { useState, useEffect, useRef } from "react";
import {
  Booking, BOOKINGS, QUEUE, THREADS, MESSAGES, WAITLIST,
  QueueItem, BookingStatus, WaitlistItem,
  Service, SERVICE_LIST, TenantConfig, TENANT, WEEK,
} from "../data/console";

import { Skeleton } from "../components/ui/Skeleton";

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

  // Sign-out
  function handleSignOut() {
    window.location.href = '/';
  }

  // Waitlist actions
  function toggleWaitlistAuto(id: string) {
    setWaitlist(wl => wl.map(w => w.id === id ? { ...w, auto: !w.auto } : w));
  }
  function removeFromWaitlist(id: string) {
    const item = waitlist.find(w => w.id === id);
    if (!item) return;
    setWaitlist(wl => wl.filter(w => w.id !== id));
    toast({
      message: `${item.name} removed from waitlist`,
      action: { label: 'Undo', onClick: () => setWaitlist(wl => [item, ...wl]) },
      duration: 5000,
    });
  }

  // Customer focus (from CommandPalette)
  function handleOpenCustomer(id: string) {
    setCustomerFocus(id);
    go('customers');
  }

  // Calendar
  const [selDay, setSelDay] = useState(3);

  // Conversations
  const [convOpen, setConvOpen] = useState('c3');
  const [convView, setConvView] = useState<'list' | 'thread'>('list');
  const [takenOver, setTakenOver] = useState<Set<string>>(new Set());

  // Waitlist
  const [autoBook, setAutoBook] = useState(true);
  const [waitlist, setWaitlist] = useState<WaitlistItem[]>(() => [...WAITLIST]);

  // Customers — focus a specific customer from CommandPalette
  const [customerFocus, setCustomerFocus] = useState<string | null>(null);

  // Business config
  const [services, setServices] = useState<Service[]>(() => [...SERVICE_LIST]);
  const [tenantCfg, setTenantCfg] = useState<TenantConfig>(() => ({ ...TENANT }));

  // Data
  const [bookings, setBookings] = useState<Booking[]>(() => [...BOOKINGS]);
  const [queue, setQueue] = useState<QueueItem[]>(() => [...QUEUE]);
  const [busy, setBusy] = useState<string | null>(null);

  // Drawers
  const [drawer, setDrawer] = useState<{
    kind: 'detail' | 'create' | 'reschedule' | null;
    bookingId: string | null;
  }>({ kind: null, bookingId: null });
  const [cancelBusy, setCancelBusy] = useState(false);
  const btRef = useRef<ReturnType<typeof setTimeout> | null>(null);

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
      if (btRef.current) clearTimeout(btRef.current);
    };
  }, []);

  const queueCount = queue.length;
  const campCount = 1;
  const hasLive = THREADS.some(t => t.live);

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
  const closeDrawer = () => setDrawer(d => ({ ...d, kind: null }));
  const startReschedule = (id: string) => setDrawer({ kind: 'reschedule', bookingId: id });

  function cancelBooking(id: string) {
    if (cancelBusy) return;
    const prevStatus = bookings.find(b => b.id === id)?.status ?? 'confirmed';
    setCancelBusy(true);
    if (btRef.current) clearTimeout(btRef.current);
    btRef.current = setTimeout(() => {
      setBookings(bs => bs.map(b => b.id === id ? { ...b, status: 'cancelled' as const } : b));
      setCancelBusy(false);
      closeDrawer();
      toast({
        message: 'Booking cancelled',
        action: {
          label: 'Undo',
          onClick: () => setBookings(bs => bs.map(b => b.id === id ? { ...b, status: prevStatus as BookingStatus } : b)),
        },
        duration: 5000,
      });
    }, 850);
  }

  function onCreated(nb: Omit<Booking, 'id'>) {
    setBookings(bs => [...bs, { ...nb, id: 'new' + Date.now() }]);
  }

  function onRescheduleDone(id: string, patch: { day: number; start: number; staff: string }) {
    setBookings(bs => bs.map(b => b.id === id ? { ...b, ...patch } : b));
  }

  function finishQueue(id: string) {
    if (busy) return;
    setBusy(id);
    if (btRef.current) clearTimeout(btRef.current);
    btRef.current = setTimeout(() => {
      setQueue(q => q.filter(x => x.id !== id));
      setBusy(null);
      toast({ message: 'Queue item resolved ✓', duration: 3000 });
    }, 900);
  }

  const dismissQueue = (id: string) => {
    if (busy) return;
    const item = queue.find(x => x.id === id);
    if (!item) return;
    setQueue(q => q.filter(x => x.id !== id));
    toast({
      message: 'Dismissed',
      action: { label: 'Undo', onClick: () => setQueue(q => [item, ...q]) },
      duration: 5000,
    });
  };

  const openConv = (id: string) => { setConvOpen(id); setConvView('thread'); };

  function takeOver() {
    setTakenOver(prev => new Set([...prev, convOpen]));
    toast({ message: "You're now handling this conversation", duration: 4000 });
  }

  function handleCustomerMessage(customerName: string) {
    const thread = THREADS.find(t => t.name === customerName);
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

  // Dynamic header subtitle for Today (tracks selected day)
  const todaySub = screen === 'today'
    ? (() => {
        const dow = WEEK.dows[selDay];
        const dom = WEEK.doms[selDay];
        return `${dow.charAt(0) + dow.slice(1).toLowerCase()}, ${dom} ${WEEK.month}`;
      })()
    : undefined;

  function renderScreen() {
    const content = (() => {
      if (loading) return <LoadingSkeletons />;
      switch (screen) {
        case 'today':
          return (
            <TodayScreen
              device={device}
              selDay={selDay}
              setSelDay={setSelDay}
              openBooking={openBooking}
              goQueue={() => go('queue')}
              queueCount={queueCount}
              bookings={bookings}
              onDragReschedule={onRescheduleDone}
              resourceLabel={tenantCfg.resourceLabel}
            />
          );
        case 'queue':
          return <QueueScreen device={device} queue={queue} busy={busy} onFinish={finishQueue} onDismiss={dismissQueue} />;
        case 'conversations': {
          const displayThreads = THREADS.map(t =>
            takenOver.has(t.id) ? { ...t, live: false } : t
          );
          return (
            <ConversationsScreen device={device} threads={displayThreads} messages={MESSAGES}
              convOpen={convOpen} convView={convView} openConv={openConv}
              backConv={() => setConvView('list')} takeOver={takeOver} />
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
            <CustomersScreen
              device={device}
              onNewBooking={openCreate}
              onMessage={handleCustomerMessage}
              focusCustomerId={customerFocus}
              onClearFocus={() => setCustomerFocus(null)}
            />
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
        booking={activeBooking}
        onClose={closeDrawer}
        onStartReschedule={startReschedule}
        onCancelBooking={cancelBooking}
        cancelBusy={cancelBusy}
        onViewTranscript={onViewTranscript}
      />
      <CreateBookingDrawer
        open={drawer.kind === 'create'}
        device={device}
        onClose={closeDrawer}
        onCreated={onCreated}
        services={services}
      />
      <RescheduleDrawer
        open={drawer.kind === 'reschedule'}
        device={device}
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
      />
    </div>
  );
}
