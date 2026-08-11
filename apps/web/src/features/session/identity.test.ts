import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { idInitials, sessionIdentity } from "./identity";

describe("idInitials", () => {
  it("splits on the separators ids actually use, not just spaces", () => {
    expect(idInitials("console-operator")).toBe("CO");
    expect(idInitials("front_desk")).toBe("FD");
    expect(idInitials("maya.chen")).toBe("MC");
    expect(idInitials("ops@bella")).toBe("OB");
  });

  it("takes at most two initials and uppercases them", () => {
    expect(idInitials("a b c d")).toBe("AB");
    expect(idInitials("demo")).toBe("D");
  });

  it("never returns an empty string for a blank or separator-only value", () => {
    // An empty avatar reads as a rendering bug; "?" reads as "unknown", which is the truth.
    expect(idInitials("")).toBe("?");
    expect(idInitials("   ")).toBe("?");
    expect(idInitials("---")).toBe("?");
  });
});

describe("sessionIdentity", () => {
  beforeEach(() => { window.APP_CONFIG = undefined; });
  afterEach(() => { window.APP_CONFIG = undefined; });

  it("reports the actor and tenant that are actually sent on the wire", () => {
    // The whole point: the shell used to assert "Maya Chen" / "Bella Nails · Owner", which no
    // endpoint had ever returned. These three values ARE `X-Actor`, `X-Actor-Role` and
    // `X-Tenant-Id`, so what the operator reads is what the platform attributes the session to.
    window.APP_CONFIG = { ACTOR: "front_desk", ACTOR_ROLE: "manager", TENANT_ID: "bella" };

    const session = sessionIdentity();
    expect(session.actor).toBe("front_desk");
    expect(session.role).toBe("manager");
    expect(session.tenant).toBe("bella");
    expect(session.actorInitials).toBe("FD");
    expect(session.tenantInitial).toBe("B");
  });

  it("falls back to the documented dev defaults", () => {
    const session = sessionIdentity();
    expect(session.actor).toBe("console-operator");
    expect(session.role).toBe("operator");
    expect(session.tenant).toBe("demo");
  });
});
