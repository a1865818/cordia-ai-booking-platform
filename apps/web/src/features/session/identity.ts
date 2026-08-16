/**
 * Who the console is acting as, for display.
 *
 * A signed-in operator session is the source of truth. Without one, the console falls back to
 * the Development actor/tenant `api/config.ts` resolves — the same values `fetchApi` puts on
 * the wire as `X-Tenant-Id` and `X-Actor` / `X-Actor-Role`.
 */

import { getActor, getActorRole, getTenantId } from "../../api/config";
import { getSession, hasSession } from "../../api/session";

export interface SessionIdentity {
  /** The value sent as `X-Actor` on staff writes, and recorded as the audit actor. */
  actor: string;
  /** The value sent as `X-Actor-Role`. */
  role: string;
  /** The value sent as `X-Tenant-Id` on every request. */
  tenant: string;
  actorInitials: string;
  tenantInitial: string;
}

/**
 * Up to two initials from an id or a name.
 *
 * Deliberately not `data/console.ts`'s `initials()`: that one splits on space and dot only,
 * because it is fed human names ("Mary-Jane Smith" must stay "MS"). These values are ids
 * (`console-operator`, `demo`), so the separators that matter are hyphen and underscore.
 */
export function idInitials(value: string): string {
  const parts = value.split(/[\s._\-@]+/).filter(Boolean);
  if (parts.length === 0) return "?";
  return parts
    .slice(0, 2)
    .map((part) => part[0])
    .join("")
    .toUpperCase();
}

const DEVELOPMENT_IDENTITY_HINT =
  "Development identity. There is no sign-in yet, so this is the actor and tenant the console " +
  "sends on every request — not a user profile.";

const SIGNED_IN_IDENTITY_HINT =
  "Signed-in session. Actor, role and tenant are what this console sends on every request.";

/** The tooltip every surface that renders this identity carries. */
export function sessionIdentityHint(): string {
  return hasSession() ? SIGNED_IN_IDENTITY_HINT : DEVELOPMENT_IDENTITY_HINT;
}

export function sessionIdentity(): SessionIdentity {
  const signedIn = getSession();
  const actor = signedIn?.actorId ?? getActor();
  const role = signedIn?.actorRole ?? getActorRole();
  const tenant = signedIn?.tenantId ?? getTenantId();
  return {
    actor,
    role,
    tenant,
    actorInitials: idInitials(actor),
    tenantInitial: idInitials(tenant).slice(0, 1),
  };
}
