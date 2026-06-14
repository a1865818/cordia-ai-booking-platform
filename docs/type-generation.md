# Type & Schema Generation

Cordia keeps types in sync across Python, C#, and TypeScript through two
one-directional generators. **Generated files are never hand-edited** and are
gitignored — you regenerate them locally and in CI.

```
                ┌─────────────────────────── source of truth ───────────────────────────┐
                │                                                                          │
  apps/langgraph/cordia_agent/*.py            apps/api/src/Cordia.Api (controllers/DTOs)
      (Pydantic models)                                  │
                │                                         │ build + run, read Swagger
                ▼                                         ▼
  libs/shared-schemas/schemas/*.json          libs/api-contract/openapi/v1.json
      (JSON Schema, shared $defs)                         │
        │                    │                            │
        ▼                    ▼                            ▼
  generated/csharp/    generated/typescript/      generated/typescript/index.ts
   SharedTypes.cs       index.ts + zod.ts               (paths/components)
        │                    │                            │
        ▼                    ▼                            ▼
   .NET API uses        web uses @cordia/shared-schemas   web uses @cordia/api-contract
   Cordia.Domain.Generated   (types + zod validators)     (request/response types)
```

Two pipelines, two purposes:

| Pipeline | Source of truth | Produces | Consumed by |
|----------|-----------------|----------|-------------|
| **shared-schemas** | Pydantic models in `cordia_agent/` | C# DTOs **and** TS types + Zod validators | .NET API + web |
| **api-contract** | The .NET API's OpenAPI/Swagger document | TS `paths`/`components` types | web only |

Use **shared-schemas** for domain/agent payloads that cross all three languages
(graph inputs/outputs, streaming frames, enums). Use **api-contract** for the
HTTP request/response shapes of the REST API the web app calls.

---

## Pipeline 1 — Pydantic → JSON Schema → C# + TS/Zod

### Step 1: Pydantic → JSON Schema
`bun nx run langgraph:generate-schemas` runs
`tools/scripts/export_pydantic_schemas.py`, which:
- Reads the models registered in that script (`MODEL_REGISTRY` + `ENUM_REGISTRY`).
- Writes one wrapper file per model (`{ "title", "$ref" }`) into
  `libs/shared-schemas/schemas/<category>/`, where category ∈ `graph-inputs`,
  `graph-outputs`, `configurations`, `streaming`, `models`.
- Writes standalone enum schemas into `schemas/enums/`.
- Merges every nested `$def` into one `schemas/shared/PydanticSharedDefs.json`
  so cross-references resolve through a single file.
- These seven subdirs are **owned** by the script (cleaned each run);
  `schemas/job-events/` (if added) is hand-authored and never touched.

**To add a new shared type:** define the Pydantic model in `cordia_agent/`, then
register it in `tools/scripts/export_pydantic_schemas.py` (`MODEL_REGISTRY` for
models, `ENUM_REGISTRY` for enums).

### Step 2: JSON Schema → C# + TS
`bun nx run shared-schemas:generate-types` (depends on step 1) runs both:
- `generate_csharp_types.mjs` → `libs/shared-schemas/generated/csharp/SharedTypes.cs`
  in namespace `Cordia.Domain.Generated`. quicktype suffixes enum type names with
  `Enum` (e.g. `GreetingStatus` → `GreetingStatusEnum`). EF Core attributes
  (`[PgName]`, `[JsonStringEnumMemberName]`) are injected for any enum listed in
  `tools/config/ef-enum-mappings.json` (empty by default).
- `generate_typescript_types.mjs` → `generated/typescript/index.ts` (interfaces +
  enums) and `generated/typescript/zod.ts` (runtime validators). Post-processing:
  `.strict()` is added to closed schemas (`additionalProperties: false`), and
  `date-time` fields become `z.string().datetime({ offset: true })`.

The .NET API consumes `SharedTypes.cs` via a conditional `<Compile Include>` in
`Cordia.Domain.csproj` (so it builds whether or not the file has been generated yet).

---

## Pipeline 2 — .NET API → OpenAPI → TS

`bun nx run api-contract:generate-types` (depends on `api:build`) runs:
1. `export_openapi.mjs` — boots the built API on a free port with
   `DatabaseBootstrap__Enabled=false`, fetches `/swagger/v1/swagger.json`,
   deterministically sorts it, and writes `libs/api-contract/openapi/v1.json`.
2. `generate_openapi_types.mjs` — runs `openapi-typescript` over that document to
   produce `libs/api-contract/generated/typescript/index.ts` (`paths`,
   `components`, and root type aliases).

> **Typed responses require typed actions.** A controller action that returns
> `IActionResult` produces `content?: never` in the generated `paths` (the body is
> untyped). Return `ActionResult<T>` (and/or add `[ProducesResponseType<T>(200)]`)
> so the generated type carries the response shape — compare `/api/Ping`
> (`ActionResult<PingResult>` → typed) vs `/api/Health` (`IActionResult` → untyped).

---

## Commands

```bash
# Full refresh (run after changing Pydantic models or API DTOs)
bun nx run langgraph:generate-schemas      # Pydantic -> JSON Schema
bun nx run shared-schemas:generate-types   # JSON Schema -> C# + TS/Zod
bun nx run api-contract:generate-types     # API OpenAPI -> TS  (needs .NET SDK)

# Individual TS-only / C#-only
bun nx run shared-schemas:generate-typescript
bun nx run shared-schemas:generate-csharp
```

Pre-commit hooks regenerate automatically: editing `cordia_agent/*.py` regenerates
shared-schemas (and stages it); editing `apps/api/**` regenerates the api-contract.

## When to regenerate

| You changed… | Run |
|--------------|-----|
| A Pydantic model / enum in `cordia_agent/` | `langgraph:generate-schemas` → `shared-schemas:generate-types` |
| A controller route, query param, or response DTO | `api-contract:generate-types` |
| Added a pg-enum-mapped enum | add it to `tools/config/ef-enum-mappings.json`, then `shared-schemas:generate-csharp` |

## Gotchas

- Generated dirs (`libs/*/generated/`) are gitignored. A fresh clone must run the
  generators before the web `tsc`/build and the .NET build can resolve the types.
- Never edit `SharedTypes.cs`, `index.ts`, `zod.ts`, or `v1.json` by hand — change
  the source (Pydantic model or API code) and regenerate.
- `api-contract` needs the .NET SDK installed (it builds and runs the API).

For how the web app imports and uses these types, see
[`frontend-types.md`](frontend-types.md).
