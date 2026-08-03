/**
 * Shared TanStack Query configuration.
 *
 * The one rule that matters: **never retry a 4xx.** A 400 (bad request / missing tenant),
 * 403 (verification required), 409 (idempotency conflict or stale version) and 422
 * (hash mismatch) are all deterministic — retrying cannot change the outcome, it just
 * delays the error the operator needs to see, and on write paths it would re-send a
 * side-effecting request. Transport-level failures and 5xx are retried with backoff.
 */

import { QueryClient } from "@tanstack/react-query";
import { isApiError } from "./problem";

/** How many times a retryable failure is re-attempted before surfacing. */
export const MAX_RETRIES = 2;

export function shouldRetry(failureCount: number, error: unknown): boolean {
  if (failureCount >= MAX_RETRIES) return false;

  // Non-ApiError means the request never got a response (network/DNS/abort) — retry.
  if (!isApiError(error)) return true;

  // 4xx is a decision the server already made; only 408 and 429 are worth re-attempting.
  if (error.status >= 400 && error.status < 500) {
    return error.status === 408 || error.status === 429;
  }

  return true;
}

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: shouldRetry,
        retryDelay: (attempt) => Math.min(1000 * 2 ** attempt, 8000),
        // Operator boards want reasonably fresh data without hammering the API.
        staleTime: 30_000,
        refetchOnWindowFocus: true,
      },
      mutations: {
        // Writes carry an idempotency key, but an automatic retry would still re-send a
        // side-effecting request without the operator knowing. Retries are explicit.
        retry: false,
      },
    },
  });
}
