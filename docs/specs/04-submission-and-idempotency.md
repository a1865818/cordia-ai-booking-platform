# 04 — Submission Contract & Idempotency

**Status:** Proposed
**Owner project(s):** `apps/api` (primary: Cordia.Application / Cordia.Domain / Cordia.Infrastructure / Cordia.Api), `apps/langgraph` (caller side of the contract), `libs/shared-schemas` (cross-service contract carriers)
**Source design sections:** §10 Submission Contract (lines 1036–1109); §13 Delivery and Effectively-Once Semantics (lines 1353–1462); supporting: §8.7–8.8 (revisions/confirmation), §11 (state machine), §12 (job/event architecture), §22 (data model), §27.2 (idempotency tests), §30 (invariants), §31 (open decisions).

---

## 1. Purpose & scope

This component defines the durable **submission contract** between the foreground agent and the booking API, and the **idempotency machinery** that turns an unreliable, retried network call into exactly one logical booking action. It owns operation identity minting and checkpointing, the versioned **canonical request hash**, the same-key/same-hash · same-key/different-hash · different-key dispositions, the single **atomic acceptance transaction** (`validate → idempotency → intent revision → operation `accepted` → first outbox event → commit`), the `202 Accepted` response shape, the per-boundary idempotency layer table, the idempotency record lifecycle/retention, and concurrent-duplicate creator election.

This is the heart of **effectively-once** (§13.1): Cordia guarantees neither exactly-once network delivery nor exactly-once worker execution; it guarantees one logical effect by combining stable identities, durable idempotency records, request-hash binding, and the transactional outbox handed off downstream.

Scope **excludes** (cross-referenced below): the foreground graph/confirmation logic that produces the request (`02`), the tool schema wrapping the call (`03`), what happens after the outbox row is committed (`05`), the workflow that consumes it (`06`), capacity (`07`), and verification/authorization details (`08`).

---

## 2. Position in the system

```
apps/langgraph (FOREGROUND, latency lane)              apps/api (BACKGROUND boundary, reliability lane)
─────────────────────────────────────────             ─────────────────────────────────────────────────
submit_confirmed_intent tool                           POST /api/bookings  (acceptance endpoint)
  • mints/checkpoints operation_id + idem key   ──────▶   • canonicalize + hash
  • carries confirmed revision + payload_hash            • elect creator (atomic upsert of idem record)
  • retries with the SAME identity                       • ONE acceptance transaction
                                                         • return 202 Accepted (durably accepted, not booked)
                                                                       │
                                                                       ▼ (committed outbox row → spec 05)
```

**Lane:** This component straddles the lane boundary. The *minting/checkpointing* half lives in the foreground agent (latency-sensitive but does no provider work). The *acceptance* half lives in `apps/api` and is the first durable, reliability-optimized step — it is the agent's only sanctioned write path into authoritative state.

**Upstream:** foreground graph confirmation (`02`), `submit_confirmed_intent` tool (`03`), tenant resolution + verification grant (`08`).

**Downstream:** the committed `outbox_messages` row drives the outbox publisher (`05`) and then the workflow state machine (`06`). `get_operation_status` (`03`/`13`) reads `booking_operations`.

**This component MUST NOT:**
- Call any external booking, payment, or notification provider (those are background handlers in `06`/`09`/`10`).
- Run any workflow step, retry, or reconciliation inline (Inv-4: acceptance only writes the *first* outbox event; the worker does the rest).
- Mint operation identity or idempotency keys inside a retry loop (§10.1).
- Return `202` before the acceptance transaction commits (§10.3).
- Reserve capacity (Inv-6: capacity reservation is atomic and *separate* from request idempotency — see `07`).
- Trust transcript text as authorization to act (Inv-1) or treat caller ID as authorization (Inv-11).

---

## 3. Component design

| Sub-component | Project / layer | Responsibility |
|---|---|---|
| **Identity minter** | `apps/langgraph/cordia_agent` (submission node) | Mints `operation_id` + `idempotency_key` once when intent is first ready; checkpoints into LangGraph session state before the first call; reuses on every retry/replay. |
| **Submission tool client** | `apps/langgraph` (`submit_confirmed_intent`) | Builds the submission payload from the confirmed revision; sends with checkpointed identity; classifies `202`/`409`/timeout; never re-mints. Schema in `03`. |
| **Acceptance endpoint** | `Cordia.Api` controller | Thin: forwards to `SubmitConfirmedIntentCommand`, maps result to `202`/`409`/`422`. |
| **Acceptance handler** | `Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/` | Orchestrates validate → canonicalize → elect → atomic acceptance → response. MediatR command. |
| **Canonicalizer + hasher** | `Cordia.Application` (`ICanonicalRequestHasher`, impl in `Cordia.Infrastructure`) | Versioned canonical serialization + cryptographic hash (§10.2). Deterministic, pure. |
| **Idempotency store** | `Cordia.Infrastructure` (`IIdempotencyRecordRepository`) | Atomic conditional insert (creator election), load-existing, request-hash compare. Backed by `idempotency_records` + unique `(tenant_id, idempotency_key)`. |
| **Acceptance transaction** | `Cordia.Infrastructure` (`IUnitOfWork` over `ApplicationDbContext`) | Wraps idempotency + intent revision + operation + outbox writes in one DB transaction. |
| **Response builder** | `Cordia.Application` | Builds the `202` body; snapshots it into the idempotency record for replay-identical responses. |

The canonicalizer and the idempotency store are deliberately separable: canonicalization is the *binding* mechanism (Inv-3), the store is the *uniqueness/election* mechanism (Inv-2, §13.4). They meet only at "compare submitted hash vs stored hash."

---

## 4. Data contracts & schemas

### 4.1 Cross-service contracts (libs/shared-schemas)

The submission request and `202` response **cross Python ↔ C#** and are therefore Pydantic-owned generated contracts (CLAUDE.md type-sharing chain, §28.3). Pydantic in `apps/langgraph/cordia_agent` is source of truth; C#/TS/Zod are generated — never hand-edited.

| Contract | shared-schemas carrier | Direction |
|---|---|---|
| `SubmitConfirmedIntentRequest` | `models/` → generated C# `Cordia.Domain.Generated` | agent → API |
| `IntentRevisionPayload` (canonical body) | `models/` | agent → API, hashed |
| `ConfirmationEvidence` | `models/` | agent → API |
| `OperationAcceptedResponse` (the `202`) | `models/` | API → agent |
| `IdempotencyConflict` (the `409`) | `models/` | API → agent |
| `OperationStatus` (status lookup) | `models/` | API → agent (read path, defined in `03`) |

### 4.2 Agent-side request (Pydantic field sketch)

```python
class IntentRevisionPayload(BaseModel):
    """The canonical, hashed body. Field set == material fields (§8.7)."""
    model_config = ConfigDict(extra="forbid")  # closed schema → Zod .strict()
    operation_type: OperationType            # create | modify | cancel
    tenant_id: str
    schema_version: str                      # e.g. "intent.v1"
    tenant_policy_version: str
    party_size: int
    requested_at: datetime                   # ISO-8601 with offset
    duration_minutes: int | None = None
    service_id: str | None = None
    resource_id: str | None = None
    location_id: str | None = None
    customer_ref: str | None = None          # opaque; NEVER PAN/CVV (Inv-12)
    notification_destination: str | None = None
    deposit_minor_units: int | None = None   # minor units only
    currency: str | None = None              # ISO-4217
    cancellation_terms_id: str | None = None
    target_booking_id: str | None = None     # required for modify/cancel
    preorder_items: list[PreorderItem] = []  # canonical-ordered (§ list ordering)

class ConfirmationEvidence(BaseModel):
    confirmation_id: str
    intent_id: str
    intent_revision: int
    intent_payload_hash: str                 # MUST equal API-computed hash (§8.8)
    confirmation_channel: str
    confirmed_at: datetime
    source_turn_id: str
    readback_version: str
    verification_level: str | None = None

class SubmitConfirmedIntentRequest(BaseModel):
    operation_id: str                        # minted+checkpointed once (§10.1)
    idempotency_key: str                     # minted+checkpointed once (§10.1)
    intent_id: str
    intent_revision: int
    replaces_operation_id: str | None = None # set when this supersedes (§10.1, §13.5)
    revision: IntentRevisionPayload
    confirmation: ConfirmationEvidence
    # transport metadata (trace ids, retry counters) travels in HEADERS,
    # is EXCLUDED from the canonical hash (§10.2).
```

### 4.3 API-side records / entities (C# sketch)

```csharp
// Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/SubmitConfirmedIntentCommand.cs
public record SubmitConfirmedIntentCommand(
    string TenantId,
    string OperationId,
    string IdempotencyKey,
    string IntentId,
    int IntentRevision,
    string? ReplacesOperationId,
    IntentRevisionPayload Revision,          // generated shared type
    ConfirmationEvidence Confirmation)
    : IRequest<AcceptanceResult>;

// AcceptanceResult discriminates the three outcomes the controller maps:
public abstract record AcceptanceResult;
public record Accepted(OperationAcceptedResponse Body) : AcceptanceResult;   // 202 (new or replayed)
public record Conflict(IdempotencyConflict Detail)    : AcceptanceResult;    // 409
// validation/policy failures throw typed domain exceptions → 422 via middleware
```

```csharp
// Cordia.Domain/BookingOperations/IdempotencyRecord.cs  (§13.3)
public class IdempotencyRecord
{
    public string TenantId { get; init; }
    public string IdempotencyKey { get; init; }
    public string OperationType { get; init; }
    public string RequestHash { get; init; }          // bound once (Inv-3)
    public string CanonicalizerVersion { get; init; } // e.g. "canon.v1"
    public string OperationId { get; init; }
    public IdempotencyStatus Status { get; set; }      // in_progress | completed | failed
    public string? ResponseSnapshotJson { get; set; }  // frozen 202 body or pointer
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset RetentionDeadline { get; init; }
}
```

> **`failed` lifecycle (Inv-3 / Inv-4).** A `failed` status marks a record that *committed* but whose acceptance preserved no durable side effect beyond the `key → request_hash` binding — the only path to it is a post-commit step failure (cf. §9 Failure-mode #9). It is retained with its binding **immutable** until `RetentionDeadline`: while it exists, a same-key/**same**-hash arrival replays the recorded outcome and a same-key/**different**-hash arrival returns `409` (Inv-3); only after the record is physically cleaned at `RetentionDeadline` may the key be re-elected with a different hash. A transaction that *rolls back* leaves **no** `failed` row at all (the `idempotency_records` insert lives inside the acceptance txn, §10.3) — there is nothing to clean, and a retry is a brand-new election.

### 4.4 DB tables touched (§22)

| Table | Write in acceptance txn | Notes / constraint |
|---|---|---|
| `idempotency_records` | insert (winner) / load (loser) | **unique `(tenant_id, idempotency_key)`** drives election (§13.4, §22.1) |
| `booking_intents` | upsert intent identity | `intent_id` stable across revisions |
| `booking_intent_revisions` | insert immutable revision | stores canonical payload + `payload_hash` |
| `intent_confirmations` | insert evidence | FK → exact revision; **check `confirmation.payload_hash = intent_revision.payload_hash`** (§22.1, §8.8) |
| `booking_operations` | insert, `status='accepted'`, `version=0` | **unique `(tenant_id, operation_id)`**; `replaces_operation_id` link |
| `outbox_messages` | insert first event `booking.command.requested.v1` | published later by `05`; `message_id` is the outbox dedup key |
| `audit_events` | append `operation.accepted` | append-only (security/audit trail) |

---

## 5. Canonical request hashing (§10.2)

A **versioned** canonicalization algorithm (`canonicalizer_version`, e.g. `canon.v1`) maps the request body to a stable byte string, then a cryptographic hash (algorithm is an Open Decision, §31 — candidate SHA-256). The version is stored on the idempotency record so a future `canon.v2` can be introduced without re-hashing historical records.

Only the **material intent revision** is hashed. Identity fields (`operation_id`, `idempotency_key`) and all transport metadata are excluded.

| Rule | Specification |
|---|---|
| **Property ordering** | Recursively sort object keys lexicographically (by Unicode code point). Order is not preserved from the wire. |
| **Timestamp / timezone** | Normalize every timestamp to UTC and serialize as ISO-8601 with explicit `Z` offset, millisecond precision (`2026-06-15T10:30:00.000Z`). Reject ambiguous local times upstream (`02`). |
| **Null / absent** | Absent and explicit `null` are treated identically: **omit** the property from the canonical form. (So adding an explicitly-null optional does not change the hash.) |
| **String normalization** | Unicode NFC; trim leading/trailing whitespace; collapse internal runs of whitespace to a single space; do **not** lowercase identifiers or names (semantic-preserving). |
| **Monetary units** | Integer **minor units** only (e.g. cents). A `currency` (ISO-4217) must accompany any monetary value; reject decimals at validation. |
| **List ordering** | Semantically unordered lists (e.g. `preorder_items`) are sorted by a defined natural key (`item_id`, then `modifier_id`). Semantically ordered lists (none in v1) preserve order and document it. |
| **Excluded transport metadata** | `operation_id`, `idempotency_key`, trace/correlation IDs, retry counters, auth headers, client timestamps, user-agent — none enter the hash. The hash is over *what is requested*, not *how it was sent*. |
| **Encoding** | Canonical form serialized as UTF-8, no insignificant whitespace, before hashing. |

### 5.1 Disposition rules (§10.2 + §13.4)

```
              ┌─────────────────────────────┐
  request ──▶ │ compute canonical hash H_new │
              └──────────────┬──────────────┘
                             ▼
        load-or-insert idempotency_records by (tenant_id, idempotency_key)
                             │
        ┌────────────────────┼─────────────────────────────┐
        ▼ (no existing)      ▼ (existing, H_old == H_new)   ▼ (existing, H_old != H_new)
   ELECTED CREATOR      SAME KEY / SAME HASH            SAME KEY / DIFFERENT HASH
   run acceptance txn   return existing operation       reject 409 IdempotencyConflict
   → 202 (new)          + snapshot/status → 202 (replay)  (do NOT mutate the record)
```

| Case | Disposition | Invariant |
|---|---|---|
| Same key, same hash | Return the existing operation + frozen response snapshot (or current status). Idempotent replay. | Inv-2, Inv-3 |
| Same key, different hash | Reject with `409` `IdempotencyConflict`. The key is permanently bound to its first hash. | Inv-3 |
| Different key, semantically duplicate body | Optionally detect via a secondary content-hash index and *warn* (return advisory in `202`); never silently merge — two keys are two distinct customer actions. | §10.2 |

---

## 6. Workflows

### 6.1 Happy path — durable acceptance (sequenceDiagram)

```mermaid
sequenceDiagram
    autonumber
    participant Agent as Foreground Agent (langgraph)
    participant API as Acceptance Endpoint (Cordia.Api)
    participant H as Canonicalizer+Hasher
    participant DB as PostgreSQL (one txn)
    participant OB as Outbox row (→ spec 05)

    Note over Agent: intent first ready → mint operation_id + idempotency_key,<br/>CHECKPOINT in session state BEFORE first call (§10.1)
    Agent->>API: POST /api/bookings (request + checkpointed identity)
    API->>API: Validate tenant, schema, policy, authorization,<br/>confirmation.payload_hash == revision hash (§8.8)
    API->>H: canonicalize(revision) → H_new
    API->>DB: BEGIN
    API->>DB: INSERT idempotency_records (tenant_id, key, H_new, op_id, in_progress)
    Note over DB: unique (tenant_id, idempotency_key) → wins election
    API->>DB: UPSERT booking_intents; INSERT booking_intent_revisions (immutable)
    API->>DB: INSERT intent_confirmations (FK→revision, hash check)
    API->>DB: INSERT booking_operations (status='accepted', version=0)
    API->>OB: INSERT outbox_messages (booking.command.requested.v1)
    API->>DB: INSERT audit_events (operation.accepted)
    API->>DB: UPDATE idempotency_records SET status='completed', response_snapshot=...
    API->>DB: COMMIT
    Note over API: ONLY NOW is 202 allowed (§10.3)
    API-->>Agent: 202 Accepted {operation_id, intent_revision, status:"accepted", status_url}
    Note over Agent: agent says "submitted, pending" — NOT "booked" (Inv-16)
    OB-->>OB: publisher (spec 05) eventually publishes even if API crashed post-commit
```

### 6.2 Failure / recovery — client timeout + concurrent duplicate (sequenceDiagram)

```mermaid
sequenceDiagram
    autonumber
    participant Agent
    participant API1 as API request #1
    participant API2 as API request #2 (retry, SAME identity)
    participant DB as PostgreSQL

    Agent->>API1: POST (op_id=X, key=K, hash=H)
    API1->>DB: BEGIN; INSERT idempotency_records(K) ... COMMIT
    Note over API1,Agent: API1 crashes / network drops AFTER commit, BEFORE 202 reaches agent
    Agent--xAPI1: timeout (no response received)

    Note over Agent: retry reuses checkpointed op_id=X, key=K — does NOT re-mint (§10.1)
    Agent->>API2: POST (op_id=X, key=K, hash=H)   %% may also race a still-running #1
    API2->>DB: BEGIN; INSERT idempotency_records(K)
    DB-->>API2: unique violation (K already present) → API2 is the LOSER (§13.4)
    API2->>DB: LOAD existing record by (tenant_id, K)
    alt stored hash == H
        API2-->>Agent: 202 {operation_id:X, status:current} (replay; ONE operation)
        Note over API2: does NOT start a duplicate workflow while #1 in progress (§13.4)
    else stored hash != H
        API2-->>Agent: 409 IdempotencyConflict (Inv-3)
    end
    Note over Agent,DB: net effect = exactly ONE booking_operation X (effectively-once)
```

### 6.3 Idempotency record lifecycle (stateDiagram-v2)

```mermaid
stateDiagram-v2
    [*] --> InProgress: winner inserts (election), status=in_progress
    InProgress --> Completed: acceptance txn commits, response snapshot frozen
    InProgress --> [*]: acceptance txn ROLLS BACK — the in_progress insert is part of that txn (§6.1/§10.3), so NO durable record remains; a retry is a brand-new election under the same key
    InProgress --> Failed: row committed but a post-commit step failed (cf. §9 Failure-mode #9) — the record is durable, so request_hash stays bound (Inv-3)
    Completed --> Completed: replay (same key+hash) returns snapshot/status
    Completed --> Retained: booking life + dispute/audit window (§13.3)
    Failed --> Retained: committed failed record is retained — same-key/same-hash replays the recorded outcome, same-key/different-hash → 409 (Inv-3)
    Retained --> [*]: RetentionDeadline passes — only then is the key free for a fresh binding (derived response cache may expire earlier)
```

---

## 7. Reliability mechanisms (mapped to invariants)

- **Inv-1 (no side effect from transcript):** Acceptance requires a structured `IntentRevisionPayload` + matching `ConfirmationEvidence` hash; free text is never an input to canonicalization or to the operation. Confirmation-hash mismatch → reject (§8.8).
- **Inv-2 (stable operation_id + idempotency key):** Both minted once by the agent and checkpointed *before* the first call (§10.1); reused verbatim across client retry, graph replay, outbox retry, queue redelivery, worker retry, DLQ replay, controlled owner retry.
- **Inv-3 (key bound to one canonical hash):** `idempotency_records.request_hash` is written by the election winner and is immutable. Same-key/different-hash → `409`. The canonicalizer version is stored so the binding is interpretable forever.
- **Inv-4 (atomic acceptance + operation + outbox):** Steps §10.3.1–6 run in **one** `ApplicationDbContext` transaction via `IUnitOfWork`. No `202` before commit (§10.3). If it rolls back, no durable job exists; if it commits, the outbox guarantees eventual publication even on post-commit crash.
- **Inv-5 (consumers safe under duplicate delivery):** Acceptance emits exactly one `outbox_messages` row with a unique `message_id`; downstream dedup is owned by the consumer inbox (`05`), but acceptance never emits more than one first event per operation.
- **Inv-6 (capacity separate from idempotency):** Acceptance does **not** reserve capacity; it only records intent + creates the operation in `accepted`. Capacity is an atomic conditional write in the worker (`07`).
- **Inv-7 (reconcile before repeat):** Acceptance is pre-provider; it can never produce an ambiguous external outcome. By forbidding inline provider calls, it preserves the property that the *only* repeated-create defense needed at this layer is request-hash binding.
- **Inv-10 (material change → new revision + confirmation):** A materially revised intent arrives as a **new** `operation_id` + `idempotency_key` with `replaces_operation_id` set (§10.1, §13.5). The old key cannot carry a new payload (→ `409`).
- **Inv-13 (tenant scope first):** Tenant is resolved (`08`) and validated as step §10.3.1 before any record is read or written; every unique constraint and query is `(tenant_id, …)`-scoped.
- **Inv-15 (human retries typed, preserve history):** Manual replay (§13.5) resumes the same operation or creates a replacement with a new key + `replaces_operation_id`; it may not reuse the original key for a different payload. Original operation/attempt history is preserved (`06`).
- **Inv-16 (agent states only authoritative outcomes):** The `202` explicitly means *durably accepted, not booked* (§10.4); the response status is `accepted`, never `booked`, so the agent cannot truthfully claim confirmation from this layer.
- **§13.4 creator election:** the DB unique constraint `(tenant_id, idempotency_key)` is the single atomic election primitive — exactly one inserter wins; losers load and return; no duplicate workflow starts while the first is in progress.

---

## 8. Latency budget & optimizations

This is a **reliability-first** component, but acceptance sits on the foreground critical path (the customer is waiting on the call), so it must meet the binding **durable-acceptance SLO owned by `13`** (request received → `202` committed: **p95 < 300 ms, p99 < 500 ms**). The agent-facing `submit_confirmed_intent` tool latency (`03`) is this same SLO. The figures below are the *internal component budget* that fits inside `13`'s end-to-end target — the **~80 ms inner atomic DB-transaction-plus-CPU budget is a component of**, not a substitute for, `13`'s SLO.

| Stage | Internal budget (p95) | Technique |
|---|---|---|
| Validation (tenant/schema/policy/hash) | ≤ 15 ms | In-memory tenant policy cache (resolved earlier in session); FluentValidation is allocation-light. |
| Canonicalization + hash | ≤ 5 ms | Pure CPU; payloads are small (material fields only); single SHA-256 pass. |
| Acceptance DB transaction | ≤ 40 ms | One round trip via batched commands; all inserts in a single txn; indexes on the unique keys; outbox insert is a plain row (no broker call on the hot path). |
| **Inner work (validate + hash + atomic txn)** | **~80 ms** | A *component* of `13`'s durable-acceptance SLO (request → `202`: p95 < 300 ms, p99 < 500 ms), not the end-to-end target. The agent gets a fast durable ack; *all* slow provider work is deferred to the background lane (`05`/`06`). |

Key latency principle: acceptance is fast precisely *because* it does no provider, capacity, or notification work — those are pushed past the outbox into the durable lane. The outbox decouples "accepted" latency from "booked" latency. The end-to-end durable-acceptance target is owned and reported by `13`.

---

## 9. Failure modes & recovery

| # | Failure / crash window | Symptom | Recovery | Invariant |
|---|---|---|---|---|
| 1 | Client timeout before request reaches API | Agent unsure if accepted | Agent retries with same checkpointed identity; first arrival wins election | Inv-2 |
| 2 | Network drop after API commit, before `202` reaches agent | Operation exists; agent saw timeout | Retry → loser path → returns existing operation (replay) | Inv-2, §13.4 |
| 3 | **API crash after acceptance commit, before response** | Durable operation + outbox row exist; agent saw nothing | Agent retry returns existing op; outbox publisher (`05`) still publishes the first event | Inv-4, §27.2 |
| 4 | API crash mid-transaction (before commit) | Partial writes | Transaction rolls back atomically; no operation, no outbox row; agent retry creates fresh | Inv-4 |
| 5 | Two concurrent requests, same key, same hash | Race on insert | Unique constraint elects one; loser loads + returns; no duplicate workflow | §13.4 |
| 6 | Two concurrent requests, same key, different hash | Race + conflicting payload | Winner inserts; loser detects hash mismatch → `409` | Inv-3, §13.4 |
| 7 | Confirmation hash ≠ revision hash | Tampered/stale confirmation | Reject `422` before any write (typed `ConfirmationHashMismatchException`) | Inv-1, §8.8 |
| 8 | Materially changed intent reuses old key | Conflicting payload under bound key | `409` conflict; agent must mint a new operation with `replaces_operation_id` | Inv-3, Inv-10 |
| 9 | Idempotency record stuck `in_progress` (winner crashed mid-txn) | New retries see `in_progress` | Because writes are one txn, an uncommitted record is invisible (rolled back); a *committed* `in_progress` only exists if the snapshot update failed — recovery reloads the operation status and re-freezes snapshot | Inv-4 |
| 10 | Manual replay attempts new payload under old key | Operator error | Rejected; replacement operation gets a new key + link (§13.5) | Inv-15 |

---

## 10. Interfaces to sibling specs

- **`02-foreground-conversational-agent.md`** — produces the confirmed immutable revision + confirmation evidence; owns minting/checkpointing of `operation_id`/`idempotency_key` in session state; interprets `202`/`409`/timeout into customer-safe language (Inv-16).
- **`03-agent-tool-contracts.md`** — defines the `submit_confirmed_intent` tool schema (request/response) and the shared-schemas mapping for the contracts in §4.1.
- **`05-job-queue-outbox-inbox.md`** — consumes the single `outbox_messages` row this component commits; owns publication, `message_id` dedup, and consumer inbox. The outbox handoff is the boundary.
- **`06-booking-workflow-and-fulfillment.md`** — consumes the `accepted` operation; owns all subsequent state transitions, attempts, retries, reconciliation, and manual-review/manual-replay (`replaces_operation_id`).
- **`07-capacity-and-holds.md`** — owns atomic capacity reservation, which is explicitly *not* part of acceptance (Inv-6).
- **`08-identity-and-manage-booking.md`** — supplies tenant resolution and the verification grant validated during acceptance; defines authorization for modify/cancel operation types.
- **`12-data-model-and-persistence.md`** — owns the EF Core mappings, constraints, and migrations for `idempotency_records`, `booking_intent_revisions`, `intent_confirmations`, `booking_operations`, `outbox_messages` (this spec names the columns; `12` finalizes DDL).
- **`13-observability-and-slos.md`** — correlation/causation IDs threaded from acceptance into the outbox envelope; acceptance latency + conflict-rate metrics.
- **`14-security-and-privacy.md`** — enforces that no PAN/CVV enters the request or canonical hash (Inv-12); audit-event requirements.

---

## 11. Implementation mapping

Following `docs/dotnet-api-patterns.md` (one request+handler per file under `Application/Handlers/<Feature>/`, thin controllers, typed exceptions, EF via Domain repository interfaces, `IEntityTypeConfiguration<T>`).

**`apps/api`**

```
src/Cordia.Domain/BookingOperations/
  IdempotencyRecord.cs, IdempotencyStatus.cs
  BookingOperation.cs, BookingOperationStatus.cs   (status enum incl. Accepted)
  BookingIntent.cs, BookingIntentRevision.cs, IntentConfirmation.cs
  IIdempotencyRecordRepository.cs                  (ElectCreatorAsync / LoadAsync)
  IBookingOperationRepository.cs
  IOutboxWriter.cs                                 (AddFirstEvent — write-only here)
  ICanonicalRequestHasher.cs                       (Hash(payload) → (version, hash))
  Exceptions/ ConfirmationHashMismatchException.cs, IdempotencyConflictException.cs,  (04 is the SOLE definer of IdempotencyConflictException; 03/08 reference it, never redefine)
              IntentPolicyViolationException.cs

src/Cordia.Application/Handlers/BookingOperations/SubmitConfirmedIntent/
  SubmitConfirmedIntentCommand.cs
  SubmitConfirmedIntentCommandHandler.cs           (orchestrates §10.3 in one IUnitOfWork txn)
  SubmitConfirmedIntentValidator.cs                (FluentValidation: required fields, minor-units, modify/cancel needs target_booking_id)
  AcceptanceResult.cs                              (Accepted | Conflict)

src/Cordia.Infrastructure/
  Idempotency/CanonicalRequestHasher.cs            (canon.v1 + SHA-256)
  Idempotency/IdempotencyRecordRepository.cs       (atomic upsert via unique-constraint election)
  Persistence/Configurations/IdempotencyRecordConfiguration.cs   (unique (tenant_id, idempotency_key))
  Persistence/Configurations/BookingOperationConfiguration.cs    (unique (tenant_id, operation_id), version token)
  Persistence/Configurations/BookingIntentRevisionConfiguration.cs
  Persistence/Configurations/IntentConfirmationConfiguration.cs  (FK + check payload_hash == revision hash)
  Persistence/UnitOfWork.cs                        (single-transaction wrapper)
  EntityFramework/Migrations/<ts>_AcceptanceAndIdempotency.cs

src/Cordia.Api/Controllers/
  BookingsController.cs                            ([HttpPost] /api/bookings → SubmitConfirmedIntentCommand; maps Accepted→202, Conflict→409)
  BookingOperationsController.cs                   ([HttpGet] /api/booking-operations/{operationId} → status lookup)
```

**`apps/langgraph/cordia_agent`** — submission node that mints+checkpoints identity and calls `submit_confirmed_intent`; Pydantic models in `models/` (source of truth for §4.2 contracts). Models exported via `bun nx run langgraph:generate-schemas` → `bun nx run shared-schemas:generate-types`.

**`libs/shared-schemas`** — generated C#/TS/Zod for the §4.1 contracts (never hand-edited).

---

## 12. Test strategy (from §27.2 + §27.3)

| Test | Category | Assertion |
|---|---|---|
| Same key + same payload → one operation | Integration (Alba) | Two `POST`s return identical `operation_id`; exactly one `booking_operations` row. |
| Same key + different payload → conflict | Integration | Second `POST` returns `409 IdempotencyConflict`; stored hash unchanged. |
| Concurrent submissions elect one operation | Integration/concurrency | N parallel identical `POST`s → one inserter wins, others load; one row; no duplicate outbox event. |
| API crash after commit, before response | Integration (fault injection) | Operation + single outbox row persisted; retry returns existing operation. |
| Confirmation hash mismatch → reject | Unit + Integration | `ConfirmationHashMismatchException` → `422`; no writes. |
| Canonical hash equivalence | Unit | Reordered keys, explicit-null vs absent, NFC variants, whitespace runs, equal monetary minor-units, reordered `preorder_items` all hash identically; material change differs. |
| Transport metadata excluded from hash | Unit | Differing trace IDs / retry counters / `operation_id` do not change hash. |
| Manual retry cannot change payload under old key | Integration | New payload + old key → `409`; replacement requires new key + `replaces_operation_id`. |
| Tenant isolation | Integration/security | Same `idempotency_key` under two tenants → two distinct operations (`(tenant_id, key)` scope). |
| Acceptance is atomic | Integration (fault injection) | Forced failure at each of steps 2–6 rolls back all of intent/operation/outbox; no orphan rows. |

---

## 13. Open decisions (from §31, resolved/flagged by this component)

| Decision | This component's stance / requirement |
|---|---|
| **Canonicalization specification and hash algorithm** | **Must resolve here.** Proposed: `canon.v1` per §5 rules + SHA-256, version-stamped on the idempotency record to allow `canon.v2` migration without rehashing. Final algorithm choice pending. |
| **Idempotency and audit retention periods** | **Must resolve here.** Proposed: durable uniqueness record retained for *booking life + tenant dispute/audit window* (§13.3); derived `response_snapshot` cache TTL may be shorter but uniqueness must outlive all automatic and manual replay paths. Concrete window values pending tenant/jurisdiction policy. |
| **Customer status transport (poll/SSE/WS/callback)** | Influences the `status_url` semantics in the `202`. Flagged; resolved in `13`/`03`. Acceptance only guarantees a stable, pollable `status_url`. |
| **Broker vs PostgreSQL job store** | Does not change acceptance — acceptance always writes to `outbox_messages` regardless of downstream transport. Resolved in `05`. |
