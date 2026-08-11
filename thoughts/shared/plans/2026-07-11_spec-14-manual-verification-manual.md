# Spec 14 — Security & Privacy: Manual Verification Test Manual

**Spec:** `docs/specs/14-security-and-privacy.md`
**Built on:** `thoughts/shared/plans/2026-07-10_spec-14-gap-closure.md` (items 1–24) +
`thoughts/shared/plans/2026-07-11_spec-14-sibling-linkage.md` (items 25–33).
**Purpose:** a beginner-friendly, executable checklist that proves every manually-verifiable
control in spec 14 actually works end to end — beyond what the automated suites already cover
(`bun nx test api`, `bun nx test-integration api`, `bun nx test langgraph`). Every command below
has been run against a real local instance; expected outputs are the **actual** captured output,
not hypothetical.

Automated coverage is noted per section so you know what's *already* proven by CI and doesn't need
re-proving by hand — this manual exists for the things that are easiest to trust by seeing them
happen against a live process/database.

---

## 0. Prerequisites (do this once per session)

1. **Postgres running.** Docker Compose must be up:
   ```powershell
   docker compose up -d
   docker ps --filter "name=cordia-postgres"
   ```
   You should see `cordia-postgres` with status `healthy`.

2. **No stray API processes.** Windows will silently accumulate duplicate `dotnet watch`
   processes if `bun nx serve api` is started more than once without a clean shutdown — this
   causes port-8001 collisions and `MSB3027` file-lock errors. Before starting, always check:
   ```powershell
   Get-Process -Name "Cordia.Api","dotnet" -ErrorAction SilentlyContinue |
     Select-Object Id, ProcessName, StartTime
   ```
   Kill anything unexpected with `Stop-Process -Id <id> -Force`.

3. **Never run `dotnet test` while `dotnet run`/`dotnet watch` is serving the API** — both write
   to overlapping `bin/` output folders and the running process holds its DLLs locked
   (`MSB3027`/`MSB3021`). Stop the server first, run tests, then restart the server. This is a
   Windows-specific footgun; it doesn't happen on Linux/macOS CI.

4. **Quiet the EF Core debug noise (recommended, not required).** The outbox/scheduled-job
   pollers log a burst of `DBG` lines every ~250–500ms, which floods the terminal and buries
   one-time startup log lines. In `apps/api/src/Cordia.Api/appsettings.Development.json`, set:
   ```json
   "Microsoft.EntityFrameworkCore": "Warning"
   ```
   (default is `"Debug"`). Revert when done.

5. **Sections 5–7 need the encrypted object store configured** (it has no dev default — only
   `appsettings.Test.json` wires it). Add this to `appsettings.Development.json` temporarily:
   ```json
   "ObjectStore": {
     "Root": ".tmp/objectstore-dev"
   },
   "Secrets": {
     "ObjectStoreEncryptionKey": "<any base64-encoded 32-byte value — dev-only, never a real secret>"
   }
   ```
   Generate a throwaway key:
   ```powershell
   $bytes = New-Object byte[] 32
   [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
   [Convert]::ToBase64String($bytes)
   ```
   **Always revert `appsettings.Development.json` when finished** (`git checkout --
   apps/api/src/Cordia.Api/appsettings.Development.json`).

6. **Start the API once, cleanly:**
   ```powershell
   cd apps/api
   dotnet run --project src/Cordia.Api/Cordia.Api.csproj
   ```
   Wait for `Application started. Press Ctrl+C to shut down.`

7. All `curl.exe` examples below use `-k`/`-sk` to skip the self-signed dev TLS cert. When a
   request body contains quotes, write it to a temp `.json` file and use `--data-binary "@file"` —
   inline `-d '{...}'` strings get mangled by PowerShell's quote handling on Windows.

---

## 1. Tenant scope guard (Inv-13, `TenantGuard.RequireTenant`)

**What it proves:** every endpoint fails closed with no `X-Tenant-Id` — a query can never run
without a trusted tenant scope. **Automated coverage:** `TenantIsolationTests`,
`QueueConsumerTenantIsolationTests` (cross-tenant read isolation, one repo/consumer at a time).
This manual check proves the *fail-closed* half at the HTTP boundary.

```powershell
curl.exe -sk "https://localhost:8001/api/model-traces/anything" -w "`nHTTP_STATUS:%{http_code}`n"
```

**Expected (confirmed):**
```json
{"title":"Missing tenant","status":400,"detail":"X-Tenant-Id header is required."}
HTTP_STATUS:400
```

---

## 2. Authorization spine — actor/reason-code fail-closed (§24.5)

**What it proves:** owner/operator actions reject silently-missing identity — no default actor,
no default reason code. **Automated coverage:** `MenuControllerTests`, `ManualRecoveryTests`,
`ReplayDeadLetterTests` fail-closed regression tests.

```powershell
curl.exe -sk -X POST "https://localhost:8001/api/menu/catalogs/test-123/approve" -H "X-Tenant-Id: demo-tenant" -w "`nHTTP_STATUS:%{http_code}`n"
```

**Expected (confirmed):**
```json
{"title":"One or more validation errors occurred.","status":400,
 "errors":{"Actor":["'Actor' must not be empty."],
           "ReasonCode":["'Reason Code' must not be empty."],
           "ActorRole":["'Actor Role' must not be empty."]}}
HTTP_STATUS:400
```

---

## 3. Role taxonomy (GD-1/GD-2, §12 "Owner and internal operations roles" — CLOSED)

**What it proves:** `owner` and `operator` are the only two roles; **owner satisfies operator**
(owner ⊇ operator), unknown/blank roles deny, and role denial is always audited before the 403.
**Automated coverage:** `StaffRoleTests` (full matrix), `OperatorActionBehaviorTests`
(role-deny + owner-satisfies-operator).

### 3a. Wrong role on an owner-required action → 403 + deny-audit

Menu approve requires `owner`. Presenting `operator` denies:
```powershell
curl.exe -sk -X POST "https://localhost:8001/api/menu/catalogs/test-catalog-1/approve" `
  -H "X-Tenant-Id: demo-tenant" -H "X-Actor: staff-1" -H "X-Actor-Role: operator" -H "X-Reason-Code: qa-manual-check" `
  -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed):**
```json
{"title":"Authorization failed","status":403,
 "detail":"This action requires the 'owner' role (spec 14 §24.5, GD-1 role matrix)."}
HTTP_STATUS:403
```

### 3b. Correct role passes the check (reaches business logic)

```powershell
curl.exe -sk -X POST "https://localhost:8001/api/menu/catalogs/test-catalog-1/approve" `
  -H "X-Tenant-Id: demo-tenant" -H "X-Actor: staff-1" -H "X-Actor-Role: owner" -H "X-Reason-Code: qa-manual-check" `
  -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed) — 404, NOT 403, because the role check passed and the catalog simply
doesn't exist:**
```json
{"title":"Vendor catalog not found","status":404,"detail":"Vendor catalog 'test-catalog-1' was not found."}
HTTP_STATUS:404
```

### 3c. Both attempts are on the audit trail — including the downstream 404

```powershell
docker exec cordia-postgres psql -U cordia -d Cordia_API -c "SELECT occurred_at, event_type, actor, metadata FROM audit_events WHERE tenant_id='demo-tenant' AND event_type='catalog.approved' ORDER BY occurred_at DESC LIMIT 5;"
```
**Expected (confirmed) — two distinct deny reasons, proving the audit trail distinguishes an
authorization denial from a downstream business failure:**
```
{"error": "CatalogNotFoundException", "action": "catalog.approved", "decision": "deny", ...}
{"error": "role_not_satisfied", "decision": "deny", "provided_role": "operator", "required_role": "owner", ...}
```

---

## 4. Consent gate + recording_allowed suppression (§5.1/§5.2)

**What it proves:** transcript persistence is gated on `recording_allowed`; declining consent
suppresses persistence WITHOUT erroring (§5.2 — suppression is a normal outcome, not a failure).
**Automated coverage:** `ConsentEndpointTests`, `test_transcript_persister.py`, plus the
"fail-closed / happy-path / idempotent-overwrite / cross-tenant" tests named in the sibling-linkage
plan Phase 2.

### 4a. Seed a session (the channel gateway is the only real writer — this INSERT stands in for it)
```powershell
docker exec cordia-postgres psql -U cordia -d Cordia_API -c "INSERT INTO conversation_sessions (tenant_id, session_id, channel, started_at) VALUES ('demo-tenant','verify-sess-1','voice', now());"
```

### 4b. Append a turn BEFORE any consent is recorded → suppressed, not an error
```powershell
'{"seq":1,"turn_id":"t1","speaker":"customer","text":"hello there"}' | Out-File turn1.json -Encoding utf8 -NoNewline
curl.exe -sk -X POST "https://localhost:8001/api/conversation-sessions/verify-sess-1/transcript-turns" `
  -H "X-Tenant-Id: demo-tenant" -H "Content-Type: application/json" --data-binary "@turn1.json" -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed):**
```json
{"session_id":"verify-sess-1","seq":1,"persisted":false}
HTTP_STATUS:200
```

### 4c. Record consent = declined → still suppressed
```powershell
'{"consent_policy_version":"us-ca-two-party:2026-01","jurisdiction_policy_id":"us-ca-two-party","outcome":"declined","recording_allowed":false,"captured_at":"2026-07-10T05:00:00Z","notice_channel":"voice"}' | Out-File consent-declined.json -Encoding utf8 -NoNewline
curl.exe -sk -X POST "https://localhost:8001/api/conversation-sessions/verify-sess-1/consent" `
  -H "X-Tenant-Id: demo-tenant" -H "Content-Type: application/json" --data-binary "@consent-declined.json" -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed):** `{"recording_allowed":false,"session_id":"verify-sess-1"}` — `200`.

### 4d. Record consent = granted → the SAME turn now persists
```powershell
'{"consent_policy_version":"us-ca-two-party:2026-01","jurisdiction_policy_id":"us-ca-two-party","outcome":"granted","recording_allowed":true,"captured_at":"2026-07-10T05:05:00Z","notice_channel":"voice"}' | Out-File consent-granted.json -Encoding utf8 -NoNewline
curl.exe -sk -X POST "https://localhost:8001/api/conversation-sessions/verify-sess-1/consent" `
  -H "X-Tenant-Id: demo-tenant" -H "Content-Type: application/json" --data-binary "@consent-granted.json" -w "`nHTTP_STATUS:%{http_code}`n"

'{"seq":1,"turn_id":"t1","speaker":"customer","text":"I would like to book a table for two"}' | Out-File turn1b.json -Encoding utf8 -NoNewline
curl.exe -sk -X POST "https://localhost:8001/api/conversation-sessions/verify-sess-1/transcript-turns" `
  -H "X-Tenant-Id: demo-tenant" -H "Content-Type: application/json" --data-binary "@turn1b.json" -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed):** `{"session_id":"verify-sess-1","seq":1,"persisted":true}` — `200`.

> **Note on PAN redaction inside transcript text:** don't hand-type a real-looking card number
> into a manual test — the existing unit test
> (`EncryptedFileObjectStoreTests.PutGet_RoundTrips_AndStoresCiphertextOnDisk`, §8 below) already
> proves masking with a standard test PAN and is safer to re-run than to reproduce by hand.

---

## 5. Consent-gated transcript reader (Inv-11, §3.1 — first `ConsentRequiredException` thrower)

**What it proves:** session identity ALONE never reads a transcript — you need either a spec-08
grant or a satisfying staff role + reason code; consent-absent readers get 409, not the data.
**Automated coverage:** `TranscriptEndpointTests` (grant path, expired/wrong-tenant grant, wrong
role, consent-declined 409, allow/deny audit shape, cross-tenant isolation). This section proves
the role path manually (the grant path needs a spec-08 `verification_grant` row and is easiest to
trust via the automated suite).

### 5a. No credentials at all → 403, deny-audited (never a silent empty result)
```powershell
curl.exe -sk "https://localhost:8001/api/conversation-sessions/verify-sess-1/transcript" -H "X-Tenant-Id: demo-tenant" -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed):**
```json
{"title":"Authorization failed","status":403,
 "detail":"Reading a transcript requires a read_sensitive verification grant or a staff role with a reason code (spec 14 §3.1)."}
```

### 5b. Operator role + reason code → 200, turns returned, allow-audited
```powershell
curl.exe -sk "https://localhost:8001/api/conversation-sessions/verify-sess-1/transcript" `
  -H "X-Tenant-Id: demo-tenant" -H "X-Actor: staff-1" -H "X-Actor-Role: operator" -H "X-Reason-Code: qa-manual-check" `
  -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed):**
```json
{"session_id":"verify-sess-1","turns":[{"seq":1,"turn_id":"t1","speaker":"customer","text":"I would like to book a table for two","ts":"..."}]}
HTTP_STATUS:200
```

### 5c. Audit trail shows BOTH the deny and the allow
```powershell
docker exec cordia-postgres psql -U cordia -d Cordia_API -c "SELECT occurred_at, event_type, actor, metadata->>'decision' AS decision FROM audit_events WHERE tenant_id='demo-tenant' AND event_type='read_sensitive_transcript' ORDER BY occurred_at DESC LIMIT 5;"
```
**Expected (confirmed):**
```
read_sensitive_transcript | staff-1 | allow
read_sensitive_transcript | unknown | deny
```

> To see the **409 consent-required** path yourself: repeat step 4c (decline consent again) then
> re-run 5b — the same operator credentials that got a `200` now get a `409` with title "Consent
> required", proving authorization is checked BEFORE consent (an unauthorized caller never learns
> whether consent was even granted).

---

## 6. Model-trace sink (§13 §25.3 telemetry tier, WI3)

**What it proves:** traces are staff-role-only (no grant analog — there's no "customer" for a
telemetry blob); **every view is audited, including views of traces that don't exist**
(§4.3 wants the attempt, not just the hit). **Automated coverage:** `ModelTraceEndpointTests`.

```powershell
'{"trace_id":"verify-trace-1","session_id":"verify-sess-1","kind":"prompt","content":"system prompt text, no sensitive data"}' | Out-File trace1.json -Encoding utf8 -NoNewline
curl.exe -sk -X POST "https://localhost:8001/api/model-traces" -H "X-Tenant-Id: demo-tenant" -H "Content-Type: application/json" --data-binary "@trace1.json" -w "`nHTTP_STATUS:%{http_code}`n"

# No role → 403
curl.exe -sk "https://localhost:8001/api/model-traces/verify-trace-1" -H "X-Tenant-Id: demo-tenant" -w "`nHTTP_STATUS:%{http_code}`n"

# Operator role → 200
curl.exe -sk "https://localhost:8001/api/model-traces/verify-trace-1" -H "X-Tenant-Id: demo-tenant" -H "X-Actor: staff-1" -H "X-Actor-Role: operator" -H "X-Reason-Code: qa-manual-check" -w "`nHTTP_STATUS:%{http_code}`n"

# Nonexistent trace, still operator role → 404, but STILL audited
curl.exe -sk "https://localhost:8001/api/model-traces/does-not-exist" -H "X-Tenant-Id: demo-tenant" -H "X-Actor: staff-1" -H "X-Actor-Role: operator" -H "X-Reason-Code: qa-manual-check" -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (all confirmed):** `200` (store) → `403` (no role) → `200` (operator, real trace) →
`404` (operator, fake trace id — but see below, this attempt is still audited):
```powershell
docker exec cordia-postgres psql -U cordia -d Cordia_API -c "SELECT subject_ref, metadata->>'decision' AS decision FROM audit_events WHERE tenant_id='demo-tenant' AND event_type='model_trace.viewed' ORDER BY occurred_at DESC LIMIT 3;"
```
```
does-not-exist  | allow   <- the VIEW ATTEMPT is audited even though the trace didn't exist
verify-trace-1  | allow
verify-trace-1  | deny    <- the no-credentials attempt
```

**Bonus check (config, no server needed):** the 14-day trace TTL is strictly shorter than the
90-day transcript TTL — proven by the unit test
`TranscriptAndModelTrace_GetTheirConfigDefaultTtls_TraceStrictlyShorter` (§8 below runs it).

---

## 7. Webhook secret custody (`ISecretStore`, §24.4)

**What it proves:** the shared webhook secret is resolved through the secret-custody seam, not
hardcoded, and a wrong secret is rejected with a constant-time comparison (no timing oracle).
**Automated coverage:** two dedicated Alba tests (store-only + precedence-over-legacy-config).

```powershell
'{"notification_id":"n1","provider_message_id":"m1","status":"delivered"}' | Out-File receipt1.json -Encoding utf8 -NoNewline
curl.exe -sk -X POST "https://localhost:8001/api/notification-webhooks/delivery-receipts" `
  -H "X-Tenant-Id: demo-tenant" -H "X-Webhook-Secret: wrong-secret" -H "Content-Type: application/json" --data-binary "@receipt1.json" `
  -w "`nHTTP_STATUS:%{http_code}`n"
```
**Expected (confirmed):** `401 Unauthorized`. (The correct dev secret is
`dev-notification-webhook-secret`, from `appsettings.Development.json` — swap it in to see `202`/
`404` instead, depending on whether `n1`/`m1` exist.)

---

## 8. Encrypted object store — ciphertext at rest (§24.3/§24.4)

**What it proves:** PAN-bearing content is never persisted as plaintext; wrong key fails
authentication; tenant paths are isolated; path traversal is rejected. **Fully automated** — this
is the fastest, most reliable way to re-verify it (no server needed):

```powershell
cd apps/api
dotnet test test/Cordia.Tests/Cordia.Tests.csproj --filter "FullyQualifiedName~EncryptedFileObjectStoreTests" --logger "console;verbosity=detailed"
```
**Expected (confirmed):** `19/19 passed`, including
`PutGet_RoundTrips_AndStoresCiphertextOnDisk` — which plants a test card number
(`4111 1111 1111 1111`) in a payload, writes it through the real store, then asserts the raw
`.bin` bytes on disk **do not contain "4111"** anywhere.

---

## 9. Retention sweep scheduling (§24.3, background job)

**What it proves:** the retention sweeper actually runs on a schedule (not dead code outside
tests). **Automated coverage:** `RetentionSweepTests`, `SecurityRetentionSweepTests`,
`RetentionSweeperTests` (seeded expired rows + one sweep call).

1. Add to `appsettings.Development.json`:
   ```json
   "Messaging": { "RetentionSweep": { "Enabled": true, "InitialDelaySeconds": 10, "IntervalHours": 1 } }
   ```
2. Also set `Microsoft.EntityFrameworkCore` to `"Warning"` (prerequisite §0.4) — otherwise the
   one-time startup log line gets buried under DB polling noise within seconds and you'll wrongly
   conclude it didn't run.
3. Start the API and watch for (confirmed captured output):
   ```
   [..:..:.. INF] Retention sweeper retention-sweep-...: starting (IntervalHours=1, InitialDelaySeconds=10)
   [..:..:.. INF] Retention sweeper retention-sweep-...: cycle complete, 0 row(s) pruned/anonymized
   ```
   `0` rows is the correct/expected result on a fresh dev database — it proves the cycle *ran*,
   not that anything was necessarily due for pruning.

---

## 10. Regression suite — DLQ replay + booking submission unaffected

**What it proves:** none of the spec-14 authorization/audit/role additions broke the surfaces they
wrap. **Fully automated:**
```powershell
dotnet test test/Cordia.Tests/Cordia.Tests.csproj --filter "(FullyQualifiedName~ReplayDeadLetterTests|FullyQualifiedName~AcceptanceEndpointTests)&Category=Integration" --logger "console;verbosity=normal"
```
**Expected (confirmed):** `24/24 passed`.

---

## 11. Untrusted-content boundary / prompt injection (§8.11 — langgraph side, no HTTP surface)

There is no manual HTTP surface for this — the boundary lives inside the agent harness
(`ToolClient` gate 1c, `PromptChannels`). The most trustworthy manual action is running the
targeted suites yourself and reading the assertions, since "manually poking an LLM" is
non-deterministic and won't prove anything CI doesn't already prove better:
```powershell
cd apps/langgraph
uv run pytest tests/unit_tests/tools/test_client_tool_gate.py tests/unit_tests/security/ -v
```
Look for: an unknown/off-allowlist tool name → `tool_not_permitted` envelope (not a `KeyError`);
a `PromptChannels` construction with `UNTRUSTED` content misfiled into the system channel raises
`ChannelSeparationError` in both directions.

---

## Cleanup (always do this after a manual-verification pass)

```powershell
# Stop the API
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' or Name='Cordia.Api.exe'" |
  Where-Object { $_.CommandLine -like "*Cordia.Api*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# Revert the dev-only config changes
git checkout -- apps/api/src/Cordia.Api/appsettings.Development.json

# Remove the seeded test session + any temp JSON files
docker exec cordia-postgres psql -U cordia -d Cordia_API -c "DELETE FROM conversation_sessions WHERE tenant_id='demo-tenant' AND session_id='verify-sess-1';"
Remove-Item -Path turn1.json,turn1b.json,consent-declined.json,consent-granted.json,trace1.json,receipt1.json -ErrorAction SilentlyContinue

git status   # should show no changes from this pass
```

`audit_events` rows generated during the pass are intentionally left in place — the whole point of
§9 is that this table is **append-only and never pruned** (GD-4); deleting from it manually would
itself be a violation of the invariant this manual exists to prove.

---

## Coverage map (spec §, invariant → section)

| Area | Spec § | Invariant | Section |
|---|---|---|---|
| Tenant scope guard | §7, §24.3 | Inv-13 | 1 |
| Actor/reason fail-closed | §24.5 | Inv-11 | 2 |
| Role taxonomy (owner/operator) | §12, §24.5, §28.4 | Inv-11 | 3 |
| Consent gate + suppression | §5.1/§5.2, §4.1 | — | 4 |
| Consent-gated transcript read | §3.1, §5.1 | Inv-11 | 5 |
| Model-trace sink + view audit | §13 §4.3/§25.3 | Inv-11, Inv-14 | 6 |
| Secret custody | §24.4 | Inv-12 | 7 |
| Encryption at rest | §24.3/§24.4 | Inv-12 | 8 |
| Retention scheduling | §24.3 | — | 9 |
| Regression (no breakage) | — | Inv-2, Inv-5 | 10 |
| Untrusted-content boundary | §8.11 | Inv-1 | 11 |
