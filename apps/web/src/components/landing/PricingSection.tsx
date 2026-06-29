import { CheckIcon } from "./icons";
import { btn, reveal, revealDelay, secEyebrow, sectionCls, shell } from "./atoms";

const pname = "font-mono font-semibold text-[13px] tracking-[0.04em] uppercase text-ink";
const pdesc = "text-[14px] text-t2 mt-[10px] min-h-[42px] leading-[1.4]";
const pprice = "mt-[22px] mb-1 flex items-baseline gap-[6px]";
const amt = "font-mono font-semibold text-[44px] tracking-[-0.04em]";
const per = "text-t3 font-mono text-[13px]";
const pnote = "font-mono text-[11px] text-t3 mb-6";
const ul = "list-none p-0 m-0 flex flex-col gap-[14px]";
const li = "flex gap-[10px] items-start text-[14px] text-ink";
const liMuted = "flex gap-[10px] items-start text-[14px] text-t3";

function Tick({ muted }: { muted?: boolean }) {
  return (
    <span className={`shrink-0 mt-px inline-flex ${muted ? "text-[#DCDCDC]" : "text-ink"}`}>
      <CheckIcon />
    </span>
  );
}

export function PricingSection() {
  return (
    <section className={sectionCls} id="pricing">
      <div className={shell}>
        <div data-reveal className={`${reveal} max-w-[720px] mb-[56px] max-[640px]:mb-[36px]`}>
          <div className={secEyebrow}>Pricing</div>
          <h2 className="font-sans font-bold text-[46px] tracking-[-0.035em] leading-[1.02] max-[980px]:text-[34px] max-[640px]:text-[28px]">Plans that pay for themselves.</h2>
          <p className="text-t2 text-[19px] mt-[18px] leading-[1.45] max-[640px]:text-[16px] max-[640px]:mt-3">Every plan includes the AI agent, calendar sync, and confirmation texts. No setup fees. Cancel anytime.</p>
        </div>
        <div data-reveal style={revealDelay(120)} className={`${reveal} grid grid-cols-3 border border-cd-border max-[980px]:grid-cols-1`}>
          <div className="py-8 px-7 border-r border-cd-border flex flex-col relative max-[980px]:border-r-0 max-[980px]:border-b">
            <div className={pname}>Starter</div>
            <div className={pdesc}>For solo operators getting their first hours back.</div>
            <div className={pprice}><span className={amt}>$49</span><span className={per}>/mo</span></div>
            <div className={pnote}>+ usage after 200 bookings/mo</div>
            <a href="#demo" className={btn("ghost", "md", "w-full mb-7")}>Start for free</a>
            <ul className={ul}>
              <li className={li}><Tick /> Phone + SMS booking agent</li>
              <li className={li}><Tick /> 1 calendar &amp; 1 location</li>
              <li className={li}><Tick /> Confirmation &amp; reminder texts</li>
              <li className={liMuted}><Tick muted /> Social channels</li>
              <li className={liMuted}><Tick muted /> Smart waitlist &amp; marketing</li>
            </ul>
          </div>
          <div className="py-9 px-7 flex flex-col relative z-[10] bg-white rounded-[16px] ring-2 ring-ink shadow-[0_30px_70px_-24px_rgba(10,10,10,0.3)] min-[981px]:-my-4 max-[980px]:rounded-none max-[980px]:ring-0 max-[980px]:shadow-none max-[980px]:my-0 max-[980px]:border-b max-[980px]:border-cd-border">
            <span className="absolute -top-[11px] left-1/2 -translate-x-1/2 font-mono text-[9.5px] font-semibold tracking-[0.06em] uppercase bg-lime text-ink px-[11px] py-[5px] rounded-full whitespace-nowrap shadow-[0_6px_16px_-4px_rgba(221,242,71,0.6)] max-[980px]:static max-[980px]:self-start max-[980px]:translate-x-0 max-[980px]:mb-3 max-[980px]:shadow-none">Most popular</span>
            <div className={pname}>Growth</div>
            <div className={pdesc}>For busy businesses that live by their calendar.</div>
            <div className={pprice}><span className={amt}>$129</span><span className={per}>/mo</span></div>
            <div className={pnote}>+ usage after 600 bookings/mo</div>
            <a href="#demo" className={btn("lime", "md", "w-full mb-7")}>Start for free</a>
            <ul className={ul}>
              <li className={li}><Tick /> Everything in Starter, plus:</li>
              <li className={li}><Tick /> WhatsApp, Instagram &amp; web chat</li>
              <li className={li}><Tick /> Smart waitlist with auto-book</li>
              <li className={li}><Tick /> CRM &amp; identity resolution</li>
              <li className={li}><Tick /> Up to 3 locations</li>
            </ul>
          </div>
          <div className="py-8 px-7 flex flex-col relative">
            <div className={pname}>Pro</div>
            <div className={pdesc}>For multi-location brands that want every lever.</div>
            <div className={pprice}><span className={amt}>$299</span><span className={per}>/mo</span></div>
            <div className={pnote}>Volume pricing · priority support</div>
            <a href="#demo" className={btn("ghost", "md", "w-full mb-7")}>Talk to sales</a>
            <ul className={ul}>
              <li className={li}><Tick /> Everything in Growth, plus:</li>
              <li className={li}><Tick /> Marketing approval queue</li>
              <li className={li}><Tick /> Unlimited locations &amp; staff</li>
              <li className={li}><Tick /> Custom agent voice &amp; persona</li>
              <li className={li}><Tick /> Dedicated success manager</li>
            </ul>
          </div>
        </div>
      </div>
    </section>
  );
}
