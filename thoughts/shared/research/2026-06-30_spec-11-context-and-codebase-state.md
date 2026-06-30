---
date: 2026-06-30
researcher: Claude (Opus 4.8)
git_commit: 0614910
branch: feat/spec-11
topic: "Spec 11 (Waitlist, Menu Ingestion & Pre-order) — codebase state & implementation context"
tags: [spec-11, waitlist, menu-ingestion, preorder-saga, dotnet, langgraph, context-handoff]
status: complete
purpose: >
  Hand-off context for the next AI session that will implement spec 11. Captures the
  current state of the codebase, the canonical patterns to follow, what already exists
  vs. what must be built, and the exact seams spec 11 plugs into — so the implementation
  matches the codebase's standard and avoids "AI slop".
---

# Spec 11 Implementation Context — Codebase State & Conventions

> **Read this first, then read these source-of-truth docs in order:**
> 1. `docs/specs/11-waitlist-menu-preorder.md` — the spec (the *what/why/how-each-component*).
> 2. `docs/specs/README.md` — single-ownership registry, frozen canonical decisions (D1–D17), invariant→spec map, delivery sequencing (spec 11 is **Phase 5**).
> 3. `docs/dotnet-api-patterns.md` — the canonical `apps/api` conventions (layering, CQRS, validation, EF, errors, tests, minimal-code checklist). **Non-negotiable.**
> 4. `docs/type-generation.md` + `docs/frontend-types.md` — the Pydantic → JSON Schema → C#/TS/Zod pipeline.
> 5. Precedent specs already shipped: `docs/specs/10-notifications.md`, `docs/specs/09-payment-deposit-link.md`.

---

## 1. TL;DR — what spec 11 actually requires

Spec 11 covers **three coordinated background features** that sit downstream of core booking and reuse its primitives:

| Lane | Feature | Boundary rule |
|---|---|---|
| **(a) Waitlist** | durable enrollment → `Matched → Offered → Accepted/Declined/Expired/Released`; temporary hold (07) + offer notification (10) + expiry job (05). | **Never silent auto-book** unless a per-tenant, constraint-scoped `AutoBookGrant` (recorded consent) exists. Default = send offer, wait for explicit accept. |
| **(b) Menu ingestion** | `upload → malware scan → isolated extraction (sandboxed, NO tools) → draft catalog → owner review → approval → immutable published version`. | Extraction agent has an **empty tool surface** and treats document text as untrusted data. Only `Published` catalog is ever visible to the foreground agent. |
| **(c) Pre-order saga** | 5-step orchestration `Revalidating → HoldingCapacity → ReservingItems → AuthorizingPayment → CommittingBooking → CommittingPreorder → Completed`, with per-step compensation and live revalidation before commit. | Saga **orchestrates**; it never calls providers / mutates capacity / charges directly. A pre-order never lingers after a failed booking → `ManualReview` (operator + customer convert, audited). |

**The governing principle (whole spec set):** *the agent proposes; the platform disposes.* Foreground (`apps/langgraph`) only **reads** the published catalog and **drafts** a pre-order; all commits, matches, publishes, and charges happen in the background (`apps/api`).

---

## 2. Current state of the codebase (as of `0614910`, branch `feat/spec-11`)

### 2.1 Monorepo shape
Nx monorepo. Projects: `apps/langgraph` (Python 3.12 LangGraph agent), `apps/api` (C# .NET 10 clean architecture), `apps/web` (React 19 + Vite), `libs/shared-schemas` (JSON Schema → C#/TS/Zod), `libs/api-contract` (OpenAPI → TS). Generated dirs (`libs/*/generated/`) are gitignored.

### 2.2 Spec delivery progress
Per `docs/specs/README.md` "Delivery sequencing": **Phases 1–4 are substantially implemented** in `apps/api` (submission/idempotency, job-queue/outbox/inbox, workflow & fulfillment, capacity & holds, identity/verification, payment deposit, **notifications (spec 10, just merged in PR #60)**). Spec 11 is **Phase 5** — the next slab. Spec 12 (data model) work has **already scaffolded spec 11's persistence layer** (see §3).

### 2.3 What ALREADY EXISTS for spec 11 (do NOT re-create — extend)

**Domain entities** (`apps/api/src/Cordia.Domain/Entities/`):
- `VendorCatalog.cs` — PK `(tenant_id, catalog_id)`; `VersionLabel`, `CatalogStatus Status`, `ApprovedAt/By`, `SourceHash`, `CreatedAt`. *(No `Version` concurrency token yet.)*
- `VendorCatalogItem.cs` — PK `(tenant_id, item_id)`; FK → `vendor_catalogs`; `Name`, `Description`, `PriceMinor`, `Currency`, jsonb `MenuMetadata` / `AllergenInfo`.
- `WaitlistEnrollment.cs` — PK `(tenant_id, enrollment_id)`; constraints (`AcceptableFrom/To`, `PartySize`, `LocationId`, `ServiceId`, `OfferLifetimeSeconds`), jsonb `DepositTerms` / `AutoBook`, `Version` token.
- `WaitlistOffer.cs` — PK `(tenant_id, offer_id)`; FK → `waitlist_enrollments`; `HoldId`, `OperationId`, `WaitlistOfferStatus Status`, `OfferExpiresAt`, `MatchedAt`, `OfferedAt`, `ResolvedAt`, `Version` token.
- `PreorderSaga.cs` — PK `(tenant_id, saga_id)`; FK → `booking_operations`; `PreorderSagaState SagaState`, jsonb `PreorderReference` (defaults `"{}"`), `HoldId`, `DepositSessionId`, `Version` token.

**Domain enums** (`Cordia.Domain/Enums/`): `CatalogStatus`, `WaitlistOfferStatus`, `PreorderSagaState` — already defined. Verify their members match the spec's frozen state sets (D4 `catalog_status` = 9 values; `WaitlistOfferStatus` = `Pending, Matched, Offered, Accepted, Declined, Expired, Released`; saga states per spec §5.3).

**EF Core configs** (`Cordia.Infrastructure/EntityFramework/Configurations/`): `VendorCatalogConfiguration`, `VendorCatalogItemConfiguration`, `WaitlistEnrollmentConfiguration`, `WaitlistOfferConfiguration`, `PreorderSagaConfiguration` — all present, snake_case, FKs `ON DELETE RESTRICT`, jsonb columns, `Version` as `IsConcurrencyToken()` with `HasDefaultValue(0L)`.

**Migration**: `20260625153819_CatalogsPaymentsWaitlistChannels` already created these tables + pg-enums.

**Persistence tests** (`test/Cordia.Tests/IntegrationTests/Persistence/`): `VendorCatalogPersistenceTests`, `WaitlistPersistenceTests`, `PreorderSagaPersistenceTests` — prove the schema round-trips.

**One repository only**: `IWaitlistOfferRepository` + `WaitlistOfferRepository` (just `AddAsync` + `TryTransitionStatusAsync` via `VersionTokenUpdate`). **No** `IVendorCatalogRepository`, `IWaitlistEnrollmentRepository`, or `IPreorderSagaRepository` yet.

**Messaging topology** (`Cordia.Domain/Messaging/LogicalQueue.cs`): `WaitlistCommands` (`waitlist.commands`, consumer `waitlist.processor`) and `MenuCommands` (`menu.commands`, consumer `menu.processor`) are **already registered** in `LogicalQueueRegistry` with prefix routing (`waitlist.` / `menu.`). The consumers themselves are **not** wired yet.

**Foreground tool stubs** (`apps/langgraph/cordia_agent/tools/foreground.py`): `search_vendor_catalog` and (likely) `get_catalog_item` already exist as thin `ToolClient.call_tool(...)` delegates from spec 03. `libs/shared-schemas/schemas/enums/CatalogItemStatus.json` exists.

**Web**: `apps/web/src/components/platform/screens/WaitlistScreen.tsx` exists (currently mock/console data). No menu-review UI yet.

### 2.4 What must be BUILT for spec 11 (the actual work)

- **Application handlers** (none exist yet): `Handlers/Waitlist/` (MatchWaitlistOffer, AcceptWaitlistOffer, DeclineWaitlistOffer, ExpireWaitlistOffer), `Handlers/Menu/` (IngestVendorMenu, ReviewCatalog, ApproveCatalog, PublishCatalog, SearchVendorCatalog [query], GetCatalogItem [query]), `Handlers/Preorder/` (RevalidatePreorder, CommitPreorder + a `PreorderSagaCoordinator`).
- **Repositories**: `IVendorCatalogRepository`/`IWaitlistEnrollmentRepository`/`IPreorderSagaRepository` (+ EF impls) as needed by handlers.
- **Domain state machines**: `WaitlistOfferStateMachine`, `CatalogStateMachine` (the §5.2 lifecycle), `PreorderSagaStateMachine` — follow the `NotificationStateMachine` `FrozenDictionary` pattern exactly.
- **Domain ports + Infrastructure providers**: `IArtifactScanner` (malware scan, stub impl like the notification adapters), the **isolated extraction agent** (`menu.commands` consumer wrapping a sandboxed LLM call with **no tools**), `LiveItemRevalidationService`.
- **Typed domain exceptions**: e.g. `ItemNoLongerAvailableException`, `PriceChangedException` (mapped by `GlobalExceptionHandler`).
- **Consumer registry factories**: register `waitlist.*` / `menu.*` / `commit_preorder` message types in `ApplicationServiceCollectionExtensions` (the `ConsumerRegistry` block) → routed through `InboxDeduplicationBehavior`.
- **Controllers**: `MenuController` (owner upload/review/approve/publish), `WaitlistController` (enrollment endpoints).
- **Cross-service contracts** (`apps/langgraph/cordia_agent/contracts/`): `catalog.py` (`Money`, `CatalogItem`, `PublishedCatalog`) and `preorder.py` (`PreorderDraft`, `PreorderDraftLine`, `PreorderReference`) — **Pydantic is the source of truth**, then regenerate C#/TS/Zod.
- **Hand-authored job-event envelopes** under `libs/shared-schemas/schemas/job-events/`: versioned `waitlist.commands.*.v1` / `menu.commands.*.v1` (no secrets/PAN/transcript text).
- **Foreground**: assemble `PreorderDraft` inside `BookingDraft`, include in canonical read-back; flesh out the catalog read tools against the real published-only query.
- **Web**: owner menu-review screen (`apps/web`).
- **Tests**: unit (`Category=Unit`) for state-machine + search/lookup + extraction-flagging; Alba integration (`Category=Integration`) for publish→foreground-visibility, offer-expiry, saga crash-window/compensation, concurrency races.

---

## 3. The canonical `apps/api` patterns (follow these exactly)

> Source: `docs/dotnet-api-patterns.md`. The codebase is **highly consistent** — match the existing files, do not invent new structure. Spec 10 (Notifications) is the closest complete precedent; mirror it.

### 3.1 Layering (dependencies point inward only)
```
Cordia.Api ──> Cordia.Application ──> Cordia.Domain
        └────> Cordia.Infrastructure ──> Cordia.Domain   (Application also refs Infrastructure)
```
| Layer | Contains | Never |
|---|---|---|
| **Domain** | entities, enums, domain interfaces, state machines, `Generated/SharedTypes.cs` | EF, HTTP, MediatR, DI |
| **Application** | MediatR handlers (CQRS), FluentValidation validators, messaging bridge | controllers, inline DbContext queries |
| **Infrastructure** | `ApplicationDbContext`, repository/service/provider impls, hosted services, transport | business rules |
| **Api** | controllers, `Program.cs`, middleware, Swagger | business logic, EF queries |

### 3.2 CQRS handler layout
- **One request + one handler (+ optional validator) per file**, colocated under `Application/Handlers/<Feature>/<UseCase>/`.
- `Handlers/` top level holds **feature** folders; `Behaviours/`, `Messaging/`, `Extensions/` are standard buckets, never feature names.
- Commands mutate, queries read. Use `record` for messages. Handlers depend on **Domain interfaces**, never `ApplicationDbContext`. Thread `CancellationToken` everywhere. Register via assembly scan in `AddApplication()` — never hand-register handlers.

### 3.3 Queue-driven handlers (the spec-11 critical pattern)
A queue consumer command implements **`IInboxConsumerRequest`** (see `SendNotificationCommand`):
```csharp
public sealed record MatchWaitlistOfferCommand(
    string TenantId, string OfferId, string MessageId, string? CorrelationId)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "waitlist.processor";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => null;
}
```
- MediatR pipeline order: **Validation → InboxDeduplication → Handler** (`ValidationPipelineBehavior` then `InboxDeduplicationBehavior`, registered in `AddApplication()`).
- **The handler runs INSIDE the inbox transaction.** Do **not** open a new `IUnitOfWork` transaction; all repo writes share the ambient scoped `DbContext` (effectively-once, Inv-5). See the big doc-comment on `SendNotificationCommandHandler`.
- **Never throw for an expected terminal failure inside the inbox txn** — that rolls back the inbox admission and causes a poison redelivery loop. Settle the row to a clean terminal state instead (the notification handler's "no adapter → FailedPermanent" branch is the model).
- Register the message-type → command factory in the `ConsumerRegistry` singleton block in `ApplicationServiceCollectionExtensions.AddApplication()`. Example existing registrations: `notification.booking_confirmed.v1`, `payment.deposit_paid.v1`, `booking.scheduled.hold_expiry.v1`. Spec 11 adds `waitlist.*` / `menu.*` / pre-order factories here.

### 3.4 Optimistic-concurrency state transitions
Conditional update guarded by `(expected status AND expected version)` via `VersionTokenUpdate.UpdateAsync<T>(...)`, returning rows-affected (`1` = success, `0` = lost race → reload). See `WaitlistOfferRepository.TryTransitionStatusAsync` — this is the template for every spec-11 transition. The `Version` token starts at 0 and bumps on each settle.

### 3.5 State machines (Domain)
Static class with a `FrozenDictionary<TState, FrozenSet<TState>>` of allowed edges + `IsAllowed`/`EnsureAllowed` (throws a typed `InvalidStateTransitionException` variant). Copy `NotificationStateMachine.cs` verbatim in shape for `WaitlistOfferStateMachine`, `CatalogStateMachine`, `PreorderSagaStateMachine`.

### 3.6 Validation
FluentValidation, one validator per command/query with input rules, same folder, targets the **MediatR command** (not the controller DTO). Note: `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` is ON → validators are the **sole** required-field enforcement; every required field needs an explicit rule.

### 3.7 Controllers (Api)
Thin. `[ApiController]`, `[Route("api/[controller]")]`, inject `IMediator`, return `ActionResult<T>`, **no try/catch** (global middleware maps exceptions), no business logic. See `BookingsController` / `NotificationWebhooksController`.

### 3.8 Error handling
Throw **typed domain exceptions** (live in `Cordia.Domain/Exceptions/`); `Cordia.Api/Errors/GlobalExceptionHandler.cs` maps them to consistent problem responses. Never raw 500s for expected failures, never swallow.

### 3.9 EF Core (Infrastructure)
- All access through `ApplicationDbContext`, behind Domain repo interfaces. `UseSnakeCaseNamingConvention` is on. Entity config via `IEntityTypeConfiguration<T>` classes (one per entity). Read queries use `.AsNoTracking()`.
- **Schema change = new migration** (never edit applied ones):
  ```bash
  cd apps/api/src/Cordia.Infrastructure
  dotnet ef migrations add <Name> --startup-project ../Cordia.Api \
    --context ApplicationDbContext --output-dir EntityFramework/Migrations
  ```
- **pg-enum-mapped enums**: add to `tools/config/ef-enum-mappings.json` (quicktype suffixes the type name with `Enum`) so `[PgName]`/`[JsonStringEnumMemberName]` get generated, then register the pg enum on the Npgsql data source. **12-data-model owns the canonical DDL/pg-enum** for `vendor_catalogs`, `waitlist_*`, `preorder_sagas` — defer to it; don't invent table/enum values not ratified there.

### 3.10 Generated shared types are READ-ONLY
Never hand-edit `Cordia.Domain/Generated/SharedTypes.cs` or `libs/*/generated/*`. To change them, edit Pydantic in `apps/langgraph/cordia_agent/` and run `bun nx run shared-schemas:generate-types`.

### 3.11 Testing
xUnit v3 with `[Trait("Category","Unit")]` / `"Integration"`. Moq for unit isolation; **Alba** (`AlbaHost.For<Program>`) against **real PostgreSQL** for integration. Test folder mirrors source. Test class names omit `Command`/`Query` suffix. Every new behavior gets a test; bug fixes get a regression test. Spec 11 specifically wants Alba for the concurrency/race + saga crash-window cases (deterministic two-transaction interleaving, resume-from-state), and an extraction-agent isolation test asserting the tool surface is **empty by construction**.

### 3.12 Minimal-code discipline
Smallest change that satisfies the pattern. No speculative abstractions, unused params, or dead code. New `.cs` files must be **UTF-8 with BOM** (`.editorconfig` enforces; `bun nx lint api` fails otherwise).

---

## 4. The seams spec 11 plugs into (sibling specs — already built)

Spec 11 **delegates** all side effects; it owns only its own state + compensation. Reuse, don't reinvent:

| Need | Owner spec | Existing code to call |
|---|---|---|
| Capacity hold + idempotent release | 07 | `ICapacityReservation` / `FirstPartyCapacityReservation`, `CapacityHold`, `ReleaseExpiredHoldsCommand`, `CapacityExpiryScheduler` |
| Outbox publish + inbox dedup + scheduled jobs | 05 | `IOutboxRepository`, `InboxDeduplicationBehavior`, `IScheduledJobRepository`, `OutboxPublisherHostedService`, `ScheduledJobPollerHostedService` |
| Booking commit / reconcile / ManualReview | 06 | `AdvanceBookingOperationCommand`, `OperationStateMachine`, `ManualReviewEscalator`, `ResolveManualReviewCommand` |
| Payment (deposit link) | 09 | `IDepositPolicy`, `DepositSession`, `ProcessDepositEventCommand` (`payment.deposit_paid.v1` / `payment.deposit_expired.v1`) |
| Notifications (offer / expiry / combined outcome) | 10 | emit `notification.*` via outbox → `SendNotificationCommand`; identity derived by consumer (Inv-9). **Spec 11 must register its own Critical `WaitlistOffer` message type** carrying `offer_expires_at` (see the note in `ConsumerRegistry` — `notification.waitlisted.v1` is deliberately not the match-offer). |
| Submission / canonical hash / operation identity | 04 | `SubmitConfirmedIntentCommand`, `ICanonicalRequestHasher`; embed `PreorderReference` into the intent-revision contract so it's part of the request hash (Inv-2/3/10). |
| Audit append | 14 | `IAuditEventRepository.AddAsync(new AuditEvent{...})` for catalog approval/publish, auto-book consent, standalone-order conversion. |
| Tenant scope | 15 | `ITenantContext`; every query/enrollment/saga step is `tenant_id`-scoped (Inv-13). |

### Invariants spec 11 must enforce (from spec §6, cross-checked with README map)
Inv-1 (no side effect from transcript — commit only from `PreorderReference` in a confirmed revision), Inv-2/3 (operation_id + key bound to one request hash), Inv-4 (offer creation + state row + expiry schedule + notification outbox in **one** txn), Inv-5 (inbox dedup on `waitlist.commands`/`menu.commands`), Inv-6 (atomic conditional hold, separate from idempotency key), Inv-7 (reconcile ambiguous before re-create/charge — `CommittingBooking → Reconciling`), Inv-8 (combined-outcome notification only after both booking + pre-order durably committed), Inv-9 (notification dedup), Inv-10 (material change → new revision + confirmation via live revalidation), Inv-12 (no PAN in agent/queue/logs; artifacts in tenant-scoped object storage), Inv-13 (tenant scope first), Inv-14 (DLQ ≠ business state — `ManualReview`/`ExtractFailed`/`Quarantined` persist & surface), Inv-16 (agent reads only published catalog, never invents prices/allergens, never claims commit), **prompt-injection boundary §27.6** (extraction agent empty tool surface, doc text = untrusted data, uncertain values flagged not published).

---

## 5. langgraph / shared-schemas / web pointers

- **langgraph** (`apps/langgraph/cordia_agent/`): graph in `graph.py`, nodes in `nodes/`, tools in `tools/` (`foreground.py` = the 11 `@tool` defs, each a single `get_tool_client().call_tool(...)` delegate; `contracts.py`, `client.py`). Cross-service Pydantic models go in `contracts/`. Tests in `tests/unit_tests/` + `tests/integration_tests/`. Run: `bun nx test langgraph`, `bun nx run langgraph:format`, `bun nx run langgraph:generate-schemas`.
- **shared-schemas** (`libs/shared-schemas/schemas/`): `enums/`, `models/`, `job-events/` (hand-authored, e.g. `notification.requested.v1.json` — copy its envelope shape: `$schema` draft-07, `additionalProperties:false`, `const` message_type discriminator, explicit `required`, no secrets). Regenerate: `bun nx run shared-schemas:generate-types`.
- **web** (`apps/web/src/`): `components/platform/screens/` (incl. existing `WaitlistScreen.tsx`), `components/platform/drawers/`, `api/client.ts` (typed fetch via OpenAPI `paths`). The menu-review surface is the new UI work. See `docs/frontend-types.md`.

---

## 6. Open decisions that gate spec 11 (resolve / confirm before coding)
From spec §12 + README "Open decisions" (coordinate with the owning spec):
- **Owner/operator roles** — who approves/publishes a catalog, converts an orphaned pre-order, configures auto-book (08/14). *Currently no role taxonomy exists in code.*
- **Payment auth-vs-capture sequence** — fixes saga `AuthorizingPayment` compensation (void vs refund). Note **D14**: the deposit-link model superseded the old `payment_authorized/captured` saga set — the saga uses `deposit_paid`/`deposit_expired` (09). Reconcile spec §5.3's "payment auth" wording against the deposit-link reality.
- **First-party vs provider-owned capacity** — whether holds are first-party atomic writes (current impl: `FirstPartyCapacityReservation`) or mirrored provider holds (07).
- **Canonicalization + hash algorithm** — how `PreorderReference` folds into the canonical request hash (04; `ICanonicalRequestHasher`).
- **Menu artifact object-storage table/retention** — README flags this as **not yet owned by spec 12** ("Menu artifact object-storage lifecycle table"). May need a new table + migration coordinated with 12/14.
- **Initial SLO values** — §7 latency targets are placeholders pending 13.

---

## 7. Recommended workflow for the next session (avoid AI slop)

1. **Re-read** the five docs in the callout at the top. Treat `docs/specs/README.md` D1–D17 + the single-ownership registry as law; if spec 11 prose disagrees with a frozen decision, the frozen decision wins.
2. **Verify the existing scaffolding** matches the spec's frozen enums/states before writing handlers (open the 3 enums + 5 entities + 5 EF configs listed in §2.3). Confirm `CatalogStatus` has exactly the 9 D4 values, `WaitlistOfferStatus` the 7 values, saga states per §5.3.
3. **Create a plan doc** under `thoughts/shared/plans/2026-06-30_spec-11-*.md` using the established format (see `thoughts/shared/plans/2026-06-21_reconnect-handler-01G.md` as the template: YAML front matter `task/status/last_updated`; Overview → Current State → Scope/Out-of-scope → Architecture Decisions (tabular) → TDD test skeletons before production stubs). Consider the `/create-plan` skill.
4. **Slice the work** — implement and test one vertical at a time, in this order (each independently shippable, mirrors how 09/10 landed):
   - **(i) Menu read path** (queries): `SearchVendorCatalog` + `GetCatalogItem` over `Published` only — smallest, foreground-facing, unblocks langgraph tools.
   - **(ii) Menu ingestion lane**: `menu.commands` consumer + `IArtifactScanner` + isolated extraction + review/approve/publish handlers + state machine + `MenuController`.
   - **(iii) Waitlist lane**: enrollment + `waitlist.commands` Match/Accept/Decline/Expire handlers + offer state machine + expiry scheduling + WaitlistController.
   - **(iv) Pre-order saga**: `PreorderSagaCoordinator` 5-step + compensation + `LiveItemRevalidationService` + `CommitPreorder`.
   - **(v) Cross-service contracts + langgraph draft + web review UI**.
5. **For every vertical**: handler + validator + repo (if needed) + state machine + typed exceptions + consumer-registry factory + `AddApplication`/`AddInfrastructure` DI + unit tests + Alba integration tests + (if schema changes) a new migration. Run `bun nx test api`, `bun nx test-integration api`, `bun nx lint api` green before moving on. Tee output to `.tmp/` (don't pipe to `tail`).
6. **Keep it minimal** — match the precedent files line-for-line in style; new `.cs` files UTF-8 **with BOM**; don't touch generated files or applied migrations.

---

## 8. Key file index (open these as templates when implementing)

| Purpose | Template file |
|---|---|
| Queue consumer command (IInboxConsumerRequest) | `Application/Handlers/Notifications/SendNotification/SendNotificationCommand.cs` |
| Queue consumer handler (inbox-txn discipline, settle-don't-throw) | `.../SendNotification/SendNotificationCommandHandler.cs` |
| Validator | `.../SendNotification/SendNotificationValidator.cs` |
| Domain state machine (FrozenDictionary edges) | `Domain/Entities/NotificationStateMachine.cs` |
| Optimistic-concurrency repo transition | `Infrastructure/Repositories/WaitlistOfferRepository.cs` |
| EF config (jsonb, FK RESTRICT, version token) | `Infrastructure/EntityFramework/Configurations/PreorderSagaConfiguration.cs` |
| Consumer-registry factory registration | `Application/Extensions/ApplicationServiceCollectionExtensions.cs` |
| Logical-queue routing (waitlist./menu. already wired) | `Domain/Messaging/LogicalQueue.cs` |
| Provider/port stub (IArtifactScanner pattern) | `Infrastructure/Providers/EmailNotificationAdapter.cs` / `StubBookingProviderAdapter.cs` |
| Hosted service (poller / sweeper) | `Infrastructure/Messaging/ScheduledJobPollerHostedService.cs` |
| Thin controller | `Api/Controllers/BookingsController.cs` |
| Typed exception + mapping | `Domain/Exceptions/*` + `Api/Errors/GlobalExceptionHandler.cs` |
| Hand-authored job-event envelope | `libs/shared-schemas/schemas/job-events/notification.requested.v1.json` |
| Foreground `@tool` delegate | `apps/langgraph/cordia_agent/tools/foreground.py` |
| Plan doc format | `thoughts/shared/plans/2026-06-21_reconnect-handler-01G.md` |
| Research doc format | `thoughts/shared/research/2026-06-21_13-39-44_reconnect-handler-01G.md` |
