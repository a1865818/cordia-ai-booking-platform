import { reveal, revealDelay, secEyebrow, sectionCls, shell } from "./atoms";

const headH2 =
  "font-sans font-bold text-[46px] tracking-[-0.035em] leading-[1.02] max-[980px]:text-[34px] max-[640px]:text-[28px]";
const headP = "text-t2 text-[19px] mt-[18px] leading-[1.45] max-[640px]:text-[16px] max-[640px]:mt-3";

const stepBase = "pt-[28px] px-6 pb-[30px] relative border-cd-border";
const numCls = "font-mono text-[12px] font-semibold text-t3 mb-10 flex items-center gap-2";
const sq = "w-[7px] h-[7px] bg-ink rounded-[1px]";
const ico = "w-11 h-11 rounded-[10px] bg-[#F5F5F5] border border-cd-border grid place-items-center mb-[18px] text-ink";
const stepH3 = "font-sans font-bold text-[18px] leading-[1.05] mb-[9px] tracking-[-0.02em]";
const stepP = "text-[14.5px] text-t2 leading-[1.5]";

export function HowItWorks() {
  return (
    <section id="how" className={sectionCls}>
      <div className={shell}>
        <div data-reveal className={`${reveal} max-w-[720px] mb-[56px] max-[640px]:mb-[36px]`}>
          <div className={secEyebrow}>How it works</div>
          <h2 className={headH2}>From first ring to confirmed booking — automatically.</h2>
          <p className={headP}>Cordia handles the whole conversation and the busywork behind it. You only step in when it actually matters.</p>
        </div>
        <div data-reveal style={revealDelay(120)} className={`${reveal} grid grid-cols-4 border border-cd-border max-[980px]:grid-cols-2 max-[640px]:grid-cols-1`}>
          <div className={`${stepBase} border-r max-[640px]:border-r-0 max-[640px]:border-b`}>
            <div className={numCls}><span className={sq} />01</div>
            <div className={ico}>
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none">
                <path d="M3 5.5C3 4.7 3.7 4 4.5 4h2.6c.6 0 1.1.4 1.3 1l1 3c.1.5 0 1-.4 1.3L7.5 11c1 2 2.5 3.5 4.5 4.5l1.7-1.5c.3-.4.8-.5 1.3-.4l3 1c.6.2 1 .7 1 1.3v2.6c0 .8-.7 1.5-1.5 1.5C9.8 20 4 14.2 4 6.5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </div>
            <h3 className={stepH3}>Customer reaches out</h3>
            <p className={stepP}>A call comes in, or a message lands on WhatsApp, Instagram, SMS or your website.</p>
          </div>
          <div className={`${stepBase} border-r max-[980px]:border-r-0 max-[640px]:border-b`}>
            <div className={numCls}><span className={sq} />02</div>
            <div className={ico}>
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none">
                <path d="M12 3a4 4 0 0 1 4 4v3a4 4 0 1 1-8 0V7a4 4 0 0 1 4-4Z" stroke="currentColor" strokeWidth="1.8" />
                <path d="M5 11a7 7 0 0 0 14 0M12 18v3" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
              </svg>
            </div>
            <h3 className={stepH3}>The agent talks &amp; checks</h3>
            <p className={stepP}>Cordia holds a natural conversation, understands the request, and checks live availability.</p>
          </div>
          <div className={`${stepBase} border-r max-[640px]:border-r-0 max-[980px]:border-t max-[640px]:border-t-0 max-[640px]:border-b`}>
            <div className={numCls}><span className={sq} />03</div>
            <div className={ico}>
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none">
                <rect x="3" y="5" width="18" height="16" rx="2.5" stroke="currentColor" strokeWidth="1.8" />
                <path d="M3 9h18M8 3v4M16 3v4" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
                <path d="m9 14 2 2 4-4" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </div>
            <h3 className={stepH3}>Auto-books &amp; updates CRM</h3>
            <p className={stepP}>The slot drops into your calendar and the customer record stays clean and current.</p>
          </div>
          <div className={`${stepBase} max-[980px]:border-t max-[640px]:border-t-0`}>
            <div className={numCls}><span className={sq} />04</div>
            <div className={ico}>
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none">
                <path d="M4 5h16v11H7l-3 3V5Z" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" />
                <path d="m8 10 2.5 2.5L16 7" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </div>
            <h3 className={stepH3}>Confirmation sent</h3>
            <p className={stepP}>Your customer gets a text in seconds.</p>
            <span className="mt-[14px] font-mono text-[10.5px] tracking-[0.03em] uppercase text-ink bg-lime inline-block px-[9px] py-[5px] rounded-[5px]">Retries &amp; waitlist run behind the scenes</span>
          </div>
        </div>
      </div>
    </section>
  );
}
