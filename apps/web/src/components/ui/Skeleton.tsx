import React from "react";

interface SkeletonProps {
  h?: number;
  w?: string | number;
  r?: number;
  style?: React.CSSProperties;
}

export function Skeleton({ h = 80, w = '100%', r = 14, style }: SkeletonProps) {
  return (
    <div style={{
      height: h,
      width: typeof w === 'number' ? `${w}px` : w,
      borderRadius: r,
      background: 'linear-gradient(90deg, #F1F1EF 25%, #E6E6E4 50%, #F1F1EF 75%)',
      backgroundSize: '200% 100%',
      animation: 'cdwave 1.4s ease-in-out infinite',
      ...style,
    }} />
  );
}
