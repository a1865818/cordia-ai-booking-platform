import { reveal, shell } from "./atoms";
import { toolLogos } from "./brandLogos";

const item =
  "inline-flex items-center gap-[9px] text-t2 transition-colors duration-200 hover:text-ink";

export function LogoStrip() {
  return (
    <section className="border-b border-cd-border">
      <div className={shell}>
        <div data-reveal className={`${reveal} py-12 text-center max-[640px]:py-9`}>
          <div className="font-mono text-[11px] tracking-[0.1em] uppercase text-t3 mb-7 max-[640px]:mb-6">
            Syncs with the calendar &amp; booking tools you already use
          </div>
          <div className="flex flex-wrap items-center justify-center gap-x-10 gap-y-5 max-[640px]:gap-x-7 max-[640px]:gap-y-4">
            {toolLogos.map(({ name, path }) => (
              <span key={name} className={item} title={name}>
                <svg width="21" height="21" viewBox="0 0 24 24" fill="currentColor" aria-hidden>
                  <path d={path} />
                </svg>
                <span className="font-sans font-semibold text-[15.5px] tracking-[-0.02em] whitespace-nowrap">{name}</span>
              </span>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}
