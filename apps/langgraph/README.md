# Cordia LangGraph Server

Python 3.12 + uv LangGraph agent server.

```bash
uv sync                       # install dependencies
bun nx serve langgraph        # dev server on http://localhost:2024
bun nx run langgraph:generate-schemas   # export Pydantic -> JSON Schema
```

The Pydantic models in `cordia_agent/` are the source of truth for shared types
(C# + TypeScript). See the root `CLAUDE.md` for the full type-sharing pipeline.
