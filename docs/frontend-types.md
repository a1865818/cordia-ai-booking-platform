# Using Shared Types in the Web App

The web app (`apps/web`) consumes two generated workspace packages so you never
hand-write request/response shapes or domain types. See
[`type-generation.md`](type-generation.md) for how they're produced.

| Package | Import | What it gives you |
|---------|--------|-------------------|
| `@cordia/shared-schemas` | types + enums | Domain/agent payload types (graph IO, streaming, enums) |
| `@cordia/shared-schemas/zod` | Zod validators | Runtime validation of those payloads (`*Schema` consts) |
| `@cordia/api-contract` | `paths`, `components` | HTTP request/response types derived from the API's OpenAPI |

Both are workspace deps in `apps/web/package.json` (`"workspace:*"`), so imports
resolve directly to the generated `.ts` — no build step, full type-checking, and
changes to the API or Pydantic models surface as TypeScript errors after regen.

> First run / fresh clone: generate the types before `bun nx serve web` / `build`
> (`bun nx run shared-schemas:generate-types` and `api-contract:generate-types`),
> otherwise the imports won't resolve.

---

## 1. Domain types & enums — `@cordia/shared-schemas`

Import generated interfaces and enums directly:

```ts
import { GreetingStatus, type HelloOutputState } from "@cordia/shared-schemas";

const result: HelloOutputState = { message: "Hi", status: GreetingStatus.Greeted };
// GreetingStatus.Greeted === "greeted"  — string enum, safe to render/compare
```

This is exactly what `src/pages/HomePage.tsx` does to prove the pipeline.

## 2. API request/response types — `@cordia/api-contract`

Derive types from the OpenAPI `paths` so they track the real endpoints. Pull the
response shape with the `paths[...]` indexed access pattern:

```ts
import type { paths, PingResult } from "@cordia/api-contract";

// Option A: the exported root alias (for named schemas)
type Ping = PingResult;                       // { message: string | null }

// Option B: index into paths (ties the type to the actual endpoint + status)
type PingResponse =
  paths["/api/Ping"]["get"]["responses"][200]["content"]["application/json"];

type PingQuery = paths["/api/Ping"]["get"]["parameters"]["query"]; // { name?: string }
```

Wrap your `fetchApi` call (from `src/api/client.ts`) with these types so the whole
call site is typed:

```ts
import { fetchApi } from "../api/client";
import type { paths } from "@cordia/api-contract";

type PingResponse =
  paths["/api/Ping"]["get"]["responses"][200]["content"]["application/json"];

export const getPing = (name: string) =>
  fetchApi<PingResponse>(`/api/Ping?name=${encodeURIComponent(name)}`);
```

If an endpoint shows `content?: never`, its action returns `IActionResult` on the
API side — make it return `ActionResult<T>` so the type flows through (see
[`type-generation.md`](type-generation.md)).

## 3. Runtime validation — `@cordia/shared-schemas/zod`

For data crossing the wire that you don't fully trust (SSE frames, webhooks,
`localStorage`, third-party callbacks), validate with the generated Zod schema
instead of casting. The `*Schema` consts mirror the types 1:1; closed schemas are
`.strict()` (unknown keys are rejected).

```ts
import { HelloStreamMessageSchema } from "@cordia/shared-schemas/zod";

function onFrame(raw: unknown) {
  const parsed = HelloStreamMessageSchema.safeParse(raw);
  if (!parsed.success) return;        // drop malformed frame
  const msg = parsed.data;            // typed HelloStreamMessage
  // msg.code is the HelloMessageCode enum, msg.text is string
}
```

Available validators match the exported consts (e.g. `GreetingStatusSchema`,
`HelloMessageCodeSchema`, `HelloConfigurationSchema`, `HelloInputStateSchema`,
`HelloOutputStateSchema`, `HelloStreamMessageSchema`).

**Rule of thumb:** use plain **types** (`@cordia/shared-schemas` /
`@cordia/api-contract`) for typed `fetch` of your own API; use **Zod**
(`/zod`) when you must verify untrusted input at runtime.

---

## Workflow when types change

1. Change the source — a Pydantic model in `cordia_agent/`, or an API
   controller/DTO in `apps/api`.
2. Regenerate (`shared-schemas:generate-types` and/or `api-contract:generate-types`),
   or just commit — the pre-commit hook regenerates and stages for you.
3. `bun nx typecheck web` (or your editor) now flags every call site that no longer
   matches. Fix the red squiggles — that's the contract enforcing itself.

## Conventions

- Never import from `libs/*/generated/**` by path — always via the package name
  (`@cordia/...`), so the public surface stays stable.
- Don't redeclare a shape the generators already produce; import it.
- Prefer `import type { … }` for type-only imports (keeps them out of the bundle).
