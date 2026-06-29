import { useEffect, useState } from "react";
import { btn } from "./atoms";

/**
 * Persistent conversion bar. Slides up once the hero capture (#start) has
 * scrolled out of view, and hides again at/past the final CTA (#demo) so it
 * never competes with the page's own closing form.
 */
export function StickyCTA() {
  const [show, setShow] = useState(false);

  useEffect(() => {
    const startEl = document.getElementById("start");
    const demoEl = document.getElementById("demo");
    let passedStart = false;
    let atOrPastDemo = false;
    const sync = () => setShow(passedStart && !atOrPastDemo);
    const observers: IntersectionObserver[] = [];

    if (startEl) {
      const o = new IntersectionObserver(
        ([e]) => {
          passedStart = !e.isIntersecting && e.boundingClientRect.top < 0;
          sync();
        },
        { threshold: 0 },
      );
      o.observe(startEl);
      observers.push(o);
    }
    if (demoEl) {
      const o = new IntersectionObserver(
        ([e]) => {
          atOrPastDemo = e.isIntersecting || e.boundingClientRect.top < 0;
          sync();
        },
        { threshold: 0 },
      );
      o.observe(demoEl);
      observers.push(o);
    }
    return () => observers.forEach((o) => o.disconnect());
  }, []);

  return (
    <div
      aria-hidden={!show}
      className={`fixed inset-x-0 bottom-0 z-[60] px-8 pb-4 max-[640px]:px-3 max-[640px]:pb-3 transition-[transform,opacity] duration-500 ease-[cubic-bezier(0.16,1,0.3,1)] motion-reduce:transition-none ${
        show ? "translate-y-0 opacity-100" : "translate-y-[140%] opacity-0 pointer-events-none"
      }`}
    >
      <div className="mx-auto max-w-[1240px] flex items-center gap-4 bg-[rgba(255,255,255,0.85)] backdrop-saturate-[1.8] backdrop-blur-[14px] border border-cd-border rounded-2xl shadow-[0_16px_50px_-16px_rgba(10,10,10,0.25)] pl-5 pr-3 py-[11px] max-[640px]:pl-4 max-[640px]:pr-[10px]">
        <div className="flex items-center gap-3 min-w-0 max-[860px]:hidden">
          <span className="w-9 h-9 rounded-[9px] bg-ink grid place-items-center shrink-0">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
              <path d="M5 6.5C5 5.4 5.9 4.5 7 4.5h10c1.1 0 2 .9 2 2v7c0 1.1-.9 2-2 2H10l-4 3.2c-.5.4-1.2 0-1.2-.6V6.5Z" fill="#fff" />
              <circle cx="9" cy="10" r="1.2" fill="#0A0A0A" />
              <circle cx="12.5" cy="10" r="1.2" fill="#0A0A0A" />
              <circle cx="16" cy="10" r="1.2" fill="#0A0A0A" />
            </svg>
          </span>
          <div className="min-w-0">
            <div className="font-sans font-bold text-[14px] tracking-[-0.01em] text-ink leading-tight truncate">Never miss a booking.</div>
            <div className="font-mono text-[10.5px] text-t2 truncate">No credit card · Cancel anytime</div>
          </div>
        </div>
        <form
          onSubmit={(e) => e.preventDefault()}
          className="flex items-center gap-2 ml-auto max-[860px]:ml-0 max-[860px]:w-full"
        >
          <input
            type="email"
            placeholder="Your work email"
            aria-label="Work email"
            className="w-[220px] max-[980px]:w-[170px] max-[860px]:flex-1 max-[860px]:w-auto border border-cd-border bg-white rounded-lg px-[13px] py-[9px] font-sans text-[14px] text-ink focus:outline-none focus:border-ink placeholder:text-t3 transition-colors duration-150 max-[420px]:hidden"
          />
          <button type="submit" className={btn("lime", "md", "whitespace-nowrap max-[420px]:flex-1")}>Start for free</button>
        </form>
      </div>
    </div>
  );
}
