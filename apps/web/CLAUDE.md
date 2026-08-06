# apps/web — Cordia Web

React 19 + Vite 6 + Vitest SPA.

## Shared types (read this before fetching from the API)

The web app consumes generated workspace packages instead of hand-written shapes:

- `@cordia/shared-schemas` — domain/agent types + enums
- `@cordia/shared-schemas/zod` — Zod runtime validators
- `@cordia/api-contract` — HTTP request/response types from the API's OpenAPI

**How to use them:** [`../../docs/frontend-types.md`](../../docs/frontend-types.md).
**How they're generated:** [`../../docs/type-generation.md`](../../docs/type-generation.md).

On a fresh clone, generate types before serving/building:
`bun nx run shared-schemas:generate-types` and `bun nx run api-contract:generate-types`.

## Calling the API

Use `fetchApi<T>` from `src/api/client.ts` — never bare `fetch`. It attaches `X-Tenant-Id`
(Inv-13) and `X-Correlation-Id` to every request, sets `Content-Type` only when there is a
body, and turns failures into the right error class:

| Server body | Thrown as | Carries |
|---|---|---|
| `ProblemDetails` | `ApiError` | `status`, `problem.title`/`detail` |
| `ValidationProblemDetails` (400) | `ApiValidationError` | `errors` map, `fieldMessages()` |
| snake_case `IdempotencyConflict` (409) | `ApiIdempotencyConflictError` | `existingOperationId` |

Per-request extras: `{ grantId }` → `X-Grant-Id` (spec-08 verification),
`{ actor: true }` → `X-Actor` + `X-Actor-Role` (required on waitlist / menu / preorder /
DLQ / operation resume-resolve), `{ reasonCode }` → `X-Reason-Code`.

Dev identity comes from `src/api/config.ts` (`VITE_DEV_*`, see `.env.example`). The
`X-Tenant-Id` header is a **dev shim**, and the API now gates it to Development
(`Tenancy:AllowHeaderShim`). Spec-15's signed `X-Tenant-Context` is verified by the API but is
only ever *issued* at channel ingress, so there is no web-origin context to send yet — see
`docs/frontend-api-wiring.md` §4 and §12 gap 1. When that issuance exists, only
`config.ts`/`client.ts` change.

Booking writes are **not** plain POSTs: they return `202 Accepted` with an operation id
that you poll via `GET /api/booking-operations/{id}` until `terminal`. See
`docs/frontend-api-wiring.md`.

## Layout

```
src/
  main.tsx, router.tsx      app entry + React Router v7 routes
  layouts/RootLayout.tsx    shell with <Outlet/>
  pages/                    HomePage, NotFoundPage
  api/client.ts             typed fetch wrapper (fetchApi<T>)
  api/problem.ts            ApiError + ProblemDetails/validation/idempotency parsing
  api/config.ts             tenant/actor/correlation resolution
  api/canonicalHash.ts      canon.v1 port (spec 04 §5) — must match the C# hasher
```

## Commands

```bash
bun nx serve web        # vite dev server, http://localhost:4200 (/api proxied to the API)
bun nx build web        # tsc -b && vite build -> dist/apps/web
bun nx test web         # vitest
bun nx lint web         # oxlint (deny warnings)
bun nx typecheck web    # tsc --noEmit
```

The dev server proxies `/api` to `VITE_API_PROXY_TARGET` (default
`https://localhost:8001`). In hosted envs the API base comes from
`window.APP_CONFIG.API_URL` (see `src/api/client.ts`).
