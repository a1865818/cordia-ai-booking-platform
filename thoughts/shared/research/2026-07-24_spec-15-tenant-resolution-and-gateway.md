---
date: 2026-07-24 22:22:17 ACST
researcher: quangnguyentechno@gmail.com
git_commit: 3f00cdce92307650ec74e3262e786f0e0218bf2c
branch: feature/spec-15-3
repository: Cordia
topic: "Spec 15 — Tenant Resolution & Channel Gateway: current codebase patterns & existing implementation"
tags: [research, codebase, tenant-resolution, gateway, multi-tenancy, jws, correlation, dotnet, langgraph]
status: complete
last_updated: 2026-07-24
last_updated_by: quangnguyentechno@gmail.com
---

# Research: Spec 15 — Tenant Resolution & Channel Gateway

**Date**: 2026-07-24 22:22:17 ACST
**Researcher**: quangnguyentechno@gmail.com
**Git Commit**: 3f00cdce92307650ec74e3262e786f0e0218bf2c
**Branch**: feature/spec-15-3
**Repository**: Cordia

## Research Question

Understand current codebase patterns and existing implementation relevant to
[`docs/specs/15-tenant-resolution-and-gateway.md`](../../../docs/specs/15-tenant-resolution-and-gateway.md):
.NET clean-architecture patterns (Domain/Application/Infrastructure/Api), CQRS/MediatR,
FluentValidation, EF Core mapping, multi-tenancy, `conversation_sessions`/`tenant_channel_endpoints`
tables, `correlation_id`/observability, the langgraph `TenantContext` Pydantic source-of-truth,
JWS/signing infrastructure — and how these compare to what spec 15 requires.

## Summary

**Spec 15 is far less greenfield than the spec text implies.** The trust-boundary *plumbing*
it depends on is already built and stable; what is genuinely missing is the narrow ingress core:
the **JWS signer/verifier**, the **resolver logic** over the (already-existing) registry table, the
**`CreateConversationSessionCommand`**, the **tenant-resolution audit append**, and the swap of the
`X-Tenant-Id` dev shim for signed-context resolution.

**Already implemented and reusable:**
- Full multi-tenancy spine — `ITenantContext`, `ITenantScoped`, global EF `HasQueryFilter`, fail-closed `TenantGuard`.
- All three tables spec 15 touches — `tenant_channel_endpoints` (with the **exact `uq_channel_signal_active` filtered-unique index from OD-2**), `conversation_sessions`, `audit_events` — as entities + configs + migrations + repositories.
- Full correlation stack in **both** apps (ambient context, ingress minter middleware, Serilog enricher, MediatR behavior, EF stamping interceptor).
- `audit_events` write path (`IAuditWriter`) + sanitization interceptor; `ISecretStore` custody seam.
- langgraph `TenantContext` model, `resolve_tenant`/`load_context` nodes, verifier **port** + registry + HMAC **test stub**, and a 10-test suite.

**Absent — spec 15 must build:**
- `ITenantContextSigner` + JWS sign/verify in `apps/api` (no JWS/JWT/EdDSA/HMAC/key-rotation exists).
- Production langgraph verifier (registry currently throws `"spec 15 not wired"`).
- Resolver logic mapping `(channel, signal_type, signal_value)` → tenant with `tenant_not_resolved`/`ambiguous_endpoint` outcomes (error tokens don't exist yet).
- `CreateConversationSessionCommand` (D12 single-writer insert path).
- The `tenant_resolution` audit event append on every resolution outcome.
- Replacement of the `X-Tenant-Id` shim (`TenantResolutionMiddleware`) with signed-context verification.
- **Contract mismatches** between the spec's `TenantContext` and the existing langgraph model (see below) — must be reconciled.
- Signing algorithm remains open (**OD-1**: HS256 vs EdDSA/ES256).

---

## Detailed Findings

### A. Multi-tenancy spine — EXISTS (reuse as-is)

- `ITenantContext` — `apps/api/src/Cordia.Domain/Interfaces/ITenantContext.cs` (read-only `string TenantId`).
- Settable scoped impl — `apps/api/src/Cordia.Infrastructure/Services/TenantContext.cs`; registered twice (interface + concrete) as the *same* scoped instance so middleware setter and DbContext filter share state — `InfrastructureServiceCollectionExtensions.cs:74-75`.
- `ITenantScoped` marker — `apps/api/src/Cordia.Domain/Interfaces/ITenantScoped.cs`.
- **Global EF query filter** — `ApplicationDbContext.cs:94-118` (`ApplyTenantFilters`) reflects over every `ITenantScoped` entity and applies `HasQueryFilter(e => e.TenantId == _tenantContext.TenantId)` (Inv-13). Note `TenantChannelEndpoint` and `AuditEvent` are deliberately **not** `ITenantScoped` (cross-tenant resolver row / append-only audit).
- **Fail-closed guard** — `apps/api/src/Cordia.Domain/Security/TenantGuard.cs`: `RequireTenant(ITenantContext)` throws `TenantScopeMissingException` (→ HTTP 400) when empty. Exception: `Cordia.Domain/Exceptions/TenantScopeMissingException.cs`; mapped at `GlobalExceptionHandler.cs:89`.
- Tests: `UnitTests/Security/TenantGuardTests.cs`, `IntegrationTests/Security/{TenantIsolationTests,QueueConsumerTenantIsolationTests}.cs`.

### B. The dev-shim spec 15 replaces — `TenantResolutionMiddleware`

- `apps/api/src/Cordia.Api/Middleware/TenantResolutionMiddleware.cs:12` — reads `X-Tenant-Id` header, sets `TenantContext.TenantId`, and **does NOT hard-fail when absent** (line 27). Its doc-comment explicitly states it is *"dev/test scaffolding until spec 08 lands"* and cites Inv-13 (tenant must come from a signed context, not a client header).
- Registered in `Program.cs:172`, after `CorrelationMiddleware` (`:169`), with comment noting replacement "by signed-context resolution in spec 08/15".
- Spec 15 replaces/hardens this into signed-`TenantContext` verification (its `TenantContextVerificationMiddleware`, spec §4/§11).

### C. The three tables — ALL EXIST (entity + config + migration + repo)

| Table | Entity | Config | Migration | Repo |
|---|---|---|---|---|
| `tenant_channel_endpoints` | `Domain/Entities/TenantChannelEndpoint.cs` | `Configurations/TenantChannelEndpointConfiguration.cs` | `20260625153819_CatalogsPaymentsWaitlistChannels.cs:112-130` | `Repositories/TenantChannelEndpointRepository.cs` |
| `conversation_sessions` | `Domain/Entities/ConversationSession.cs` | `Configurations/ConversationSessionConfiguration.cs` | `20260625141115_InitialSchema.cs:23-43` | `Repositories/ConversationSessionRepository.cs` |
| `audit_events` | `Domain/Entities/AuditEvent.cs` | `Configurations/AuditEventConfiguration.cs` | `20260625152614_NotificationsVerificationAudit.cs:45-63` | `Repositories/AuditEventRepository.cs` |

Key points:
- **`tenant_channel_endpoints` already has the OD-2 structure.** `TenantChannelEndpointConfiguration.cs:30-33`:
  ```csharp
  builder.HasIndex(e => new { e.Channel, e.SignalType, e.SignalValue })
      .IsUnique()
      .HasDatabaseName("uq_channel_signal_active")
      .HasFilter("status = 'active'");
  ```
  PK `(tenant_id, channel_endpoint_id)`; fields `Channel`, `SignalType`, `SignalValue`, `ConfigurationVersion`, `PolicyBundleId`, `Status`, `Version`. Enums: `TenantChannel`, `SignalType`, `EndpointStatus` (all in `Domain/Enums/`).
  Existing repo read (`TenantChannelEndpointRepository.cs:27-35`) already uses `.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(...)` — but it is a plain single-row read; the spec's exactly-one / zero→`tenant_not_resolved` / many→`ambiguous_endpoint` semantics are **not** implemented.
- **`conversation_sessions`** exists with `channel`, `consent_state?`, consent/recording fields; the controller (`ConversationSessionsController.cs`) + repo only do **read + consent/transcript UPDATE**. There is **no `CreateConversationSessionCommand`** (D12 gateway insert) — grep found none.
- **`audit_events`** is append-only, PK `(tenant_id, event_id)`, implements `ITenantScoped`, carries `CorrelationId`/`OperationId`, `metadata` jsonb.

> Note: an earlier spec-14 baseline research doc claimed `tenant_channel_endpoints` was "completely absent." That is **stale** — the live codebase at this commit has it fully. Live findings supersede the historical note.

### D. CQRS / MediatR / FluentValidation conventions to mirror

- **Command triad per use case**: folder `Cordia.Application/Handlers/<Vertical>/<UseCase>/` containing `XxxCommand.cs` (`sealed record ... : IRequest<TResult>`, **`TenantId` as first param**), `XxxCommandHandler.cs` (`: IRequestHandler<,>`, primary-ctor DI), and optional `XxxValidator.cs`.
  - Simplest reference triad: `Handlers/Waitlist/Enroll/` (`EnrollWaitlistCommand.cs` + handler + `EnrollWaitlistValidator.cs`).
  - Result records use `[property: JsonPropertyName("snake_case")]`.
- **Validation**: `AddValidatorsFromAssembly(assembly)` (`ApplicationServiceCollectionExtensions.cs:66`); runs via `ValidationPipelineBehavior.cs:41` (throws `FluentValidation.ValidationException` → 400). Validators target the MediatR command, not the HTTP DTO. Example: `SubmitBookingCancellationValidator.cs` (`RuleFor(x => x.TenantId).NotEmpty()`, `When(...)`).
- **MediatR pipeline** (registered in order, `ApplicationServiceCollectionExtensions.cs:51-61`): Validation → OperatorAction → InboxDedup → CorrelationEnrichment.
- **Controllers** are thin (`Cordia.Api/Controllers/`, 15 total): `[ApiController]`, inject `IMediator` + `ITenantContext`, call `TenantGuard.RequireTenant(tenant)`, read `[FromHeader(Name="X-Actor")]`, `mediator.Send(...)`. Reference: `WaitlistController.cs:28`, `NotificationWebhooksController.cs:32` (closest existing **authenticated ingress/webhook** pattern).
- **Exception→HTTP mapping**: `Cordia.Api/Errors/GlobalExceptionHandler.cs` (`IExceptionHandler`, switch on exception type; RFC7807 `ProblemDetails`). All domain exceptions extend `Domain/Exceptions/DomainException.cs`. Spec 15's `TenantNotResolvedException`/`AmbiguousEndpointException` are new arms to add here (map to channel-appropriate responses, never 500/fallback tenant).

### E. EF Core mapping conventions

- `ApplicationDbContext.cs` — DbSets as expression-bodied `Set<T>()`; `OnModelCreating` sets schema `public`, `ApplyConfigurationsFromAssembly(...)`, then tenant filters. Interceptors registered in `OnConfiguring` (`AuditEventSanitizationInterceptor`) and DI (`CorrelationStampingInterceptor`, singleton).
- **snake_case** via `.UseSnakeCaseNamingConvention()` in DI (`InfrastructureServiceCollectionExtensions.cs:67`), *not* the context — so configs use PascalCase and never call `HasColumnName` for ordinary columns.
- **pg-enums** — three layers: (a) CLR enum with `[PgName]` + `[JsonStringEnumMemberName]` (e.g. `Domain/Enums/EndpointStatus.cs`); (b) `.MapEnum<TEnum>("pg_name")` per enum in `InfrastructureServiceCollectionExtensions.cs:39-66` (incl. `.MapEnum<TenantChannel>("channel_enum")`, `.MapEnum<EndpointStatus>("endpoint_status_enum")`); (c) `tools/config/ef-enum-mappings.json` feeds codegen injection (currently only 2 entries); (d) `Repositories/PgEnum.cs` helper for raw-SQL paths.
- **Write patterns**: append-only `Add`+`SaveChanges` (`AuditEventRepository.cs`); set-based `ExecuteUpdateAsync` with manual `Version+1` (`ConversationSessionRepository.cs:32-42`); optimistic concurrency via `EntityFramework/Concurrency/VersionTokenUpdate.cs` (`IgnoreQueryFilters().Where(key).Where(version==expected).ExecuteUpdateAsync(...)`).
- **No caching layer exists** anywhere in `apps/api` (no `IMemoryCache`/`IDistributedCache`/Redis). Spec 15's §8 "hot endpoints cached / cached by configuration_version" would be a **new pattern** with no precedent to mirror.
- Migrations: `EntityFramework/Migrations/` (30+); design-time factory `DesignTimeDbContextFactory.cs`.

### F. Correlation / observability — FULLY EXISTS (both apps)

apps/api:
- `Domain/Observability/AmbientCorrelationContext.cs` (record: `TenantId, CorrelationId, CausationId, SessionId, TurnId, OperationId` + `.Empty`); `Domain/Interfaces/ICorrelationContext.cs`; `Infrastructure/Services/CorrelationContextHolder.cs` (AsyncLocal).
- `Cordia.Api/Middleware/CorrelationMiddleware.cs:19` — **ingress minter**: echoes valid inbound `X-Correlation-Id`, else mints `corr_{guid}` once (never re-mints), regex-allowlists ids (`^[A-Za-z0-9_-]{1,64}$`), stamps OTel Activity, runs **before** tenant resolution. This is the template for a new signed-ingress middleware's shape.
- `Infrastructure/Observability/SerilogCorrelationEnricher.cs`; `Application/Observability/CorrelationEnrichmentBehavior.cs`; `Infrastructure/EntityFramework/Interceptors/CorrelationStampingInterceptor.cs` (marker `Domain/Observability/ICorrelatable.cs`).

apps/langgraph:
- `cordia_agent/observability/correlation.py` — `CorrelationContext` Pydantic model (source of truth → generated C#/TS; `libs/shared-schemas/schemas/models/CorrelationContext.json`).

**Spec 15 implication**: it "originates" `correlation_id`. The origination already happens in `CorrelationMiddleware`; spec 15 must make `correlation_id` a **signed claim** in the `TenantContext` and ensure it is not re-minted downstream.

### G. Secret store & signing — the core gap

- **Custody seam EXISTS**: `Domain/Interfaces/ISecretStore.cs` + `Infrastructure/Security/ConfigurationSecretStore.cs` (reads `Secrets` config section; never logs/serializes). Used by `NotificationWebhooksController.cs:60` for a constant-time shared-secret compare (`GetSecret("NotificationsWebhookSecret")`). Spec 15 says signing keys live here.
- **NO signer/verifier**: grep for `ITenantContextSigner`, `TenantContextSigner`, `tenantctx`, `kid` → **zero files**. No JWS, no JWT (`JwtSecurityToken`), no EdDSA/ES256, no `ECDsa`/`RSA` signing, no key-rotation anywhere in `apps/api/src`. Only crypto-adjacent code: the webhook shared-secret (with a `TODO §31: replace with full HMAC signature verification`) and `Storage/EncryptedFileObjectStore.cs` (at-rest encryption, unrelated).
- **This is spec 15's central build**: `JwsTenantContextSigner` in Infrastructure + `ITenantContextSigner` port in Domain, compact JWS with `kid` header, active-key set, zero-downtime rotation. Algorithm is **OD-1** (unresolved).

### H. audit_events write path — EXISTS (caller for spec 15 does not)

- `Application/Common/Audit/IAuditWriter.cs` (+ `AuditWriter.cs`, `AuditRecord.cs`, `AuditEnvelope.cs`) — D13 convention: append inside the same transaction, sanitized `metadata` jsonb (Inv-12). Sanitization via `Interceptors/AuditEventSanitizationInterceptor.cs`.
- Existing writers: `RecordDeliveryReceiptCommandHandler`, catalog approve/publish, waitlist auto-accept, preorder conversion, `RetentionSweepHandler`, `AuditMissingTerminalNotificationCommandHandler`.
- **Absent**: no `tenant_resolution` audit event type/write is wired (spec 15 §6 wants signal type + resolved tenant + endpoint + outcome appended on **every** resolution, including failures). Spec also wants the append to be **fire-and-async** (§8) so it doesn't block handoff — existing writers are synchronous in-transaction, so the async-audit shape is a new consideration.

### I. langgraph `TenantContext` / resolve_tenant — EXISTS, with contract mismatches

- **Model already exists** at `cordia_agent/safety/tenant_context.py:21-43` (frozen, `extra="forbid"`). Also defines `InvalidTenantContext`, `TenantContextVerifier` Protocol, and a pluggable registry (`register_verifier`/`get_verifier`/`clear_verifier`/`verify_signed_context`). `get_verifier()` currently **raises** `RuntimeError("no TenantContext verifier registered (spec 15 not wired)")`.
- **`resolve_tenant` node exists** (`cordia_agent/nodes/resolve_tenant.py:25-62`): reads `state.signed_tenant_context`, calls `verify_signed_context(token)` (hard gate), injects ctx into the `ToolClient`, returns `tenant_id`/`tenant_policy_version`/`correlation_id`/`channel_endpoint_id`/`allowed_policy_ids`/`tenant_context_verified=True`. Wired first in the graph: `graph.py:64` `START → resolve_tenant → consent_gate → load_context`.
- **`load_context` node exists** (`nodes/load_context.py:30-107`) — runs only after resolve_tenant.
- **Test stub** (test-only HMAC-SHA256): `tests/_stubs/tenant_context_stub.py` — header says *"FROZEN SPEC-15 CONTRACT FAKE — replace when spec 15 lands."* 10-test suite in `tests/unit_tests/safety/test_tenant_guard.py`.

**Contract mismatches to reconcile (spec §5.1 vs existing model):**

| Spec 15 `TenantContext` field | Existing `safety/tenant_context.py` | Gap |
|---|---|---|
| `channel: TenantChannel` (`voice/messaging/web/email`) | `channel: Channel` (`voice/**sms**/web/email`) | enum value `messaging` vs `sms`; reuses `state.Channel` |
| `session_id` | *absent* | must add |
| `allowed_agent_policy_ids` + `allowed_tool_policy_ids` (split) | single `allowed_policy_ids` | must split |
| `issued_at` / `expires_at` (AwareDatetime) | *absent* | must add (needed for verifier expiry check) |
| location `models/tenant_context.py` (spec §11) | actually in `safety/tenant_context.py` | location difference |
| registered as shared schema (C#/TS/Zod) | **NOT** in `export_pydantic_schemas.py` MODEL_REGISTRY | must register for cross-service contract |

- **Schema export** (`tools/scripts/export_pydantic_schemas.py`): explicit registration (not reflection) via `MODEL_REGISTRY` + `ENUM_REGISTRY`; owned subdirs `enums, models, graph-inputs, graph-outputs, configurations, streaming, shared`. `Channel` is already exported; adding `TenantContext` = add import + append to `MODEL_REGISTRY["models"]`.

---

## Code References

- `apps/api/src/Cordia.Api/Middleware/TenantResolutionMiddleware.cs:12` — dev-shim spec 15 replaces
- `apps/api/src/Cordia.Api/Middleware/CorrelationMiddleware.cs:19` — ingress minter (template)
- `apps/api/src/Cordia.Infrastructure/EntityFramework/ApplicationDbContext.cs:94-118` — global tenant query filter
- `apps/api/src/Cordia.Domain/Security/TenantGuard.cs` — fail-closed guard
- `apps/api/src/Cordia.Domain/Entities/TenantChannelEndpoint.cs` — registry entity
- `apps/api/src/Cordia.Infrastructure/EntityFramework/Configurations/TenantChannelEndpointConfiguration.cs:30-33` — `uq_channel_signal_active` (OD-2)
- `apps/api/src/Cordia.Infrastructure/Repositories/TenantChannelEndpointRepository.cs:27-35` — existing read (no exactly-one/ambiguous semantics)
- `apps/api/src/Cordia.Domain/Interfaces/ISecretStore.cs` + `Infrastructure/Security/ConfigurationSecretStore.cs` — key custody seam
- `apps/api/src/Cordia.Api/Controllers/NotificationWebhooksController.cs:32-60` — authenticated-ingress pattern + HMAC TODO
- `apps/api/src/Cordia.Api/Errors/GlobalExceptionHandler.cs:89` — tenant-exception mapping
- `apps/api/src/Cordia.Application/Handlers/Waitlist/Enroll/` — simplest command/handler/validator triad to mirror
- `apps/api/src/Cordia.Application/Extensions/ApplicationServiceCollectionExtensions.cs:51-66` — MediatR + validator registration
- `apps/api/src/Cordia.Infrastructure/Extensions/InfrastructureServiceCollectionExtensions.cs:24-176` — AddInfrastructure, `.MapEnum`, repo registration
- `apps/api/src/Cordia.Application/Common/Audit/IAuditWriter.cs` — audit write path (D13)
- `apps/langgraph/cordia_agent/safety/tenant_context.py:21-120` — model + verifier port/registry
- `apps/langgraph/cordia_agent/nodes/resolve_tenant.py:25-62` — verification node (Inv-13 gate)
- `apps/langgraph/cordia_agent/graph.py:64-83` — START → resolve_tenant → consent_gate → load_context
- `apps/langgraph/tests/_stubs/tenant_context_stub.py` — FROZEN spec-15 HMAC test stub
- `tools/scripts/export_pydantic_schemas.py:136-243` — MODEL_REGISTRY / ENUM_REGISTRY / OWNED_SUBDIRS

## Architecture Insights

- **The platform pre-wired the trust boundary as seams awaiting spec 15.** The `X-Tenant-Id` shim, the throwing verifier registry, the frozen test stub, and the unused registry table are all explicit "spec 15 lands here" markers. This is a fill-in-the-core job, not a from-scratch build.
- **Inv-13 ordering is enforced structurally already** — the global EF query filter + `TenantGuard` mean an unscoped read fails closed regardless of ingress. Spec 15 supplies the *trustworthy* tenant; the platform already *applies* it.
- **Pydantic-source-of-truth discipline** means the `TenantContext` contract must be reconciled and registered in the export pipeline so the C#/TS/Zod shapes generate — otherwise the API and agent verify different shapes. The existing model is `sms`-based and lacks `session_id`/`issued_at`/`expires_at`; aligning these is a prerequisite before any signer work.
- **Two open design forks**: OD-1 (HS256 vs EdDSA/ES256 — drives whether the agent verifies with a shared secret or a public key) and OD-3 (TTL). Neither changes the contract shape, but OD-1 determines the `ISecretStore` key layout and the langgraph verifier's crypto.
- **New patterns with no precedent**: (1) endpoint-registry caching (§8) — no caching layer exists; (2) fire-and-async audit append (§8) — existing audit writes are synchronous in-transaction.

## Historical Context (from thoughts/)

From `thoughts/shared/research/2026-07-09_18-55-30_spec-14-security-privacy-baseline.md` and `2026-07-10_23-30-00_spec-14-sibling-linkage-baseline.md` (spec-14 baselines, older commits):
- Spec 14 was required to leave `TenantResolutionMiddleware` **byte-for-byte** and explicitly deferred signed-`TenantContext` minting to spec 15 (D15). The langgraph verifier already expects signed contexts.
- `audit_events` + D13 write path (`IAuditWriter.AddAsync` inside the mutating transaction) are **fixed contracts** — reuse, no new columns/migrations ("zero migrations" was a hard constraint across spec-14 runs; extra audit fields ride inside `metadata` jsonb).
- Session **creation** was flagged as unowned and belonging to "spec 15/02" — spec 14 only added the consent-UPDATE path.
- **Caveat**: these baselines predate the current commit and state `tenant_channel_endpoints` is "absent" — the live codebase now has it. Trust live findings.

Related spec docs: `docs/specs/12-data-model-and-persistence.md` (tenant_id-leading keys, PKs `(tenant_id, <id>)`, append-only `audit_events`), `13-observability-and-slos.md` (correlation set once at ingress, `causation_id` per hop, build-failing null-correlation contract test), `14-security-and-privacy.md` (§24.1 tenant-scope guard, owns `audit_events` write path, Inv-13 signed-context requirement).

## Related Research

- `thoughts/shared/research/2026-07-09_18-55-30_spec-14-security-privacy-baseline.md`
- `thoughts/shared/research/2026-07-10_23-30-00_spec-14-sibling-linkage-baseline.md`
- `thoughts/shared/research/2026-07-10_01-05-31_spec-14-gap-closure-baseline.md`

## Open Questions

1. **OD-1 (signing algorithm)** — HS256 (shared secret) vs EdDSA/ES256 (agent verifies with public key). Determines `ISecretStore` key layout + langgraph verifier crypto. Is `apps/langgraph` treated as lower-trust?
2. **OD-3 (TTL)** — `expires_at` window; pending measured ingress+setup latency (ties to spec 13 baselines).
3. **`TenantContext` reconciliation** — align the langgraph model (add `session_id`/`issued_at`/`expires_at`, split policy ids, `messaging` vs `sms` enum, move to `models/` or keep in `safety/`?) and register it in the schema export so C#/TS/Zod generate. Is the `Channel` enum's `sms` value changing to `messaging` a breaking change for other consumers?
4. **Endpoint-registry caching** — §8 wants hot-endpoint caching keyed by `configuration_version`, but no caching abstraction exists. Introduce `IMemoryCache`, or skip caching for v1?
5. **Async audit append** — §8 wants fire-and-async; existing audit writes are synchronous in-transaction. Which wins for the resolution audit (durability of the failed-resolution record vs handoff latency)?
6. **Co-located vs HTTP for `CreateConversationSessionCommand`** — §8 assumes in-process MediatR send when API+gateway share the process. Are they co-located in this deployment, or is the gateway a separate process needing an HTTP client?
