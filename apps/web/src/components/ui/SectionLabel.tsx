import React from "react";

interface SectionLabelProps {
  children: React.ReactNode;
  line?: boolean;
  right?: React.ReactNode;
  style?: React.CSSProperties;
}

export function SectionLabel({ children, line, right, style }: SectionLabelProps) {
  return (
    <div className="flex items-center gap-[8px] mx-[2px] mb-[10px]" style={style}>
      <span className="font-mono text-[11px] tracking-[0.1em] uppercase text-t3">{children}</span>
      {line && <div className="flex-1 h-px bg-hair" />}
      {right}
    </div>
  );
}
