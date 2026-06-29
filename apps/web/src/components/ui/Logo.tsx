interface LogoProps {
  size?: number;
}

export function Logo({ size = 26 }: LogoProps) {
  const d = size;
  const dot = size * 0.19;
  const sq = size * 0.27;
  return (
    <span style={{
      width: d, height: d, borderRadius: size * 0.3,
      background: 'linear-gradient(155deg,#262626 0%,#0A0A0A 62%)',
      position: 'relative', display: 'inline-block', flexShrink: 0,
      boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.16), 0 1px 2px rgba(0,0,0,0.28)',
    }}>
      <span style={{
        position: 'absolute', width: sq, height: sq, borderRadius: size * 0.08,
        background: '#fff', top: size * 0.23, left: size * 0.23,
      }} />
      <span style={{
        position: 'absolute', width: dot, height: dot, borderRadius: '50%',
        background: '#DDF247', top: size * 0.5, left: size * 0.5,
        boxShadow: `0 0 ${size * 0.23}px rgba(221,242,71,0.75)`,
      }} />
    </span>
  );
}
