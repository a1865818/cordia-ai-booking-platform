import type { CSSProperties, ReactNode } from "react";

/* ============================================================
   Shared landing atoms — Tailwind class strings + tiny helpers
   that reproduce the old tokens.css `.btn` / `.pill` and the
   landing.css `.shell` / `.section` / `.eyebrow` primitives.
   ============================================================ */

/* --- Layout --- */
export const shell = "max-w-[1240px] mx-auto px-8 relative z-[1]";
// .section { padding: 100px 0 } + the per-section inline paddingTop:40px override,
// + responsive @640: padding 72px 0 (top stays 40 via the inline override).
export const sectionCls = "relative pt-10 pb-[100px] max-[640px]:pb-[72px]";

/* --- Buttons (tokens.css .btn*) --- */
const btnBase =
  "inline-flex items-center justify-center gap-2 font-sans font-semibold whitespace-nowrap leading-none cursor-pointer border " +
  "transition-[transform,box-shadow,background-color,border-color] duration-150 ease-[cubic-bezier(0.22,1,0.36,1)] active:translate-y-px";

const btnSize = {
  md: "text-sm px-[18px] py-[11px] rounded-lg",
  lg: "text-lg px-6 py-[14px] rounded-[10px]",
  sm: "text-xs px-[13px] py-2 rounded-lg",
} as const;

const btnVariant = {
  primary:
    "bg-ink text-white border-transparent hover:bg-[#2A2A2A] hover:shadow-[0_6px_18px_-6px_rgba(10,10,10,0.28)]",
  lime: "bg-lime text-ink border-transparent hover:bg-[#E8FF66] hover:shadow-[0_6px_18px_-6px_rgba(221,242,71,0.5)]",
  ghost: "bg-white text-ink border-cd-border hover:bg-[#FAFAFA] hover:border-[#DCDCDC]",
  soft: "bg-[#F5F5F5] text-ink border-transparent hover:bg-[#EFEFEF]",
} as const;

export function btn(
  variant: keyof typeof btnVariant = "lime",
  size: keyof typeof btnSize = "md",
  extra = "",
) {
  return `${btnBase} ${btnSize[size]} ${btnVariant[variant]} ${extra}`.trim();
}

/* --- Eyebrows --- */
// tokens.css .eyebrow
export const eyebrow = "font-mono font-medium text-[11px] tracking-[0.1em] uppercase text-t3";
// landing.css .sec-eyebrow (with lime square ::before)
export const secEyebrow =
  "font-mono text-[11px] tracking-[0.08em] uppercase text-t3 flex items-center gap-[9px] mb-[22px] " +
  "before:content-[''] before:w-[7px] before:h-[7px] before:bg-lime before:rounded-[1px]";

/* --- Scroll reveal ---
   Hidden initial state + smooth fade/rise. The central IntersectionObserver in
   HomePage adds `.reveal-in` when the element enters the viewport. Reduced-motion
   users skip the animation entirely (content visible immediately). Stagger items
   within a group with `style={revealDelay(ms)}`. */
export const reveal =
  "opacity-0 [transform:translateY(22px)] transition-[opacity,transform] duration-[800ms] " +
  "ease-[cubic-bezier(0.16,1,0.3,1)] [&.reveal-in]:opacity-100 [&.reveal-in]:[transform:translateY(0)] " +
  "motion-reduce:opacity-100 motion-reduce:[transform:none] motion-reduce:transition-none";

export const revealDelay = (ms: number): CSSProperties => ({ transitionDelay: `${ms}ms` });

/* --- Feature-card pills (tokens.css .pill + the #features / .dark overrides) ---
   On light feature cards every pill is monochrome; only `confirmed` gets a lime dot.
   On the dark card, confirmed + pending get tinted treatments. */
type PillVariant = "confirmed" | "pending" | "waitlisted" | "failed" | "neutral";
// size keys encode the exact font-size + padding combinations used on the page
type PillSize = "base" | "row" | "ten" | "rowTen";

const pillSize: Record<PillSize, string> = {
  base: "text-xs py-[5px] pl-[8px] pr-[10px]", // top-level .pill (12px)
  row: "text-[9.5px] py-[4px] pl-[7px] pr-[8px]", // .mini-row .pill (9.5px)
  ten: "text-[10px] py-[5px] pl-[8px] pr-[10px]", // inline fontSize:10 on a non-mini-row pill
  rowTen: "text-[10px] py-[4px] pl-[7px] pr-[8px]", // inline fontSize:10 inside a mini-row
};

export function FeaturePill({
  variant = "neutral",
  dark = false,
  size = "base",
  children,
}: {
  variant?: PillVariant;
  dark?: boolean;
  size?: PillSize;
  children: ReactNode;
}) {
  let box: string;
  let dot: string;
  if (dark) {
    if (variant === "confirmed") {
      box = "bg-[rgba(221,242,71,0.14)] text-[#c9da2e] border-[rgba(221,242,71,0.22)]";
      dot = "bg-lime";
    } else if (variant === "pending") {
      box = "bg-[rgba(255,255,255,0.07)] text-[rgba(255,255,255,0.44)] border-[rgba(255,255,255,0.09)]";
      dot = "bg-[#9A9A9A]";
    } else {
      box = "bg-[rgba(255,255,255,0.08)] text-[rgba(255,255,255,0.6)] border-[rgba(255,255,255,0.1)]";
      dot = "bg-[#9A9A9A]";
    }
  } else {
    box = "bg-[#F5F5F5] text-t2 border-cd-border";
    dot = variant === "confirmed" ? "bg-[#C2DA1E]" : "bg-[#9A9A9A]";
  }
  return (
    <span
      className={`inline-flex items-center gap-[6px] font-sans font-semibold leading-none rounded-full whitespace-nowrap border ${pillSize[size]} ${box}`}
    >
      <span className={`w-[6px] h-[6px] rounded-full ${dot}`} />
      {children}
    </span>
  );
}
