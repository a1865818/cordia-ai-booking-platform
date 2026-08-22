/**
 * Waitlist enrolment — transport and mutation behaviour.
 *
 * The transport tests pin the exact wire contract (`EnrollWaitlistValidator`'s required
 * fields, the audited actor headers, and that the never-sendable optional fields are truly
 * absent, and that the attempt's idempotency key IS). The mutation tests pin the
 * refetch-on-both-outcomes rule: the key makes a retry safe, but it cannot say whether the
 * original request landed, so the refetched list remains the only truth about that.
 */

import { describe, expect, it, vi, beforeEach, afterEach } from "../../test/vitest-compat";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, act, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";

describe("enrollGuest (transport)", () => {
  const input = {
    customerRef: "Riley P",
    serviceId: "cat_1:gel",
    locationId: "loc_001",
    partySize: 3,
    notificationChannel: "sms",
    offerLifetimeSeconds: 3600,
    acceptableFrom: "2026-08-10T00:00:00.000Z",
    acceptableTo: "2026-08-11T00:00:00.000Z",
    idempotencyKey: "idem_fixed_for_test",
  };

  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ enrollment_id: "wle_1", status: "enrolled" }), { status: 201 })
    ));
  });
  afterEach(() => vi.unstubAllGlobals());

  it("POSTs the validator's required fields with audited actor headers", async () => {
    // importActual: the module is mocked for the mutation tests below, but these transport
    // tests exercise the real request-building code against a stubbed global fetch.
    const { enrollGuest } = __actual_api;
    await enrollGuest(input);

    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/waitlist/enrollments");
    expect(init.method).toBe("POST");

    const headers = new Headers(init.headers);
    // Every waitlist mutation is audited against the actor (house rule), and the body is JSON.
    expect(headers.get("X-Actor")).toBeTruthy();
    expect(headers.get("X-Actor-Role")).toBeTruthy();
    expect(headers.get("X-Tenant-Id")).toBeTruthy();
    expect(headers.get("Content-Type")).toBe("application/json");

    expect(JSON.parse(init.body as string)).toEqual({
      customer_ref: "Riley P",
      service_id: "cat_1:gel",
      location_id: "loc_001",
      party_size: 3,
      notification_channel: "sms",
      offer_lifetime_seconds: 3600,
      acceptable_from: "2026-08-10T00:00:00.000Z",
      acceptable_to: "2026-08-11T00:00:00.000Z",
      idempotency_key: "idem_fixed_for_test",
    });
  });

  it("sends the attempt's idempotency key, so a retry is recognised by identity", async () => {
    // Without a caller key the server falls back to hashing the enrolment's own fields, which
    // cannot tell a retry apart from a deliberate second entry carrying the same details.
    const { enrollGuest } = __actual_api;
    await enrollGuest(input);

    const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body.idempotency_key).toBe("idem_fixed_for_test");
  });

  it("never sends the consent/deposit/verification fields the console cannot vouch for", async () => {
    const { enrollGuest } = __actual_api;
    await enrollGuest(input);

    const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    // `auto_book` is a consent grant (spec 11 §20), `deposit_terms` is money the platform
    // never requested, `verification_level` is an assurance claim, `identity_ref` defaults
    // server-side. Their absence is the assertion.
    for (const key of ["auto_book", "deposit_terms", "verification_level", "identity_ref"]) {
      expect(body).not.toHaveProperty(key);
    }
  });
});

// ---------------------------------------------------------------------------

const __actual_api = { ...(await import("./api")) };
vi.mock("./api", () => ({
  ...__actual_api,
  enrollGuest: vi.fn(),
}));

import { enrollGuest } from "./api";
import { useEnrollGuest, enrollFailureMessage } from "../bookings/mutations";

describe("useEnrollGuest", () => {
  const variables = {
    customerRef: "Riley P",
    serviceId: "cat_1:gel",
    locationId: "loc_001",
    partySize: 2,
    notificationChannel: "sms",
    offerLifetimeSeconds: 3600,
    acceptableFrom: "2026-08-10T00:00:00.000Z",
    acceptableTo: "2026-08-11T00:00:00.000Z",
    idempotencyKey: "idem_fixed_for_test",
  };

  function makeWrapper() {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const invalidate = vi.spyOn(client, "invalidateQueries");
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    return { wrapper, invalidate };
  }

  beforeEach(() => vi.clearAllMocks());

  it("refetches the waitlist after a success", async () => {
    vi.mocked(enrollGuest).mockResolvedValueOnce({ enrollment_id: "wle_1" });
    const { wrapper, invalidate } = makeWrapper();

    const { result } = renderHook(() => useEnrollGuest(), { wrapper });
    await act(async () => { await result.current.mutateAsync(variables); });

    expect(enrollGuest).toHaveBeenCalledWith(variables);
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: expect.arrayContaining(["waitlist"]) })
    );
  });

  it("refetches the waitlist after a FAILURE too — the request may have landed", async () => {
    // The attempt's key makes a retry safe, but an ambiguous failure may still have enrolled
    // the guest — a dropped response is indistinguishable from a rejected request. The refetch
    // is what lets the operator see which of the two happened.
    vi.mocked(enrollGuest).mockRejectedValueOnce(new Error("network dropped"));
    const { wrapper, invalidate } = makeWrapper();

    const { result } = renderHook(() => useEnrollGuest(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync(variables).catch(() => {});
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: expect.arrayContaining(["waitlist"]) })
    );
  });

  it("is never retried automatically", async () => {
    vi.mocked(enrollGuest).mockRejectedValue(new Error("boom"));
    const { wrapper } = makeWrapper();

    const { result } = renderHook(() => useEnrollGuest(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync(variables).catch(() => {});
    });

    // One call, no silent replays — a duplicate enrolment cannot be walked back by a key.
    expect(enrollGuest).toHaveBeenCalledTimes(1);
  });
});

describe("enrollFailureMessage", () => {
  it("prefers the server's own message and falls back to a named action", () => {
    expect(enrollFailureMessage(new Error("party_size must be greater than 0")))
      .toBe("party_size must be greater than 0");
    expect(enrollFailureMessage("not-an-error"))
      .toBe("Could not add this guest to the waitlist");
  });
});
