import { btn, reveal, shell } from "./atoms";
import { CheckIcon } from "./icons";

export function FinalCTA() {
  return (
    <section className="relative pt-10 pb-[100px] max-[640px]:pb-[72px]" id="demo">
      <div className={shell}>
        <div data-reveal className={`${reveal} bg-ink rounded-[20px] py-[76px] px-14 text-center relative overflow-hidden max-[640px]:py-11 max-[640px]:px-6 max-[640px]:rounded-2xl max-[480px]:py-9 max-[480px]:px-[18px] before:content-[''] before:absolute before:inset-0 before:[background-image:radial-gradient(rgba(255,255,255,0.07)_1px,transparent_1px)] before:[background-size:24px_24px]`}>
          <div className="relative z-[1]">
            <h2 className="text-white font-sans font-bold text-[54px] tracking-[-0.04em] leading-none max-[980px]:text-[40px] max-[640px]:text-[30px] max-[480px]:text-[26px]">Your next customer is calling.</h2>
            <p className="text-[rgba(255,255,255,0.6)] text-[18px] mx-auto mt-[22px] mb-9 max-w-[32em] max-[640px]:text-[16px] max-[640px]:mb-7">Start free in minutes — Cordia answers, books, and confirms on your own number from your very first call.</p>
            <form
              className="flex items-center gap-[6px] bg-[rgba(255,255,255,0.08)] border border-[rgba(255,255,255,0.16)] rounded-xl p-[6px] max-w-[470px] mx-auto transition-[border-color,box-shadow] duration-150 focus-within:border-ink focus-within:shadow-[0_0_0_4px_rgba(10,10,10,0.05)] max-[640px]:flex-col max-[640px]:items-stretch max-[640px]:max-w-none"
              onSubmit={(e) => e.preventDefault()}
            >
              <input
                type="email"
                placeholder="What's your work email?"
                aria-label="Work email"
                className="flex-1 border-none bg-transparent px-[14px] py-3 font-sans text-[15px] text-white focus:outline-none placeholder:text-[rgba(255,255,255,0.4)]"
              />
              <button className={btn("lime", "md", "max-[640px]:w-full")} type="submit">Start for free</button>
            </form>
            <div className="mt-4 flex items-center justify-center gap-x-4 gap-y-1.5 flex-wrap font-mono text-[11px] tracking-[0.02em] text-[rgba(255,255,255,0.6)]">
              <span className="inline-flex items-center gap-1.5"><span className="text-lime"><CheckIcon size={12} /></span>No credit card required</span>
              <span className="inline-flex items-center gap-1.5"><span className="text-lime"><CheckIcon size={12} /></span>No setup fees</span>
              <span className="inline-flex items-center gap-1.5"><span className="text-lime"><CheckIcon size={12} /></span>Cancel anytime</span>
            </div>
            <div className="mt-5 font-mono text-[12px] tracking-[0.03em] text-[rgba(255,255,255,0.5)]">
              Prefer a walkthrough? <a href="#how" className="text-white underline underline-offset-[3px]">Book a 20-min demo</a> · or <a href="#features" className="text-white underline underline-offset-[3px]">explore the platform →</a>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
