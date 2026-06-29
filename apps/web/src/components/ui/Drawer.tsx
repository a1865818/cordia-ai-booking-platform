import React, { useEffect, useState } from "react";
import { Icon } from "./Icon";

interface DrawerProps {
  open: boolean;
  onClose: () => void;
  device: 'phone' | 'desktop';
  title: string;
  eyebrow?: string;
  children: React.ReactNode;
  footer?: React.ReactNode;
  width?: number;
}

export function Drawer({ open, onClose, device, title, eyebrow, children, footer, width = 420 }: DrawerProps) {
  const [mounted, setMounted] = useState(open);
  const [shown, setShown] = useState(false);

  useEffect(() => {
    if (open) {
      setMounted(true);
      requestAnimationFrame(() => requestAnimationFrame(() => setShown(true)));
    } else {
      setShown(false);
      const t = setTimeout(() => setMounted(false), 260);
      return () => clearTimeout(t);
    }
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    // Restore the prior value (not a blanket reset) so nested/overlapping
    // overlays don't unlock page scroll while another is still open.
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
    };
  }, [open, onClose]);

  if (!mounted) return null;

  const isPhone = device === 'phone';

  const panelStyle: React.CSSProperties = isPhone
    ? {
        position: 'fixed', left: 0, right: 0, bottom: 0, maxHeight: '92%',
        background: '#fff', borderRadius: '22px 22px 0 0',
        display: 'flex', flexDirection: 'column',
        transform: shown ? 'translateY(0)' : 'translateY(100%)',
        transition: 'transform .26s cubic-bezier(.32,.72,0,1)',
        boxShadow: '0 -20px 50px -20px rgba(0,0,0,0.3)',
      }
    : {
        position: 'fixed', top: 0, right: 0, bottom: 0,
        width: width, maxWidth: '92%',
        background: '#fff', borderLeft: '1px solid #EAEAEA',
        display: 'flex', flexDirection: 'column',
        transform: shown ? 'translateX(0)' : 'translateX(100%)',
        transition: 'transform .26s cubic-bezier(.32,.72,0,1)',
        boxShadow: '-30px 0 60px -30px rgba(0,0,0,0.3)',
      };

  return (
    <div className="fixed inset-0 z-50">
      <div
        onClick={onClose}
        className="fixed inset-0 transition-opacity duration-[260ms]"
        style={{ background: 'rgba(10,10,10,0.32)', opacity: shown ? 1 : 0 }}
      />
      <div style={panelStyle}>
        <div className="flex items-start gap-[10px] px-4 pt-4 pb-[14px] border-b border-hair shrink-0 relative">
          {isPhone && (
            <span className="absolute top-2 left-1/2 -translate-x-1/2 w-9 h-1 rounded-full bg-[#E2E2DE]" />
          )}
          <div className="flex-1 min-w-0" style={{ marginTop: isPhone ? 6 : 0 }}>
            {eyebrow && (
              <div className="font-mono text-[10px] tracking-[0.14em] uppercase text-t3 mb-[3px]">
                {eyebrow}
              </div>
            )}
            <div className="text-[17px] font-bold tracking-[-0.02em] text-ink">{title}</div>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="w-8 h-8 rounded-[9px] border border-cd-border bg-white cursor-pointer flex items-center justify-center shrink-0 transition-all duration-150 hover:bg-[#FAFAF9]"
          >
            <Icon name="x" size={16} strokeWidth={2.2} color="#0A0A0A" />
          </button>
        </div>
        <div className="cd-scroll flex-1 min-h-0 overflow-y-auto p-4">
          {children}
        </div>
        {footer && (
          <div className="shrink-0 px-4 py-3 border-t border-hair bg-white">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}
