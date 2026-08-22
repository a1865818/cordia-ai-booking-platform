import { afterEach, beforeEach, describe, expect, it } from "../../test/vitest-compat";
import { clearSession, getSession, hasSession, setSession, type OperatorSession } from "../../api/session";

const sample: OperatorSession = {
  tenantContext: "eyJ.payload.sig",
  actorId: "act_owner",
  actorRole: "owner",
  tenantId: "acme",
  expiresAt: "2099-01-01T00:00:00Z",
};

describe("operator session storage", () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it("round-trips a session through sessionStorage, not localStorage", () => {
    expect(hasSession()).toBe(false);
    expect(getSession()).toBeNull();

    setSession(sample);

    expect(hasSession()).toBe(true);
    expect(getSession()).toEqual(sample);
    expect(sessionStorage.getItem("cordia.operator_session")).toContain("act_owner");
    expect(localStorage.getItem("cordia.operator_session")).toBeNull();
  });

  it("clears the stored session", () => {
    setSession(sample);
    clearSession();
    expect(hasSession()).toBe(false);
    expect(getSession()).toBeNull();
    expect(sessionStorage.getItem("cordia.operator_session")).toBeNull();
  });

  it("treats corrupt or incomplete storage as no session", () => {
    sessionStorage.setItem("cordia.operator_session", "{not json");
    expect(getSession()).toBeNull();
    expect(hasSession()).toBe(false);

    sessionStorage.setItem("cordia.operator_session", JSON.stringify({ actorId: "x" }));
    expect(getSession()).toBeNull();
  });
});
