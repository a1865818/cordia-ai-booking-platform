import type { ReactNode } from "react";
import { Navigate } from "react-router";
import { hasSession } from "../../api/session";

/** Sends unsigned visitors to login rather than rendering the console empty-handed. */
export function RequireSession({ children }: { children: ReactNode }) {
  if (!hasSession()) {
    return <Navigate to="/login" replace />;
  }
  return children;
}
