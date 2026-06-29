import { reveal, revealDelay, secEyebrow, sectionCls, shell } from "./atoms";

const by = "flex items-center gap-[13px] mt-auto pt-8";
const nm = "font-sans font-bold text-[15px]";
const bz = "font-mono text-[11.5px] text-t2 mt-[2px]";
const avatar =
  "w-11 h-11 rounded-full bg-ink text-white grid place-items-center font-sans font-semibold text-[14px] tracking-[0.01em] shrink-0";
const quoteMark = "font-sans font-bold text-[56px] leading-[0.5] text-cd-border select-none mb-3 block";

// quantified results — numbers + faces are what actually build trust
function Stat({ value, label }: { value: string; label: string }) {
  return (
    <span className="inline-flex items-baseline gap-1.5 border border-cd-border rounded-full pl-3 pr-[14px] py-[7px] bg-white">
      <span className="font-mono font-semibold text-[14px] tracking-[-0.02em] text-ink">{value}</span>
      <span className="font-mono text-[11px] text-t2">{label}</span>
    </span>
  );
}
const statsRow = "flex flex-wrap gap-2 mt-7";

export function TestimonialsSection() {
  return (
    <section className={sectionCls} id="customers">
      <div className={shell}>
        <div data-reveal className={`${reveal} max-w-[720px] mb-[56px] max-[640px]:mb-[36px]`}>
          <div className={secEyebrow}>Customers</div>
          <h2 className="font-sans font-bold text-[46px] tracking-[-0.035em] leading-[1.02] max-[980px]:text-[34px] max-[640px]:text-[28px]">Loved by the people who answer the phone.</h2>
        </div>
        <div data-reveal style={revealDelay(120)} className={`${reveal} grid grid-cols-[1.4fr_1fr] border border-cd-border max-[980px]:grid-cols-1`}>
          <div className="p-10 border-r border-cd-border flex flex-col max-[980px]:border-r-0 max-[980px]:border-b max-[640px]:py-7 max-[640px]:px-6">
            <span aria-hidden className={quoteMark}>&ldquo;</span>
            <div className="font-sans font-semibold text-[26px] tracking-[-0.02em] leading-[1.25] text-ink max-[640px]:text-[20px] max-[480px]:text-[18px]">"We were missing 20–30 calls a week during services. Cordia picks up every one and books them. It paid for itself in the first weekend — and my chair's been full since."</div>
            <div className={statsRow}>
              <Stat value="+38%" label="bookings" />
              <Stat value="0" label="missed calls" />
            </div>
            <div className={by}>
              <span className={avatar}>MC</span>
              <div>
                <div className={nm}>Maya Chen</div>
                <div className={bz}>Owner · Vela Salon, Portland</div>
              </div>
            </div>
          </div>
          <div className="p-10 border-r border-cd-border flex flex-col max-[640px]:py-7 max-[640px]:px-6">
            <div className="font-sans font-semibold text-[20px] tracking-[-0.02em] leading-[1.25] text-ink max-[640px]:text-[17px] max-[480px]:text-[16px]">"Friday nights used to be chaos. Now the phone just handles itself, and the waitlist fills our cancellations automatically."</div>
            <div className={statsRow}>
              <Stat value="100%" label="cancellations re-filled" />
              <Stat value="5 hrs" label="saved / wk" />
            </div>
            <div className={by}>
              <span className={avatar}>RT</span>
              <div>
                <div className={nm}>Rafael Torres</div>
                <div className={bz}>GM · Trattoria Lume</div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
