interface SegmentedProps {
  value: string;
  options: { value: string; label: string }[];
  onChange: (v: string) => void;
  size?: 'sm' | 'md';
}

export function Segmented({ value, options, onChange, size = 'md' }: SegmentedProps) {
  const pad = size === 'sm' ? '5px 11px' : '6px 14px';
  const fs = size === 'sm' ? '12px' : '12.5px';
  return (
    <div className="inline-flex gap-[3px] p-[3px] rounded-[9px]" style={{ background: '#dcdcd8' }}>
      {options.map(o => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            onClick={() => onChange(o.value)}
            aria-pressed={active}
            className={`rounded-[7px] border-none cursor-pointer transition-all duration-150 ${
              active
                ? 'bg-white shadow-[0_1px_2px_rgba(10,10,10,0.12)]'
                : 'bg-transparent hover:bg-black/5'
            }`}
            style={{
              padding: pad,
              fontSize: fs,
              fontFamily: "'Inter Tight', system-ui, sans-serif",
              color: active ? '#0A0A0A' : '#55554F',
              fontWeight: active ? 600 : 500,
            }}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
