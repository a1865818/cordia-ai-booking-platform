# Cordia Monorepo

Nx monorepo for Cordia (agentic booking software).

## Projects

| Project | Language | Path | Description |
|---------|----------|------|-------------|
| langgraph | Python 3.12 | `apps/langgraph/` | LangGraph AI agent server |
| api | C# .NET 10 | `apps/api/` | REST API (clean architecture) |
| web | TypeScript | `apps/web/` | React 19 + Vite frontend |
| shared-schemas | JSON Schema | `libs/shared-schemas/` | Generated types (C#, TypeScript+Zod) |
| api-contract | TypeScript | `libs/api-contract/` | OpenAPI-derived TS types |

## .NET clean architecture (apps/api)

> **Patterns:** when writing or reviewing .NET API code, follow
> [`docs/dotnet-api-patterns.md`](docs/dotnet-api-patterns.md) — the canonical
> conventions for layering, CQRS handlers, validation, controllers, EF Core,
> error handling, testing, and the minimal-code checklist. Read it before
> editing `apps/api`.

Layer dependency direction (mirrors the reference setup):

```
Cordia.Api ──> Cordia.Application ──> Cordia.Domain
        └────> Cordia.Infrastructure ──> Cordia.Domain
        (Application also references Infrastructure)
```

- **Cordia.Domain** — entities, enums, interfaces; links generated C# types from
  `libs/shared-schemas/generated/csharp/SharedTypes.cs` (conditional include).
- **Cordia.Application** — MediatR handlers (CQRS), FluentValidation validators; `AddApplication()` DI.
- **Cordia.Infrastructure** — EF Core `ApplicationDbContext` (Npgsql, snake_case), repositories; `AddInfrastructure()` DI.
- **Cordia.Api** — controllers, `Program.cs`, Swagger, Serilog, CORS.
- **Cordia.Tests** — xUnit v3 unit (`Category=Unit`) + integration (`Category=Integration`, Alba host).

## NX Commands

All commands use `bun nx`. **Do not run multiple `nx` commands in parallel** (daemon lock).

| Command | Notes |
|---------|-------|
| `bun nx run <project>:install` | Python: `uv sync` + pre-commit; .NET: `dotnet restore` |
| `bun nx build <project>` | Python: `uv build`; .NET: `dotnet build` |
| `bun nx test <project>` | Python: `pytest tests/unit_tests`; .NET: `dotnet test --filter Category=Unit`; web: `vitest run` |
| `bun nx test-integration <project>` | integration suites |
| `bun nx lint <project>` | Python: `ruff check`; .NET: `dotnet format --verify-no-changes`; web: `oxlint` |
| `bun nx run langgraph:format` | `ruff format` |
| `bun nx run langgraph:generate-schemas` | Export Pydantic → JSON Schema |
| `bun nx run shared-schemas:generate-types` | JSON Schema → C# + TS/Zod |
| `bun nx run api-contract:generate-types` | .NET OpenAPI → TS |
| `bun nx serve <project>` | langgraph: `:2024`, api: `https://localhost:8001`, web: `:4200` |
| `bun run dev` | api + langgraph (NX TUI) |
| `bun run dev:log` | api + langgraph + web, output teed to `.tmp/*-dev.log` |

When the user runs `dev:log`, read `.tmp/{api,langgraph,web}-dev.log` instead of starting servers yourself.

> **Capturing test output**: don't pipe through `| tail`. Tee to `.tmp/` and grep, e.g.
> `bun nx test api 2>&1 | tee .tmp/test-api.log`.

## Type Sharing

> **Docs:** [`docs/type-generation.md`](docs/type-generation.md) — how the C#/TS/Zod
> types are generated from Python + .NET. [`docs/frontend-types.md`](docs/frontend-types.md)
> — how the web app imports and uses them (typed fetch, OpenAPI `paths`, Zod validation).

```
Pydantic (apps/langgraph/cordia_agent) → JSON Schema (libs/shared-schemas/schemas)
   → C# (generated/csharp/SharedTypes.cs)  [namespace Cordia.Domain.Generated]
   → TypeScript (generated/typescript/index.ts) + Zod (generated/typescript/zod.ts)

.NET API → OpenAPI (libs/api-contract/openapi/v1.json) → TS (generated/typescript/index.ts)
```

- `tools/scripts/export_pydantic_schemas.py` — owns the `enums, models, graph-inputs,
  graph-outputs, configurations, streaming, shared` subdirs (cleaned each run). All model
  `$defs` are merged into `shared/PydanticSharedDefs.json`; each model is a `{title, $ref}` wrapper.
- `tools/scripts/generate_csharp_types.mjs` — quicktype → C#. EF Core enum attributes (`[PgName]`,
  `[JsonStringEnumMemberName]`) are injected from `tools/config/ef-enum-mappings.json` (empty by
  default — add an entry per pg-enum-mapped enum; note quicktype suffixes enum type names with `Enum`).
- `tools/scripts/generate_typescript_types.mjs` — quicktype → `index.ts` interfaces + `zod.ts`
  (`.strict()` on closed schemas, `z.string().datetime()` for date-time fields).
- `tools/scripts/export_openapi.mjs` + `generate_openapi_types.mjs` — spawn the API, fetch
  `/swagger/v1/swagger.json`, generate TS via `openapi-typescript`.

Generated dirs (`libs/*/generated/`) are gitignored — regenerate locally / in CI.

## Versioning

NX Release, fixed relationship across `[langgraph, api, web]`, unified `v{version}` tag.
Conventional Commits drive bumps (`feat:` minor, `fix:` patch, `!` major). `defaultBase: main`.

## Pre-commit

`.pre-commit-config.yaml` runs selectively by staged file type: Python (ruff/pytest),
.NET (dotnet format/test), web (oxlint), schema/contract regeneration (auto-staged),
plus generic validators and commitizen for commit messages.
