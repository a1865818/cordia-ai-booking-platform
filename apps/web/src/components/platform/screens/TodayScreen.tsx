import { useState, useRef, useEffect, useCallback } from "react";
import { Booking, STAFF, WEEK, dayBounds, statusMeta, fmtT, fmtRange } from "../../../data/console";
import { Icon } from "../../ui/Icon";

// ── Constants ──────────────────────────────────────────────────────────────
const COL_W    = 68;           // px per 15-min slot (fixed layout unit)
const ROW_H    = 40;           // px per resource row
const LABEL_W  = 148;          // px for resource labels column
const NOW_MINS = 13 * 60 + 15; // demo "now" = 1:15 PM
// DAY_START / DAY_END / COLS are derived per-day from dayBounds() inside DiagramView

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
    b => b.id !== excludeId && b.staff === staff && b.day === day &&
         start < b.start + b.dur && b.start < start + dur,
  );
}

function yToStaffIdx(y: number): number {
  return Math.max(0, Math.min(Math.floor(y / ROW_H), STAFF.length - 1));
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
function StatsCard({ bookings, selDay }: { bookings: Booking[]; selDay: number }) {
  const day = bookings.filter(b => b.day === selDay && isActive(b.status));
  const total = day.length;
  const remaining = day.filter(b => b.start >= NOW_MINS).length;
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
        {([
          ['bookings',  String(total)],
          ['remaining', String(remaining)],
          ['clients',   String(total)],
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
  bookings, selDay, timeFilter, openBooking,
}: {
  bookings: Booking[];
  selDay: number;
  timeFilter: string;
  openBooking: (id: string) => void;
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
      <div className="py-6 text-center text-[12px]" style={{ color: '#9A9A9A' }}>No bookings</div>
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
            const stf = STAFF.find(s => s.id === b.staff);
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
                  {b.serviceName.split(' ')[0]} · {stf?.name.split(' ')[0]}
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
  bookings, selDay, timeFilter, openBooking, onDragReschedule, resourceLabel,
}: {
  bookings: Booking[];
  selDay: number;
  timeFilter: string;
  openBooking: (id: string) => void;
  onDragReschedule: (id: string, patch: { day: number; start: number; staff: string }) => void;
  resourceLabel: string;
}) {
  const { start: dayStart, end: dayEnd } = dayBounds(selDay);
  const cols = (dayEnd - dayStart) / 15;

  const gridRef    = useRef<HTMLDivElement>(null);
  const headRef    = useRef<HTMLDivElement>(null);
  const [drag, setDrag]   = useState<DragState | null>(null);
  const dragRef = useRef<DragState | null>(null);

  const gridW = cols * COL_W;
  const gridH = STAFF.length * ROW_H;

  const staffY: Record<string, number> = {};
  STAFF.forEach((s, i) => { staffY[s.id] = i * ROW_H; });

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
          nstf = STAFF[yToStaffIdx(e.clientY - r.top)].id;
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

    const onUp = (e: MouseEvent) => {
      const d = dragRef.current;
      dragRef.current = null;
      setDrag(null);
      if (!d) return;
      if (!d.moved) {
        openBooking(d.id);
      } else if (!d.conflict) {
        onDragReschedule(d.id, { day: selDay, start: d.curStart, staff: d.curStaff });
      }
    };

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    return () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
  }, [drag, bookings, selDay, openBooking, onDragReschedule]);

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

  const showNow = NOW_MINS >= dayStart && NOW_MINS <= dayEnd;
  const nowLeft = (NOW_MINS - dayStart) / 15 * COL_W;

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
            {resourceLabel}
          </span>
          {showNow && (
            <span className="font-mono text-[10.5px] font-medium mt-[2px]" style={{ color: '#0A0A0A' }}>
              ● {fmtT(NOW_MINS)}
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
          {STAFF.map(st => (
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
                  {st.role.split(' · ')[0]}
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
            {STAFF.map((_, i) => (
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
  bookings, selDay, timeFilter, openBooking,
}: {
  bookings: Booking[];
  selDay: number;
  timeFilter: string;
  openBooking: (id: string) => void;
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
            No bookings
          </div>
          <div className="text-[12px]" style={{ color: '#C5C9D0' }}>
            Try changing the time filter
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
                const stf = STAFF.find(s => s.id === b.staff);
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
                        {b.serviceName} · {stf?.name} · {fmtRange(b.start, b.dur)}
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
  selDay: number;
  setSelDay: (d: number) => void;
  openBooking: (id: string) => void;
  goQueue: () => void;
  queueCount: number;
  bookings: Booking[];
  onDragReschedule: (id: string, patch: { day: number; start: number; staff: string }) => void;
  resourceLabel: string;
}

const VIEW_OPTS   = [{ value: 'diagram', label: 'Diagram' }, { value: 'list', label: 'List' }];
const TIME_OPTS   = [
  { value: 'all',     label: 'All'     },
  { value: 'morning', label: 'Morning' },
  { value: 'lunch',   label: 'Lunch'   },
  { value: 'evening', label: 'Evening' },
];

export function TodayScreen({
  device, selDay, setSelDay,
  openBooking, goQueue, queueCount,
  bookings, onDragReschedule, resourceLabel,
}: TodayScreenProps) {
  const isPhone = device === 'phone';
  const [calView,    setCalView]    = useState<'diagram' | 'list'>('diagram');
  const [timeFilter, setTimeFilter] = useState('all');

  const dayLabel = `${WEEK.dows[selDay].charAt(0)}${WEEK.dows[selDay].slice(1).toLowerCase()}, ${WEEK.month} ${WEEK.doms[selDay]}`;
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
            onClick={() => setSelDay(Math.max(0, selDay - 1))}
            disabled={selDay === 0}
            className="w-[28px] h-[28px] flex items-center justify-center rounded-[7px] border border-cd-border bg-white disabled:opacity-30 cursor-pointer hover:bg-wash transition-colors"
          >
            <Icon name="chevL" size={13} strokeWidth={2.4} color="#55554F" />
          </button>
          <span className="text-[14px] font-semibold flex-1 text-center" style={{ color: '#0A0A0A' }}>{dayLabel}</span>
          <button
            aria-label="Next day"
            onClick={() => setSelDay(Math.min(6, selDay + 1))}
            disabled={selDay === 6}
            className="w-[28px] h-[28px] flex items-center justify-center rounded-[7px] border border-cd-border bg-white disabled:opacity-30 cursor-pointer hover:bg-wash transition-colors"
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
          <div className="py-12 text-center text-[13px]" style={{ color: '#9A9A9A' }}>No bookings</div>
        ) : (
          <div className="flex flex-col gap-2">
            {filtered.map(b => {
              const m = statusMeta(b.status);
              const stf = STAFF.find(s => s.id === b.staff);
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
                    {b.serviceName} · {stf?.name}
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
              onClick={() => setSelDay(Math.max(0, selDay - 1))}
              disabled={selDay === 0}
              className="w-[30px] h-[30px] flex items-center justify-center border border-r-0 disabled:opacity-30 cursor-pointer hover:bg-wash transition-colors"
              style={{ borderColor: '#EAEAEA', borderRadius: '7px 0 0 7px', background: '#fff' }}
            >
              <Icon name="chevL" size={14} strokeWidth={2.4} color="#55554F" />
            </button>
            <button
              aria-label="Jump to today"
              onClick={() => setSelDay(WEEK.today)}
              className="h-[30px] px-[11px] border cursor-pointer transition-colors hover:bg-wash"
              style={{ borderColor: '#EAEAEA', background: '#fff', fontFamily: 'inherit', fontSize: 12.5, color: '#55554F', fontWeight: 500 }}
            >
              Today
            </button>
            <button
              aria-label="Next day"
              onClick={() => setSelDay(Math.min(6, selDay + 1))}
              disabled={selDay === 6}
              className="w-[30px] h-[30px] flex items-center justify-center border border-l-0 disabled:opacity-30 cursor-pointer hover:bg-wash transition-colors"
              style={{ borderColor: '#EAEAEA', borderRadius: '0 7px 7px 0', background: '#fff' }}
            >
              <Icon name="chevR" size={14} strokeWidth={2.4} color="#55554F" />
            </button>
          </div>
          <h2 className="text-[16px] font-semibold tracking-[-0.02em] m-0" style={{ color: '#0A0A0A' }}>
            {dayLabel}
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
                {WEEK.month} {WEEK.doms[0]}–{WEEK.doms[6]}
              </span>
            </div>
            <div className="grid grid-cols-7 gap-[2px]">
              {WEEK.dows.map((dw, i) => {
                const sel     = i === selDay;
                const isToday = i === WEEK.today;
                return (
                  <button
                    key={i}
                    onClick={() => setSelDay(i)}
                    className="flex flex-col items-center gap-[3px] py-[6px] rounded-[7px] border-none cursor-pointer transition-all"
                    style={{
                      fontFamily: 'inherit',
                      background: sel ? '#0A0A0A' : 'transparent',
                      color: sel ? '#fff' : isToday ? '#0A0A0A' : '#55554F',
                      boxShadow: isToday && !sel ? 'inset 0 0 0 1.5px #0A0A0A' : 'none',
                    }}
                  >
                    <span className="font-mono text-[9px] font-medium tracking-[0.04em]" style={{ opacity: 0.75 }}>
                      {dw.charAt(0)}
                    </span>
                    <span className="font-mono text-[12px] font-bold">{WEEK.doms[i]}</span>
                  </button>
                );
              })}
            </div>
          </div>

          {/* Stats */}
          <StatsCard bookings={bookings} selDay={selDay} />

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
              resourceLabel={resourceLabel}
            />
          ) : (
            <ListView
              bookings={bookings}
              selDay={selDay}
              timeFilter={timeFilter}
              openBooking={openBooking}
            />
          )}
        </main>
      </div>
    </div>
  );
}
