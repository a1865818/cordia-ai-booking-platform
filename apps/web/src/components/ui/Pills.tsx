interface PillOption {
  value: string;
  label: string;
  dot?: string;
}

interface PillsProps {
  value: string;
  options: PillOption[];
  onChange: (v: string) => void;
}

export function Pills({ value, options, onChange }: PillsProps) {
  return (
    <div className="flex gap-[7px] flex-wrap">
      {options.map(o => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            onClick={() => onChange(o.value)}
            aria-pressed={active}
            className={`inline-flex items-center gap-[6px] h-[32px] px-[13px] rounded-full cursor-pointer text-[12.5px] whitespace-nowrap transition-all duration-150 ${
              active
                ? 'bg-[#0A0A0A] text-white border border-[#0A0A0A]'
                : 'bg-white text-[#55554F] border border-[#EAEAEA] hover:border-[#DEDEDA] hover:bg-[#FAFAF9]'
            }`}
            style={{
              fontWeight: active ? 600 : 500,
              fontFamily: "'Inter Tight', system-ui, sans-serif",
            }}
          >
            {o.dot && <span className="w-[7px] h-[7px] rounded-full" style={{ background: o.dot }} />}
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
