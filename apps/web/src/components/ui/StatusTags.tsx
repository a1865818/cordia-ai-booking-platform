import { chMeta, depStatusMeta } from "../../data/console";
import { Icon } from "./Icon";

// ---- DepTag ----
interface DepTagProps { dep: number; }
export function DepTag({ dep }: DepTagProps) {
  if (dep > 0) {
    return (
      <span className="inline-flex items-center font-mono text-[10.5px] font-semibold px-2 py-[3px] rounded-[7px] bg-wash2 text-ink">
        A${dep} held
      </span>
    );
  }
  return (
    <span className="inline-flex items-center text-[10.5px] font-medium px-2 py-[3px] rounded-[7px] text-t3"
      style={{ background: 'transparent', border: '1px dashed #DCDCDC' }}>
      No deposit
    </span>
  );
}

// ---- ProvTag ----
interface ProvTagProps { prov: 'ai' | 'you'; }
export function ProvTag({ prov }: ProvTagProps) {
  if (prov === 'ai') {
    return (
      <span className="inline-flex items-center gap-1 text-[10.5px] font-semibold px-2 py-[3px] rounded-[7px] bg-ink text-white">
        <Icon name="star" size={9} fill="#DDF247" />
        Booked by AI
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1 text-[10.5px] font-semibold px-2 py-[3px] rounded-[7px] bg-wash2 text-ink70">
      Booked by you
    </span>
  );
}

// ---- DepChip (for Deposits screen) ----
interface DepChipProps { status: string; }
export function DepChip({ status }: DepChipProps) {
  const m = depStatusMeta[status];
  if (!m) return null;
  return (
    <span className="inline-flex items-center gap-[5px] text-[11px] font-semibold px-[9px] py-[3px] rounded-full whitespace-nowrap"
      style={{ background: m.fill, color: m.text }}>
      <span className="w-[6px] h-[6px] rounded-full" style={{ background: m.accent }} />
      {m.label}
    </span>
  );
}

// ---- Chan (channel icon badge) ----

const CHAN_CONFIG: Record<string, {
  bg: string | string[];
  gradientAngle?: number;
  svg: string;
}> = {
  instagram: {
    bg: ['#405DE6', '#833AB4', '#C13584', '#E1306C', '#FD1D1D', '#F77737', '#FCAF45'],
    gradientAngle: 45,
    // Camera body + lens ring + dot — matches Instagram glyph
    svg: `<rect x="3" y="3" width="18" height="18" rx="4.8" ry="4.8" fill="none" stroke="white" stroke-width="1.7"/>
          <circle cx="12" cy="12" r="3.6" fill="none" stroke="white" stroke-width="1.7"/>
          <circle cx="17.1" cy="6.9" r="1.1" fill="white"/>`,
  },
  whatsapp: {
    bg: '#25D366',
    // Speech bubble + phone handset — matches WhatsApp glyph
    svg: `<path fill="white" d="M12 2.4C6.698 2.4 2.4 6.698 2.4 12c0 1.703.454 3.3 1.247 4.68L2.4 21.6l5.02-1.23A9.548 9.548 0 0 0 12 21.6c5.302 0 9.6-4.298 9.6-9.6S17.302 2.4 12 2.4zm0 17.28a7.637 7.637 0 0 1-3.89-1.063l-.28-.165-2.877.754.77-2.806-.181-.288A7.636 7.636 0 0 1 4.32 12c0-4.234 3.446-7.68 7.68-7.68 4.234 0 7.68 3.446 7.68 7.68 0 4.234-3.446 7.68-7.68 7.68zm4.176-5.746c-.23-.115-1.353-.668-1.562-.743-.21-.077-.362-.115-.514.115-.152.23-.59.743-.723.896-.133.152-.266.172-.494.057-.23-.115-.968-.357-1.844-1.138-.681-.608-1.141-1.36-1.274-1.588-.133-.23-.014-.354.1-.468.103-.103.23-.267.344-.4.115-.134.152-.23.23-.383.076-.153.038-.287-.02-.4-.057-.115-.513-1.237-.703-1.695-.184-.444-.373-.383-.513-.39-.133-.006-.287-.008-.44-.008-.153 0-.4.057-.61.287-.21.23-.8.78-.8 1.9s.82 2.204.933 2.357c.115.153 1.615 2.466 3.913 3.459.547.236.974.377 1.306.482.549.174 1.048.15 1.443.091.44-.066 1.353-.553 1.544-1.087.19-.534.19-.992.133-1.087-.057-.096-.21-.153-.44-.267z"/>`,
  },
  sms: {
    bg: '#007AFF',
    // iMessage-style filled speech bubble
    svg: `<path fill="white" d="M20 2H4C2.9 2 2 2.9 2 4v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2z"/>`,
  },
  phone: {
    bg: '#1C1C1E',
    // Classic telephone handset
    svg: `<path fill="none" stroke="white" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"
           d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72c.13.96.36 1.9.7 2.81a2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45c.91.34 1.85.57 2.81.7A2 2 0 0 1 22 16.92z"/>`,
  },
  web: {
    bg: '#5E5CE6',
    // Browser window — address bar + traffic-light dots
    svg: `<rect x="2" y="3.5" width="20" height="17" rx="2.5" fill="none" stroke="white" stroke-width="1.7"/>
          <path fill="none" stroke="white" stroke-width="1.7" d="M2 8.5h20"/>
          <circle cx="5.5" cy="6" r="1.05" fill="white"/>
          <circle cx="9.1" cy="6" r="1.05" fill="white"/>`,
  },
};

interface ChanProps { channel: string; size?: number; radius?: number; }
export function Chan({ channel, size = 34, radius }: ChanProps) {
  const cfg = CHAN_CONFIG[channel] ?? CHAN_CONFIG.web;
  const r = radius ?? Math.round(size * 0.29);
  const svgSize = Math.round(size * 0.72);

  let bg: string;
  if (Array.isArray(cfg.bg)) {
    const stops = cfg.bg.map((c, i) => `${c} ${Math.round(i / (cfg.bg as string[]).length * 100)}%`).join(', ');
    bg = `linear-gradient(${cfg.gradientAngle ?? 135}deg, ${stops})`;
  } else {
    bg = cfg.bg;
  }

  return (
    <span style={{
      width: size, height: size, borderRadius: r,
      background: bg,
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      flexShrink: 0,
      boxShadow: '0 1px 3px rgba(10,10,10,0.18)',
    }}>
      <svg
        width={svgSize}
        height={svgSize}
        viewBox="0 0 24 24"
        fill="none"
        dangerouslySetInnerHTML={{ __html: cfg.svg }}
      />
    </span>
  );
}

// ---- Tag (customer label) ----
interface TagProps { children: string; }
export function Tag({ children }: TagProps) {
  const styles: Record<string, { bg: string; fg: string }> = {
    VIP:            { bg: '#0A0A0A', fg: '#DDF247' },
    'No-show risk': { bg: '#FBEBEA', fg: '#93231B' },
    New:            { bg: '#E9F0FE', fg: '#2056CC' },
  };
  const s = styles[children] || { bg: '#F2F2F0', fg: '#55554F' };
  return (
    <span className="inline-flex items-center text-[10px] font-semibold px-2 py-[2px] rounded-full whitespace-nowrap"
      style={{ background: s.bg, color: s.fg }}>
      {children}
    </span>
  );
}
