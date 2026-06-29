import { reveal, revealDelay, sectionCls, shell } from "./atoms";

const metricV = "font-mono font-semibold text-[52px] tracking-[-0.04em] leading-none max-[640px]:text-[44px]";
const metricL = "mt-4 text-[14.5px] text-t2 leading-[1.4]";

export function MetricsSection() {
  return (
    <section className={sectionCls}>
      <div className={shell}>
        <div data-reveal className={`${reveal} flex items-baseline justify-between mb-7 flex-wrap gap-3 max-[640px]:flex-col max-[640px]:gap-[6px] max-[640px]:items-start`}>
          <h2 className="font-sans font-bold text-[40px] leading-[1.05] tracking-[-0.035em] max-[640px]:text-[32px]">The math is simple.</h2>
          <span className="font-mono text-[11px] tracking-[0.05em] uppercase text-t3">First 90 days · across 1,200 businesses</span>
        </div>
        <div data-reveal style={revealDelay(120)} className={`${reveal} border border-cd-border grid grid-cols-4 max-[980px]:grid-cols-2 max-[640px]:grid-cols-1`}>
          <div className="py-10 px-7 border-r border-cd-border max-[980px]:border-b max-[640px]:border-r-0">
            <div className={metricV}>
              <span className="relative inline-block">
                <span className="relative z-[1]">+38%</span>
                <span aria-hidden className="absolute left-[-2px] right-[-2px] bottom-[7px] h-[12px] bg-lime rounded-[2px] z-0 max-[640px]:h-[10px]" />
              </span>
            </div>
            <div className={metricL}>more bookings captured</div>
          </div>
          <div className="py-10 px-7 border-r border-cd-border max-[980px]:border-r-0 max-[980px]:border-b">
            <div className={metricV}>0</div>
            <div className={metricL}>missed calls — every ring answered</div>
          </div>
          <div className="py-10 px-7 border-r border-cd-border max-[640px]:border-r-0 max-[640px]:border-b">
            <div className={metricV}>5<span className="text-[0.5em]"> hrs</span></div>
            <div className={metricL}>front-desk time saved weekly</div>
          </div>
          <div className="py-10 px-7">
            <div className={metricV}>24/7</div>
            <div className={metricL}>always on, nights &amp; weekends</div>
          </div>
        </div>
      </div>
    </section>
  );
}
