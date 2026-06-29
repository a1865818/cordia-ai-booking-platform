import { useEffect } from "react";

import { LandingNav } from "../components/landing/LandingNav";
import { HeroSection } from "../components/landing/HeroSection";
import { LogoStrip } from "../components/landing/LogoStrip";
import { SocialProof } from "../components/landing/SocialProof";
import { HowItWorks } from "../components/landing/HowItWorks";
import { FeaturesSection } from "../components/landing/FeaturesSection";
import { MetricsSection } from "../components/landing/MetricsSection";
import { PricingSection } from "../components/landing/PricingSection";
import { TestimonialsSection } from "../components/landing/TestimonialsSection";
import { FAQSection } from "../components/landing/FAQSection";
import { FinalCTA } from "../components/landing/FinalCTA";
import { SiteFooter } from "../components/landing/SiteFooter";
import { StickyCTA } from "../components/landing/StickyCTA";

export function HomePage() {
  useEffect(() => {
    const intervals: ReturnType<typeof setInterval>[] = [];
    const observers: IntersectionObserver[] = [];
    const cleanups: (() => void)[] = [];
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    // Nav scroll
    const nav = document.getElementById("nav");
    const handleScroll = () => {
      if (nav) nav.classList.toggle("scrolled", window.scrollY > 12);
    };
    window.addEventListener("scroll", handleScroll, { passive: true });
    cleanups.push(() => window.removeEventListener("scroll", handleScroll));

    // (FAQ accordion is now self-contained React state in FAQSection.)

    // Generic scroll-reveal — fade + rise for any [data-reveal] element
    // (section headers, grids, cards). Reduced-motion users see them instantly
    // via the motion-reduce: classes baked into the shared `reveal` atom.
    const revealNodes = document.querySelectorAll("[data-reveal]");
    const revealObs = new IntersectionObserver(
      (entries) => {
        entries.forEach((e) => {
          if (e.isIntersecting) {
            e.target.classList.add("reveal-in");
            revealObs.unobserve(e.target);
          }
        });
      },
      { threshold: 0.12, rootMargin: "0px 0px -8% 0px" }
    );
    revealNodes.forEach((el) => revealObs.observe(el));
    observers.push(revealObs);

    // Count-up animation
    function animateCount(el: Element, target: number, opts: { dur?: number; suffix?: string } = {}) {
      const dur = opts.dur || 1400;
      const start = performance.now();
      const fmt = (n: number) => n.toLocaleString("en-US");
      function tick(now: number) {
        const p = Math.min((now - start) / dur, 1);
        const eased = 1 - Math.pow(1 - p, 3);
        el.textContent = fmt(Math.floor(target * eased)) + (opts.suffix || "");
        if (p < 1) requestAnimationFrame(tick);
        else el.textContent = fmt(target) + (opts.suffix || "");
      }
      requestAnimationFrame(tick);
    }

    // Ticker count-up
    let tickerDone = false;
    const tickerObs = new IntersectionObserver(
      (entries) => {
        entries.forEach((e) => {
          if (e.isIntersecting && !tickerDone) {
            tickerDone = true;
            if (!reduce) {
              document.querySelectorAll(".tick .v[data-count]").forEach((el) => {
                const count = +((el as HTMLElement).dataset["count"] ?? "0");
                animateCount(el, count);
              });
            }
          }
        });
      },
      { threshold: 0.4 }
    );
    const tk = document.querySelector(".ticker");
    if (tk) tickerObs.observe(tk);
    observers.push(tickerObs);

    // Hero stat slow climb
    const heroStat = document.getElementById("heroStat");
    if (heroStat && !reduce) {
      let base = 128940;
      intervals.push(
        setInterval(() => {
          base += Math.floor(Math.random() * 4) + 1;
          heroStat.textContent = base.toLocaleString("en-US");
        }, 2600)
      );
    }

    // Mouse parallax state
    type ParallaxState = { mx: number; my: number };
    const win = window as Window & { parallaxState?: ParallaxState };
    win.parallaxState = { mx: 0, my: 0 };

    if (!reduce) {
      const handleMouseMove = (e: MouseEvent) => {
        if (win.parallaxState) {
          win.parallaxState.mx = (e.clientX / (window.innerWidth || 1) - 0.5) * 2;
          win.parallaxState.my = (e.clientY / (window.innerHeight || 1) - 0.5) * 2;
        }
      };
      window.addEventListener("mousemove", handleMouseMove, { passive: true });
      cleanups.push(() => window.removeEventListener("mousemove", handleMouseMove));
    }

    // Bento reveal + float + parallax + proof tiles
    const revealEls = [...document.querySelectorAll<HTMLElement>(".reveal-wrap")];

    if (reduce) {
      revealEls.forEach((el) => {
        el.style.opacity = "1";
        el.style.transform = "none";
      });
      document.querySelectorAll<HTMLElement>(".ctile").forEach((el) => {
        el.style.opacity = "1";
        el.style.transform = "translate(-50%,-50%) scale(1)";
      });
      document.querySelectorAll<HTMLElement>(".proof-reveal").forEach((el) => {
        el.style.opacity = "1";
        el.style.transform = "none";
      });
    } else {
      const easeOut = (t: number) => 1 - Math.pow(1 - t, 3);
      const seg = (p: number, a: number, b: number) =>
        Math.max(0, Math.min(1, (p - a) / (b - a)));
      const DUR = 760;
      const active: Array<{ el: HTMLElement; start: number }> = [];
      let order = 0;

      const floats = [...document.querySelectorAll<HTMLElement>(".float-layer")]
        .filter((el) => !el.closest("#features"))
        .map((el) => {
        const num = (v: string, d: number) => {
          const n = parseFloat(el.style.getPropertyValue(v));
          return isNaN(n) ? d : n;
        };
        return {
          el,
          dur: num("--dur", 9) * 1000,
          fd: num("--fd", 0) * 1000,
          rot: num("--rot", 1.6),
          fl: num("--fl", -7),
        };
      });

      const t0 = Date.now();
      const parallaxEls = [...document.querySelectorAll<HTMLElement>(".parallax")]
        .filter((el) => !el.closest("#features"));
      let pcx = 0, pcy = 0;

      const proofSec = document.getElementById("proof");
      const proofField = document.getElementById("proofField");
      // [element, reveal-start, reveal-end, latch]
      // latch=true → driven by monotonic progress, so it never reverses once shown
      const proofReveals: Array<[HTMLElement | null, number, number, boolean]> = proofSec
        ? [
            [proofSec.querySelector<HTMLElement>(".eyebrow"), 0.3, 0.44, true],
            [proofSec.querySelector<HTMLElement>("h2"), 0.42, 0.58, false],
            [proofSec.querySelector<HTMLElement>(".proof-bignum"), 0.56, 0.72, false],
            [proofSec.querySelector<HTMLElement>(".sub"), 0.7, 0.86, false],
          ]
        : [];
      const tiles = [...document.querySelectorAll<HTMLElement>(".ctile")].map((el) => {
        const n = (a: string, d: number) => {
          const v = parseFloat(el.dataset[a] ?? "");
          return isNaN(v) ? d : v;
        };
        return {
          el,
          dur: n("dur", 9) * 1000,
          amp: n("amp", 8),
          rot: n("rot", 2),
          depth: n("depth", 1),
          ph: Math.random() * Math.PI * 2,
          ox: 0,
          oy: 0,
        };
      });

      function measureTiles() {
        if (!proofField) return;
        const fw = proofField.offsetWidth, fh = proofField.offsetHeight;
        tiles.forEach((t) => {
          t.ox = t.el.offsetLeft + t.el.offsetWidth / 2 - fw / 2;
          t.oy = t.el.offsetTop + t.el.offsetHeight / 2 - fh / 2;
        });
      }
      measureTiles();
      window.addEventListener("resize", measureTiles, { passive: true });
      cleanups.push(() => window.removeEventListener("resize", measureTiles));

      let smoothProgress = 0; // eased scroll progress (inertia → smooth scrub)
      let maxProgress = 0;    // highest progress ever reached (latched elements)

      const tick = () => {
        const vh = window.innerHeight || document.documentElement.clientHeight;
        revealEls.forEach((el) => {
          if (el.dataset["seen"] === "1") return;
          const r = el.getBoundingClientRect();
          if (r.top < vh * 0.86 && r.bottom > 0) {
            el.dataset["seen"] = "1";
            active.push({ el, start: Date.now() + (order % 3) * 110 });
            order++;
          }
        });

        const now = Date.now();
        for (let i = active.length - 1; i >= 0; i--) {
          const a = active[i];
          if (now < a.start) continue;
          let p = (now - a.start) / DUR;
          if (p > 1) p = 1;
          const e = easeOut(p);
          a.el.style.opacity = e.toFixed(3);
          a.el.style.transform = `translateY(${((1 - e) * 32).toFixed(2)}px) scale(${(0.95 + e * 0.05).toFixed(4)})`;
          if (p >= 1) {
            a.el.style.opacity = "1";
            a.el.style.transform = "none";
            active.splice(i, 1);
          }
        }

        const ms = now - t0;
        floats.forEach((f) => {
          const ph = ((ms + f.fd) % f.dur) / f.dur;
          const s = Math.sin(ph * Math.PI * 2);
          const ty = (f.fl / 2) * (s + 1);
          const rz = f.rot * s;
          f.el.style.transform = `translateY(${ty.toFixed(2)}px) rotate(${rz.toFixed(3)}deg)`;
        });

        const ps = win.parallaxState || { mx: 0, my: 0 };
        pcx += (ps.mx - pcx) * 0.08;
        pcy += (ps.my - pcy) * 0.08;
        parallaxEls.forEach((el) => {
          const d = parseFloat(el.dataset["depth"] || "1");
          el.style.setProperty("--px", (pcx * 9 * d).toFixed(2) + "px");
          el.style.setProperty("--py", (pcy * 9 * d).toFixed(2) + "px");
        });

        if (tiles.length || proofReveals.length) {
          // raw scroll progress through the pinned proof section (0 → 1)
          let progress = 0;
          if (proofSec) {
            const r = proofSec.getBoundingClientRect();
            const dist = proofSec.offsetHeight - vh;
            progress = dist > 0 ? Math.max(0, Math.min(1, -r.top / dist)) : 0;
          }
          // inertia: ease the displayed progress toward the raw value (smooth scrub)
          smoothProgress += (progress - smoothProgress) * 0.1;
          if (Math.abs(progress - smoothProgress) < 0.0004) smoothProgress = progress;
          // monotonic: logos + eyebrow latch on and never reverse once revealed
          if (smoothProgress > maxProgress) maxProgress = smoothProgress;

          // staggered text reveal — latched elements use maxProgress (one-way),
          // the rest scrub bidirectionally off the live (smoothed) progress
          proofReveals.forEach(([el, a, b, latch]) => {
            if (!el) return;
            const e = easeOut(seg(latch ? maxProgress : smoothProgress, a, b));
            el.style.opacity = e.toFixed(3);
            el.style.transform = `translateY(${((1 - e) * 24).toFixed(2)}px)`;
          });

          // logos spread out over the first ~42% of scroll, then stay (latched)
          const ease = easeOut(seg(maxProgress, 0, 0.42));
          tiles.forEach((t) => {
            const a = (ms / t.dur) * Math.PI * 2 + t.ph;
            const fy = Math.sin(a) * t.amp;
            const fx = Math.cos(a * 0.8) * (t.amp * 0.4);
            const rz = Math.sin(a) * t.rot;
            const px = pcx * 14 * t.depth, py = pcy * 14 * t.depth;
            const cx = -t.ox * (1 - ease), cy = -t.oy * (1 - ease);
            const sc = 0.4 + 0.6 * ease;
            t.el.style.opacity = ease.toFixed(3);
            t.el.style.transform = `translate(-50%,-50%) translate(${(cx + fx + px).toFixed(2)}px, ${(cy + fy + py).toFixed(2)}px) rotate(${(rz * ease).toFixed(3)}deg) scale(${sc.toFixed(3)})`;
          });
        }
      };

      // rAF loop — synced to the display refresh for a smooth scrub
      let rafId = 0;
      const loop = () => {
        tick();
        rafId = requestAnimationFrame(loop);
      };
      rafId = requestAnimationFrame(loop);
      cleanups.push(() => cancelAnimationFrame(rafId));
    }

    // Hero stage animation
    const thread = document.getElementById("thread");
    const bookCard = document.getElementById("bookCard");
    const heroVisual = document.getElementById("heroVisual");
    const chatPanel = heroVisual ? heroVisual.querySelector<HTMLElement>(".chat-panel") : null;
    const smsChip = heroVisual ? heroVisual.querySelector<HTMLElement>(".float-chip.sms") : null;

    const convo = [
      { side: "agent", text: "Hi! Thanks for calling Vela Salon — how can I help?" },
      { side: "cust", text: "Hey! Can I get a balayage with Maya on Friday afternoon?" },
      { side: "agent", text: "Maya has 2:30pm open Friday — about 2 hours. Want me to book it?" },
      { side: "cust", text: "Perfect, yes please." },
      { side: "agent", text: "Done! You're booked for 2:30. Texting your confirmation now 🎉" },
    ];

    const easeOutCubic = (t: number) => 1 - Math.pow(1 - t, 3);
    const bubbleAnims: Array<{ el: HTMLElement; start: number }> = [];

    function bubble(side: string, text: string) {
      if (!thread) return;
      const el = document.createElement("div");
      const bubbleBase = "max-w-[86%] px-[13px] py-[9px] rounded-[13px] text-[13.5px] leading-[1.4]";
      const bubbleSide =
        side === "cust"
          ? "self-end bg-ink text-white rounded-br-[4px]"
          : "self-start bg-[#F5F5F5] text-ink rounded-bl-[4px]";
      el.className = `${bubbleBase} ${bubbleSide}`;
      el.textContent = text;
      el.style.opacity = "0";
      el.style.transform = "translateY(10px)";
      thread.appendChild(el);
      if (!reduce) bubbleAnims.push({ el, start: Date.now() });
      else { el.style.opacity = "1"; el.style.transform = "none"; }
    }

    let curTyping: HTMLElement | null = null;
    function showTyping() {
      if (!thread) return;
      const el = document.createElement("div");
      el.className = "self-start max-w-[86%] inline-flex gap-1 items-center px-[14px] py-3 rounded-[13px] rounded-bl-[4px] bg-[#F5F5F5] text-ink";
      const dot = "w-[6px] h-[6px] rounded-full bg-t3 animate-[typedot_1.1s_infinite]";
      el.innerHTML =
        `<span class="${dot}"></span>` +
        `<span class="${dot} [animation-delay:.18s]"></span>` +
        `<span class="${dot} [animation-delay:.36s]"></span>`;
      thread.appendChild(el);
      curTyping = el;
    }
    function clearTyping() {
      if (curTyping) { curTyping.remove(); curTyping = null; }
    }

    let heroStarted = false, chatStart = 0, bookShown = false, bookStart = 0;
    const HERO_T0 = Date.now();
    const isWide = () => (window.innerWidth || 0) > 860;

    type Step = { t: string; m?: { side: string; text: string }; dur: number };
    const steps: Step[] = [];
    convo.forEach((m) => {
      if (m.side === "agent") steps.push({ t: "typing", dur: 620 });
      else steps.push({ t: "gap", dur: 320 });
      steps.push({ t: "bubble", m, dur: m.text.length * 15 + 420 });
    });
    steps.push({ t: "book", dur: 4400 });
    steps.push({ t: "reset", dur: 360 });

    let seqIdx = 0, seqNextAt = 0, seqActive = false;

    function doStep(s: Step) {
      if (s.t === "typing") { if (!reduce) showTyping(); }
      else if (s.t === "gap") { /* pause */ }
      else if (s.t === "bubble") { clearTyping(); if (s.m) bubble(s.m.side, s.m.text); }
      else if (s.t === "book") { bookShown = true; bookStart = Date.now(); }
      else if (s.t === "reset") {
        if (thread) thread.innerHTML = "";
        bubbleAnims.length = 0;
        curTyping = null;
        bookShown = false;
      }
    }

    function sequence(now: number) {
      if (!seqActive) return;
      if (reduce) {
        if (thread && !thread.childElementCount) {
          convo.forEach((m) => bubble(m.side, m.text));
          bookShown = true;
          bookStart = now - 1000;
        }
        return;
      }
      if (now >= seqNextAt) {
        doStep(steps[seqIdx]);
        seqNextAt = now + steps[seqIdx].dur;
        seqIdx = (seqIdx + 1) % steps.length;
      }
    }

    function heroTick() {
      const now = Date.now();
      const ms = now - HERO_T0;
      sequence(now);

      for (let i = bubbleAnims.length - 1; i >= 0; i--) {
        const a = bubbleAnims[i];
        let p = (now - a.start) / 360;
        if (p > 1) p = 1;
        const e = easeOutCubic(p);
        a.el.style.opacity = e.toFixed(3);
        a.el.style.transform = `translateY(${((1 - e) * 10).toFixed(2)}px)`;
        if (p >= 1) bubbleAnims.splice(i, 1);
      }

      if (!chatPanel) return;

      if (!isWide()) {
        chatPanel.style.transform = "";
        chatPanel.style.opacity = "1";
        return;
      }

      if (reduce) {
        chatPanel.style.opacity = "1";
        chatPanel.style.transform = "translate(-116%,-50%)";
        if (bookCard) {
          (bookCard as HTMLElement).style.opacity = bookShown ? "1" : "0";
          (bookCard as HTMLElement).style.transform = "translate(12%,-50%)";
        }
        return;
      }

      let ce = chatStart ? Math.min(1, (now - chatStart) / 600) : 0;
      ce = easeOutCubic(ce);
      const cFloatY = Math.sin(ms / 3400) * 5;
      const cFloatX = Math.cos(ms / 4200) * 3;
      const cEnterY = (1 - ce) * 20;
      chatPanel.style.opacity = ce.toFixed(3);
      chatPanel.style.transform = `translate(-116%,-50%) translate(${cFloatX.toFixed(2)}px, ${(cEnterY + cFloatY).toFixed(2)}px)`;

      if (bookCard) {
        const bc = bookCard as HTMLElement;
        if (!bookShown) {
          bc.style.opacity = "0";
        } else {
          let bp = Math.min(1, (now - bookStart) / 560);
          bp = easeOutCubic(bp);
          const bFloatY = Math.sin(ms / 3000 + 1.2) * 6;
          const bFloatX = Math.cos(ms / 3800 + 1.2) * 3;
          const bEnterY = (1 - bp) * 26;
          bc.style.opacity = bp.toFixed(3);
          bc.style.transform = `translate(12%,-50%) translate(${bFloatX.toFixed(2)}px, ${(bEnterY + bFloatY).toFixed(2)}px) scale(${(0.96 + 0.04 * bp).toFixed(3)})`;
        }
      }

      if (smsChip) {
        smsChip.style.transform = `translate(${(Math.cos(ms / 3600) * 3).toFixed(2)}px, ${(Math.sin(ms / 2800 + 2) * 6).toFixed(2)}px)`;
      }
    }

    intervals.push(setInterval(heroTick, 24));
    const handleHeroResize = () => heroTick();
    window.addEventListener("resize", handleHeroResize, { passive: true });
    cleanups.push(() => window.removeEventListener("resize", handleHeroResize));

    function start() {
      if (heroStarted) return;
      heroStarted = true;
      chatStart = Date.now();
      seqActive = true;
      seqIdx = 0;
      seqNextAt = Date.now() + 420;
    }

    if (heroVisual) {
      const heroIo = new IntersectionObserver(
        (entries) => { entries.forEach((e) => { if (e.isIntersecting) { heroIo.disconnect(); start(); } }); },
        { threshold: 0.2 }
      );
      heroIo.observe(heroVisual);
      observers.push(heroIo);
    }
    const t1 = setTimeout(start, 500);
    const t2 = setTimeout(start, 1200);

    return () => {
      intervals.forEach(clearInterval);
      observers.forEach((o) => o.disconnect());
      cleanups.forEach((fn) => fn());
      clearTimeout(t1);
      clearTimeout(t2);
    };
  }, []);

  return (
    <>
      {/* fixed dot-grid backdrop (was body::before) */}
      <div
        aria-hidden
        className="fixed inset-0 z-0 pointer-events-none [background-image:radial-gradient(#E4E4E4_1.1px,transparent_1.1px)] [background-size:24px_24px] opacity-[0.85]"
      />
      <div className="overflow-x-clip font-sans text-ink text-base leading-normal antialiased [text-rendering:optimizeLegibility] selection:bg-lime selection:text-ink">
        <LandingNav />
        <HeroSection />
        <LogoStrip />
        <SocialProof />
        <HowItWorks />
        <FeaturesSection />
        <MetricsSection />
        <PricingSection />
        <TestimonialsSection />
        <FAQSection />
        <FinalCTA />
        <SiteFooter />
        <StickyCTA />
      </div>
    </>
  );
}
