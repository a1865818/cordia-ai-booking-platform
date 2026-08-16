# Text-driven booking end-to-end (no audio, no telephony)

How to drive the LangGraph agent with a plain transcript against the **real**
.NET API and a real Postgres, all the way to a durable `booking_operations` row.

Everything here is opt-in. The automated suites (`bun nx test langgraph`,
`bun nx test-integration langgraph`) stay 100% offline — they inject
`httpx.MockTransport` clients and stub extractors, and register nothing from the
environment.

## What got wired

Two registries that previously only tests ever populated are now bootstrapped at
graph import time, both following the `bootstrap_verifier()` shape in
`cordia_agent/graph.py` — **unset ⇒ nothing registered, behaviour unchanged;
set-but-broken ⇒ the process fails at startup, not mid-conversation**.

| Registry | Env var | Bootstrap |
|---|---|---|
| `ToolClient` (HTTP tool transport) | `CORDIA_API_BASE_URL` | `cordia_agent.tools.client.bootstrap_tool_client()` |
| `SlotExtractor` (model-backed capture) | `AZURE_OPENAI_DEPLOYMENT_SLOT_EXTRACTOR` | `cordia_agent.slots.extractor.bootstrap_slot_extractor()` |

`CORDIA_API_BASE_URL` is the only new variable. The extractor deliberately reuses
the per-role deployment override that `settings_from_env("slot_extractor")`
already reads, so there is no knob that exists purely to flip the bootstrap.

Both are idempotent and never replace a client a test fixture already installed.

## Run sequence

### 1. Postgres

```bash
docker compose up -d          # creates Cordia_Agent + Cordia_API via docker/postgres/init
```

### 2. API with migrations

`DatabaseBootstrap:MigrateOnStartup` is now `true` in
`apps/api/src/Cordia.Api/appsettings.Development.json` (it stays `false` in
`appsettings.json` for every other environment), so a Development run applies the
EF migrations itself — no `dotnet ef database update` step.

```bash
cd apps/api
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="http://localhost:8000" \
dotnet run --project src/Cordia.Api/Cordia.Api.csproj
```

> **Watch out:** the repo's `dotnet user-secrets` store may override
> `ConnectionStrings:CordiaDBConnection` with a remote (Neon) database. Env vars
> outrank user secrets, so force the local container when you want the local DB:
>
> ```bash
> ConnectionStrings__CordiaDBConnection="Host=localhost;Port=5432;Database=Cordia_API;Username=cordia;Password=localdev"
> ```

### 3. Seed a tenant + ingress endpoint

There is no `HasData` and no seeder anywhere in the API. The channel webhooks
resolve a tenant from `tenant_channel_endpoints`, so on an empty table
`POST /api/channels/telephony` returns 422 and no `TenantContext` can ever be
minted. Apply the repeatable seed (idempotent, safe to re-run):

```bash
docker exec -i cordia-postgres psql -U cordia -d Cordia_API \
  < apps/api/scripts/seed-dev-tenant.sql
```

It inserts `tenant_dev_001` / `cep_dev_voice_001` with the ingress signal below,
plus 60 days of hourly `capacity_slots` rows for resource `svc_001`. Those slots
are what turns `POST /api/availability/candidates` and the fulfillment engine's
`reserve_capacity` step onto the first-party *tracked* path (`mode=live`, a real
`capacity_holds` row, `booked_capacity` moved on finalize). They are not
required: an un-provisioned resource/time answers `mode=informational` with the
requested time as an advisory candidate, and the booking still completes.

The ingress endpoint row is `channel='voice'`, `signal_type='phone_number'`, `signal_value='+61400000001'`,
`status='active'` (the `[PgName]` values on `TenantChannel`/`SignalType`/
`EndpointStatus`). The unique index `uq_channel_signal_active` is filtered on
`status = 'active'`, so the script upserts on the primary key rather than
inserting a duplicate active row.

Smoke-test it:

```bash
curl -s -X POST http://localhost:8000/api/channels/telephony \
  -H 'Content-Type: application/json' -d '{"to":"+61400000001"}'
# -> {"tenant_context":"eyJ...","claims":{"tenant_id":"tenant_dev_001",...}}
```

### 4. Signing keys

`apps/langgraph/.env` already carries `CORDIA_TENANT_CONTEXT_SIGNING_KEYS`
matching the dev signer in `appsettings.Development.json`. The contract is
`base64decode(apps/api value) == utf8(langgraph value)` per kid.

### 5. Run the agent

```bash
cd apps/langgraph
CORDIA_API_BASE_URL=http://localhost:8000 \
  uv run python scripts/run_text_booking_e2e.py [--stub-availability]
```

The script mints a `TenantContext` from the telephony webhook, then drives the
compiled graph turn by turn over an `InMemorySaver`, streaming
`stream_mode="updates"` so each node's state delta is printed as it lands.

## Why the driver, not the graph, ends a turn

Two structural facts shape the runner:

1. **A dialog is several graph invocations.** Each turn runs START → … and the
   §8.3 topology has no interrupt, so multi-turn state only survives via a
   checkpointer. `build_agent_graph()` therefore takes an optional
   `checkpointer`; the module-level `agent_graph` still compiles stateless.
2. **The unconfirmed path cycles rather than ending.** `await_confirmation` →
   `capture_fields` → … → `read_back` → `await_confirmation` has no END, and so
   does `capture_fields` → `clarify` when no draft exists yet. Left alone a turn
   dies on `GraphRecursionError`. The runner breaks out of `astream` at the
   confirmation gate instead.

Consequently the script uses three turns: **(1)** greeting → consent notice ends
the turn; **(2)** consent reply *and* booking details in one utterance (splitting
them would leave turn 3 with no booking intent and no draft, i.e. the
`clarify` cycle); **(3)** confirmation.

## Current state

The text path produces a real booking end to end. Verified against the live stack:

- Telephony ingress mints a signed `TenantContext`; `resolve_tenant` verifies it
  and installs it on the `ToolClient` (Inv-13 gate satisfied).
- `consent_gate` really calls `GET /api/consent-policies` and
  `POST /api/conversation-sessions/{id}/consent`, and the row lands in Postgres:

  ```
  session_id                            | consent_state | consent_policy_version
  sess_bf25b5e016b8402d87d3715c3587c546 | granted       | default:2026-01
  ```

- The model-backed `SlotExtractor` really extracts from free text:

  ```
  BookingRequest(operation=create, service_id='svc_001', location_id='loc_001',
                 starts_at=2026-08-10 19:00+00:00, party_size=4,
                 special_requests=['high chair'])
  ```

- `check_candidate_availability` really calls `POST /api/availability/candidates`
  and gets a typed answer back, so the turn advances to `read_back`:

  ```
  availability_mode = "live"
  availability_candidates = [{"starts_at": "2026-08-10T19:00:00Z",
                              "duration_minutes": 60, "availability_mode": "live"}]
  ```

- `read_back` renders, `await_confirmation` records a `Confirmation` bound to the
  revision hash, and `submit_intent` mints `operation_id` / `idempotency_key`
  once and POSTs `/api/bookings` — which the API accepts (`202`) and the
  fulfillment engine drives to `booked`:

  ```
  operation_id    | op_b1793647b92c4ae3a5a3b61cdc3ada8f
  status          | booked
  request_hash    | canon.v1:9efa756fc92721ac095d700d7467e30161781146f53f7295a24db182009ec45e
  ```

`--stub-availability` is therefore no longer needed; it is kept only as a way to
drive the chain with the API's availability route bypassed.

`/api/tenant-context`, `/api/customers/context`, `/api/booking-drafts/validate`
and `/api/handoffs` still answer `405` — all four degrade gracefully and none
blocks the booking.

One remaining gap: nothing in production populates
`AgentState.disclosures_presented`, and `await_confirmation` refuses to record a
`Confirmation` while it is empty (§5.5/§14 — the disclosure set is still a spec-14
stub). The runner supplies it via `aupdate_state`, since presenting disclosures is
channel-gateway work.

## Still blocking a full voice E2E

Audio ingress, STT/TTS and the `AgentPort` that maps `TranscriptFinalFrame` onto
`AgentInputState` all live under `cordia_agent/streaming/` and are not exercised
by this path at all. The disclosure gap above also has to close, since a voice
call has no driver to inject `disclosures_presented`.
