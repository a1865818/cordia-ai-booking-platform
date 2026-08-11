/**
 * Who the console is acting as, for display.
 *
 * There is no authentication and no profile endpoint — no `[Authorize]` exists anywhere in
 * `apps/api`, and nothing returns a venue display name or an operator profile. The only
 * identity the platform actually attributes this session to is what `api/config.ts` resolves
 * and `fetchApi` puts on the wire: `X-Tenant-Id` and `X-Actor` / `X-Actor-Role`.
 *
 * Showing those is the only honest answer. The shell previously asserted a specific person and
 * a specific venue ("Maya Chen", "Bella Nails · Owner") as plain fact, in three independently
 * hard-coded places, none of which any endpoint had ever returned — the same fabrication the
 * frozen `9-15 June` week was, one layer further out.
 */

import { getActor, getActorRole, getTenantId } from "../../api/config";

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

/** The tooltip every surface that renders this identity carries, so nobody reads it as an account. */
export const SESSION_IDENTITY_HINT =
  "Development identity. There is no sign-in yet, so this is the actor and tenant the console " +
  "sends on every request — not a user profile.";

export function sessionIdentity(): SessionIdentity {
  const actor = getActor();
  const tenant = getTenantId();
  return {
    actor,
    role: getActorRole(),
    tenant,
    actorInitials: idInitials(actor),
    tenantInitial: idInitials(tenant).slice(0, 1),
  };
}
