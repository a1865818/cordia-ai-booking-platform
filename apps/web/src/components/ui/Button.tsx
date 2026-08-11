import React from "react";
import { Icon, IconName } from "./Icon";
import { Spinner } from "./Spinner";

interface BtnProps {
  children?: React.ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  busy?: boolean;
  icon?: IconName;
  full?: boolean;
  style?: React.CSSProperties;
  label?: string;
  /** Native tooltip — the place to explain WHY a control is disabled. */
  title?: string;
  type?: 'button' | 'submit';
}

export function PrimaryButton({ children, onClick, disabled, busy, icon, full, style, label, title, type = 'button' }: BtnProps) {
  return (
    <button
      type={type}
      onClick={disabled ? undefined : onClick}
      disabled={disabled}
      aria-busy={busy || undefined}
      aria-label={label}
      title={title}
      className="inline-flex items-center justify-center gap-[7px] h-[36px] px-[14px] border-none rounded-[9px] cursor-pointer font-sans text-[13px] font-semibold whitespace-nowrap transition-all duration-150 shadow-[0_1px_2px_rgba(10,10,10,0.25)] hover:brightness-110 hover:shadow-[0_4px_12px_-4px_rgba(10,10,10,0.35)] active:scale-[0.985] active:translate-y-px disabled:cursor-not-allowed"
      style={{
        background: busy ? '#3A3A38' : '#0A0A0A',
        color: '#fff',
        width: full ? '100%' : undefined,
        opacity: disabled && !busy ? 0.5 : 1,
        fontFamily: "'Inter Tight', system-ui, sans-serif",
        ...style,
      }}
    >
      {busy && <Spinner size={13} />}
      {!busy && icon && <Icon name={icon} size={15} strokeWidth={2.6} />}
      {children}
    </button>
  );
}

// `disabled` was declared on BtnProps and silently ignored here, so every caller that passed it
// got a fully live button. That is the wrong direction for a shared primitive to fail in: the
// call site reads as guarded and is not. Honoured now, and matched to PrimaryButton's treatment.
export function GhostButton({ children, onClick, disabled, icon, style, label, title, type = 'button' }: BtnProps) {
  return (
    <button
      type={type}
      onClick={disabled ? undefined : onClick}
      disabled={disabled}
      aria-label={label}
      title={title}
      className="inline-flex items-center justify-center gap-[7px] h-[36px] px-[14px] rounded-[9px] cursor-pointer text-[13px] font-medium whitespace-nowrap transition-all duration-150 hover:bg-[#FAFAF9] hover:border-[#DEDEDA] active:scale-[0.985] active:translate-y-px disabled:cursor-not-allowed"
      style={{
        border: '1px solid #EAEAEA',
        background: '#fff',
        color: '#0A0A0A',
        opacity: disabled ? 0.5 : 1,
        fontFamily: "'Inter Tight', system-ui, sans-serif",
        ...style,
      }}
    >
      {icon && <Icon name={icon} size={15} strokeWidth={2.2} />}
      {children}
    </button>
  );
}
