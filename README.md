# Cordia monorepo

Agentic booking software.

Nx + Bun monorepo containing the Cordia system components:

- **langgraph** — Python 3.12 LangGraph agent server (`apps/langgraph/`)
- **api** — C# .NET 10 REST API, clean architecture (`apps/api/`)
- **web** — React 19 + Vite frontend (`apps/web/`)
- **shared-schemas** — Pydantic → JSON Schema → C#/TypeScript types (`libs/shared-schemas/`)
- **api-contract** — .NET OpenAPI → TypeScript types (`libs/api-contract/`)

## Prerequisites

- Bun 1.0+
- Python 3.12 + uv
- .NET 10 SDK
- Docker (for the local Postgres via `docker-compose`)

## Getting Started

```bash
# Node/workspace dependencies
bun install

# Python environment
cd apps/langgraph && uv sync && cd ../..

# Local Postgres (pgvector)
docker compose up -d

# Generate shared types (Pydantic -> JSON Schema -> C#/TS)
bun nx run langgraph:generate-schemas
bun nx run shared-schemas:generate-types

# Run dev servers
bun run dev          # api (https://localhost:8001) + langgraph (http://localhost:2024)
bun nx serve web     # web on http://localhost:4200
```

## Common Commands

```bash
bun nx run-many -t build           # build everything
bun nx affected -t lint test build # affected only
bun nx graph                       # project graph
```

## Type Sharing

The Pydantic models in `apps/langgraph/cordia_agent/` are the source of truth.
`bun nx run shared-schemas:generate-types` exports JSON Schema and generates C#
DTOs (`libs/shared-schemas/generated/csharp/`) and TypeScript types + Zod
validators (`libs/shared-schemas/generated/typescript/`). The .NET API's OpenAPI
document feeds `libs/api-contract` for the web app's request/response types.

See `CLAUDE.md` for the full architecture and command reference.
