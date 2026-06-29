import { ToastItem } from '../../hooks/useToast';

export function ToastContainer({ toasts, onDismiss, device }: {
  toasts: ToastItem[];
  onDismiss: (id: string) => void;
  device?: 'phone' | 'desktop';
}) {
  if (toasts.length === 0) return null;
  // On mobile, sit above the 62px BottomNav + 12px gap
  const bottom = device === 'phone' ? 'calc(62px + 12px)' : '20px';
  const right = device === 'phone' ? '50%' : '20px';
  const transform = device === 'phone' ? 'translateX(50%)' : undefined;
  return (
    <div className="fixed z-[200] flex flex-col gap-2 items-end"
      style={{ bottom, right, transform }}>
      {toasts.map(t => (
        <div
          key={t.id}
          className="flex items-center gap-3 bg-[#0A0A0A] text-white text-[13px] font-medium px-4 py-3 rounded-[11px] shadow-[0_8px_24px_-8px_rgba(0,0,0,0.5)] min-w-[240px] max-w-[340px]"
          style={{ animation: 'fadeIn 160ms ease-out', fontFamily: "'Inter Tight', system-ui, sans-serif" }}
        >
          <span className="flex-1">{t.message}</span>
          {t.action && (
            <button
              onClick={() => { t.action!.onClick(); onDismiss(t.id); }}
              className="text-[#DDF247] font-semibold text-[12.5px] shrink-0 border-none bg-transparent cursor-pointer hover:opacity-80 transition-opacity p-0"
              style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
            >
              {t.action.label}
            </button>
          )}
          <button
            onClick={() => onDismiss(t.id)}
            className="text-white/40 hover:text-white/70 border-none bg-transparent cursor-pointer text-[18px] leading-none transition-colors p-0 ml-1"
            aria-label="Dismiss"
          >
            ×
          </button>
        </div>
      ))}
    </div>
  );
}
