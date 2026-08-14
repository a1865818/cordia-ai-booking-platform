/**
 * Web-origin operator session (spec-15 `X-Tenant-Context` issued at login).
 *
 * Held in `sessionStorage` so closing the tab is a logout. Never `localStorage`.
 */

const STORAGE_KEY = "cordia.operator_session";

export type ActorRole = "owner" | "operator";

export interface OperatorSession {
  /** Compact JWS sent as `X-Tenant-Context`. */
  tenantContext: string;
  actorId: string;
  actorRole: ActorRole;
  tenantId: string;
  expiresAt: string;
}

function readRaw(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function isActorRole(value: unknown): value is ActorRole {
  return value === "owner" || value === "operator";
}

function isOperatorSession(value: unknown): value is OperatorSession {
  if (typeof value !== "object" || value === null) return false;
  const record = value as Record<string, unknown>;
  return (
    typeof record.tenantContext === "string" &&
    record.tenantContext.length > 0 &&
    typeof record.actorId === "string" &&
    record.actorId.length > 0 &&
    isActorRole(record.actorRole) &&
    typeof record.tenantId === "string" &&
    record.tenantId.length > 0 &&
    typeof record.expiresAt === "string" &&
    record.expiresAt.length > 0
  );
}

export function getSession(): OperatorSession | null {
  const raw = readRaw();
  if (!raw) return null;
  try {
    const parsed: unknown = JSON.parse(raw);
    return isOperatorSession(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

export function setSession(session: OperatorSession): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  } catch {
    // A blocked sessionStorage must not crash sign-in; the in-memory request still goes through
    // this tab, and a refresh will require signing in again.
  }
}

export function clearSession(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    /* ignore */
  }
}

export function hasSession(): boolean {
  return getSession() !== null;
}
