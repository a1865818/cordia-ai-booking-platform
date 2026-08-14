interface ToggleProps {
  on: boolean;
  onClick: () => void;
  label?: string;
  disabled?: boolean;
}

export function Toggle({ on, onClick, label, disabled }: ToggleProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      role="switch"
      aria-checked={on}
      aria-label={label ?? 'Toggle'}
      className={`shrink-0 border-none p-0 relative focus-visible:ring-2 focus-visible:ring-[#2F6BEB] focus-visible:ring-offset-2 ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
      style={{
        width: 46, height: 27, borderRadius: 999,
        background: on ? '#0A0A0A' : '#D8D8D4',
        transition: 'background .15s',
      }}
    >
      <span style={{
        position: 'absolute', top: 3, left: on ? 22 : 3,
        width: 21, height: 21, borderRadius: '50%',
        background: '#fff', transition: 'left .15s',
        boxShadow: '0 1px 3px rgba(0,0,0,0.25)',
      }} />
    </button>
  );
}
