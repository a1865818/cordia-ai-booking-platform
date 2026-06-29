import { useState } from "react";
import { Link } from "react-router";
import { btn, shell } from "./atoms";

export function LandingNav() {
  const [menuOpen, setMenuOpen] = useState(false);
  const close = () => setMenuOpen(false);

  return (
    <header
      id="nav"
      className="sticky top-0 z-50 bg-[rgba(255,255,255,0.82)] backdrop-saturate-[1.8] backdrop-blur-[12px] border-b border-transparent transition-[border-color] duration-300 [&.scrolled]:border-cd-border"
    >
      <div className={`${shell} flex items-center h-[72px] gap-9 max-[768px]:gap-3`}>
        <a href="#" className="flex items-center gap-2 font-sans font-bold text-[23px] tracking-[-0.045em]" onClick={close}>
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

        {/* Desktop links */}
        <nav className="flex items-center gap-7 ml-1 max-[980px]:hidden">
          <a href="#features" className="text-[15px] font-medium text-ink transition-colors duration-150 inline-flex items-center gap-[5px] hover:text-t2">
            Product{" "}
            <svg className="text-t3" width="13" height="13" viewBox="0 0 24 24" fill="none">
              <path d="M6 9l6 6 6-6" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </a>
          <a href="#how" className="text-[15px] font-medium text-ink transition-colors duration-150 hover:text-t2">How it works</a>
          <a href="#pricing" className="text-[15px] font-medium text-ink transition-colors duration-150 hover:text-t2">Pricing</a>
          <a href="#customers" className="text-[15px] font-medium text-ink transition-colors duration-150 hover:text-t2">Customers</a>
        </nav>

        <div className="ml-auto flex items-center gap-[18px] max-[768px]:gap-2">
          <Link to="/dashboard" className="text-[15px] font-medium whitespace-nowrap max-[768px]:hidden">Sign in</Link>
          <a
            href="#start"
            className={btn("lime", "md", "max-[640px]:px-[14px] max-[640px]:py-[9px] max-[640px]:text-[13px] max-[480px]:px-3 max-[480px]:py-2 max-[480px]:text-[12.5px]")}
          >
            Start for free
          </a>
        </div>

        {/* Hamburger */}
        <button
          className="hidden bg-transparent border-none p-0 cursor-pointer text-ink max-[980px]:grid max-[980px]:place-items-center shrink-0 max-[980px]:w-10 max-[980px]:h-10 max-[980px]:border max-[980px]:border-cd-border max-[980px]:rounded-lg transition-[background-color,border-color] duration-150 hover:bg-[#F5F5F5]"
          onClick={() => setMenuOpen(!menuOpen)}
          aria-label={menuOpen ? "Close menu" : "Open menu"}
          aria-expanded={menuOpen}
        >
          {menuOpen ? (
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
              <path d="M18 6 6 18M6 6l12 12" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
            </svg>
          ) : (
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
              <path d="M3 7h18M3 12h18M3 17h18" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
            </svg>
          )}
        </button>
      </div>

      {/* Mobile drawer — sits below the bar, inside sticky header */}
      <div
        className={`flex-col bg-[rgba(255,255,255,0.98)] border-t border-t-hair border-b border-b-cd-border shadow-[0_8px_24px_-8px_rgba(10,10,10,0.1)] [&>a:last-of-type]:border-b-0 ${
          menuOpen ? "hidden max-[980px]:flex" : "hidden"
        }`}
      >
        <a href="#features" onClick={close} className="block text-base font-medium text-ink border-b border-hair py-[14px] px-8 max-[640px]:px-5">Product</a>
        <a href="#how" onClick={close} className="block text-base font-medium text-ink border-b border-hair py-[14px] px-8 max-[640px]:px-5">How it works</a>
        <a href="#pricing" onClick={close} className="block text-base font-medium text-ink border-b border-hair py-[14px] px-8 max-[640px]:px-5">Pricing</a>
        <a href="#customers" onClick={close} className="block text-base font-medium text-ink border-b border-hair py-[14px] px-8 max-[640px]:px-5">Customers</a>
        <div className="flex gap-2 pt-[14px] px-8 pb-[18px] max-[640px]:px-5">
          <Link to="/dashboard" className={btn("ghost", "md", "flex-1")} onClick={close}>Sign in</Link>
          <a href="#start" className={btn("lime", "md", "flex-1")} onClick={close}>Start for free</a>
        </div>
      </div>
    </header>
  );
}
