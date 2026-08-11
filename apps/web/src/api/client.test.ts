import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, fetchApi, isApiError } from "./client";
import { ApiIdempotencyConflictError, ApiValidationError, ApiVerificationRequiredError } from "./problem";

function jsonResponse(status: number, body: unknown, contentType = "application/json"): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": contentType },
  });
}

/** Captures the RequestInit the client passed to `fetch`. */
function captureFetch(response: Response) {
  const spy = vi.fn<typeof fetch>(async () => response.clone());
  vi.stubGlobal("fetch", spy);
  return spy;
}

function headersOf(spy: ReturnType<typeof captureFetch>): Headers {
  const init = spy.mock.calls[0]?.[1] as RequestInit | undefined;
  return new Headers(init?.headers);
}

describe("fetchApi", () => {
  beforeEach(() => {
    window.APP_CONFIG = undefined;
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    window.APP_CONFIG = undefined;
  });

  it("sends tenant and correlation headers on every request", async () => {
    const spy = captureFetch(jsonResponse(200, { ok: true }));

    await fetchApi("/api/Ping");

    const headers = headersOf(spy);
    expect(headers.get("Accept")).toBe("application/json");
    expect(headers.get("X-Tenant-Id")).toBe("demo");
    expect(headers.get("X-Correlation-Id")).toMatch(/^[A-Za-z0-9_-]{1,64}$/);
  });

  it("omits Content-Type when there is no body and sets it when there is", async () => {
    const spy = captureFetch(jsonResponse(200, {}));
    await fetchApi("/api/Ping");
    expect(headersOf(spy).get("Content-Type")).toBeNull();

    vi.unstubAllGlobals();
    const postSpy = captureFetch(jsonResponse(200, {}));
    await fetchApi("/api/Bookings", { method: "POST", body: JSON.stringify({ a: 1 }) });
    expect(headersOf(postSpy).get("Content-Type")).toBe("application/json");
  });

  it("sends grant, actor and reason-code headers when asked", async () => {
    const spy = captureFetch(jsonResponse(200, {}));

    await fetchApi("/api/waitlist/enrollments", {
      method: "POST",
      body: "{}",
      grantId: "grant_123",
      actor: true,
      reasonCode: "operator_request",
    });

    const headers = headersOf(spy);
    expect(headers.get("X-Grant-Id")).toBe("grant_123");
    expect(headers.get("X-Actor")).toBe("console-operator");
    expect(headers.get("X-Actor-Role")).toBe("operator");
    expect(headers.get("X-Reason-Code")).toBe("operator_request");
  });

  it("lets an explicit actor string override the configured default", async () => {
    const spy = captureFetch(jsonResponse(200, {}));

    await fetchApi("/api/waitlist/enrollments", { method: "POST", body: "{}", actor: "alice" });

    expect(headersOf(spy).get("X-Actor")).toBe("alice");
  });

  it("lets caller-supplied headers win over the defaults", async () => {
    const spy = captureFetch(jsonResponse(200, {}));

    await fetchApi("/api/Ping", { headers: { "X-Tenant-Id": "other-tenant" } });

    expect(headersOf(spy).get("X-Tenant-Id")).toBe("other-tenant");
  });

  it("reuses a caller-supplied correlation id", async () => {
    const spy = captureFetch(jsonResponse(200, {}));

    await fetchApi("/api/Ping", { correlationId: "corr_fixed" });

    expect(headersOf(spy).get("X-Correlation-Id")).toBe("corr_fixed");
  });

  it("does not leak the custom options into RequestInit", async () => {
    const spy = captureFetch(jsonResponse(200, {}));

    await fetchApi("/api/Ping", { grantId: "g", actor: true, reasonCode: "r", correlationId: "corr_x" });

    const init = spy.mock.calls[0]?.[1] as Record<string, unknown>;
    expect(init).not.toHaveProperty("grantId");
    expect(init).not.toHaveProperty("actor");
    expect(init).not.toHaveProperty("reasonCode");
    expect(init).not.toHaveProperty("correlationId");
  });

  it("prefixes the path with window.APP_CONFIG.API_URL when set", async () => {
    window.APP_CONFIG = { API_URL: "https://api.example.com/" };
    const spy = captureFetch(jsonResponse(200, {}));

    await fetchApi("/api/Ping");

    expect(spy.mock.calls[0]?.[0]).toBe("https://api.example.com/api/Ping");
  });

  it("reads the tenant from window.APP_CONFIG when provided", async () => {
    window.APP_CONFIG = { TENANT_ID: "runtime-tenant" };
    const spy = captureFetch(jsonResponse(200, {}));

    await fetchApi("/api/Ping");

    expect(headersOf(spy).get("X-Tenant-Id")).toBe("runtime-tenant");
  });

  it("returns undefined for 204", async () => {
    captureFetch(new Response(null, { status: 204 }));

    await expect(fetchApi("/api/waitlist/enrollments/e1")).resolves.toBeUndefined();
  });

  it("returns undefined for a 200 with an empty body", async () => {
    captureFetch(new Response("", { status: 200 }));

    await expect(fetchApi("/api/Ping")).resolves.toBeUndefined();
  });

  it("maps a 400 ValidationProblemDetails to ApiValidationError", async () => {
    captureFetch(
      jsonResponse(400, {
        title: "One or more validation errors occurred.",
        status: 400,
        errors: { "Request.OperationId": ["'Operation Id' must not be empty."] },
      })
    );

    const error = await fetchApi("/api/Bookings", { method: "POST", body: "{}" }).catch((e) => e);

    expect(error).toBeInstanceOf(ApiValidationError);
    const validation = error as ApiValidationError;
    expect(validation.status).toBe(400);
    expect(validation.errors["Request.OperationId"]).toEqual(["'Operation Id' must not be empty."]);
    expect(validation.fieldMessages()).toEqual([
      "Request.OperationId: 'Operation Id' must not be empty.",
    ]);
  });

  it("maps the snake_case 409 idempotency body to ApiIdempotencyConflictError", async () => {
    captureFetch(
      jsonResponse(409, {
        idempotency_key: "idem_1",
        existing_operation_id: "op_9",
        message: "Idempotency key already bound to a different request.",
      })
    );

    const error = await fetchApi("/api/Bookings", { method: "POST", body: "{}" }).catch((e) => e);

    expect(error).toBeInstanceOf(ApiIdempotencyConflictError);
    const conflict = error as ApiIdempotencyConflictError;
    expect(conflict.status).toBe(409);
    expect(conflict.existingOperationId).toBe("op_9");
    expect(conflict.message).toBe("Idempotency key already bound to a different request.");
  });

  it("maps a 403 requires_verification body to ApiVerificationRequiredError", async () => {
    // GET /api/Bookings/{id} answers 200/403/404 with the SAME body; a 403 there is a typed
    // instruction ("get a grant and retry"), not an opaque failure.
    captureFetch(
      jsonResponse(403, {
        status: "requires_verification",
        booking: { booking_id: "bk_1", version: 3 },
      })
    );

    const error = await fetchApi("/api/Bookings/bk_1").catch((e) => e);

    expect(error).toBeInstanceOf(ApiVerificationRequiredError);
    const verification = error as ApiVerificationRequiredError;
    expect(verification.status).toBe(403);
    expect(verification.message).not.toContain("failed: 403");
    // The partial projection survives, so a caller can still read the version it came with.
    expect((verification.body.booking as Record<string, unknown>).version).toBe(3);
  });

  it("leaves an ordinary 403 ProblemDetails as a plain ApiError", async () => {
    captureFetch(jsonResponse(403, { title: "Forbidden", status: 403 }));

    const error = await fetchApi("/api/Bookings/bk_1").catch((e) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).not.toBeInstanceOf(ApiVerificationRequiredError);
  });

  it("does not mistake a 409 ProblemDetails for an idempotency conflict", async () => {
    captureFetch(jsonResponse(409, { title: "Stale booking version", status: 409 }));

    const error = await fetchApi("/api/Bookings/b1/modifications", {
      method: "POST",
      body: "{}",
    }).catch((e) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).not.toBeInstanceOf(ApiIdempotencyConflictError);
    expect((error as ApiError).message).toBe("Stale booking version");
  });

  it("surfaces the ProblemDetails detail for a 422", async () => {
    captureFetch(
      jsonResponse(422, {
        title: "Confirmation hash mismatch",
        detail: "confirmation.intent_payload_hash 'abc' does not match the computed canonical hash 'def'.",
        status: 422,
      })
    );

    const error = await fetchApi("/api/Bookings", { method: "POST", body: "{}" }).catch((e) => e);

    expect(isApiError(error)).toBe(true);
    expect((error as ApiError).status).toBe(422);
    expect((error as ApiError).message).toContain("does not match the computed canonical hash");
    expect((error as ApiError).problem?.title).toBe("Confirmation hash mismatch");
  });

  it("degrades to a status-only message when the error body is not JSON", async () => {
    captureFetch(new Response("<html>502</html>", { status: 502 }));

    const error = await fetchApi("/api/Ping").catch((e) => e);

    expect(isApiError(error)).toBe(true);
    expect((error as ApiError).status).toBe(502);
    expect((error as ApiError).message).toBe("Request to /api/Ping failed: 502");
  });

  it("degrades to a status-only message when the error body is empty", async () => {
    captureFetch(new Response("", { status: 500 }));

    const error = await fetchApi("/api/Ping").catch((e) => e);

    expect((error as ApiError).message).toBe("Request to /api/Ping failed: 500");
  });
});
