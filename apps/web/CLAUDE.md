# apps/web — Cordia Web

React 19 + Vite 6 SPA. Tests run on Bun's native test runner, not Vitest (see Testing below).

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
bun nx test web         # bun test --parallel
bun nx lint web         # oxlint (deny warnings)
bun nx typecheck web    # tsc --noEmit
```

The dev server proxies `/api` to `VITE_API_PROXY_TARGET` (default
`https://localhost:8001`). In hosted envs the API base comes from
`window.APP_CONFIG.API_URL` (see `src/api/client.ts`).

## Testing

Tests run on Bun's native test runner (`bun test --parallel`), migrated off Vitest for
speed. `--parallel` is mandatory, not a preference: Bun's bare default shares one module
registry across every file in the run, so one file's `mock.module()` silently leaks into
another's and produces false failures. `vitest run` still works if you need it (`bun run
test:vitest`) — the deps and `vite.config.ts`'s `test` block are untouched.

Every test file imports `describe`/`it`/`expect`/`vi`/etc. from `src/test/vitest-compat`
instead of `"vitest"` — a thin shim (`src/test/vi.ts`) that re-exports `bun:test`'s
equivalents plus a `vi` shaped like vitest's, so most files needed no other changes.
`bunfig.toml` preloads `src/test-setup.bun.ts`, which registers happy-dom (Bun has no DOM
by default) and `@testing-library/react`'s cleanup as a real per-test hook — the latter
normally self-registers on import, but Bun's shared module cache means that only fires
for whichever file happens to import RTL first.

**A real, still-open Bun bug**: a `mock.module()` factory that itself calls `import()`
deadlocks Bun's resolver once a *second* `mock.module()` exists anywhere in the same file
— confirmed on 1.4.0, undocumented. This breaks vitest's `vi.mock(path, async
(importOriginal) => ({ ...(await importOriginal()), foo: vi.fn() }))` partial-mock idiom
the moment a file has more than one mocked module (which is normal). The fix, applied to
the handful of files that used it: capture the real module as a plain top-level `await
import(path)` snapshot *before* any `mock.module()` call, then hand that snapshot to a
synchronous factory — e.g.:

```ts
const __actual_api = { ...(await import("./api")) };
vi.mock("./api", () => ({ ...__actual_api, someFn: vi.fn() }));
```

If you add a new file using this pattern, follow the same shape — `vi.mock`'s shim
deliberately has no `importOriginal` support, so the naive vitest idiom will hang the
whole run, not fail one test.
