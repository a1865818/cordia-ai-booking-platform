import { btn, reveal, revealDelay, shell } from "./atoms";
import { CheckIcon } from "./icons";

// crop-mark corner (old .cm). pos selects which corner.
function CropMark({ pos }: { pos: "tl" | "tr" | "bl" | "br" }) {
  const corner = {
    tl: "left-[-4px] top-[-4px]",
    tr: "right-[-4px] top-[-4px]",
    bl: "left-[-4px] bottom-[-4px]",
    br: "right-[-4px] bottom-[-4px]",
  }[pos];
  return (
    <span
      className={`absolute w-2 h-2 z-[4] ${corner} before:content-[''] before:absolute before:left-[3.5px] before:top-0 before:w-px before:h-2 before:bg-ink after:content-[''] after:absolute after:top-[3.5px] after:left-0 after:h-px after:w-2 after:bg-ink`}
    />
  );
}

export function HeroSection() {
  return (
    <section className="pt-16 pb-9 max-[640px]:pt-10 max-[640px]:pb-7">
      <div className={shell}>
        <div data-reveal className={`${reveal} inline-flex items-center gap-[10px] mb-10 flex-wrap max-[640px]:mb-7`}>
          <span className="font-mono text-[11px] tracking-[0.07em] uppercase text-t3">Bookings captured by Cordia this month</span>
          <span className="font-mono text-[12px] font-semibold text-ink bg-[#F5F5F5] border border-cd-border px-[9px] py-[3px] rounded-md" id="heroStat">128,940</span>
        </div>
        <h1 data-reveal style={revealDelay(90)} className={`${reveal} font-sans font-bold text-[80px] tracking-[-0.04em] leading-[0.96] max-w-[13ch] max-[980px]:text-[56px] max-[640px]:text-[40px] max-[640px]:max-w-none max-[640px]:leading-none max-[480px]:text-[32px]`}>
          Never miss a booking.
        </h1>
        <p data-reveal style={revealDelay(180)} className={`${reveal} text-[22px] text-t2 mt-7 max-w-[30em] leading-[1.4] font-normal max-[980px]:text-[19px] max-[640px]:text-[17px] max-[640px]:mt-[18px] max-[640px]:max-w-none max-[480px]:text-[16px]`}>
          Your AI receptionist answers every call and message, books the slot, and updates your customers —{" "}
          <a href="#features" className="text-ink underline underline-offset-[3px] decoration-1">24/7</a>, in the blink of AI.
        </p>

        <div id="start" data-reveal style={revealDelay(270)} className={reveal}>
          <form
            className="flex items-center gap-[6px] mt-9 bg-[#F5F5F5] border border-cd-border rounded-xl p-[6px] max-w-[470px] transition-[border-color,box-shadow] duration-150 focus-within:border-ink focus-within:shadow-[0_0_0_4px_rgba(10,10,10,0.05)] max-[640px]:flex-col max-[640px]:items-stretch max-[640px]:max-w-none"
            onSubmit={(e) => e.preventDefault()}
          >
            <input
              type="email"
              placeholder="What's your work email?"
              aria-label="Work email"
              className="flex-1 border-none bg-transparent px-[14px] py-3 font-sans text-[15px] text-ink focus:outline-none placeholder:text-t3"
            />
            <button className={btn("lime", "md", "max-[640px]:w-full")} type="submit">Start for free</button>
          </form>
        </div>

        {/* risk reversal — kills form anxiety at the point of decision */}
        <div data-reveal style={revealDelay(330)} className={`${reveal} mt-3 flex items-center gap-x-4 gap-y-1.5 flex-wrap font-mono text-[11px] tracking-[0.02em] text-t2`}>
          <span className="inline-flex items-center gap-1.5"><span className="text-ink"><CheckIcon size={12} /></span>No credit card required</span>
          <span className="inline-flex items-center gap-1.5"><span className="text-ink"><CheckIcon size={12} /></span>No setup fees</span>
          <span className="inline-flex items-center gap-1.5"><span className="text-ink"><CheckIcon size={12} /></span>Cancel anytime</span>
        </div>

        <div data-reveal style={revealDelay(360)} className={`${reveal} flex items-center gap-6 mt-5 flex-wrap`}>
          <a href="#demo" className={btn("ghost", "md", "group max-[640px]:w-full")}>
            Book a 20-min demo{" "}
            <svg className="transition-transform duration-[180ms] ease-[cubic-bezier(0.22,1,0.36,1)] group-hover:translate-x-[3px]" width="16" height="16" viewBox="0 0 24 24" fill="none">
              <path d="M5 12h14M13 6l6 6-6 6" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </a>
        </div>

        <div data-reveal style={revealDelay(450)} className={`${reveal} flex items-center gap-[10px] mt-[30px] flex-wrap text-t3 font-mono text-[11px] tracking-[0.05em] uppercase max-[640px]:mt-[18px]`}>
          <span>Answers in 1 ring</span>
          <span className="text-[#DCDCDC]">/</span>
          <span>Books 24/7</span>
          <span className="text-[#DCDCDC]">/</span>
          <span>No app for your customers</span>
        </div>

        {/* Hero stage */}
        <div
          id="heroVisual"
          data-reveal
          style={revealDelay(220)}
          className="mt-14 bg-[#F5F5F5] border border-cd-border rounded-2xl h-[480px] relative overflow-hidden opacity-0 [transform:translateY(22px)] transition-[opacity,transform] duration-[800ms] ease-[cubic-bezier(0.16,1,0.3,1)] [&.reveal-in]:opacity-100 [&.reveal-in]:[transform:translateY(0)] motion-reduce:opacity-100 motion-reduce:[transform:none] motion-reduce:transition-none max-[980px]:h-[440px] max-[860px]:h-auto max-[860px]:p-5 max-[640px]:mt-10 before:content-[''] before:absolute before:inset-0 before:[background-image:radial-gradient(#DCDCDC_1px,transparent_1px)] before:[background-size:22px_22px] before:opacity-70"
        >
          <div className="absolute top-5 left-[22px] font-mono text-[11px] tracking-[0.07em] uppercase text-t3 flex items-center gap-2 z-[5] max-[860px]:static max-[860px]:mb-4">
            <span className="text-ink">
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none">
                <path d="M3 5.5C3 4.7 3.7 4 4.5 4h2.6c.6 0 1.1.4 1.3 1l1 3c.1.5 0 1-.4 1.3L7.5 11c1 2 2.5 3.5 4.5 4.5l1.7-1.5c.3-.4.8-.5 1.3-.4l3 1c.6.2 1 .7 1 1.3v2.6c0 .8-.7 1.5-1.5 1.5C9.8 20 4 14.2 4 6.5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>{" "}
            Incoming call → confirmed booking
          </div>

          <div className="chat-panel absolute left-1/2 top-1/2 [transform:translate(-116%,-50%)] w-[352px] bg-white border border-cd-border rounded-xl shadow-[0_12px_32px_-10px_rgba(10,10,10,0.12)] p-4 opacity-0 max-[860px]:static max-[860px]:w-full max-[860px]:[transform:none]">
            <div className="flex items-center gap-[11px] pb-[13px] border-b border-hair mb-[13px]">
              <span className="w-[34px] h-[34px] rounded-lg bg-ink grid place-items-center text-white shrink-0">
                <svg width="19" height="19" viewBox="0 0 24 24" fill="none">
                  <path d="M12 3a4 4 0 0 1 4 4v3a4 4 0 1 1-8 0V7a4 4 0 0 1 4-4Z" fill="#fff" />
                  <path d="M6 18c0-2.2 2.7-3.5 6-3.5s6 1.3 6 3.5" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" />
                </svg>
              </span>
              <div className="flex flex-col gap-[3px]">
                <div className="font-sans font-bold text-[14px]">Cordia</div>
                <div className="font-mono text-[10.5px] text-t2 flex items-center gap-[5px] tracking-[0.02em]">
                  <span className="w-[6px] h-[6px] rounded-full bg-confirmed-accent animate-[livepulse_2s_infinite]" /> On a call · Vela Salon
                </div>
              </div>
              <span className="ml-auto font-mono text-[9.5px] font-semibold text-t2 bg-[#F5F5F5] border border-cd-border px-2 py-1 rounded-[5px] tracking-[0.05em]">PHONE</span>
            </div>
            <div className="flex flex-col gap-[9px] min-h-[280px] max-[640px]:min-h-[180px]" id="thread"></div>
          </div>

          <div className="book-card absolute left-1/2 top-1/2 [transform:translate(12%,-50%)] w-[300px] bg-white border border-cd-border rounded-xl shadow-[0_24px_56px_-16px_rgba(10,10,10,0.16)] p-[18px] opacity-0 max-[860px]:hidden" id="bookCard">
            <CropMark pos="tl" />
            <CropMark pos="tr" />
            <CropMark pos="bl" />
            <CropMark pos="br" />
            <div className="font-mono text-[10px] tracking-[0.07em] uppercase text-t3 mb-[14px] flex items-center justify-between">
              <span>Booking confirmed</span>
              <span>#VS-4471</span>
            </div>
            <div className="flex items-center gap-[10px] mb-[14px]">
              <span className="w-[30px] h-[30px] rounded-[7px] bg-lime grid place-items-center text-ink shrink-0">
                <svg width="17" height="17" viewBox="0 0 24 24" fill="none">
                  <path d="M20 6 9 17l-5-5" stroke="#0A0A0A" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </span>
              <div>
                <div className="font-sans font-bold text-[15px]">Balayage + cut</div>
                <div className="font-mono text-[10.5px] text-t3 mt-[2px]">Added to calendar · SMS sent</div>
              </div>
            </div>
            <div className="flex items-center gap-3 p-3 bg-[#F5F5F5] border border-hair rounded-[10px] mb-3">
              <div className="w-[46px] h-[50px] rounded-lg bg-ink text-white grid place-items-center text-center shrink-0">
                <span className="font-mono font-bold text-[18px] leading-none">14</span>
                <span className="font-mono text-[9px] tracking-[0.07em] opacity-70 mt-[3px]">FRI</span>
              </div>
              <div>
                <div className="font-sans font-bold text-[14px]">2:30 PM · with Maya</div>
                <div className="font-mono text-[11px] text-t2 mt-[2px]">2h · Jordan Bell</div>
              </div>
            </div>
            <div className="flex gap-[7px] flex-wrap">
              <span className="font-mono text-[10px] font-medium text-t2 bg-[#F5F5F5] border border-hair px-2 py-1 rounded-[5px] inline-flex items-center gap-[5px] tracking-[0.02em]">
                <svg className="text-confirmed-accent" width="11" height="11" viewBox="0 0 24 24" fill="none">
                  <path d="M20 6 9 17l-5-5" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" />
                </svg>{" "}
                CRM updated
              </span>
              <span className="font-mono text-[10px] font-medium text-t2 bg-[#F5F5F5] border border-hair px-2 py-1 rounded-[5px] inline-flex items-center gap-[5px] tracking-[0.02em]">
                <svg className="text-confirmed-accent" width="11" height="11" viewBox="0 0 24 24" fill="none">
                  <path d="M20 6 9 17l-5-5" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" />
                </svg>{" "}
                Reminder set
              </span>
            </div>
          </div>

          <div className="float-chip sms absolute bottom-10 right-[10%] bg-white border border-cd-border rounded-[10px] shadow-[0_4px_14px_-4px_rgba(10,10,10,0.08)] px-[13px] py-2 font-mono text-[10.5px] tracking-[0.03em] uppercase flex items-center gap-2 z-[6] text-ink max-[860px]:hidden">
            <span className="text-[#0F7C3E]">
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none">
                <path d="M20 6 9 17l-5-5" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>
            Texted to Jordan
          </div>
        </div>
      </div>

      {/* Stat ticker */}
      <div className="ticker border-t border-b border-cd-border mt-16 max-[640px]:mt-11">
        <div className={`${shell} flex items-stretch flex-wrap max-[640px]:flex-col`}>
          <div className="font-mono text-[11px] tracking-[0.07em] uppercase text-ink flex items-center gap-[9px] pt-[18px] pr-[26px] pb-[18px] pl-0 max-[640px]:py-[14px] max-[640px]:px-0">
            <span className="w-[7px] h-[7px] rounded-full bg-[#C2DA1E] animate-[livepulse_1.4s_infinite] shrink-0" /> Cordia agents at work right now
          </div>
          <div className="py-[14px] px-[26px] border-l border-cd-border flex flex-col justify-center max-[640px]:border-l-0 max-[640px]:border-t max-[640px]:border-cd-border max-[640px]:py-3 max-[640px]:px-0">
            <span className="font-mono text-[9.5px] tracking-[0.06em] uppercase text-t3">Calls answered today</span>
            <span className="v font-mono text-[19px] font-semibold text-ink mt-[3px]" data-count="2841">2,841</span>
          </div>
          <div className="py-[14px] px-[26px] border-l border-cd-border flex flex-col justify-center max-[640px]:border-l-0 max-[640px]:border-t max-[640px]:border-cd-border max-[640px]:py-3 max-[640px]:px-0">
            <span className="font-mono text-[9.5px] tracking-[0.06em] uppercase text-t3">Messages handled</span>
            <span className="v font-mono text-[19px] font-semibold text-ink mt-[3px]" data-count="6190">6,190</span>
          </div>
          <div className="py-[14px] px-[26px] border-l border-cd-border flex flex-col justify-center max-[640px]:border-l-0 max-[640px]:border-t max-[640px]:border-cd-border max-[640px]:py-3 max-[640px]:px-0">
            <span className="font-mono text-[9.5px] tracking-[0.06em] uppercase text-t3">Slots auto-booked</span>
            <span className="v font-mono text-[19px] font-semibold text-ink mt-[3px]" data-count="1204">1,204</span>
          </div>
          <div className="py-[14px] px-[26px] border-l border-cd-border flex flex-col justify-center max-[640px]:border-l-0 max-[640px]:border-t max-[640px]:border-cd-border max-[640px]:py-3 max-[640px]:px-0">
            <span className="font-mono text-[9.5px] tracking-[0.06em] uppercase text-t3">Waitlist filled</span>
            <span className="v font-mono text-[19px] font-semibold text-ink mt-[3px]" data-count="318">318</span>
          </div>
          <div className="py-[14px] px-[26px] border-l border-cd-border flex flex-col justify-center max-[640px]:border-l-0 max-[640px]:border-t max-[640px]:border-cd-border max-[640px]:py-3 max-[640px]:px-0">
            <span className="font-mono text-[9.5px] tracking-[0.06em] uppercase text-t3">Avg. answer time</span>
            <span className="v font-mono text-[19px] font-semibold text-ink mt-[3px]">0.9s</span>
          </div>
        </div>
      </div>
    </section>
  );
}
