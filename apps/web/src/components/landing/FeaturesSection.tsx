import type { CSSProperties } from "react";
import { ArrowIcon } from "./icons";
import { btn, FeaturePill, reveal, secEyebrow, sectionCls, shell } from "./atoms";

/* ---- card shells ---- */
const cardShape =
  "group flex-1 rounded-[20px] p-[26px] relative overflow-hidden flex flex-col min-h-[340px] border " +
  "transition-[transform,box-shadow,border-color] duration-[350ms] ease-[cubic-bezier(0.16,1,0.3,1)] hover:-translate-y-[3px] max-[640px]:min-h-0 max-[480px]:p-5";
const cardLight =
  "bg-white border-cd-border shadow-[0_1px_2px_rgba(10,10,10,0.04),0_4px_14px_-4px_rgba(10,10,10,0.07)] hover:border-[#DCDCDC] hover:shadow-[0_14px_40px_-10px_rgba(10,10,10,0.14),0_4px_14px_-6px_rgba(10,10,10,0.08)]";
const cardDark =
  "bg-ink border-[rgba(255,255,255,0.07)] text-white shadow-[0_20px_64px_-16px_rgba(0,0,0,0.55),0_6px_20px_-6px_rgba(0,0,0,0.3)] hover:border-[rgba(255,255,255,0.14)] hover:shadow-[0_28px_80px_-16px_rgba(0,0,0,0.65),0_8px_24px_-6px_rgba(0,0,0,0.35)]";

const arrowBase =
  "card-arrow absolute top-5 right-5 w-[30px] h-[30px] border rounded-lg grid place-items-center z-[3] " +
  "transition-[background-color,color,border-color,transform] duration-150 ease-[cubic-bezier(0.22,1,0.36,1)] group-hover:[transform:translate(2px,-2px)]";
const arrowLight =
  "border-cd-border text-t3 bg-white group-hover:bg-ink group-hover:text-white group-hover:border-ink";
const arrowDark =
  "border-[rgba(255,255,255,0.11)] text-[rgba(255,255,255,0.65)] bg-[rgba(255,255,255,0.08)] group-hover:bg-[rgba(255,255,255,0.18)] group-hover:border-[rgba(255,255,255,0.22)] group-hover:text-white";

const cardTitle =
  "font-sans font-bold text-[21px] tracking-[-0.028em] leading-[1.13] max-w-[22ch] mb-[7px] text-balance max-[640px]:text-[19px]";
const cardSubLight = "text-[13.5px] text-t2 leading-[1.42] max-w-[32ch]";
const cardSubDark = "text-[13.5px] text-[rgba(255,255,255,0.44)] leading-[1.42] max-w-[32ch]";
const stageLight = "mt-auto pt-6 relative border-t border-hair";
const stageDark = "mt-auto pt-6 relative border-t border-t-[rgba(255,255,255,0.07)]";
const dotsLight =
  "before:content-[''] before:absolute before:[inset:24px_-26px_-26px_-26px] before:[background-image:radial-gradient(#DCDCDC_1px,transparent_1px)] before:[background-size:20px_20px] before:opacity-20 before:pointer-events-none";
const dotsDark =
  "before:content-[''] before:absolute before:[inset:24px_-26px_-26px_-26px] before:[background-image:radial-gradient(rgba(255,255,255,0.09)_1px,transparent_1px)] before:[background-size:20px_20px] before:opacity-100 before:pointer-events-none";

/* ---- mini-row pieces ---- */
const rt = "ml-auto text-right flex-none";
const nmLight = "font-sans font-semibold text-[13px] tracking-[-0.01em] text-ink truncate";
const mtLight = "font-mono text-[10px] tracking-[0.01em] text-t3 mt-[2px] whitespace-nowrap overflow-hidden text-ellipsis";
const nmDark = "font-sans font-semibold text-[13px] tracking-[-0.01em] text-white truncate";
const mtDark = "font-mono text-[10px] tracking-[0.01em] text-[rgba(255,255,255,0.38)] mt-[2px] whitespace-nowrap overflow-hidden text-ellipsis";
const miniRowDark =
  "flex items-center gap-[11px] px-[13px] py-[10px] bg-[rgba(255,255,255,0.055)] border border-[rgba(255,255,255,0.09)] rounded-[12px] mb-2 relative z-[1] shadow-[0_1px_3px_rgba(0,0,0,0.22)]";
const logoTileDark =
  "w-8 h-8 rounded-full bg-[rgba(255,255,255,0.09)] border border-[rgba(255,255,255,0.13)] text-white grid place-items-center shrink-0 overflow-hidden";
// top-bento (card2) mini-rows: padding 10/13, av 32
const miniRowTop = "flex items-center gap-[11px] px-[13px] py-[10px] bg-white border border-cd-border rounded-[12px] mb-2 relative z-[1] shadow-[0_1px_2px_rgba(10,10,10,0.04)]";
const avTop = "w-8 h-8 rounded-full grid place-items-center font-sans font-semibold text-[11px] tracking-[0.01em] text-ink bg-[#F5F5F5] border border-cd-border shrink-0";
// row3 mini-rows: padding 9/11, av 30
const miniRowRow3 = "flex items-center gap-[11px] px-[11px] py-[9px] bg-white border border-cd-border rounded-[12px] mb-2 relative z-[1] shadow-[0_1px_2px_rgba(10,10,10,0.04)]";
const avRow3 = "w-[30px] h-[30px] rounded-full grid place-items-center font-sans font-semibold text-[11px] tracking-[0.01em] text-ink bg-[#F5F5F5] border border-cd-border shrink-0";

/* Whole-card stretched link — makes the entire card a single tap target.
   Inset focus ring so it stays visible inside the card's overflow-hidden. */
const cardLink =
  "absolute inset-0 z-[4] rounded-[20px] cursor-pointer " +
  "focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-lime";

/* One proof metric per card — sits under the subtitle. Replace the numbers
   with real values from analytics before launch. */
function ProofStat({ value, label, dark = false }: { value: string; label: string; dark?: boolean }) {
  return (
    <div
      className={
        "mt-[10px] flex items-center gap-[7px] font-mono text-[11px] tracking-[0.01em] " +
        (dark ? "text-[rgba(255,255,255,0.5)]" : "text-t2")
      }
    >
      <span className="w-[5px] h-[5px] rounded-full bg-lime shrink-0" />
      <span
        className={
          "font-sans font-bold tabular-nums text-[13px] tracking-[-0.02em] " +
          (dark ? "text-white" : "text-ink")
        }
      >
        {value}
      </span>
      <span className="min-w-0 truncate">{label}</span>
    </div>
  );
}

export function FeaturesSection() {
  return (
    <section className={sectionCls} id="features">
      <div className={shell}>
        <div data-reveal className={`${reveal} flex items-end justify-between gap-6 mb-[56px] flex-wrap max-[768px]:flex-col max-[768px]:items-start`}>
          <div className="max-w-[680px]">
            <div className={secEyebrow}>The platform</div>
            <h2 className="font-sans font-bold text-[46px] tracking-[-0.035em] leading-[1.02] max-[980px]:text-[34px] max-[640px]:text-[28px]">
              One agent for your entire front desk. <span className="text-t3">Always on, never misses.</span>
            </h2>
          </div>
          <div className="flex flex-col items-end gap-[10px] flex-none max-[768px]:w-full max-[768px]:items-stretch">
            <div className="flex gap-3 max-[768px]:w-full max-[768px]:gap-2 max-[480px]:flex-col">
              <a href="#demo" className={btn("lime", "md", "max-[768px]:flex-1")}>Start for free</a>
              <a href="#how" className={btn("ghost", "md", "max-[768px]:flex-1")}>Explore platform</a>
            </div>
            <div className="flex items-center gap-[7px] font-mono text-[11px] tracking-[0.02em] text-t3 max-[768px]:justify-center">
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" className="text-lime shrink-0" aria-hidden>
                <path d="M20 6 9 17l-5-5" />
              </svg>
              No card required · live in a day
            </div>
          </div>
        </div>

        {/* Top row */}
        <div className="grid grid-cols-2 gap-4 max-[640px]:grid-cols-1">
          {/* Card 1 — dark: voice + social booking */}
          <div className="reveal-wrap flex min-w-0 opacity-0">
            <div className="parallax flex-1 flex min-w-0 [transform:translate3d(var(--px,0px),var(--py,0px),0)] transition-transform duration-500 ease-[cubic-bezier(0.16,1,0.3,1)]" data-depth="0.7">
              <div className="float-layer flex-1 flex min-w-0" style={{ "--dur": "9s", "--fd": "0s", "--rot": "1.5deg", "--fl": "-7px" } as CSSProperties}>
                <div className={`${cardShape} ${cardDark}`}>
                  <a href="#demo" className={cardLink} aria-label="Voice and social booking on every channel" />
                  <span aria-hidden className={`${arrowBase} ${arrowDark}`}><ArrowIcon /></span>
                  <div className={cardTitle}><b>Voice + social booking</b> <span className="text-[rgba(255,255,255,0.36)]">on every channel.</span></div>
                  <div className={cardSubDark}>One agent across phone, WhatsApp, Instagram, SMS &amp; web.</div>
                  <ProofStat dark value="1,240" label="bookings captured last month" />
                  <div className={`${stageDark} ${dotsDark}`}>
                    <div className={miniRowDark}>
                      <div className={logoTileDark}>
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
                          <path d="M3 5.5C3 4.7 3.7 4 4.5 4h2.6c.6 0 1.1.4 1.3 1l1 3c.1.5 0 1-.4 1.3L7.5 11c1 2 2.5 3.5 4.5 4.5l1.7-1.5c.3-.4.8-.5 1.3-.4l3 1c.6.2 1 .7 1 1.3v2.6c0 .8-.7 1.5-1.5 1.5C9.8 20 4 14.2 4 6.5" />
                        </svg>
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className={nmDark}>Phone · Jordan B.</div>
                        <div className={mtDark}>"Friday balayage with Maya?"</div>
                      </div>
                      <div className={rt}><FeaturePill variant="confirmed" dark size="row">Booked</FeaturePill></div>
                    </div>
                    <div className={miniRowDark}>
                      <div className={logoTileDark}>
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                          <path d="M12.04 2C6.58 2 2.13 6.45 2.13 11.91c0 1.75.46 3.46 1.32 4.96L2 22l5.25-1.38a9.86 9.86 0 0 0 4.79 1.22h.01c5.46 0 9.91-4.45 9.91-9.91 0-2.65-1.03-5.14-2.9-7.01A9.82 9.82 0 0 0 12.04 2Zm5.8 14.16c-.25.69-1.44 1.32-1.97 1.36-.5.05-.5.41-3.16-.66-2.67-1.07-4.35-3.8-4.48-3.98-.13-.18-1.07-1.43-1.07-2.72 0-1.3.68-1.93.92-2.2.25-.26.54-.33.72-.33l.52.01c.17 0 .39-.06.61.47.25.6.81 2.07.88 2.22.07.15.12.32.02.5-.09.18-.14.29-.27.45-.14.15-.29.34-.41.46-.14.13-.28.28-.12.55.16.27.71 1.18 1.53 1.91 1.05.94 1.94 1.23 2.21 1.37.27.13.43.11.59-.07.16-.18.68-.79.86-1.06.18-.27.36-.23.61-.14.25.09 1.6.76 1.87.9.27.13.45.2.52.31.07.11.07.64-.18 1.32Z" />
                        </svg>
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className={nmDark}>WhatsApp · Priya N.</div>
                        <div className={mtDark}>"Table for 4 at 8pm Sat?"</div>
                      </div>
                      <div className={rt}><FeaturePill variant="confirmed" dark size="row">Booked</FeaturePill></div>
                    </div>
                    <div className={`${miniRowDark} mb-0`}>
                      <div className={logoTileDark}>
                        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                          <rect x="2.5" y="2.5" width="19" height="19" rx="5.5" />
                          <circle cx="12" cy="12" r="4.2" />
                          <circle cx="17.4" cy="6.6" r="1.2" fill="currentColor" stroke="none" />
                        </svg>
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className={nmDark}>Instagram · @leoruns</div>
                        <div className={mtDark}>"Lawn quote for next week"</div>
                      </div>
                      <div className={rt}><FeaturePill variant="pending" dark size="row">Pending</FeaturePill></div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Card 2 — light: waitlist */}
          <div className="reveal-wrap flex min-w-0 opacity-0">
            <div className="parallax flex-1 flex min-w-0 [transform:translate3d(var(--px,0px),var(--py,0px),0)] transition-transform duration-500 ease-[cubic-bezier(0.16,1,0.3,1)]" data-depth="1.1">
              <div className="float-layer flex-1 flex min-w-0" style={{ "--dur": "11s", "--fd": ".6s", "--rot": "1.1deg", "--fl": "-6px" } as CSSProperties}>
                <div className={`${cardShape} ${cardLight}`}>
                  <a href="#demo" className={cardLink} aria-label="A waitlist that fills itself" />
                  <span aria-hidden className={`${arrowBase} ${arrowLight}`}><ArrowIcon /></span>
                  <div className={cardTitle}><b>A waitlist</b> <span className="text-t3">that fills itself.</span></div>
                  <div className={cardSubLight}>A slot opens — Cordia re-books the best-matched customer.</div>
                  <ProofStat value="312" label="slots auto-rebooked last month" />
                  <div className={`${stageLight} ${dotsLight}`}>
                    <div className="flex items-center gap-[9px] mb-3 relative z-[1] flex-wrap">
                      <FeaturePill variant="failed" size="base">2:30 cancelled</FeaturePill>
                      <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
                        <path d="M5 12h14M13 6l6 6-6 6" stroke="#9A9A9A" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                      </svg>
                      <FeaturePill variant="waitlisted" size="base">3 matched</FeaturePill>
                    </div>
                    <div className={miniRowTop}>
                      <div className={avTop}>AR</div>
                      <div className="min-w-0 flex-1">
                        <div className={nmLight}>Amara R.</div>
                        <div className={mtLight}>Balayage · Friday PM · Maya</div>
                      </div>
                      <div className={rt}><FeaturePill variant="confirmed" size="row">Auto-booked</FeaturePill></div>
                    </div>
                    <div className={miniRowTop}>
                      <div className={avTop}>DK</div>
                      <div className="min-w-0 flex-1">
                        <div className={nmLight}>Devin K.</div>
                        <div className={mtLight}>Cut · any afternoon</div>
                      </div>
                      <div className={rt}><FeaturePill variant="waitlisted" size="row">Next up</FeaturePill></div>
                    </div>
                    <div className={`${miniRowTop} mb-0`}>
                      <div className={avTop}>SM</div>
                      <div className="min-w-0 flex-1">
                        <div className={nmLight}>Sofia M.</div>
                        <div className={mtLight}>Color · Saturday AM</div>
                      </div>
                      <div className={rt}><FeaturePill variant="waitlisted" size="row">Waitlisted</FeaturePill></div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        {/* Bottom row */}
        <div className="grid grid-cols-3 gap-4 mt-4 max-[980px]:grid-cols-2 max-[640px]:grid-cols-1">
          {/* Customer records */}
          <div className="reveal-wrap flex min-w-0 opacity-0">
            <div className="parallax flex-1 flex min-w-0 [transform:translate3d(var(--px,0px),var(--py,0px),0)] transition-transform duration-500 ease-[cubic-bezier(0.16,1,0.3,1)]" data-depth="0.8">
              <div className="float-layer flex-1 flex min-w-0" style={{ "--dur": "7.5s", "--fd": ".2s", "--rot": "1.8deg", "--fl": "-8px" } as CSSProperties}>
                <div className={`${cardShape} ${cardLight}`}>
                  <a href="#demo" className={cardLink} aria-label="Customer records that write themselves" />
                  <span aria-hidden className={`${arrowBase} ${arrowLight}`}><ArrowIcon /></span>
                  <div className={cardTitle}><b>Customer records</b> <span className="text-t3">that write themselves.</span></div>
                  <div className={cardSubLight}>Duplicates merged, history logged — no typing.</div>
                  <ProofStat value="8,400" label="profiles merged automatically" />
                  <div className={stageLight}>
                    <div className="bg-white border border-cd-border rounded-[10px] p-[14px] shadow-[0_1px_2px_rgba(10,10,10,0.04)]">
                      <div className="flex items-center gap-[11px] mb-3">
                        <div className="w-[38px] h-[38px] rounded-full bg-[#F5F5F5] border border-cd-border grid place-items-center text-ink font-sans font-semibold text-[12px] shrink-0">JB</div>
                        <div className="flex-1 min-w-0">
                          <div className="font-sans font-bold text-[14px]">Jordan Bell</div>
                          <div className="font-mono text-[10.5px] text-t2">Since 2023 · 14 visits</div>
                        </div>
                        <FeaturePill variant="waitlisted" size="ten">Matched 2</FeaturePill>
                      </div>
                      <div className="flex gap-2">
                        <div className="flex-1 bg-[#F5F5F5] border border-hair rounded-lg p-[9px]">
                          <div className="font-mono text-[9px] tracking-[0.04em] uppercase text-t3">LTV</div>
                          <div className="font-mono tabular-nums tracking-[-0.01em] font-semibold text-[16px]">$1,840</div>
                        </div>
                        <div className="flex-1 bg-[#F5F5F5] border border-hair rounded-lg p-[9px]">
                          <div className="font-mono text-[9px] tracking-[0.04em] uppercase text-t3">Cadence</div>
                          <div className="font-mono tabular-nums tracking-[-0.01em] font-semibold text-[16px]">6 wks</div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Campaigns */}
          <div className="reveal-wrap flex min-w-0 opacity-0">
            <div className="parallax flex-1 flex min-w-0 [transform:translate3d(var(--px,0px),var(--py,0px),0)] transition-transform duration-500 ease-[cubic-bezier(0.16,1,0.3,1)]" data-depth="1.25">
              <div className="float-layer flex-1 flex min-w-0" style={{ "--dur": "12s", "--fd": ".4s", "--rot": "1.3deg", "--fl": "-5px" } as CSSProperties}>
                <div className={`${cardShape} ${cardLight}`}>
                  <a href="#demo" className={cardLink} aria-label="Campaigns that draft themselves" />
                  <span aria-hidden className={`${arrowBase} ${arrowLight}`}><ArrowIcon /></span>
                  <div className={cardTitle}><b>Campaigns</b> <span className="text-t3">that draft themselves.</span></div>
                  <div className={cardSubLight}>Drafts for your slow days — published only on your yes.</div>
                  <ProofStat value="+23%" label="bookings on slow days" />
                  <div className={stageLight}>
                    <div className="bg-white border border-cd-border rounded-[10px] p-[14px] shadow-[0_1px_2px_rgba(10,10,10,0.04)]">
                      <div className="flex items-center gap-2 mb-[10px]">
                        <FeaturePill variant="pending" size="ten">Awaiting approval</FeaturePill>
                        <span className="font-mono tabular-nums ml-auto text-[9px] tracking-[0.04em] uppercase text-t3">IG · TUE</span>
                      </div>
                      <div className="text-[13px] leading-[1.45] mb-3 text-ink">"Midweek glow-up ✨ Book any color service this Tuesday — free gloss finish. 💚"</div>
                      <div className="flex gap-[7px]">
                        <span className={btn("lime", "sm", "pointer-events-none flex-1")}>Approve</span>
                        <span className={btn("ghost", "sm", "pointer-events-none")}>Edit</span>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Failures */}
          <div className="reveal-wrap flex min-w-0 opacity-0 max-[980px]:col-span-2 max-[640px]:col-span-1">
            <div className="parallax flex-1 flex min-w-0 [transform:translate3d(var(--px,0px),var(--py,0px),0)] transition-transform duration-500 ease-[cubic-bezier(0.16,1,0.3,1)]" data-depth="0.7">
              <div className="float-layer flex-1 flex min-w-0" style={{ "--dur": "8.5s", "--fd": ".1s", "--rot": "1.6deg", "--fl": "-7px" } as CSSProperties}>
                <div className={`${cardShape} ${cardLight}`}>
                  <a href="#demo" className={cardLink} aria-label="Failures that fix themselves" />
                  <span aria-hidden className={`${arrowBase} ${arrowLight}`}><ArrowIcon /></span>
                  <div className={cardTitle}><b>Failures</b> <span className="text-t3">that fix themselves.</span></div>
                  <div className={cardSubLight}>Auto-retries with backoff. Only the truly stuck reach you.</div>
                  <ProofStat value="99.2%" label="of errors auto-resolved" />
                  <div className={`${stageLight} ${dotsLight}`}>
                    <div className="max-[980px]:grid max-[980px]:grid-cols-2 max-[980px]:gap-2 max-[640px]:block">
                    <div className={`${miniRowRow3} max-[980px]:mb-0`}>
                      <div className={avRow3}>
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                          <path d="M21 12a9 9 0 1 1-3-6.7M21 4v5h-5" />
                        </svg>
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className={nmLight}>Retried &amp; recovered</div>
                        <div className={mtLight}>Sync → ok on retry 2</div>
                      </div>
                      <div className={rt}><FeaturePill variant="confirmed" size="rowTen">Resolved</FeaturePill></div>
                    </div>
                    <div className={`${miniRowRow3} mb-0`}>
                      <div className={`${avRow3} !bg-ink`}>
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2">
                          <path d="M12 9v4m0 4h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z" />
                        </svg>
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className={nmLight}>Needs attention</div>
                        <div className={mtLight}>Double-booked · 3×</div>
                      </div>
                      <div className={rt}><span className={btn("primary", "sm", "pointer-events-none")}>Re-book</span></div>
                    </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
