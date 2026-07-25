---
date: 2026-07-25 09:59:27 ACST
researcher: quangnguyentechno@gmail.com
git_commit: 3f00cdce92307650ec74e3262e786f0e0218bf2c
branch: feature/spec-15-3
repository: Cordia
task: "Spec 15 — Tenant Resolution & Channel Gateway"
tags: [implementation-plan, tenant-resolution, gateway, jws, tenant-context, ingress, langgraph]
status: draft
last_updated: 2026-07-25
last_updated_by: quangnguyentechno@gmail.com
---

# Spec 15 — Tenant Resolution & Channel Gateway Implementation Plan

## Overview

Implement the trust boundary at channel ingress: resolve a tenant from a **channel-controlled
signal**, mint a **signed short-lived `TenantContext`** (compact JWS, HS256), originate the
`correlation_id`, and cause the Booking API to create the tenant-scoped `conversation_sessions`
row (D12). Downstream verifiers (the `apps/api` middleware and the `apps/langgraph` `resolve_tenant`
node) accept `tenant_id` only from a verified context (Inv-13).

The platform plumbing already exists (multi-tenancy spine, the three DB tables, correlation stack,
`ISecretStore`, `IAuditWriter`, the langgraph verifier port + frozen HMAC test stub). This plan
fills the missing ingress core: the signer/verifier, the resolver semantics, the two issuance
commands, the ingress controllers + verification middleware, and the production langgraph verifier.

## Current State Analysis

**Already built (reuse as-is):**
- Multi-tenancy spine: `ITenantContext`/`TenantContext` (`Cordia.Infrastructure/Services/TenantContext.cs`), `ITenantScoped`, global EF `HasQueryFilter` (`ApplicationDbContext.cs:94-118`), fail-closed `TenantGuard` (`Cordia.Domain/Security/TenantGuard.cs`) + `TenantScopeMissingException`.
- Three tables as entity + config + migration + repo:
  - `tenant_channel_endpoints` — `TenantChannelEndpoint.cs`, config with the **`uq_channel_signal_active` partial-unique index** (OD-2 satisfied), repo `TenantChannelEndpointRepository.cs` (`ResolveActiveAsync` returns a single row via `FirstOrDefaultAsync`). Not `ITenantScoped` (cross-tenant resolver).
  - `conversation_sessions` — `ConversationSession.cs` (`ITenantScoped`), repo `IConversationSessionRepository` (read + `RecordConsentAsync` only — **no create path**).
  - `audit_events` — `AuditEvent.cs` (append-only), `IAuditWriter` write path.
- Enums already correct on the C# side: `TenantChannel` = `voice/messaging/web/email`; `SignalType` = all six values (`phone_number`, `messaging_recipient`, …). pg-enums registered via `.MapEnum<>()` in `InfrastructureServiceCollectionExtensions.cs:39-66`.
- Correlation stack: `CorrelationMiddleware.cs:19` (mints/echoes `corr_{guid}` once), `AmbientCorrelationContext`, `ICorrelationContext`/`CorrelationContextHolder`, `SerilogCorrelationEnricher`, `CorrelationEnrichmentBehavior`.
- Secret custody: `ISecretStore` + `ConfigurationSecretStore` (reads `Secrets` config section; never logs/serializes). Used already by `NotificationWebhooksController.cs:60` (constant-time compare).
- langgraph: `TenantContext` Pydantic model (`cordia_agent/safety/tenant_context.py:21`), verifier `Protocol` + registry (`get_verifier()` throws `"spec 15 not wired"`), `resolve_tenant` node (`nodes/resolve_tenant.py:25`) hard-gating before `load_context`, and a **frozen HMAC test stub** (`tests/_stubs/tenant_context_stub.py`) + 10-test suite (`tests/unit_tests/safety/test_tenant_guard.py`).

**Missing (this plan builds):**
- No JWS/JWT/HMAC/EdDSA signer anywhere in `apps/api` (grep for `ITenantContextSigner`, `kid`, `tenantctx` → zero). This is the central build.
- No production langgraph verifier (registry throws until `register_verifier` is wired).
- Resolver ambiguity semantics: `FirstOrDefaultAsync` silently hides a duplicate; `many → ambiguous_endpoint` is not detected. Error tokens `tenant_not_resolved`/`ambiguous_endpoint` don't exist.
- No `ResolveTenantContextCommand` / `CreateConversationSessionCommand`.
- No `tenant_resolution` audit append.
- `TenantResolutionMiddleware.cs` is the `X-Tenant-Id` dev shim to be superseded by signed-context verification.
- **Contract mismatch** on the langgraph `TenantContext`: it lacks `session_id`, `issued_at`, `expires_at`; has a single `allowed_policy_ids` (spec splits into agent/tool); reuses `state.Channel` (`sms`) instead of a `messaging` enum; and is **not registered** in `export_pydantic_schemas.py` (no generated C#/TS/Zod shape).

### Key Discoveries:
- Ingress lives inside `apps/api` → gateway is **co-located**; `CreateConversationSessionCommand` is an in-process MediatR send (no HTTP client). (§8 co-location note.)
- The C# `TenantChannel`/`SignalType` enums are hand-written in `Cordia.Domain/Enums/` (namespace `Cordia.Domain.Enums`), separate from generated `Cordia.Domain.Generated`. The Pydantic-generated `TenantContext` will land in `Cordia.Domain.Generated` — the API command/handler will map between the generated claim DTO and these hand-written enums.
- Reference command triad to mirror: `Cordia.Application/Handlers/Waitlist/Enroll/`. Reference authenticated ingress: `NotificationWebhooksController.cs`. Reference ingress middleware shape: `CorrelationMiddleware.cs`.
- New `.cs` files **must be UTF-8 with BOM** (`.editorconfig`) or `bun nx lint api` fails.

## Desired End State

An inbound voice or messaging webhook to `apps/api` resolves the tenant from its transport-attested
signal, mints a signed `TenantContext` JWS (HS256, `kid`, short TTL), originates/echoes one
`correlation_id`, creates the `conversation_sessions` row via a single-writer MediatR command, and
appends a `tenant_resolution` audit event — or fails closed (no session, no context, audited
`tenant_not_resolved`/`ambiguous_endpoint`) with a channel-appropriate response. The API middleware
and the langgraph `resolve_tenant` node both verify the JWS before any data access, using the same
shared secret and canonical form.

**Verification**: `bun nx test api`, `bun nx test-integration api`, `bun nx test langgraph`, and
`bun nx lint api` all pass; the langgraph verifier registry no longer throws in production; a full
ingress integration test creates exactly one session row with `tenant_id` leading the PK and the
`correlation_id` echoed onto both the session and the audit row.

## What We're NOT Doing

- **Channels beyond voice + messaging**: web-widget, email, web-origin, and directory ingress
  controllers are out of scope (the registry/enums already support them; adding controllers is a
  later increment). OD-4/OD-5 auth-binding details stay deferred to spec 14/08.
- **Asymmetric signing (EdDSA/ES256)**: HS256 shared-secret only (OD-1 decision). The signer is
  built behind `ITenantContextSigner` so a future alg swap is localized.
- **Endpoint-registry caching** (§8 optimization): direct `.AsNoTracking()` read in v1 (no caching
  precedent exists). Revisit once latency is measured.
- **Fire-and-async audit** (§8 optimization): the `tenant_resolution` audit is written synchronously
  for durability (failed resolutions have no session — the audit is their only record). Deferred.
- **Changing `state.Channel`** (the `sms`-valued enum used by other models): untouched. A dedicated
  `TenantChannel` Pydantic enum is introduced for the context only.
- **Consent fields on session create**: left null for the spec-14 consent gate to UPDATE (D12).
- **New EF migration for the three tables**: they already exist. No schema change is expected; if a
  column proves missing during Phase 3, that is a flagged deviation, not a silent migration.

## Implementation Approach

TDD (RED → GREEN → REFACTOR) per phase. The langgraph frozen stub and its 10-test suite define the
verifier contract; the C# signer is tested against the same canonical form so both sides interoperate.
Phases are ordered so the shared contract (Phase 1) precedes the crypto (Phase 2), which precedes the
handlers that assemble+sign claims (Phase 3), the HTTP edge (Phase 4), and finally the langgraph
production verifier (Phase 5) that must match Phase 2's output byte-for-byte.

**Canonical wire form (shared by Phases 2 and 5)** — compact JWS:
`base64url(header).base64url(payload).base64url(signature)`, `header = {"alg":"HS256","kid":<id>,"typ":"tenantctx+jws"}`,
`payload` = canonical JSON of the claim set (UTF-8, sorted keys, `(",",":")` separators, `ensure_ascii`),
signature = HMAC-SHA256 over `base64url(header) + "." + base64url(payload)` (standard JWS signing input).

> Note the existing frozen stub signs `payload.signature` (2-part, no header) over canonical JSON of
> the claims. Phase 1 migrates the stub and the node to the real 3-part JWS form so the stub stays a
> faithful contract fake; Phase 5's production verifier and Phase 2's signer share this exact form.

---

## Phase 1: `TenantContext` contract alignment (source of truth + regen)

### Phase 1: Overview
Bring the langgraph Pydantic `TenantContext` to spec §5.1, introduce a `TenantChannel` enum
(`messaging`), register both in the schema export, regenerate C#/TS/Zod, and update the stub, node,
and `AgentState` to the new shape. No crypto yet.

### Phase 1: Changes Required

#### 1. Pydantic model + enum (source of truth)
**File**: `apps/langgraph/cordia_agent/safety/tenant_context.py`
**Changes**: Add a `TenantChannel(str, Enum)` (`voice/messaging/web/email`); expand `TenantContext`
to the full §5.1 claim set; keep `frozen=True, extra="forbid"`.

```python
from enum import Enum
from pydantic import AwareDatetime

class TenantChannel(str, Enum):
    voice = "voice"
    messaging = "messaging"
    web = "web"
    email = "email"

class TenantContext(BaseModel):
    model_config = {"extra": "forbid", "frozen": True}
    tenant_id: str = Field(...)
    channel: TenantChannel = Field(...)              # was state.Channel (sms)
    channel_endpoint_id: str = Field(...)
    configuration_version: str = Field(...)
    correlation_id: str = Field(...)
    session_id: str = Field(...)                     # NEW
    allowed_agent_policy_ids: list[str] = Field(...) # split from allowed_policy_ids
    allowed_tool_policy_ids: list[str] = Field(...)  # NEW
    issued_at: AwareDatetime = Field(...)            # NEW
    expires_at: AwareDatetime = Field(...)           # NEW
```

#### 2. Register in schema export
**File**: `tools/scripts/export_pydantic_schemas.py`
**Changes**: import `TenantContext` + `TenantChannel`; append `TenantContext` to `MODEL_REGISTRY["models"]`
and `TenantChannel` to `ENUM_REGISTRY`. Then regenerate:
`bun nx run langgraph:generate-schemas && bun nx run shared-schemas:generate-types`.

#### 3. Update the frozen stub + node + state
**File**: `apps/langgraph/tests/_stubs/tenant_context_stub.py` — migrate to the real 3-part JWS form
(header `{"alg":"HS256","kid":...,"typ":"tenantctx+jws"}`), keep HMAC-SHA256 over the JWS signing input;
`sign()` accepts the new claim fields.
**File**: `apps/langgraph/cordia_agent/nodes/resolve_tenant.py` — return `allowed_agent_policy_ids` /
`allowed_tool_policy_ids` (and keep populating `tenant_id`, `tenant_policy_version`, `correlation_id`,
`channel_endpoint_id`, `session_id`).
**File**: `apps/langgraph/cordia_agent/state.py` — reconcile `AgentState` fields the node returns
(`allowed_agent_policy_ids`/`allowed_tool_policy_ids`, ensure `session_id` field exists) so channel
merges validate.

#### 4. Update existing tests to the new shape (RED first)
**File**: `apps/langgraph/tests/unit_tests/safety/test_tenant_guard.py` and any model tests — update
fixtures to the new claim set + 3-part token; add assertions for the new fields.

### Phase 1: Success Criteria

#### Phase 1: Programmatic Verification
- [x] `bun nx run langgraph:generate-schemas` succeeds and emits `TenantContext` under `schemas/models/` + `TenantChannel` under `schemas/enums/`.
- [x] `bun nx run shared-schemas:generate-types` regenerates C# (`SharedTypes.cs`) + TS/Zod without error; generated `TenantContext` is present.
- [x] `bun nx test langgraph 2>&1 | tee .tmp/test-langgraph-p1.log` passes (877 passed — updated stub + node + guard tests).
- [x] `bun nx lint langgraph` passes; `bun nx run langgraph:format` clean.

#### Phase 1: Agent Verification
- [x] Agent greps generated `libs/shared-schemas/generated/typescript/zod.ts` to confirm `TenantContext` uses `.strict()` and `z.string().datetime()` for `issued_at`/`expires_at`. (Emitted as `.datetime({ offset: true })` — repo convention for `AwareDatetime`.)
- [x] Agent confirms `state.Channel` (sms) is unchanged and only `TenantContext.channel` moved to `TenantChannel`.
- [x] Agent confirms the stub emits a 3-part `header.payload.signature` token.

### Phase 1: Discoveries and Notable Information

**Implemented + independently reviewed (VERDICT: PASS). 877 langgraph tests pass; lint/format clean; both regen steps succeed.**

**Technical Discoveries:**
- **`injection_harness.py` had to be edited (beyond the plan's file list):** `ToolAllowlist.from_tenant_context` read the removed `ctx.allowed_policy_ids`. Repointed to `ctx.allowed_tool_policy_ids` (tool-authorization semantics — the method builds a TOOL allowlist). The only production `ToolAllowlist` construction (`menu/extraction_agent.py:91`) uses `ToolAllowlist([])`; `from_tenant_context` is called only by tests and remains deprecated scaffolding — behaviour unchanged.
- **16 test files + the guard suite** construct `TenantContext`/call the stub `sign()` and were migrated to the new claim set. Where prior behaviour was policy-agnostic, both `allowed_agent_policy_ids` and `allowed_tool_policy_ids` were set to the old list value.
- **Quicktype enum-name unification (watch in Phase 2 + web):** `SessionConsentState.notice_channel` is `Literal["voice","web","messaging","email"]` — the identical value set to the new `TenantChannel`. Quicktype now merges them into one generated type: C# `TenantChannelEnum`, TS `TenantChannelSchema`. Wire-identical (same string values) and both generated files are internally consistent (verified — no dangling type refs), but the generated **type name** for `notice_channel` changed. The `sms`-valued state `ChannelEnum` and `VerificationRequestChannel` remain distinct.

**Interop notes carried to Phase 2/5 (CRITICAL):**
- **Signing input:** compact JWS `b64url(header).b64url(payload).b64url(signature)`; header canonicalized as `{"alg":"HS256","kid":<id>,"typ":"tenantctx+jws"}`; signature = HMAC-SHA256 over `b64url(header) + "." + b64url(payload)`.
- **Canonical JSON = `json.dumps(sort_keys=True, separators=(",",":"), ensure_ascii=True)`.** `ensure_ascii=True` is retained deliberately — the C# signer MUST match (non-ASCII → `\uXXXX`).
- **Datetime format:** `model_dump(mode="json")` serializes `issued_at`/`expires_at` with a trailing `Z` (e.g. `2026-07-25T00:00:00Z`). The C# signer must byte-match this exact string form for the byte-parity assertion. NOTE: JWS *verification* recomputes HMAC over the received base64 bytes and never re-serializes — so cross-impl interop is most robustly guarded by a **fixed token string** verified on both sides (Phase 2 & Phase 5), with byte-parity of the two independent serializers as an additional (stricter) assertion.

**Future Considerations:**
- `ToolAllowlist.from_tenant_context` is dead/deprecated scaffolding pending a tracked spec-15 open decision — candidate for removal once the tool-authorization path is finalized.
- Pre-existing (out of scope, untouched): `ruff check` reports 4 errors in `tests/unit_tests/observability/{test_context,test_metrics}.py`; not gated by the `langgraph` lint target (which scopes to `cordia_agent/`).

---

## Phase 2: JWS signer/verifier in `apps/api` (HS256)

### Phase 2: Overview
Add the Domain port `ITenantContextSigner` and its Infrastructure impl `JwsTenantContextSigner`:
HS256 compact JWS, `kid` header, active-key set for zero-downtime rotation, canonical-JSON payload,
full verification order. Keys from `ISecretStore`.

### Phase 2: Changes Required

#### 1. Domain port + result types
**File**: `apps/api/src/Cordia.Domain/TenantResolution/ITenantContextSigner.cs` (new)
```csharp
public interface ITenantContextSigner
{
    string Sign(TenantContextClaims claims);          // → compact JWS
    TenantContextClaims Verify(string jws);            // throws on any failure
}
```
`TenantContextClaims` is a Domain record mirroring the generated claim set (or the generated
`Cordia.Domain.Generated.TenantContext` reused directly — decide during impl to avoid duplication).
Typed failures: `TenantContextVerificationException` (unknown/retired `kid`, bad signature, expired,
future `issued_at`, wrong `typ`).

#### 2. Infrastructure signer
**File**: `apps/api/src/Cordia.Infrastructure/TenantResolution/JwsTenantContextSigner.cs` (new)
- Load the active signing key + `kid` and the full active-key set (`kid → secret`) from `ISecretStore`
  once at construction (swap on rotation). Config layout under `Secrets:TenantContextSigningKeys`
  (e.g. `{ activeKid, keys: { kid: base64secret } }`).
- `Sign`: build header `{"alg":"HS256","kid":activeKid,"typ":"tenantctx+jws"}`, canonical-JSON payload,
  HMAC-SHA256 over `b64(header) + "." + b64(payload)`, return 3-part token.
- `Verify`: parse header → `kid` in active set (else throw) → recompute + constant-time compare
  (`CryptographicOperations.FixedTimeEquals`) → parse claims → `expires_at` not passed / `issued_at`
  not in future (small skew, e.g. 60s) → `typ == "tenantctx+jws"`. Never log key/secret/claims.
- Canonical JSON must match the Python `_canonical_json` (sorted keys, no spaces, UTF-8). Use a
  deterministic serializer (custom `Utf8JsonWriter` with ordered properties), not default options.

#### 3. DI + config
**File**: `apps/api/src/Cordia.Infrastructure/Extensions/InfrastructureServiceCollectionExtensions.cs`
**Changes**: `services.AddSingleton<ITenantContextSigner, JwsTenantContextSigner>();` (keys loaded from
`ISecretStore`). Add `appsettings` sample keys under `Secrets:` (dev only; real keys via secret store).

#### 4. Tests (RED first)
**File**: `apps/api/test/Cordia.Tests/UnitTests/TenantResolution/JwsTenantContextSignerTests.cs` (new)
- Round-trip sign→verify returns equal claims.
- Tampered claim (flip a `tenant_id` byte) → verification throws.
- Rotation: token signed with `kid_A` verifies while `kid_A` in active set; after removing `kid_A`, rejected; a `kid_B` token verifies.
- Expired `expires_at` and future `issued_at` (beyond skew) rejected; unknown `kid` rejected; wrong `typ` rejected.
- No secret/key/claim value appears in any thrown exception message (assert message hygiene).
- **Cross-impl vector**: a token produced by the Python stub form (same secret + claims) verifies here (guards Phase 5 interop). Encode a fixed claim set + secret as a test constant.

### Phase 2: Success Criteria

#### Phase 2: Programmatic Verification
- [x] `bun nx build api` compiles. (0 errors; needed a Phase-1 build-regression fix — see discoveries.)
- [x] `bun nx test api 2>&1 | tee .tmp/test-api-p2.log` — `JwsTenantContextSignerTests` all pass (19 incl. added parity test; 997 total green).
- [x] `bun nx lint api` passes (new files UTF-8 + BOM).

#### Phase 2: Agent Verification
- [x] Agent confirms `JwsTenantContextSigner` reads keys only from `ISecretStore` and holds no plaintext key in a field that is logged/serialized.
- [x] Agent confirms the canonical-JSON serialization byte-matches the Python `_canonical_json` for a shared fixture (Python-regenerated token == C# `VectorToken` constant, byte-for-byte; added `ensure_ascii` special-char parity test).

### Phase 2: Discoveries and Notable Information

**Implemented + independently reviewed (VERDICT: PASS). Build 0 errors; 997 api unit tests pass (19 in the signer class); lint/BOM clean. Cross-impl interop loop closed (Python-regenerated token == C# constant).**

**THE CROSS-IMPL VECTOR (reuse verbatim in Phase 5 — durable record):**
- Secret (Python `str`, HMAC key = its UTF-8 bytes): `spec15-cross-impl-vector-secret`
- Same secret as a C# `Secrets` config value (base64 of the UTF-8 bytes): `c3BlYzE1LWNyb3NzLWltcGwtdmVjdG9yLXNlY3JldA==`
- `kid`: `vector-kid-1`
- Claims: `tenant_id=tenant-abc`, `channel=voice`, `channel_endpoint_id=did:+15550001111`, `configuration_version=cfg-v1`, `correlation_id=corr_…0001`, `session_id=sess_…0001`, `allowed_agent_policy_ids=["agent-policy-a","agent-policy-b"]`, `allowed_tool_policy_ids=["tool-policy-ü"]` (U+00FC → `ü`, exercises `ensure_ascii`), `issued_at=2026-07-25T00:00:00Z`, `expires_at=2026-07-25T00:05:00Z`
- Token: `eyJhbGciOiJIUzI1NiIsImtpZCI6InZlY3Rvci1raWQtMSIsInR5cCI6InRlbmFudGN0eCtqd3MifQ.eyJhbGxvd2VkX2FnZW50X3BvbGljeV9pZHMiOlsiYWdlbnQtcG9saWN5LWEiLCJhZ2VudC1wb2xpY3ktYiJdLCJhbGxvd2VkX3Rvb2xfcG9saWN5X2lkcyI6WyJ0b29sLXBvbGljeS1cdTAwZmMiXSwiY2hhbm5lbCI6InZvaWNlIiwiY2hhbm5lbF9lbmRwb2ludF9pZCI6ImRpZDorMTU1NTAwMDExMTEiLCJjb25maWd1cmF0aW9uX3ZlcnNpb24iOiJjZmctdjEiLCJjb3JyZWxhdGlvbl9pZCI6ImNvcnJfMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDEiLCJleHBpcmVzX2F0IjoiMjAyNi0wNy0yNVQwMDowNTowMFoiLCJpc3N1ZWRfYXQiOiIyMDI2LTA3LTI1VDAwOjAwOjAwWiIsInNlc3Npb25faWQiOiJzZXNzXzAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAxIiwidGVuYW50X2lkIjoidGVuYW50LWFiYyJ9.-6d8E_84lKWbIUdh8zCrMrqx76DAPX9UNW6axXhKT0M`  (verify with clock pinned between issued/expires, e.g. `2026-07-25T00:02:00Z`). Defined in `JwsTenantContextSignerTests.cs` under a `// CROSS-IMPL VECTOR — shared with Phase 5 test` comment.

**Design decisions (Phase 3/4/5 must honour):**
- **Hand-written `TenantContextClaims` Domain record** (not the generated `Cordia.Domain.Generated.TenantContext`) is the signer's claim type — fully owns canonical bytes, keeps the Domain port decoupled from generated code. `Channel` is the hand-written `Cordia.Domain.Enums.TenantChannel`; timestamps are `DateTimeOffset`.
- **Canonical serializer is hand-rolled** (a custom escaper), because no built-in .NET encoder matches CPython `json.dumps(ensure_ascii=True)`: CPython does NOT escape `/ < > & ' +` (the .NET defaults do), and escapes every char >0x7e as lowercase `\uXXXX` (astral → UTF-16 surrogate pair). Verified byte-identical incl. these edge cases.
- **Datetime format** matches pydantic v2 `model_dump(mode="json")`: UTC → trailing `Z`; non-UTC → `+HH:MM`; microsecond precision, fractional omitted when zero. NOTE: Python only *verifies* (recomputes HMAC over received bytes, never re-serializes), so production interop only requires C# to emit RFC3339 that Pydantic can parse — byte-parity is a test-fixture guarantee, not a runtime dependency.
- **Key custody / config layout:** keys loaded once from `ISecretStore` at construction; the flat `ISecretStore.GetSecret` can't enumerate a child map, so config adds a `KeyIds` comma-list: `Secrets:TenantContextSigningKeys:{ActiveKid, KeyIds, Keys:<kid>}`, each secret = base64 of the raw HMAC key bytes. **Phase 5 deployment config must include `KeyIds` and use the same secret bytes.** `_keys`/`_activeKid` are `private readonly`, never logged/serialized.
- **Clock** via injectable `TimeProvider` (skew 60s); DI resolves the `(ISecretStore)`-only ctor. **Verify order:** header-parse → `alg=="HS256"` (alg pinned; no alg-confusion) → `kid` in active set → HMAC + `CryptographicOperations.FixedTimeEquals` → parse claims → `typ` → expiry/skew. Exception messages are fixed generic strings — never leak secret/kid/claims.

**DEVIATION — Phase-1 build regression, fixed here (committed as a separate `fix` commit):** the Phase 1 C# schema regen introduced `Cordia.Domain.Generated.TenantContext`/`TenantChannel` (quicktype) which collided by simple name (`CS0104`, ~130 errors) with the hand-written `Cordia.Domain.Enums.TenantChannel` and `Cordia.Infrastructure.Services.TenantContext`, and renamed the generated `notice_channel` type to the merged `TenantChannelEnum` (`CS0246` in `RecordConsentCommand`). **Phase 1's success criteria never ran `bun nx build api`, so this was missed at the Phase 1 gate** (process gap — future schema-regen phases must build the C# side). Fixed minimally with two `GlobalUsings.cs` alias files (Infrastructure + Tests) resolving the simple names to the hand-written types, plus the `RecordConsentCommand` rename. Reviewer grepped the whole `apps/api` tree: every unqualified `TenantChannel`/`TenantContext` resolves to the intended hand-written type; no production code needs the generated DTO by simple name (use `Cordia.Domain.Generated.TenantContext` fully-qualified if ever needed). Behaviour/wire meaning unchanged.

---

## Phase 3: Resolution + issuance handlers (Application)

### Phase 3: Overview
The MediatR core: resolve the signal to exactly one active endpoint (zero→`tenant_not_resolved`,
many→`ambiguous_endpoint`), pin config version + policy ids, originate/echo `correlation_id`, allocate
`session_id`, mint+sign the context, create the session row (D12 single-writer), and append the
`tenant_resolution` audit event on every outcome.

### Phase 3: Changes Required

#### 1. Typed domain exceptions + resolver ambiguity
**Files** (new): `Cordia.Domain/Exceptions/TenantNotResolvedException.cs`, `AmbiguousEndpointException.cs` (extend `DomainException`).
**File**: `Cordia.Domain/Interfaces/ITenantChannelEndpointRepository.cs` + `Cordia.Infrastructure/Repositories/TenantChannelEndpointRepository.cs`
**Changes**: add a resolve method that distinguishes zero/one/many — e.g. `ResolveAllActiveAsync(...)`
returning a small list (`Take(2)`) so the handler maps `0 → TenantNotResolved`, `>1 → Ambiguous`,
`1 → row`. (Keep the existing `ResolveActiveAsync` or replace its single call site.)

#### 2. Resolve/issue command + handler + validator
**Files** (new) under `Cordia.Application/Handlers/TenantResolution/ResolveTenantContext/`:
- `ResolveTenantContextCommand.cs` — `record ResolveTenantContextCommand(TenantChannel Channel, SignalType SignalType, string SignalValue, string? InboundCorrelationId) : IRequest<SignedTenantContext>;` + `SignedTenantContext(string Jws, TenantContextClaims Claims)`.
- `ResolveTenantContextCommandHandler.cs` — resolve endpoint → build claims (pin `configuration_version` + `PolicyBundleId` → allowed agent/tool policy ids; allocate `session_id` (e.g. `sess_{guid}`); `correlation_id` = echo `InboundCorrelationId` if valid else from `ICorrelationContext`/mint; `issued_at=now`, `expires_at=now+TTL`) → `ITenantContextSigner.Sign` → `mediator.Send(CreateConversationSessionCommand)` → append audit (`resolved`) → return. On `TenantNotResolvedException`/`AmbiguousEndpointException`: append audit (`tenant_not_resolved`/`ambiguous`) then rethrow.
- `ResolveTenantContextValidator.cs` — `SignalType` in closed set, `SignalValue` non-empty; `Channel` valid. (Validators are the sole required-field enforcement.)

TTL is a config value (`TenantContext:TtlSeconds`, minutes-scale; OD-3 placeholder).

#### 3. Session single-writer command (D12)
**Files** (new) under `Cordia.Application/Handlers/ConversationSessions/CreateConversationSession/`:
- `CreateConversationSessionCommand.cs` — `record (string TenantId, string SessionId, TenantChannel Channel, string ChannelEndpointId, string CorrelationId) : IRequest<ConversationSessionDto>;`
- `CreateConversationSessionCommandHandler.cs` — insert the row with `StartedAt=now`, `Version=0`, consent fields null; idempotent on `(TenantId, SessionId)` (retry-safe insert; treat unique-violation as success/return existing). Uses a new `IConversationSessionRepository.CreateAsync` (add to interface + repo).
- `CreateConversationSessionValidator.cs` — non-empty `TenantId`/`SessionId`/`ChannelEndpointId`/`CorrelationId`.

#### 4. Audit append (synchronous) + exception mapping
- Use `IAuditWriter` inside the resolution handler; `event_type = "tenant.resolution"`, `metadata` jsonb carries `signal_type`, resolved `tenant_id` (on success), `channel_endpoint_id`, `outcome` (`resolved|tenant_not_resolved|ambiguous_endpoint|signing_failed|session_insert_failed`), `correlation_id`. Sanitized by the existing `AuditEventSanitizationInterceptor`.
- **File**: `Cordia.Api/Errors/GlobalExceptionHandler.cs` — add arms: `TenantNotResolvedException`/`AmbiguousEndpointException` → a channel-appropriate 4xx (e.g. 422 with a generic "can't reach this business" problem body; **never** 500, never a fallback tenant); `TenantContextVerificationException` (from Phase 4 middleware) → 401/403.

#### 5. Tests (RED first, Moq)
**Dir** (new): `apps/api/test/Cordia.Tests/UnitTests/TenantResolution/`
- Each `signal_type` resolves the correct single tenant; zero → `TenantNotResolvedException`; two → `AmbiguousEndpointException` (no context minted, audit written with the right outcome).
- `correlation_id`: valid inbound echoed; absent → minted once; never re-minted.
- Signing failure and session-insert failure → fail closed, audited, no `SignedTenantContext` returned.
- `CreateConversationSession` inserts with `tenant_id` leading PK, consent fields null; idempotent on duplicate.

### Phase 3: Success Criteria

#### Phase 3: Programmatic Verification
- [ ] `bun nx build api` compiles.
- [ ] `bun nx test api 2>&1 | tee .tmp/test-api-p3.log` — new resolution/session/handler tests pass.
- [ ] `bun nx lint api` passes.

#### Phase 3: Agent Verification
- [ ] Agent confirms the handler appends an audit row on **every** branch (success + each failure) by reading `ResolveTenantContextCommandHandler.cs`.
- [ ] Agent confirms `CreateConversationSessionCommand` is the only insert path for `conversation_sessions` (grep shows no other `.ConversationSessions.Add`).
- [ ] Agent confirms no fallback/default tenant exists on any failure branch.

### Phase 3: Discoveries and Notable Information
[Filled by the implementing agent during Phase 3 execution.]

---

## Phase 4: Ingress controllers + verification middleware (Api)

### Phase 4: Overview
Add the two thin webhook controllers (voice + messaging) that extract the transport-attested signal
and dispatch `ResolveTenantContextCommand`, and a `TenantContextVerificationMiddleware` that verifies
an inbound signed context on API-side downstream calls before any handler touches data.

### Phase 4: Changes Required

#### 1. Ingress controllers
**Files** (new) under `apps/api/src/Cordia.Api/Controllers/Channels/`:
- `TelephonyWebhookController.cs` — `POST api/channels/telephony`; extract the **called DID** (transport-attested field only; ignore caller name/SIP display name) → `ResolveTenantContextCommand(Voice, PhoneNumber, did, inboundCorrelationId)` → return `SignedTenantContext` (JWS + claims). Echo any inbound provider correlation id.
- `MessagingWebhookController.cs` — `POST api/channels/messaging`; extract the **recipient/page/business id** → `ResolveTenantContextCommand(Messaging, MessagingRecipient, recipient, ...)`.
- Thin, no `TenantGuard.RequireTenant` here (tenant is the *output*, not a precondition). DTO records with `[property: JsonPropertyName]` in the same file.

#### 2. Verification middleware
**File**: `apps/api/src/Cordia.Api/Middleware/TenantContextVerificationMiddleware.cs` (new)
- If a signed context header is present (e.g. `X-Tenant-Context`), `ITenantContextSigner.Verify` it,
  then set `TenantContext.TenantId` + fold `tenant_id` into `AmbientCorrelationContext` **before** the
  handler runs. On verification failure → throw `TenantContextVerificationException` (→ 401/403). This
  is the API-side mirror of the langgraph `resolve_tenant` node.
- **File**: `apps/api/src/Cordia.Api/Program.cs` — register after `CorrelationMiddleware`; keep the
  `X-Tenant-Id` shim only for endpoints/dev without a signed context (documented as dev-only), or gate
  it behind Development. Do not let the shim override a verified context.

#### 3. Integration tests (RED first — Alba + Testcontainers Postgres)
**Dir** (new): `apps/api/test/Cordia.Tests/IntegrationTests/TenantResolution/`
- Full ingress: POST telephony webhook with a seeded active endpoint → 200 with a JWS whose claims verify → exactly one `conversation_sessions` row with `tenant_id` leading PK, consent fields null.
- Zero/ambiguous registry rows → channel-appropriate 4xx, no session row, audit row with the outcome.
- Exactly one `correlation_id` on both the session row and the `audit_events` row; inbound provider correlation echoed.
- Inv-13 ordering: a downstream data read attempted without a verified context fails closed at the tenant-scope guard (no unscoped read).

### Phase 4: Success Criteria

#### Phase 4: Programmatic Verification
- [ ] `bun nx build api` compiles.
- [ ] `bun nx test-integration api 2>&1 | tee .tmp/test-int-api-p4.log` — ingress integration tests pass.
- [ ] `bun nx test api` still green; `bun nx lint api` passes.

#### Phase 4: Agent Verification
- [ ] Agent starts the api dev server (dev-server protocol — see below) and POSTs the telephony webhook with a seeded endpoint, confirming a verifiable JWS is returned and one session row is created.
- [ ] Agent confirms `TenantContextVerificationMiddleware` runs before controllers and the `X-Tenant-Id` shim cannot override a verified context (reads `Program.cs` pipeline order).

### Phase 4: Discoveries and Notable Information
[Filled by the implementing agent during Phase 4 execution.]

---

## Phase 5: langgraph production verifier wiring

### Phase 5: Overview
Implement the real HS256 JWS verifier in production code (replacing the test stub in production paths),
register it at startup, and guarantee byte-for-byte parity with the Phase 2 C# signer.

### Phase 5: Changes Required

#### 1. Production verifier
**File** (new): `apps/langgraph/cordia_agent/safety/jws_verifier.py`
- `JwsTenantContextVerifier` implementing the `TenantContextVerifier` Protocol: parse 3-part JWS →
  header `kid` in the active-key set (from env/config, shared secret matching `apps/api`) → recompute
  HMAC-SHA256 over the JWS signing input, constant-time compare → validate claims via
  `TenantContext.model_validate` → check `expires_at`/`issued_at` (skew) + `typ`. Raise
  `InvalidTenantContext` on any failure. Never log the secret or token.

#### 2. Startup registration
**File**: the langgraph app/graph bootstrap (where the graph/tool client is initialized) —
`register_verifier(JwsTenantContextVerifier(keys_from_config()))` once at startup. Secret sourced from
the same value as `apps/api` `Secrets:TenantContextSigningKeys` (deployment wiring).

#### 3. Tests
**File** (new): `apps/langgraph/tests/unit_tests/safety/test_jws_verifier.py`
- Round-trip against a token produced by the (migrated) stub `sign()` in the real JWS form.
- Tampered payload, wrong secret, unknown `kid`, expired/future, wrong `typ` all raise `InvalidTenantContext`.
- **Cross-impl vector**: the fixed claim set + secret constant shared with the Phase 2 C# test verifies here (interop guard).
- `resolve_tenant` node test suite passes with the production verifier registered (not just the stub).

### Phase 5: Success Criteria

#### Phase 5: Programmatic Verification
- [ ] `bun nx test langgraph 2>&1 | tee .tmp/test-langgraph-p5.log` passes.
- [ ] `bun nx lint langgraph` passes; `bun nx run langgraph:format` clean.
- [ ] `get_verifier()` no longer raises in a production-bootstrapped process (covered by a startup/registration test).

#### Phase 5: Agent Verification
- [ ] Agent confirms the shared cross-impl vector verifies identically in both the C# (`JwsTenantContextSignerTests`) and Python (`test_jws_verifier`) suites.
- [ ] Agent confirms production code never imports `tests/_stubs/tenant_context_stub.py`.

### Phase 5: Discoveries and Notable Information
[Filled by the implementing agent during Phase 5 execution.]

---

## Testing Strategy

### Unit Tests:
- **Signer/verifier (C# + Python)**: round-trip, tampered claim, key rotation overlap, expired/future/skew, unknown `kid`, wrong `typ`, message hygiene (no secret in exceptions), and a **shared cross-impl vector** proving both sides agree on the canonical form.
- **Resolver**: each `signal_type` → correct single tenant; zero → `tenant_not_resolved`; many → `ambiguous_endpoint`; caller-mutable fields never resolve; every outcome audited.
- **Correlation**: exactly one `correlation_id` per session; inbound echoed, never re-minted.
- **Session create**: `tenant_id`-leading insert, consent null, idempotent on `(tenant_id, session_id)`.

### Integration Tests (Alba + Testcontainers Postgres):
- Full voice + messaging ingress → signed context + exactly one session row via the API (D12).
- Fail-closed: zero/ambiguous → no session, no context, audited.
- Inv-13 ordering: pre-verify data read fails closed at the tenant-scope guard.
- One `correlation_id` on the session row and the `audit_events` resolution row.

## Human Verification

**Performed once by a person after the entire plan is complete (not per phase):**
1. Trigger a real (or provider-simulated) inbound voice call to a seeded DID and confirm the caller reaches the correct tenant's conversation, and that a spoken business name that contradicts the DID does **not** change resolution (Inv-13, §7).
2. Confirm a messaging webhook to a seeded recipient resolves the correct tenant and creates one session.
3. Inspect logs/traces for a resolution and confirm **no** signing key, secret, or full token appears anywhere (Inv-12), and that one `correlation_id` threads the whole flow.
4. Exercise a key rotation in a staging config (publish `kid_B`, flip signer, retire `kid_A`) and confirm in-flight contexts keep verifying with zero downtime.

## Performance Considerations

- Gateway budget is < 150 ms end-to-end (§8). v1 skips registry caching; the resolve is a single
  indexed read on `uq_channel_signal_active`, sign is in-memory HMAC, session-create is a single
  in-process MediatR insert (co-located API). If p95 exceeds budget, add the versioned tenant-scoped
  endpoint cache (§8) as a follow-up.
- Synchronous audit is a small extra insert on the critical path; acceptable for v1. The §8
  fire-and-async optimization is deferred and noted.

## Migration Notes

- No new EF migration expected — `tenant_channel_endpoints`, `conversation_sessions`, `audit_events`
  already exist. If Phase 3 reveals a genuinely missing column, add a **new** migration (never edit an
  applied one) and flag it as a deviation from this plan.
- Shared-schema regeneration (Phase 1) touches gitignored generated dirs — regenerate locally/in CI;
  do not hand-edit `SharedTypes.cs`/`zod.ts`.
- The signing secret must be provisioned in the secret store for `apps/api` and the matching env/config
  for `apps/langgraph` before either verifies in a real environment (deployment concern).

## References

- Spec: `docs/specs/15-tenant-resolution-and-gateway.md`
- Research: `thoughts/shared/research/2026-07-24_spec-15-tenant-resolution-and-gateway.md`
- Canonical patterns: `docs/dotnet-api-patterns.md`
- Reference command triad: `apps/api/src/Cordia.Application/Handlers/Waitlist/Enroll/`
- Reference ingress/auth: `apps/api/src/Cordia.Api/Controllers/NotificationWebhooksController.cs`
- Reference middleware shape: `apps/api/src/Cordia.Api/Middleware/CorrelationMiddleware.cs`
- Frozen contract fake: `apps/langgraph/tests/_stubs/tenant_context_stub.py`
- Verifier port + node: `apps/langgraph/cordia_agent/safety/tenant_context.py`, `cordia_agent/nodes/resolve_tenant.py`
- Registry entity/repo: `apps/api/src/Cordia.Domain/Entities/TenantChannelEndpoint.cs`, `apps/api/src/Cordia.Infrastructure/Repositories/TenantChannelEndpointRepository.cs`
- Session entity/repo: `apps/api/src/Cordia.Domain/Entities/ConversationSession.cs`, `apps/api/src/Cordia.Domain/Interfaces/IConversationSessionRepository.cs`
