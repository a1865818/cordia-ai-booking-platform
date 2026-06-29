import { initials } from "../../data/console";

interface AvatarProps {
  name: string;
  size?: number;
  radius?: number;
  bg?: string;
  color?: string;
  font?: string;
}

export function Avatar({ name, size = 34, radius, bg = '#F2F2F0', color = '#0A0A0A', font }: AvatarProps) {
  return (
    <span style={{
      width: size, height: size, borderRadius: radius ?? size * 0.29,
      background: bg, color, display: 'flex', alignItems: 'center', justifyContent: 'center',
      fontSize: font ?? `${size * 0.36}px`, fontWeight: 600, flexShrink: 0,
      fontFamily: "'Inter Tight', system-ui, sans-serif",
    }}>
      {initials(name)}
    </span>
  );
}
