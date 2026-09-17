/**
 * Session boundaries for the query cache.
 *
 * Query keys are rooted at the TENANT (Inv-13), not the operator, so two operators of the same
 * tenant share keys. Without clearing, a second operator signing in on the same tab — no reload —
 * would be shown the first operator's cached reads (customers, deposits, notification state,
 * transcripts) until each background refetch landed, and a read the second operator is NOT
 * permitted would render from cache without ever reaching the server's guard.
 *
 * So the cache is dropped at both edges: on sign-out, and again on sign-in in case the previous
 * session ended without a sign-out (expiry, a closed drawer, a manual storage clear).
 */

import type { QueryClient } from "@tanstack/react-query";
import { clearSession, setSession, type OperatorSession } from "../../api/session";
import { logout } from "./api";

/** Revokes the server session when it can, then always drops the local session and every cached read. */
export async function signOut(queryClient: QueryClient, revoke: () => Promise<unknown> = logout): Promise<void> {
  try {
    await revoke();
  } catch {
    // A failed revoke must not trap the operator in a console they asked to leave.
  }
  clearSession();
  queryClient.clear();
}

/** Starts a session with an empty cache, so nothing read under an earlier session survives into it. */
export function beginSession(queryClient: QueryClient, session: OperatorSession): void {
  queryClient.clear();
  setSession(session);
}
