/**
 * Minimal typed fetch wrapper for the Cordia API.
 *
 * Resolves the API base from `window.APP_CONFIG.API_URL` when present (runtime
 * injection in hosted environments); otherwise uses relative `/api` paths so the
 * Vite dev-server proxy (see vite.config.ts) forwards to the local API.
 */

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export function isApiError(value: unknown): value is ApiError {
  return value instanceof ApiError;
}

function resolveUrl(path: string): string {
  const base = window.APP_CONFIG?.API_URL;
  if (base) {
    return `${base.replace(/\/$/, "")}${path}`;
  }
  return path;
}

export async function fetchApi<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(resolveUrl(path), {
    headers: { Accept: "application/json", ...init?.headers },
    ...init,
  });

  if (!response.ok) {
    throw new ApiError(response.status, `Request to ${path} failed: ${response.status}`);
  }

  return (await response.json()) as T;
}
