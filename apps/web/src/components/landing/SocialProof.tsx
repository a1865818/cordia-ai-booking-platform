import type { CSSProperties } from "react";
import { channelLogos } from "./brandLogos";

const ctileShadow = "shadow-[0_10px_30px_-10px_rgba(10,10,10,0.18),0_3px_10px_-4px_rgba(10,10,10,0.10)]";
// .proof-reveal — initial hidden state; JS scrubs opacity/translateY on scroll.
const reveal =
  "proof-reveal opacity-0 [transform:translateY(24px)] [will-change:opacity,transform] motion-reduce:opacity-100 motion-reduce:[transform:none]";

// Tile layout (positions + float params). Each maps 1:1 to channelLogos by index,
// so the floating marks are the real channels customers reach the business on.
const tiles = [
  { variant: "ink", smHide: false, icon: 30, style: { left: "9%", top: "20%", width: "66px", height: "66px" }, dur: 8.5, amp: 9, rot: 2.2, depth: 1.3 },
  { variant: "lime", smHide: false, icon: 28, style: { left: "14%", top: "72%", width: "60px", height: "60px" }, dur: 9.2, amp: 10, rot: 2.6, depth: 1.1 },
  { variant: "white", smHide: false, icon: 27, style: { left: "70%", top: "15%", width: "58px", height: "58px" }, dur: 9.8, amp: 8, rot: 2, depth: 1.0 },
  { variant: "white", smHide: false, icon: 26, style: { left: "87%", top: "60%", width: "56px", height: "56px" }, dur: 10.6, amp: 7, rot: -1.8, depth: 0.9 },
  { variant: "ink", smHide: false, icon: 27, style: { left: "60%", top: "82%", width: "58px", height: "58px" }, dur: 9.5, amp: 9, rot: 2.3, depth: 1.05 },
] as const;

export function SocialProof() {
  return (
    <section className="relative h-[280vh] max-[760px]:h-[240vh] max-[640px]:h-[220vh] motion-reduce:h-auto" id="proof">
      <div className="sticky top-0 h-screen overflow-hidden motion-reduce:static motion-reduce:h-auto motion-reduce:min-h-screen">
        <div className="absolute left-0 right-0 top-0 h-[110px] z-[5] [background:linear-gradient(0deg,transparent,#ffffff)] pointer-events-none"></div>
        <div className="absolute left-1/2 top-1/2 [transform:translate(-50%,-50%)] z-[6] text-center w-[90%] max-w-[940px] pointer-events-none">
          <div className={`eyebrow ${reveal} font-mono font-medium text-[11px] tracking-[0.1em] uppercase text-t3 inline-flex mb-[18px]`}>On every channel your customers use</div>
          <h2 className={`${reveal} font-sans font-bold text-[44px] tracking-[-0.035em] leading-[1.06] max-[760px]:text-[28px] max-[640px]:text-[22px]`}>Calls, texts, and DMs — answered the second they arrive.</h2>
          <div className={`proof-bignum ${reveal} font-mono font-semibold text-[clamp(64px,11vw,132px)] tracking-[-0.05em] leading-[0.9] text-[#DCDCDC] mt-2 select-none max-[640px]:text-[52px]`}>1,200+</div>
          <div className={`sub ${reveal} font-mono text-[11px] tracking-[0.08em] uppercase text-t3 mt-[14px]`}>businesses · every channel · zero missed</div>
        </div>
        <div className="absolute inset-0 z-[2] pointer-events-none" id="proofField">
          {tiles.map((t, i) => {
            const logo = channelLogos[i];
            const v =
              t.variant === "ink"
                ? "bg-ink text-white"
                : t.variant === "lime"
                  ? "bg-lime text-ink"
                  : "bg-white text-ink border border-cd-border";
            return (
              <span
                key={logo.name}
                className={`ctile absolute [transform:translate(-50%,-50%)] opacity-0 grid place-items-center rounded-[18px] ${ctileShadow} [will-change:transform,opacity] ${v} ${t.smHide ? "max-[760px]:hidden" : ""}`}
                style={t.style as CSSProperties}
                data-dur={t.dur}
                data-amp={t.amp}
                data-rot={t.rot}
                data-depth={t.depth}
              >
                <svg width={t.icon} height={t.icon} viewBox="0 0 24 24" fill="currentColor" aria-hidden>
                  <path d={logo.path} />
                </svg>
                <span className="absolute top-[calc(100%+8px)] left-1/2 [transform:translateX(-50%)] font-mono text-[10px] tracking-[0.02em] text-t3 whitespace-nowrap">{logo.name}</span>
              </span>
            );
          })}
        </div>
        <div className="absolute left-0 right-0 bottom-0 h-[110px] z-[5] [background:linear-gradient(180deg,transparent,#ffffff)] pointer-events-none"></div>
      </div>
    </section>
  );
}
