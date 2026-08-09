import { useState, useRef, useEffect, useCallback } from "react";
import { Booking, statusMeta, fmtT, fmtRange } from "../../../data/console";
import type { GridResource } from "../../../features/bookings/resources";
import { dayLabel, type ConsoleWeek } from "../../../features/bookings/week";
import { useTickingNow } from "../../../hooks/useTickingNow";
import { Icon } from "../../ui/Icon";

// ── Constants ──────────────────────────────────────────────────────────────
const COL_W    = 68;           // px per 15-min slot (fixed layout unit)
const ROW_H    = 40;           // px per resource row
const LABEL_W  = 148;          // px for resource labels column
// The visible time window. Derived from the bookings on screen (see `dayWindow`), NOT from
// operating hours: `dayBounds()` read the hand-written `SETTINGS.hours`, which SettingsScreen
// itself badges "no operating-hours endpoint yet". Two things followed from that. A live Sunday
// was labelled "Closed" on the strength of a constant, and — worse — the grid framed only
// 9am-6pm, so a real booking at 08:00 or 20:30 was positioned outside the rendered columns and
// simply could not be seen.
const DEFAULT_WINDOW = { start: 8 * 60, end: 20 * 60 };
const WINDOW_PAD = 60;
/**
 * How far past midnight the window may extend.
 *
 * NOT 24:00. `toDayAndStart` gives a start in 0..1439, but `dur` comes straight from the API with
 * no upper bound, so a 23:20 booking of 90 minutes ends at 1490 — past midnight. Clamping the
 * window at 1440 left its bar drawn beyond the last column, gridline and row stripe, in blank
 * background with no time reference: the very "positioned outside the rendered columns" defect
 * this window exists to prevent. The cap is generous enough to frame any real overnight booking
 * and still bounds a corrupt `duration_minutes` to a grid that can be scrolled.
 */
const MAX_WINDOW_END = 36 * 60;

/** Minutes since local midnight, for a given instant. */
function minutesOfDay(at: number): number {
  const when = new Date(at);
  return when.getHours() * 60 + when.getMinutes();
}

/**
 * The window the grid frames for a day: wide enough to contain every booking it must show.
 *
 * Every booking is guaranteed to be inside it, which is the property that matters — a schedule
 * that silently omits a booking is the double-booking defect this console has already had once.
 */
export function dayWindow(bookings: Booking[], selDay: number): { start: number; end: number } {
  const onDay = bookings.filter(b => b.day === selDay && isActive(b.status));
  if (onDay.length === 0) return { ...DEFAULT_WINDOW };

  const earliest = Math.min(...onDay.map(b => b.start));
  const latest = Math.max(...onDay.map(b => b.start + b.dur));
  return {
    start: Math.max(0, Math.min(DEFAULT_WINDOW.start, snap15(earliest - WINDOW_PAD))),
    // `min` against the cap is applied to the PADDED requirement, then `max` against the default
    // — in that order, so the cap can shrink the padding but can never pull `end` below `latest`
    // for any booking inside the cap.
    end: Math.max(DEFAULT_WINDOW.end, Math.min(MAX_WINDOW_END, snap15(latest + WINDOW_PAD))),
  };
}

function snap15(v: number) { return Math.round(v / 15) * 15; }

function segOf(m: number): 'morning' | 'lunch' | 'evening' {
  if (m < 720)  return 'morning';
  if (m < 1020) return 'lunch';
  return 'evening';
}

function inTimeFilter(start: number, f: string) {
  return f === 'all' || segOf(start) === f;
}

function isActive(s: string) {
  return s !== 'cancelled' && s !== 'failed';
}

function hasConflict(
  bs: Booking[], excludeId: string,
  staff: string, start: number, dur: number, day: number,
): boolean {
  return bs.some(
    b => b.id !== excludeId && isActive(b.status) && b.staff === staff && b.day === day &&
         start < b.start + b.dur && b.start < start + dur,
  );
}

/** Which lane a pointer y-offset falls in, clamped to the lanes that exist. */
function yToResourceIdx(y: number, laneCount: number): number {
  return Math.max(0, Math.min(Math.floor(y / ROW_H), Math.max(0, laneCount - 1)));
}

// ── Drag state ────────────────────────────────────────────────────────────
interface DragState {
  id: string;
  mode: 'move' | 'resize-l' | 'resize-r';
  mx0: number; my0: number;
  origStart: number; origDur: number; origStaff: string;
  curStart: number; curDur: number; curStaff: string;
  conflict: boolean;
  moved: boolean;
}

// ── SegRow ─────────────────────────────────────────────────────────────────
function SegRow({
  label, options, value, onChange,
}: {
  label: string;
  options: { value: string; label: string }[];
  value: string;
  onChange: (v: string) => void;
}) {
  return (
    <div className="flex items-center gap-2">
      <span
        className="font-mono text-[10.5px] font-medium tracking-[0.12em] uppercase shrink-0"
        style={{ color: '#9A9A9A' }}
      >
        {label}
      </span>
      <div
        className="flex gap-[2px] p-[2px] rounded-[8px]"
        style={{ background: '#F2F2F0' }}
      >
        {options.map(o => {
          const active = value === o.value;
          return (
            <button
              key={o.value}
              onClick={() => onChange(o.value)}
              className={`rounded-[6px] border-none cursor-pointer text-[12.5px] transition-all duration-150 whitespace-nowrap ${
                active
                  ? 'bg-white shadow-[0_1px_2px_rgba(10,10,10,0.10)] text-[#0A0A0A] font-semibold'
                  : 'bg-transparent text-[#6B6B6B] font-medium hover:bg-black/5'
              }`}
              style={{ padding: '5px 12px', fontFamily: 'inherit' }}
            >
              {o.label}
            </button>
          );
        })}
      </div>
    </div>
  );
}

// ── Stats card ────────────────────────────────────────────────────────────
function StatsCard(
  { bookings, selDay, isToday, now }:
  { bookings: Booking[]; selDay: number; isToday: boolean; now: number }
) {
  // `now` is TICKING, not read once — without it the count freezes at whatever moment last
  // triggered a render and quietly stops being true as the day goes on. It arrives as a prop
  // rather than from a second `useTickingNow()`: see the one clock in TodayScreen.
  const day = bookings.filter(b => b.day === selDay && isActive(b.status));
  const total = day.length;
  // "Remaining" is only meaningful for today, and only against the REAL clock. It used to count
  // against `NOW_MINS`, a hard-coded 1:15 PM, on every day of the week.
  const remaining = isToday ? day.filter(b => b.start >= minutesOfDay(now)).length : null;
  const segCounts = {
    morning: day.filter(b => segOf(b.start) === 'morning').length,
    lunch:   day.filter(b => segOf(b.start) === 'lunch').length,
    evening: day.filter(b => segOf(b.start) === 'evening').length,
  };
  const maxSeg = Math.max(1, segCounts.morning, segCounts.lunch, segCounts.evening);

  return (
    <div
      className="rounded-[9px] p-[10px_12px] bg-white mb-[12px]"
      style={{ border: '1px solid #ECEEF1' }}
    >
      <div className="flex gap-2 mb-[10px]">
        {/*
          No "clients" tile. It rendered `String(total)` — the booking count under a different
          name — so it asserted a distinct fact (how many separate guests) that was never
          computed, sitting next to two correct figures which made it read as trustworthy.
          The projection's `customer_ref` cannot be deduplicated reliably either: an unfinalised
          operation has none, and every one of those falls back to the same "Guest" label.
        */}
        {([
          ['bookings',  String(total)],
          ...(remaining === null ? [] : [['remaining', String(remaining)] as const]),
        ] as const).map(([lbl, val], i) => (
          <div
            key={lbl}
            className="flex-1"
            style={{ borderLeft: i > 0 ? '1px solid #F0F1F3' : 'none', paddingLeft: i > 0 ? 8 : 0 }}
          >
            <div className="font-mono text-[15px] font-bold tracking-[-0.02em]" style={{ color: '#0A0A0A' }}>
              {val}
            </div>
            <div className="text-[10px] mt-[1px]" style={{ color: '#9A9A9A' }}>{lbl}</div>
          </div>
        ))}
      </div>
      <div className="flex flex-col gap-[5px]" style={{ borderTop: '1px solid #F3F4F6', paddingTop: 8 }}>
        {((['morning', 'lunch', 'evening'] as const)).map(seg => {
          const count = segCounts[seg];
          const barBg = seg === 'evening' ? '#0A0A0A' : seg === 'lunch' ? '#9A9A9A' : '#D6D6D2';
          return (
            <div key={seg} className="flex items-center gap-2">
              <span className="text-[11px] capitalize w-[48px]" style={{ color: '#55554F' }}>{seg}</span>
              <div
                className="flex-1 h-[3px] rounded-[2px] overflow-hidden"
                style={{ background: '#F1F2F4' }}
              >
                <div style={{
                  height: '100%',
                  width: `${Math.round(count / maxSeg * 100)}%`,
                  background: barBg,
                  borderRadius: 2,
                }} />
              </div>
              <span className="font-mono text-[10.5px] w-[16px] text-right" style={{ color: '#9A9A9A' }}>
                {count}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ── Side booking list ─────────────────────────────────────────────────────
function SideList({
  bookings, selDay, timeFilter, openBooking, resources, loaded,
}: {
  bookings: Booking[];
  selDay: number;
  timeFilter: string;
  openBooking: (id: string) => void;
  resources: readonly GridResource[];
  loaded: boolean;
}) {
  const items = bookings
    .filter(b => b.day === selDay && isActive(b.status) && inTimeFilter(b.start, timeFilter))
    .sort((a, b) => a.start - b.start);

  const groups: { t: number; items: Booking[] }[] = [];
  const map = new Map<number, Booking[]>();
  items.forEach(b => {
    if (!map.has(b.start)) map.set(b.start, []);
    map.get(b.start)!.push(b);
  });
  map.forEach((list, t) => groups.push({ t, items: list }));

  if (groups.length === 0) {
    return (
      <div className="py-6 text-center text-[12px]" style={{ color: '#9A9A9A' }}>
        {loaded ? 'No bookings' : 'Not loaded yet'}
      </div>
    );
  }

  return (
    <div>
      {groups.map(g => (
        <div key={g.t} className="mb-[9px]">
          <div className="text-[11.5px] font-semibold mb-[4px] mx-[2px]" style={{ color: '#9A9A9A' }}>
            {fmtT(g.t)}
          </div>
          {g.items.map(b => {
            const m = statusMeta(b.status);
            const stf = resources.find(s => s.id === b.staff);
            return (
              <button
                key={b.id}
                onClick={() => openBooking(b.id)}
                className="flex items-center gap-2 w-full px-[7px] py-[4px] rounded-[6px] border-none cursor-pointer text-left hover:bg-[#F2F3F5] transition-colors"
                style={{ fontFamily: 'inherit', background: 'transparent' }}
              >
                <span
                  className="w-[9px] h-[9px] rounded-[3px] shrink-0"
                  style={{ background: m.accent }}
                />
                <span
                  className="flex-1 min-w-0 text-[12.5px] font-medium truncate"
                  style={{ color: '#0A0A0A' }}
                >
                  {b.name}
                </span>
                <span className="text-[10.5px] shrink-0 truncate max-w-[80px]" style={{ color: '#9A9A9A' }}>
                  {b.serviceName.split(' ')[0]}{stf?.name ? ` · ${stf.name.split(' ')[0]}` : ''}
                </span>
              </button>
            );
          })}
        </div>
      ))}
    </div>
  );
}

// ── Diagram view ──────────────────────────────────────────────────────────
function DiagramView({
  bookings, selDay, timeFilter, openBooking, onDragReschedule, onDragRefused, resourceLabel,
  resources, isToday, now,
}: {
  bookings: Booking[];
  selDay: number;
  timeFilter: string;
  openBooking: (id: string) => void;
  onDragReschedule: (id: string, patch: { day: number; start: number; staff: string; dur: number }) => void;
  onDragRefused?: () => void;
  resourceLabel: string;
  resources: readonly GridResource[];
  /** True only when the selected column is the real current day. Gates the now-line. */
  isToday: boolean;
  /** The screen's single ticking clock — see TodayScreen. */
  now: number;
}) {
  const { start: dayStart, end: dayEnd } = dayWindow(bookings, selDay);
  const cols = (dayEnd - dayStart) / 15;

  const gridRef    = useRef<HTMLDivElement>(null);
  const headRef    = useRef<HTMLDivElement>(null);
  const [drag, setDrag]   = useState<DragState | null>(null);
  const dragRef = useRef<DragState | null>(null);

  const tickingNow = now;
  const gridW = cols * COL_W;
  const gridH = resources.length * ROW_H;

  const staffY: Record<string, number> = {};
  resources.forEach((s, i) => { staffY[s.id] = i * ROW_H; });

  const visible = bookings.filter(
    b => b.day === selDay && isActive(b.status) && inTimeFilter(b.start, timeFilter),
  );

  // Sync time header on grid scroll
  const onGridScroll = (e: React.UIEvent<HTMLDivElement>) => {
    if (headRef.current) {
      headRef.current.style.transform = `translateX(${-e.currentTarget.scrollLeft}px)`;
    }
  };

  // Start drag
  const startDrag = useCallback(
    (id: string, mode: DragState['mode'], e: React.MouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      const b = bookings.find(x => x.id === id);
      if (!b) return;
      // An unfinalised operation has no booking to move — `id` is an operation id. Dragging it
      // would snap the row to a new time and silently snap back a round trip later.
      if (b.isBooking === false) return;
      const d: DragState = {
        id, mode,
        mx0: e.clientX, my0: e.clientY,
        origStart: b.start, origDur: b.dur, origStaff: b.staff,
        curStart: b.start, curDur: b.dur, curStaff: b.staff,
        conflict: false, moved: false,
      };
      dragRef.current = d;
      setDrag(d);
    },
    [bookings],
  );

  // Drag mousemove / mouseup
  useEffect(() => {
    if (!drag) return;

    const onMove = (e: MouseEvent) => {
      const d = dragRef.current;
      if (!d) return;
      const dx = e.clientX - d.mx0;
      const dy = e.clientY - d.my0;
      if (Math.abs(dx) > 3 || Math.abs(dy) > 3) d.moved = true;

      let ns = d.curStart, nd = d.curDur, nstf = d.curStaff;

      if (d.mode === 'move') {
        ns   = snap15(d.origStart + (dx / COL_W) * 15);
        ns   = Math.max(dayStart, Math.min(ns, dayEnd - d.origDur));
        nd   = d.origDur;
        if (gridRef.current) {
          const r = gridRef.current.getBoundingClientRect();
          nstf = resources[yToResourceIdx(e.clientY - r.top, resources.length)]?.id ?? d.origStaff;
        }
      } else if (d.mode === 'resize-r') {
        nd   = snap15(d.origDur + (dx / COL_W) * 15);
        nd   = Math.max(15, Math.min(nd, dayEnd - d.origStart));
        ns   = d.origStart;
        nstf = d.origStaff;
      } else {
        const end = d.origStart + d.origDur;
        ns   = snap15(d.origStart + (dx / COL_W) * 15);
        ns   = Math.max(dayStart, Math.min(ns, end - 15));
        nd   = end - ns;
        nstf = d.origStaff;
      }

      const conflict = hasConflict(bookings, d.id, nstf, ns, nd, selDay);
      const updated = { ...d, curStart: ns, curDur: nd, curStaff: nstf, conflict };
      dragRef.current = updated;
      setDrag({ ...updated });
    };

    const onUp = () => {
      const d = dragRef.current;
      dragRef.current = null;
      setDrag(null);
      if (!d) return;
      if (!d.moved) {
        openBooking(d.id);
      } else if (!d.conflict) {
        onDragReschedule(d.id, { day: selDay, start: d.curStart, staff: d.curStaff, dur: d.curDur });
      } else {
        // A refusal has to SAY it refused.
        //
        // This branch did nothing at all: no request, no message, and the bar snapped back to where
        // it started as the drag state cleared. That is indistinguishable from the drag not having
        // registered, so the operator's reading is "the grid is broken", and they try again.
        onDragRefused?.();
      }
    };

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    return () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
  }, [drag, bookings, selDay, openBooking, onDragReschedule, onDragRefused]);

  // Build time columns for header
  const headerCols: { left: number; t: number; isHour: boolean; isHalf: boolean; label: string }[] = [];
  for (let i = 0; i < cols; i++) {
    const t = dayStart + i * 15;
    const isHour = t % 60 === 0;
    const isHalf = t % 60 === 30;
    headerCols.push({
      left: i * COL_W, t, isHour, isHalf,
      label: isHour ? fmtT(t) : isHalf ? ':30' : '',
    });
  }

  // The now-line marks the present moment. It may only appear on the day that IS today, and at
  // the actual time — it used to be drawn on every day at a hard-coded 1:15 PM.
  const nowMins = minutesOfDay(tickingNow);
  const showNow = isToday && nowMins >= dayStart && nowMins <= dayEnd;
  const nowLeft = (nowMins - dayStart) / 15 * COL_W;

  // Build bars
  const bars = visible.map(b => {
    const isDragging = drag?.id === b.id;
    const start  = isDragging ? drag!.curStart  : b.start;
    const dur    = isDragging ? drag!.curDur    : b.dur;
    const staffId = isDragging ? drag!.curStaff : b.staff;

    const left  = (start - dayStart) / 15 * COL_W;
    const width = dur / 15 * COL_W;
    const top   = (staffY[staffId] ?? 0) + 4;
    const m     = statusMeta(b.status);
    const narrow = width < 80;
    const conflict = isDragging && drag!.conflict;
    return { b, isDragging, left, width, top, m, narrow, conflict };
  });

  const showGhost = !!(drag?.moved && drag);
  const gL = showGhost ? (drag!.origStart - dayStart) / 15 * COL_W : 0;
  const gW = showGhost ? drag!.origDur / 15 * COL_W : 0;
  const gT = showGhost ? (staffY[drag!.origStaff] ?? 0) + 4 : 0;
  const dL = showGhost ? (drag!.curStart - dayStart) / 15 * COL_W : 0;
  const dW = showGhost ? drag!.curDur / 15 * COL_W : 0;
  const dT = showGhost ? (staffY[drag!.curStaff] ?? 0) + 4 : 0;

  return (
    <div className="flex flex-col min-h-0 h-full">

      {/* Time axis header */}
      <div
        className="flex shrink-0 border-b"
        style={{ background: '#fff', borderColor: '#EAEAEA' }}
      >
        <div
          className="shrink-0 border-r flex flex-col justify-center px-[11px]"
          style={{ width: LABEL_W, borderColor: '#EAEAEA' }}
        >
          <span
            className="font-mono text-[10px] font-medium tracking-[0.12em] uppercase"
            style={{ color: '#9A9A9A' }}
          >
            {resources.some(r => r.name) ? resourceLabel : 'Bookings'}
          </span>
          {showNow && (
            <span className="font-mono text-[10.5px] font-medium mt-[2px]" style={{ color: '#0A0A0A' }}>
              ● {fmtT(nowMins)}
            </span>
          )}
        </div>
        <div className="flex-1 overflow-hidden relative" style={{ height: 40 }}>
          <div
            ref={headRef}
            className="absolute"
            style={{ width: gridW, height: 40, willChange: 'transform' }}
          >
            {headerCols.map((hc, i) => (
              <div
                key={i}
                style={{
                  position: 'absolute', left: hc.left, top: 0, width: COL_W, height: 40,
                  borderLeft: `1px solid ${hc.isHour ? '#E6E8EB' : '#F1F2F4'}`,
                }}
              >
                {hc.label && (
                  <div
                    className="flex items-center h-full pl-[5px] font-mono"
                    style={{
                      fontSize: 11,
                      fontWeight: hc.isHour ? 600 : 500,
                      color: hc.isHour ? '#0A0A0A' : '#9A9A9A',
                    }}
                  >
                    {hc.label}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      </div>

      {/* Body: labels + scrollable grid */}
      <div className="flex flex-1 min-h-0">

        {/* Staff labels */}
        <div
          className="shrink-0 overflow-hidden border-r bg-white"
          style={{ width: LABEL_W, borderColor: '#EAEAEA' }}
        >
          {resources.map(st => (
            <div
              key={st.id}
              className="flex items-center gap-[8px] px-[11px] border-b"
              style={{ height: ROW_H, borderColor: '#F3F4F6' }}
            >
              <div
                className="w-[19px] h-[19px] rounded-[5px] flex items-center justify-center shrink-0 text-[11px] font-semibold"
                style={{ background: '#F1F2F4', color: '#4A4F59' }}
              >
                {st.name.charAt(0)}
              </div>
              <div className="min-w-0">
                <div className="text-[11px] font-medium truncate" style={{ color: '#55554F' }}>
                  {st.name}
                </div>
                <div className="text-[9.5px] truncate" style={{ color: '#9A9A9A' }}>
                  {st.role ? st.role.split(' · ')[0] : ''}
                </div>
              </div>
            </div>
          ))}
        </div>

        {/* Grid viewport */}
        <div
          className="flex-1 overflow-auto"
          style={{ background: '#F9F9F7' }}
          onScroll={onGridScroll}
        >
          <div
            ref={gridRef}
            className="relative"
            style={{ width: gridW, height: gridH }}
          >
            {/* Row stripes */}
            {resources.map((_, i) => (
              <div
                key={i}
                style={{
                  position: 'absolute', left: 0, top: i * ROW_H,
                  width: gridW, height: ROW_H,
                  borderBottom: '1px solid #F3F4F6', zIndex: 1,
                }}
              />
            ))}

            {/* Column lines */}
            {headerCols.map((hc, i) => (
              <div
                key={i}
                style={{
                  position: 'absolute', top: 0, left: hc.left,
                  width: 1, height: gridH,
                  background: hc.isHour ? '#E6E8EB' : '#F3F4F6',
                }}
              />
            ))}

            {/* Now line */}
            {showNow && (
              <>
                <div style={{
                  position: 'absolute', top: 0, left: nowLeft,
                  width: 1.5, height: gridH,
                  background: '#0A0A0A', zIndex: 40,
                }} />
                <div style={{
                  position: 'absolute', top: -3, left: nowLeft - 4,
                  width: 9, height: 9, borderRadius: '50%',
                  background: '#DDF247', boxShadow: '0 0 0 1.5px #0A0A0A', zIndex: 41,
                }} />
              </>
            )}

            {/* Ghost (original position during drag) */}
            {showGhost && (
              <div style={{
                position: 'absolute',
                left: gL + 1, top: gT,
                width: Math.max(gW - 2, 8), height: ROW_H - 8,
                border: '1px dashed #B6BCC4', borderRadius: 4,
                background: 'rgba(148,163,184,0.10)',
                zIndex: 8, boxSizing: 'border-box',
              }} />
            )}

            {/* Drop indicator */}
            {showGhost && (
              <div style={{
                position: 'absolute',
                left: dL + 1, top: dT,
                width: Math.max(dW - 2, 8), height: ROW_H - 8,
                border: `1.5px solid ${drag!.conflict ? '#D23B30' : '#0A0A0A'}`,
                borderRadius: 5,
                background: drag!.conflict
                  ? 'rgba(210,59,48,0.10)'
                  : 'rgba(10,10,10,0.06)',
                zIndex: 9, boxSizing: 'border-box',
              }} />
            )}

            {/* Booking bars */}
            {bars.map(({ b, isDragging, left, width, top, m, narrow, conflict }) => (
              <div
                key={b.id}
                role="button"
                tabIndex={0}
                aria-label={`${b.name} — ${b.serviceName}, ${fmtT(b.start)}`}
                onMouseDown={e => startDrag(b.id, 'move', e)}
                style={{
                  position: 'absolute',
                  left: left + 1, top,
                  width: Math.max(width - 2, 16), height: ROW_H - 8,
                  background: m.fill,
                  borderRadius: 5,
                  border: conflict
                    ? '1.5px solid #D23B30'
                    : `1px solid ${m.accent}33`,
                  borderLeft: `3px solid ${m.accent}`,
                  padding: '0 7px 0 8px',
                  display: 'flex', alignItems: 'center', gap: 5,
                  overflow: 'hidden', fontSize: 11.5, lineHeight: 1,
                  cursor: isDragging ? 'grabbing' : 'grab',
                  userSelect: 'none', boxSizing: 'border-box',
                  boxShadow: isDragging ? '0 10px 24px -6px rgba(10,10,10,0.30)' : 'none',
                  zIndex: isDragging ? 50 : 10,
                  transition: isDragging ? 'none' : 'box-shadow 0.15s ease',
                  color: m.text,
                }}
              >
                {/* Resize-left handle */}
                <div
                  onMouseDown={e => { e.stopPropagation(); startDrag(b.id, 'resize-l', e); }}
                  style={{
                    position: 'absolute', left: 0, top: 0, bottom: 0, width: 7,
                    cursor: 'ew-resize', zIndex: 2,
                  }}
                />
                <span className="font-semibold truncate" style={{ flex: '1 1 auto', minWidth: 0, fontSize: 11.5 }}>
                  {b.name}
                </span>
                {!narrow && (
                  <span className="shrink-0 text-[10px] opacity-75">
                    {b.serviceName.split(' ')[0]}
                  </span>
                )}
                {/* Resize-right handle */}
                <div
                  onMouseDown={e => { e.stopPropagation(); startDrag(b.id, 'resize-r', e); }}
                  style={{
                    position: 'absolute', right: 0, top: 0, bottom: 0, width: 7,
                    cursor: 'ew-resize', zIndex: 2,
                  }}
                />
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

// ── List view ─────────────────────────────────────────────────────────────
function ListView({
  bookings, selDay, timeFilter, openBooking, resources, loaded,
}: {
  bookings: Booking[];
  selDay: number;
  timeFilter: string;
  openBooking: (id: string) => void;
  resources: readonly GridResource[];
  loaded: boolean;
}) {
  const items = bookings
    .filter(b => b.day === selDay && isActive(b.status) && inTimeFilter(b.start, timeFilter))
    .sort((a, b) => a.start - b.start);

  const groups: { t: number; items: Booking[] }[] = [];
  const map = new Map<number, Booking[]>();
  items.forEach(b => {
    if (!map.has(b.start)) map.set(b.start, []);
    map.get(b.start)!.push(b);
  });
  map.forEach((list, t) => groups.push({ t, items: list }));

  if (groups.length === 0) {
    return (
      <div
        className="flex items-center justify-center"
        style={{ height: 200 }}
      >
        <div className="text-center">
          <div className="text-[14px] font-medium mb-1" style={{ color: '#9A9A9A' }}>
            {loaded ? 'No bookings' : 'Not loaded yet'}
          </div>
          <div className="text-[12px]" style={{ color: '#C5C9D0' }}>
            {loaded ? 'Try changing the time filter' : 'The schedule has not answered yet.'}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="overflow-y-auto flex-1 p-[16px]">
      <div style={{ maxWidth: 780, margin: '0 auto' }}>
        {groups.map(g => (
          <div key={g.t} className="mb-[14px]">
            <div className="flex items-center gap-[10px] mb-[8px]">
              <span className="text-[13px] font-semibold" style={{ color: '#55554F' }}>
                {fmtT(g.t)}
              </span>
              <div className="flex-1 h-px" style={{ background: '#EAEAEA' }} />
            </div>
            <div className="rounded-[10px] overflow-hidden" style={{ background: '#fff', border: '1px solid #ECEEF1' }}>
              {g.items.map((b, i) => {
                const m = statusMeta(b.status);
                const stf = resources.find(s => s.id === b.staff);
                return (
                  <button
                    key={b.id}
                    onClick={() => openBooking(b.id)}
                    className="flex items-center gap-[11px] w-full text-left cursor-pointer border-none transition-colors hover:bg-[#F9F9F7]"
                    style={{
                      background: 'transparent',
                      padding: '11px 14px',
                      borderBottom: i < g.items.length - 1 ? '1px solid #F3F4F6' : 'none',
                      fontFamily: 'inherit',
                    }}
                  >
                    <span
                      className="w-[4px] self-stretch rounded-[3px] shrink-0"
                      style={{ background: m.accent }}
                    />
                    <div className="flex-1 min-w-0">
                      <div className="text-[14px] font-semibold" style={{ color: '#0A0A0A' }}>
                        {b.name}
                      </div>
                      <div className="text-[12px] mt-[1px]" style={{ color: '#9A9A9A' }}>
                        {b.serviceName}{stf?.name ? ` · ${stf.name}` : ''} · {fmtRange(b.start, b.dur)}
                      </div>
                    </div>
                    <span
                      className="text-[11px] px-[9px] py-[3px] rounded-[6px] whitespace-nowrap font-semibold shrink-0"
                      style={{ background: m.fill, color: m.text }}
                    >
                      {b.status.charAt(0).toUpperCase() + b.status.slice(1)}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── TodayScreen ───────────────────────────────────────────────────────────
export interface TodayScreenProps {
  device: 'phone' | 'desktop';
  /** The displayed week, derived from the window the schedule query used. */
  week: ConsoleWeek;
  onPrevWeek: () => void;
  onNextWeek: () => void;
  onGoToToday: () => void;
  selDay: number;
  setSelDay: (d: number) => void;
  openBooking: (id: string) => void;
  goQueue: () => void;
  queueCount: number;
  bookings: Booking[];
  onDragReschedule: (id: string, patch: { day: number; start: number; staff: string; dur: number }) => void;
  /**
   * Called when a drag is dropped somewhere the grid will not submit, so the refusal can be
   * reported to the operator. Without it the drop was a silent no-op and the bar simply sprang
   * back, which reads as a broken grid rather than as a decision.
   */
  onDragRefused?: () => void;
  resourceLabel: string;
  /**
   * The lanes to draw. One unnamed lane while the platform models no resource — see
   * `features/bookings/resources.ts`.
   */
  resources: readonly GridResource[];
  /**
   * True once the schedule has answered for this week. `false` means "not yet known" — which
   * must not render as "No bookings", the same rule Queue and Waitlist hold.
   */
  loaded?: boolean;
}

const VIEW_OPTS   = [{ value: 'diagram', label: 'Diagram' }, { value: 'list', label: 'List' }];
const TIME_OPTS   = [
  { value: 'all',     label: 'All'     },
  { value: 'morning', label: 'Morning' },
  { value: 'lunch',   label: 'Lunch'   },
  { value: 'evening', label: 'Evening' },
];

export function TodayScreen({
  device, week, onPrevWeek, onNextWeek, onGoToToday, selDay, setSelDay,
  openBooking, goQueue, queueCount,
  bookings, onDragReschedule, onDragRefused, resourceLabel, resources, loaded = true,
}: TodayScreenProps) {
  const isPhone = device === 'phone';
  const [calView,    setCalView]    = useState<'diagram' | 'list'>('diagram');
  const [timeFilter, setTimeFilter] = useState('all');

  /**
   * ONE clock for the screen, passed down.
   *
   * `StatsCard` and `DiagramView` each used to call `useTickingNow()`, which starts an interval
   * at mount — so the two ticked out of phase by however long apart they mounted, and the
   * "remaining today" tile could disagree with the grid's now-line about the current minute for
   * up to a full interval. That is the drift `Countdown` documents its one-clock-per-screen rule
   * to prevent, and `WaitlistScreen` already follows.
   */
  const now = useTickingNow();

  const selectedDayLabel = dayLabel(week, selDay);

  /** Day arrows roll over into the adjacent week rather than dead-ending at Monday/Sunday. */
  function stepDay(delta: -1 | 1) {
    const next = selDay + delta;
    if (next < 0) { onPrevWeek(); setSelDay(6); return; }
    if (next > 6) { onNextWeek(); setSelDay(0); return; }
    setSelDay(next);
  }
  // No "Closed" pill. `dayBounds().closed` came from `SETTINGS.hours` — a hand-written constant
  // that SettingsScreen renders under a "no operating-hours endpoint yet" badge. Telling an
  // operator their venue is closed on the strength of that is the same fabrication, unbadged,
  // on the live schedule.
  const isToday = week.todayIndex === selDay;
  const dayBookings = bookings.filter(b => b.day === selDay && isActive(b.status));

  // ── Phone: simplified card list ──────────────────────────────────────────
  if (isPhone) {
    const filtered = dayBookings
      .filter(b => inTimeFilter(b.start, timeFilter))
      .sort((a, b) => a.start - b.start);

    return (
      <div>
        {/* Controls */}
        <div className="flex items-center gap-2 mb-[12px]">
          <button
            aria-label="Previous day"
            onClick={() => stepDay(-1)}
            className="w-[28px] h-[28px] flex items-center justify-center rounded-[7px] border border-cd-border bg-white cursor-pointer hover:bg-wash transition-colors"
          >
            <Icon name="chevL" size={13} strokeWidth={2.4} color="#55554F" />
          </button>
          <div className="flex-1 flex items-center justify-center gap-2 min-w-0">
            <span className="text-[14px] font-semibold truncate" style={{ color: '#0A0A0A' }}>{selectedDayLabel}</span>
          </div>
          <button
            aria-label="Next day"
            onClick={() => stepDay(1)}
            className="w-[28px] h-[28px] flex items-center justify-center rounded-[7px] border border-cd-border bg-white cursor-pointer hover:bg-wash transition-colors"
          >
            <Icon name="chevR" size={13} strokeWidth={2.4} color="#55554F" />
          </button>
        </div>
        <div className="flex flex-wrap gap-[6px] mb-[14px]">
          {TIME_OPTS.map(o => (
            <button
              key={o.value}
              onClick={() => setTimeFilter(o.value)}
              className={`px-[12px] py-[5px] rounded-[99px] text-[12.5px] font-medium border cursor-pointer transition-all ${
                timeFilter === o.value
                  ? 'bg-[#0A0A0A] text-white border-[#0A0A0A]'
                  : 'bg-white text-[#55554F] border-[#EAEAEA] hover:border-[#DEDEDA] hover:bg-[#FAFAF9]'
              }`}
              style={{ fontFamily: 'inherit' }}
            >
              {o.label}
            </button>
          ))}
        </div>
        {filtered.length === 0 ? (
          <div className="py-12 text-center text-[13px]" style={{ color: '#9A9A9A' }}>
            {loaded ? 'No bookings' : 'Not loaded yet'}
          </div>
        ) : (
          <div className="flex flex-col gap-2">
            {filtered.map(b => {
              const m = statusMeta(b.status);
              const stf = resources.find(s => s.id === b.staff);
              return (
                <button
                  key={b.id}
                  onClick={() => openBooking(b.id)}
                  className="block w-full text-left bg-white rounded-[13px] p-[12px_14px] cursor-pointer transition-all duration-150 hover:shadow-[0_4px_14px_-6px_rgba(10,10,10,0.12)] hover:-translate-y-px border-none"
                  style={{ border: '1px solid #EAEAEA', borderLeft: `3px solid ${m.accent}`, fontFamily: 'inherit' }}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-mono text-[12.5px] font-semibold" style={{ color: '#0A0A0A' }}>
                      {fmtT(b.start)}
                    </span>
                    <span className="text-[11px] px-[8px] py-[2px] rounded-[6px] font-semibold"
                      style={{ background: m.fill, color: m.text }}>
                      {b.status.charAt(0).toUpperCase() + b.status.slice(1)}
                    </span>
                  </div>
                  <div className="text-[15px] font-semibold mt-[7px] tracking-[-0.01em]" style={{ color: '#0A0A0A' }}>
                    {b.name}
                  </div>
                  <div className="text-[12.5px] mt-[1px]" style={{ color: '#9A9A9A' }}>
                    {b.serviceName}{stf?.name ? ` · ${stf.name}` : ''}
                  </div>
                </button>
              );
            })}
          </div>
        )}
      </div>
    );
  }

  // ── Desktop: Otto Bookings-style split layout ────────────────────────────
  return (
    <div className="flex flex-col bg-white rounded-[12px]" style={{ padding: '14px 0 0' }}>

      {/* Controls row */}
      <div
        className="flex items-center justify-between gap-[16px] flex-wrap mb-[10px]"
        style={{ padding: '0 14px 10px', borderBottom: '1px solid #ECEEF1' }}
      >
        {/* Left: day nav */}
        <div className="flex items-center gap-[10px]">
          <div className="flex items-center">
            <button
              aria-label="Previous day"
              onClick={() => stepDay(-1)}
              className="w-[30px] h-[30px] flex items-center justify-center border border-r-0 cursor-pointer hover:bg-wash transition-colors"
              style={{ borderColor: '#EAEAEA', borderRadius: '7px 0 0 7px', background: '#fff' }}
            >
              <Icon name="chevL" size={14} strokeWidth={2.4} color="#55554F" />
            </button>
            <button
              aria-label="Jump to today"
              onClick={onGoToToday}
              className="h-[30px] px-[11px] border cursor-pointer transition-colors hover:bg-wash"
              style={{ borderColor: '#EAEAEA', background: '#fff', fontFamily: 'inherit', fontSize: 12.5, color: '#55554F', fontWeight: 500 }}
            >
              Today
            </button>
            <button
              aria-label="Next day"
              onClick={() => stepDay(1)}
              className="w-[30px] h-[30px] flex items-center justify-center border border-l-0 cursor-pointer hover:bg-wash transition-colors"
              style={{ borderColor: '#EAEAEA', borderRadius: '0 7px 7px 0', background: '#fff' }}
            >
              <Icon name="chevR" size={14} strokeWidth={2.4} color="#55554F" />
            </button>
          </div>
          <h2 className="text-[16px] font-semibold tracking-[-0.02em] m-0" style={{ color: '#0A0A0A' }}>
            {selectedDayLabel}
          </h2>
          {queueCount > 0 && (
            <button
              onClick={goQueue}
              className="flex items-center gap-[5px] px-[10px] h-[28px] rounded-[7px] border cursor-pointer text-[12px] font-semibold transition-colors hover:opacity-80"
              style={{
                fontFamily: 'inherit', background: '#FBF3DF',
                border: '1px solid #F6E6B8', color: '#7E5606',
              }}
            >
              {queueCount} needs action
            </button>
          )}
        </div>

        {/* Right: view + time filters */}
        <div className="flex items-center gap-[18px] flex-wrap">
          <SegRow label="View" options={VIEW_OPTS} value={calView} onChange={v => setCalView(v as 'diagram' | 'list')} />
          <SegRow label="Time" options={TIME_OPTS} value={timeFilter} onChange={setTimeFilter} />
        </div>
      </div>

      {/* Main split layout */}
      <div
        className="flex min-h-0 overflow-hidden rounded-[11px]"
        style={{
          height: 'calc(100vh - 200px)',
          minHeight: 400,
          border: '1px solid #E5E7EB',
        }}
      >
        {/* Left sidebar */}
        <aside
          className="shrink-0 overflow-y-auto p-[13px]"
          style={{
            width: 256,
            background: '#FCFCFD',
            borderRight: '1px solid #E5E7EB',
          }}
        >
          {/* Week strip */}
          <div className="rounded-[10px] p-[11px_12px] mb-[12px]" style={{ border: '1px solid #ECEEF1' }}>
            <div className="flex items-center justify-between mb-[10px]">
              <span className="text-[12.5px] font-semibold" style={{ color: '#0A0A0A' }}>
                {week.rangeLabel}
              </span>
              <span className="flex items-center gap-[2px]">
                <button
                  aria-label="Previous week"
                  onClick={onPrevWeek}
                  className="w-[22px] h-[22px] flex items-center justify-center rounded-[6px] border-none bg-transparent cursor-pointer hover:bg-wash transition-colors"
                >
                  <Icon name="chevL" size={13} strokeWidth={2.4} color="#55554F" />
                </button>
                <button
                  aria-label="Next week"
                  onClick={onNextWeek}
                  className="w-[22px] h-[22px] flex items-center justify-center rounded-[6px] border-none bg-transparent cursor-pointer hover:bg-wash transition-colors"
                >
                  <Icon name="chevR" size={13} strokeWidth={2.4} color="#55554F" />
                </button>
              </span>
            </div>
            <div className="grid grid-cols-7 gap-[2px]">
              {week.dows.map((dw, i) => {
                const sel     = i === selDay;
                // Null in any week that isn't the current one — nothing is marked "today"
                // in a week that does not contain today.
                const isTodayColumn = i === week.todayIndex;
                return (
                  <button
                    key={i}
                    onClick={() => setSelDay(i)}
                    className="flex flex-col items-center gap-[3px] py-[6px] rounded-[7px] border-none cursor-pointer transition-all"
                    style={{
                      fontFamily: 'inherit',
                      background: sel ? '#0A0A0A' : 'transparent',
                      color: sel ? '#fff' : isTodayColumn ? '#0A0A0A' : '#55554F',
                      boxShadow: isTodayColumn && !sel ? 'inset 0 0 0 1.5px #0A0A0A' : 'none',
                    }}
                  >
                    <span className="font-mono text-[9px] font-medium tracking-[0.04em]" style={{ opacity: 0.75 }}>
                      {dw.charAt(0)}
                    </span>
                    <span className="font-mono text-[12px] font-bold">{week.doms[i]}</span>
                  </button>
                );
              })}
            </div>
          </div>

          {/* Stats */}
          <StatsCard bookings={bookings} selDay={selDay} isToday={isToday} now={now} />

          {/* Bookings list label */}
          <div
            className="font-mono text-[10.5px] font-medium tracking-[0.12em] uppercase mb-[9px] mx-[2px]"
            style={{ color: '#9A9A9A' }}
          >
            Today's bookings
          </div>
          <SideList
            bookings={bookings}
            selDay={selDay}
            timeFilter={timeFilter}
            openBooking={openBooking}
            resources={resources}
            loaded={loaded}
          />
        </aside>

        {/* Right panel */}
        <main
          className="flex-1 min-w-0 flex flex-col"
          style={{ background: '#F9F9F7' }}
        >
          {calView === 'diagram' ? (
            <DiagramView
              bookings={bookings}
              selDay={selDay}
              timeFilter={timeFilter}
              openBooking={openBooking}
              onDragReschedule={onDragReschedule}
              onDragRefused={onDragRefused}
              resourceLabel={resourceLabel}
              resources={resources}
              isToday={isToday}
              now={now}
            />
          ) : (
            <ListView
              bookings={bookings}
              selDay={selDay}
              timeFilter={timeFilter}
              openBooking={openBooking}
              resources={resources}
              loaded={loaded}
            />
          )}
        </main>
      </div>
    </div>
  );
}

/**
 * Test-only alias.
 *
 * `dayWindow` is the one piece of this file with a stated invariant ("every booking is inside the
 * window"), and it is the piece whose previous version hid real bookings — so it is tested
 * directly rather than through the grid's layout maths.
 */
export { dayWindow as dayWindowForTest };
