/**
 * Web-origin login / logout. The API may not exist yet — these still talk to the contract.
 *
 * Login is `anonymous` so a leftover session cannot stamp `X-Tenant-Context` on the request
 * that is supposed to mint a new one.
 */

import { fetchApi } from "../../api/client";
import type { ActorRole, OperatorSession } from "../../api/session";

interface LoginResponse {
  tenant_context: string;
  actor_id: string;
  actor_role: ActorRole;
  tenant_id: string;
  expires_at: string;
}

export async function login(email: string, password: string): Promise<OperatorSession> {
  const body = await fetchApi<LoginResponse>("/api/auth/login", {
    method: "POST",
    body: JSON.stringify({ email, password }),
    anonymous: true,
  });
  return {
    tenantContext: body.tenant_context,
    actorId: body.actor_id,
    actorRole: body.actor_role,
    tenantId: body.tenant_id,
    expiresAt: body.expires_at,
  };
}

export function logout() {
  return fetchApi<void>("/api/auth/logout", { method: "POST" });
}
