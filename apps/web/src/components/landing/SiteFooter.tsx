import { reveal, shell } from "./atoms";

const footH4 = "font-mono text-[11px] font-semibold tracking-[0.06em] uppercase text-t3 mb-[18px]";
const footLink = "block text-[14.5px] text-t2 mb-[13px] transition-colors duration-150 hover:text-ink";
const social = "w-9 h-9 rounded-lg border border-cd-border grid place-items-center text-t2 transition-all duration-150 hover:text-ink hover:border-ink";

export function SiteFooter() {
  return (
    <footer className="pt-[72px] pb-10 border-t border-cd-border mt-[100px] max-[640px]:pt-12 max-[640px]:pb-7 max-[640px]:mt-16">
      <div className={shell}>
        <div data-reveal className={`${reveal} grid grid-cols-[1.6fr_1fr_1fr_1fr] gap-10 max-[980px]:grid-cols-2 max-[980px]:gap-8 max-[480px]:grid-cols-1`}>
          <div>
            <a href="#" className="flex items-center gap-2 font-sans font-bold text-[23px] tracking-[-0.045em] mb-[18px]">
              <span className="w-7 h-7 rounded-[7px] bg-ink grid place-items-center shrink-0">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
                  <path d="M5 6.5C5 5.4 5.9 4.5 7 4.5h10c1.1 0 2 .9 2 2v7c0 1.1-.9 2-2 2H10l-4 3.2c-.5.4-1.2 0-1.2-.6V6.5Z" fill="#fff" />
                  <circle cx="9" cy="10" r="1.2" fill="#0A0A0A" />
                  <circle cx="12.5" cy="10" r="1.2" fill="#0A0A0A" />
                  <circle cx="16" cy="10" r="1.2" fill="#0A0A0A" />
                </svg>
              </span>
              Cordia
            </a>
            <p className="text-t2 text-[14px] max-w-[30ch] leading-[1.55]">The AI receptionist for local service businesses. Answer every call, fill every slot.</p>
          </div>
          <div>
            <h4 className={footH4}>Product</h4>
            <a href="#features" className={footLink}>Features</a>
            <a href="#how" className={footLink}>How it works</a>
            <a href="#pricing" className={footLink}>Pricing</a>
            <a href="#features" className={footLink}>Platform</a>
          </div>
          <div>
            <h4 className={footH4}>Company</h4>
            <a href="#" className={footLink}>About</a>
            <a href="#customers" className={footLink}>Customers</a>
            <a href="#" className={footLink}>Careers</a>
            <a href="#" className={footLink}>Contact</a>
          </div>
          <div>
            <h4 className={footH4}>Resources</h4>
            <a href="#faq" className={footLink}>Help center</a>
            <a href="#" className={footLink}>Privacy</a>
            <a href="#" className={footLink}>Terms</a>
            <a href="#" className={footLink}>Status</a>
          </div>
        </div>
        <div className="flex items-center justify-between mt-14 pt-7 border-t border-cd-border text-t3 font-mono text-[11.5px] tracking-[0.03em] max-[640px]:flex-col max-[640px]:gap-4 max-[640px]:text-center max-[480px]:pt-5 max-[480px]:mt-10">
          <span>© 2026 CORDIA, INC. · MADE FOR LOCAL BUSINESSES</span>
          <div className="flex gap-[10px] max-[640px]:justify-center">
            <a href="#" aria-label="Twitter" className={social}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor">
                <path d="M18.9 2H22l-7.3 8.3L23 22h-6.6l-5.2-6.8L5.3 22H2.2l7.8-8.9L1.5 2h6.8l4.7 6.2L18.9 2Zm-1.2 18h1.7L7.2 3.8H5.4L17.7 20Z" />
              </svg>
            </a>
            <a href="#" aria-label="Instagram" className={social}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
                <rect x="3" y="3" width="18" height="18" rx="5" />
                <circle cx="12" cy="12" r="4" />
                <circle cx="17.5" cy="6.5" r="1" fill="currentColor" stroke="none" />
              </svg>
            </a>
            <a href="#" aria-label="LinkedIn" className={social}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor">
                <path d="M6.5 8H3.5v12h3V8ZM5 3.5A1.8 1.8 0 1 0 5 7a1.8 1.8 0 0 0 0-3.5ZM20.5 20h-3v-6c0-1.5-.5-2.4-1.8-2.4-1 0-1.6.7-1.8 1.4-.1.2-.1.6-.1.9V20h-3V8h3v1.3c.4-.7 1.2-1.6 3-1.6 2.2 0 3.7 1.4 3.7 4.5V20Z" />
              </svg>
            </a>
          </div>
        </div>
      </div>
    </footer>
  );
}
