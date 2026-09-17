import { beforeEach, describe, expect, it, vi } from "../../test/vitest-compat";
import { QueryClient } from "@tanstack/react-query";
import { getSession, setSession, type OperatorSession } from "../../api/session";
import { beginSession, signOut } from "./sessionTransition";

const operator = (actorId: string): OperatorSession => ({
  tenantContext: `jws-${actorId}`,
  actorId,
  actorRole: "operator",
  tenantId: "tenant-a",
  expiresAt: "2099-01-01T00:00:00Z",
});

// Same tenant-rooted key both operators would use for a booking's delivery state.
const notificationsKey = ["cordia", "tenant-a", "bookings", "notifications", "bk_1"];

beforeEach(() => sessionStorage.clear());

describe("signOut", () => {
  it("drops every cached read along with the session", async () => {
    const client = new QueryClient();
    setSession(operator("ops.jane"));
    client.setQueryData(notificationsKey, { notifications: [{ notification_id: "n1" }] });

    await signOut(client, async () => undefined);

    expect(getSession()).toBeNull();
    expect(client.getQueryData(notificationsKey)).toBeUndefined();
  });

  it("still clears the cache when the server revoke fails", async () => {
    const client = new QueryClient();
    setSession(operator("ops.jane"));
    client.setQueryData(notificationsKey, { notifications: [] });

    await signOut(client, vi.fn().mockRejectedValue(new Error("offline")));

    expect(getSession()).toBeNull();
    expect(client.getQueryData(notificationsKey)).toBeUndefined();
  });
});

describe("beginSession", () => {
  it("starts a new operator with none of the previous session's cached reads", () => {
    const client = new QueryClient();
    // The previous session ended without a sign-out (e.g. it expired) — its data is still cached.
    client.setQueryData(notificationsKey, { notifications: [{ notification_id: "n1" }] });

    beginSession(client, operator("ops.sam"));

    expect(getSession()?.actorId).toBe("ops.sam");
    expect(client.getQueryData(notificationsKey)).toBeUndefined();
  });
});
