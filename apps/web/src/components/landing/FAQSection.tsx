import { useState } from "react";
import { reveal, revealDelay, secEyebrow, sectionCls, shell } from "./atoms";

const PlusIcon = () => (
  <svg width="13" height="13" viewBox="0 0 24 24" fill="none">
    <path d="M12 5v14M5 12h14" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" />
  </svg>
);

const faqs = [
  {
    q: "Will customers know they're talking to an AI?",
    a: "Cordia introduces itself naturally and is transparent if asked. Most customers simply experience a friendly, fast receptionist who books them in seconds. You control the agent's name, voice, and tone in Settings.",
  },
  {
    q: "Does it work with my existing calendar and tools?",
    a: "Yes. Cordia connects to Google Calendar, Outlook, and popular booking systems, plus telephony, WhatsApp, Instagram and SMS. Connect your channels once in Settings → Integrations and the agent works across all of them.",
  },
  {
    q: "What happens if a booking fails?",
    a: "Cordia retries automatically with smart backoff. If something is genuinely stuck — say a double-booking conflict — it lands in your Needs attention queue with the reason and a one-tap re-book. You're never left guessing.",
  },
  {
    q: "Do my customers need to download an app?",
    a: "Never. They call or message exactly the way they do today. There's nothing for them to install, sign up for, or learn.",
  },
  {
    q: "How long does it take to set up?",
    a: "Most businesses are live the same day. You connect your calendar and number, add your services and hours, and Cordia starts answering. Our team helps with onboarding on Growth and Pro plans.",
  },
  {
    q: "Is my customer data secure?",
    a: "Data is encrypted in transit and at rest. Cordia is GDPR-compliant, never sells customer data, and gives you full control to export or delete records at any time.",
  },
];

export function FAQSection() {
  // index of the open item (one at a time); -1 = all closed. First open by default.
  const [open, setOpen] = useState(0);

  return (
    <section className={sectionCls} id="faq">
      <div className={shell}>
        <div data-reveal className={`${reveal} max-w-[720px] mb-[56px] max-[640px]:mb-[36px]`}>
          <div className={secEyebrow}>FAQ</div>
          <h2 className="font-sans font-bold text-[46px] tracking-[-0.035em] leading-[1.02] max-[980px]:text-[34px] max-[640px]:text-[28px]">Questions, answered.</h2>
        </div>
        <div data-reveal style={revealDelay(120)} className={`${reveal} border-t border-cd-border`}>
          {faqs.map(({ q, a }, i) => {
            const isOpen = open === i;
            return (
              <div key={q} className="border-b border-cd-border">
                <button
                  type="button"
                  aria-expanded={isOpen}
                  onClick={() => setOpen(isOpen ? -1 : i)}
                  className="w-full text-left cursor-pointer py-[26px] px-1 flex items-center justify-between gap-5 font-sans font-semibold text-[20px] tracking-[-0.02em] max-[640px]:text-[17px] max-[640px]:py-[22px]"
                >
                  {q}
                  <span
                    className={`w-7 h-7 rounded-md border grid place-items-center flex-none transition-[transform,background-color,color,border-color] duration-[250ms] ease-[cubic-bezier(0.22,1,0.36,1)] motion-reduce:transition-none ${
                      isOpen ? "rotate-45 bg-ink text-white border-ink" : "text-ink border-cd-border"
                    }`}
                  >
                    <PlusIcon />
                  </span>
                </button>
                {/* grid 0fr→1fr animates the answer's height smoothly */}
                <div
                  className={`grid transition-[grid-template-rows] duration-[420ms] ease-[cubic-bezier(0.22,1,0.36,1)] motion-reduce:transition-none ${
                    isOpen ? "grid-rows-[1fr]" : "grid-rows-[0fr]"
                  }`}
                >
                  <div className="overflow-hidden">
                    <div
                      className={`px-1 pb-7 text-t2 text-[16px] leading-[1.6] max-w-[70ch] transition-opacity duration-[300ms] motion-reduce:transition-none ${
                        isOpen ? "opacity-100" : "opacity-0"
                      }`}
                    >
                      {a}
                    </div>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}
