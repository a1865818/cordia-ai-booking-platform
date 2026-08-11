import React from "react";
import { Icon } from "./Icon";

/** The two ratified staff roles (`StaffRole`, gate decision GD-1). */
export type StaffRole = "owner" | "operator";

interface PermissionGateProps {
  /** The role the server requires for this action. */
  requiredRole: StaffRole;
  /** The role this session actually asserts — `sessionIdentity().role`, verbatim off the wire. */
  role: string | null | undefined;
  /** What is being gated, named for the refusal copy. e.g. "Managing menu catalogs". */
  capability: string;
  /** Rendered only when the role satisfies `requiredRole`. */
  children: React.ReactNode;
  /**
   * Replaces the default refusal panel. Pass `null` to render nothing at all when refused —
   * appropriate when the surrounding screen already explains the absence.
   */
  fallback?: React.ReactNode;
}

/**
 * Hides what the platform would refuse anyway.
 *
 * **This is a UI hint, not an authorization boundary, and the distinction is load-bearing.** The
 * role arrives as the client-asserted `X-Actor-Role` header, so anyone can claim any role; the
 * server re-checks every action regardless. What this buys is honesty rather than security — it
 * stops the console offering a button whose call is certain to 403, which is its own defect.
 *
 * It mirrors `StaffRole.Satisfies` exactly, and that matters more than it looks: the matrix is
 * **not** an equality test. `owner ⊇ operator`, so an owner satisfies an operator-required
 * action, and the two hand-written checks this replaces would both have refused that case had
 * either been applied to an operator-gated surface. Unknown and blank roles satisfy nothing
 * (fail-closed), matching the server.
 */
export function PermissionGate({
  requiredRole, role, capability, children, fallback,
}: PermissionGateProps) {
  if (satisfiesRole(role, requiredRole)) return <>{children}</>;
  if (fallback !== undefined) return <>{fallback}</>;

  return (
    <div className="flex items-start gap-[10px] py-2">
      <Icon name="shield" size={16} strokeWidth={2} color="#9A9A9A" />
      <div className="text-[12.5px] text-t2 leading-[1.5]">
        {capability} requires the <strong>{requiredRole}</strong> role, and this console is
        running as <span className="font-mono">{describeRole(role)}</span>. The platform refuses
        the action for any other role, so none is offered here.
      </div>
    </div>
  );
}

/**
 * Does `provided` satisfy `required`? A direct port of `StaffRole.Satisfies` (apps/api).
 *
 * Deliberately **not** case-folded and not trimmed beyond whitespace: the server compares the
 * header verbatim, so a session configured as `"Owner"` genuinely is refused. A gate that
 * accepted it would offer a button the platform then rejects — the gate has to agree with the
 * wire, not with what the operator meant.
 */
export function satisfiesRole(provided: string | null | undefined, required: StaffRole): boolean {
  const value = (provided ?? "").trim();

  switch (required) {
    case "owner":
      return value === "owner";
    case "operator":
      return value === "operator" || value === "owner";
    default:
      return false;
  }
}

function describeRole(role: string | null | undefined): string {
  const value = (role ?? "").trim();
  return value === "" ? "unset" : value;
}
