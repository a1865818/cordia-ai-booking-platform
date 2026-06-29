import { Icon } from "./Icon";

interface KpiCardProps {
  value: string;
  label: string;
  delta?: string;
  up?: boolean;
  sub?: string;
  onClick?: () => void;
  active?: boolean;
}

export function KpiCard({ value, label, delta, up, sub, onClick, active }: KpiCardProps) {
  const inner = (
    <>
      <div className="text-[11.5px] text-t2">{label}</div>
      <span className="font-mono text-[23px] font-bold tracking-[-0.02em] block my-[4px]"
        style={{ color: active ? '#7E5606' : '#0A0A0A' }}>
        {value}
      </span>
      {delta && (
        <span className="inline-flex items-center gap-[3px] text-[11px] font-semibold"
          style={{ color: up ? '#0B6431' : '#B82C22' }}>
          <Icon name="trendUp" size={12} strokeWidth={2.2} />
          {delta}
        </span>
      )}
      {sub && (
        <span className="text-[10.5px] text-t3" style={{ marginLeft: delta ? 5 : 0 }}>{sub}</span>
      )}
    </>
  );

  if (onClick) {
    return (
      <button
        type="button"
        onClick={onClick}
        className="block text-left w-full rounded-[13px] p-[14px] cursor-pointer border transition-all duration-150 hover:shadow-[0_8px_24px_-10px_rgba(10,10,10,0.18)]"
        style={{
          background: active ? '#FCF7EC' : '#fff',
          border: `1px solid ${active ? '#F0DCA8' : '#EAEAEA'}`,
          fontFamily: "'Inter Tight', system-ui, sans-serif",
        }}
      >
        {inner}
      </button>
    );
  }

  return (
    <div className="bg-white border border-cd-border rounded-[13px] p-[14px]">
      {inner}
    </div>
  );
}
