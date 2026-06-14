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

## Layout

```
src/
  main.tsx, router.tsx      app entry + React Router v7 routes
  layouts/RootLayout.tsx    shell with <Outlet/>
  pages/                    HomePage, NotFoundPage
  api/client.ts             typed fetch wrapper (fetchApi<T>, ApiError)
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
