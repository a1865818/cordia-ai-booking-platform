interface SpinnerProps {
  size?: number;
  color?: string;
  track?: string;
}

/** Shared loading spinner. Used by buttons, chips, and any inline busy state. */
export function Spinner({ size = 13, color = '#fff', track = 'rgba(255,255,255,0.4)' }: SpinnerProps) {
  return (
    <span style={{
      width: size, height: size, borderRadius: '50%',
      border: `${size > 14 ? '2px' : '1.7px'} solid ${track}`,
      borderTopColor: color,
      animation: 'cdspin .7s linear infinite', display: 'inline-block', flexShrink: 0,
    }} />
  );
}
