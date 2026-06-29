import React from "react";

interface PanelProps {
  children: React.ReactNode;
  style?: React.CSSProperties;
  pad?: boolean;
}

export function Panel({ children, style, pad = true }: PanelProps) {
  return (
    <div
      className="bg-white border border-cd-border rounded-[14px]"
      style={{ padding: pad ? '4px 15px' : 0, ...style }}
    >
      {children}
    </div>
  );
}
