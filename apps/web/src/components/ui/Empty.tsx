import { Icon, IconName } from "./Icon";

interface EmptyProps {
  icon?: IconName;
  tone?: 'good' | 'neutral' | 'warn';
  title: string;
  body?: string;
}

export function Empty({ icon = 'check', tone = 'good', title, body }: EmptyProps) {
  const tones = {
    good:    { bg: '#E7F6EC', fg: '#15924B' },
    neutral: { bg: '#F2F2F0', fg: '#6B6B6B' },
    warn:    { bg: '#FBF3DF', fg: '#C98A09' },
  };
  const tn = tones[tone];
  return (
    <div className="flex flex-col items-center justify-center text-center py-[64px] px-[24px]">
      <span className="w-[60px] h-[60px] rounded-full flex items-center justify-center mb-[18px]"
        style={{ background: tn.bg }}>
        <Icon name={icon} size={28} strokeWidth={2.2} color={tn.fg} />
      </span>
      <div className="text-[19px] font-bold tracking-[-0.02em] text-ink">{title}</div>
      {body && <div className="text-[13.5px] text-t2 mt-[6px] max-w-[300px] leading-[1.5]">{body}</div>}
    </div>
  );
}
