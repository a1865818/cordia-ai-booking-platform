# Cordia — Agentic Booking Platform

**A production-shaped AI agent system: a LangGraph voice/chat agent that turns natural-language
conversations into bookings, backed by a deterministic, auditable .NET fulfillment engine on
Postgres, with an operator console in React.**

> Public portfolio copy maintained by **Andrew (Tuan Minh) Nguyen** ([@a1865818](https://github.com/a1865818)).
> Cordia was co-developed with **Harry Nguyen** ([@Hazzng](https://github.com/Hazzng)), who owns the
> original private repository and has permitted this copy. The **full commit history is preserved
> with original authors**. See [`CONTRIBUTIONS.md`](CONTRIBUTIONS.md) for exactly what I built, or
> run `git shortlog -sn`.

---

## The problem

A customer calls or messages a business ("table for four tomorrow at 7, and can I pre-order the
lamb?"). An LLM agent is great at understanding that — and terrible at being the system of record.
Cordia draws a hard line:

| Probabilistic (agent) | Deterministic (platform) |
|---|---|
| Understand speech/text, extract slots, confirm intent | Availability, capacity holds, payment, provider booking |
| Typed tool calls against published contracts | Idempotent, at-least-once processing with effectively-once effects |
| Consent gate + PII redaction before anything leaves the model | Audit trail, retention sweeps, operator recovery |

The agent **captures and confirms a structured intent**; a durable workflow **executes it**.
Full design: [`docs/booking-agent-reliability-design.md`](docs/booking-agent-reliability-design.md)
and 15 numbered specs in [`docs/specs/`](docs/specs/).

## Architecture

```
 Phone / SMS / Web chat
          │
          ▼
┌──────────────────────────┐   typed tool calls (JSON Schema contracts)   ┌───────────────────────────┐
│ apps/langgraph (Python)  │ ───────────────────────────────────────────▶ │ apps/api (.NET 10)        │
│ LangGraph agent          │   JWS-signed tenant context, idempotency keys │ Clean architecture + CQRS │
│ • Azure AI STT/TTS, LLM  │                                               │ • Booking workflow/saga   │
│ • consent gate           │ ◀─────────────── menu extraction ──────────── │ • transactional outbox    │
│ • redaction filter       │                                               │ • capacity holds          │
│ • isolated menu-extract  │                                               │ • audit + privacy sweeps  │
└──────────────────────────┘                                               └─────────────┬─────────────┘
                                                                                         │ EF Core
                                    ┌──────────────────────────┐            ┌────────────▼────────────┐
                                    │ apps/web (React 19)      │ ◀── REST ─▶│ PostgreSQL + pgvector   │
                                    │ operator console         │            └─────────────────────────┘
                                    └──────────────────────────┘
```

**Single source of truth for data contracts:** Pydantic models → JSON Schema → generated C# DTOs
and TypeScript + Zod validators; the .NET OpenAPI document → TypeScript client types. Python,
C# and TypeScript cannot drift apart silently.

## Highlights relevant to production AI & data work

- **Agents on Azure.** Azure AI Foundry models, Azure speech providers, LangGraph with a Postgres
  checkpointer. Deploys as containers to Azure Container Apps and App Service.
- **Governed AI.** Consent recording by jurisdiction, a pre-emit redaction filter, a gate on
  which tools each tenant's agent policy allows, and a scrub on stored checkpoints. Transcripts can
  only be read with consent and the right role.
- **Auditable by construction.** Operator actions carry reason codes. Every audit row is
  sanitised at a single `DbContext` choke point. Retention sweeps run on a schedule. An encrypted
  object store enforces TTLs.
- **Reliable data pipelines.** Transactional outbox/inbox with leased lanes and bounded heartbeat
  renewal. A circuit breaker guards provider lookups. Reconciliation and dead-lettering handle
  failures. Delivery receipts are HMAC-verified.
- **Multi-tenant isolation.** HS256 JWS tenant-context tokens, verified identically in Python and
  C# (shared cross-implementation test vectors). The tenant is stamped once per scope.
- **Postgres performance.** The console scan uses keyset pagination over a supporting index,
  plus targeted indexes for hot reads.
- **Tests.** About 1,860 .NET tests, 1,200 Python tests and 600 web tests. Unit and integration
  suites run in CI. Pre-commit hooks enforce ruff, Bandit (with thresholds) and dotnet format.

## Tech stack

Python 3.12 · LangGraph / LangChain · Azure AI Foundry & Speech · Pydantic ·
C# / .NET 10 · MediatR · FluentValidation · EF Core (Npgsql) · xUnit v3 + Alba ·
PostgreSQL + pgvector · React 19 · Vite · TanStack Query · Zod ·
Nx + Bun monorepo · Docker · GitHub Actions · Azure Container Apps / App Service

## Repository layout

| Path | What |
|---|---|
| `apps/langgraph/` | Python LangGraph agent server (voice + text, menu extraction) |
| `apps/api/` | .NET 10 REST API — Domain / Application / Infrastructure / Api / Tests |
| `apps/web/` | React 19 operator console + landing page |
| `libs/shared-schemas/` | Pydantic-derived JSON Schema → C# / TS / Zod |
| `libs/api-contract/` | OpenAPI → TypeScript |
| `docs/` | Architecture design, 15 specs, patterns, deployment, handoff notes |
| `thoughts/shared/` | Research → plan → review records for each delivery wave |

## Running locally

Prerequisites: Bun 1.0+, Python 3.12 + uv, .NET 10 SDK, Docker.

```bash
bun install
cd apps/langgraph && uv sync && cd ../..
docker compose up -d                         # Postgres + pgvector

bun nx run langgraph:generate-schemas        # Pydantic -> JSON Schema
bun nx run shared-schemas:generate-types     # -> C# + TS/Zod

bun run dev                                  # api (https://localhost:8001) + agent (http://localhost:2024)
bun nx serve web                             # console on http://localhost:4200

bun nx run-many -t test                      # all unit suites
```

The committed configuration contains **development-only** defaults. The API refuses to start
with them outside `Development`. Real model and speech credentials go in `.env` (see
`.env.example`).

## How the work was run

Each feature went through a written loop: research, plan, implementation in phases, an
adversarial review, and a recorded outcome. That includes noting where the plan turned out to be
wrong. Those records are in `thoughts/shared/`. Commits follow Conventional Commits.

## License

Proprietary. Copyright © 2026 Hazzng (see [`LICENSE`](LICENSE)). It is published here with the
owner's permission, for portfolio review only. No licence to use, copy or deploy is granted.
