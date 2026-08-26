# Wave 0 — Foundations

Status: Plan · Branch: `chore/wave-0-foundations` · Base: `main` @30f46fd
Research: [`thoughts/shared/research/2026-08-26_wave-0-foundations.md`](../research/2026-08-26_wave-0-foundations.md)
Roadmap: https://claude.ai/code/artifact/e67f1cc3-4539-43bd-b844-553b6d0267e1
Closes: #91 · Partially closes: #94 (D2, D12)

## Why this wave exists

Nine open issues name an integration test as their primary regression guard, and the integration
suite has never run in CI. Worse — measured during Stage 1 — **nx replays cached `api:build` /
`api:test` results when the generated shared types change**, because those paths are gitignored and
Nx's file hasher skips them. So "green" currently does not mean what every later wave will assume it
means. Wave 0 makes the rest of the roadmap falsifiable.

## How this maps onto Cordia

| New concept | Existing Cordia artifact it extends | Consequence |
|---|---|---|
| Tenant-scoped scheduled-job claim in tests | `IntegrationTests/Messaging/OutboxTenantScope.cs` — the proven DI-override pattern | New `ScheduledJobTenantScope.cs` is a sibling, not a new mechanism. Mirrors `ScheduledJobRepository.ClaimDueAsync` exactly, plus `tenant_id`. |
| Parity pin for that mirror | `OutboxTenantScopeParityTests` | A sibling parity test, or the new mirror drifts from production the way the outbox one would have. |
| CI integration job | `ci-cd.yml`'s existing `ci-dotnet` job shape | A new job reusing the same setup steps + a `services: postgres` block. Not a new pipeline. |
| Redis in local dev | `docker-compose.yml`'s single `postgres` service | A second service in the same file. No new compose variant. |
| Finalize unit-test harness | `ReconcileOperationCommandHandlerTests.cs` `CreateHandler()` factory | Same Moq-fields + factory + shared-defaults shape. Copy the convention, do not invent one. |
| nx cache correctness | `nx.json` `namedInputs` | Fix the three inert gitignored entries; do not restructure the input sets. |

**Governing directive:** integrate with existing mechanisms, never add parallel implementations.

## Spec ownership & frozen decisions

| Contract / mechanism | Owner | This wave is | Action |
|---|---|---|---|
| Transactional outbox + inbox (`ClaimBatchAsync`) | spec 05 | **referencer** | production code untouched; test mirror kept byte-identical + parity-pinned |
| `scheduled_jobs` claim (D11 `run_after`) | spec 12 (DDL) / 05 (protocol) | **referencer** | production code untouched |
| `TenantContext` / tenant scope (Inv-13) | spec 15 | **referencer** | no production change; closes a *test-harness* leak |
| All DDL / pg-enums | spec 12 | **referencer** | **no migration in this wave** |
| Generated type pipelines | `docs/type-generation.md` | **referencer** | fixes cache inputs only; no contract change |

**Invariants in play:** Inv-13 only, and only in the test harness. No invariant changes meaning.
**Frozen decisions:** none touched.

## Phases

### Phase 1 — Make "green" mean something

`fix(tooling): hash the generated types, close the appsettings cache hole, drop two dead exporter keys`

The measured finding (research B8): `nx.json`'s `dotnet` and `typescript` namedInputs reference
three generated paths that are **gitignored**, so they contribute nothing to any hash. Proven:
`SharedTypes.cs` gained `resource_id`, `api:build` still reported `[local cache]`, and the exporter
scraped a stale DLL. `--skip-nx-cache` produced a clean contract.

Changes:
1. `nx.json` — make the generated-type inputs actually hash. Preferred fix: add
   `{ "runtime": "..." }`-free explicit inputs by un-ignoring nothing and instead adding the
   generated dirs to `.nxignore`-aware hashing via `inputs` on the *producing* targets' `outputs`
   already declared — concretely, add `"^generate-types"` / `"^generate-schemas"` to `dependsOn` on
   `api:build` and `web:build|test|lint` so the producing task's hash flows into the consumer.
   *(Chosen over un-gitignoring the generated dirs, which would break the "generated dirs are
   gitignored" convention in CLAUDE.md and `docs/type-generation.md`.)*
2. `nx.json` — add `{projectRoot}/src/**/appsettings*.json` to the `dotnet` namedInput.
   Do **not** touch `apps/web/bunfig.toml` (already covered by `apps/web/project.json`) and do
   **not** add `inputs`/`cache` to `test-integration` (correctly uncached).
3. `tools/scripts/export_openapi.mjs` — `ConnectionStrings__MAgeDBConnection` →
   `ConnectionStrings__CordiaDBConnection` (`:249`); delete or wire up `OpenApiExport__Enabled`
   (`:251`), which has zero readers in `apps/api`.
4. `CLAUDE.md` — add the `v1.json` conflict recipe:
   `git checkout <base-ref> -- libs/api-contract/openapi/v1.json && bun nx run api-contract:generate-types && git add …`

**Automated Verification**
- [x] `bun nx run api-contract:generate-types` then `git status --porcelain libs/` is **empty**
- [x] perturb a **Pydantic contract** (`cordia_agent/state.py` description); `api:build` re-runs — 7 of 8 tasks executed, vs. `api:build [local cache]` before the fix
- [x] edit `apps/api/src/Cordia.Api/appsettings.Test.json` trivially; `> nx run api:test` appears with **no** `[local cache]` marker; reverted
- [x] `bun nx test api` green — **1801 passed, 0 failed** · `bun nx lint api` green

**Manual Verification**
- [x] Regression check: `v1.json` regenerates byte-identical before and after the exporter change; drift check empty both times.

**Discoveries and Notable Information**

1. **The stated criterion was wrong and was corrected.** The plan originally said to verify by
   touching `SharedTypes.cs`. That would not work and proving why matters: `dependsOn` propagates
   the **producing task's hash**, not the artifact's content. Hand-editing a generated file still
   invalidates nothing — which is fine, because hand-editing generated files is already forbidden.
   The real-world trigger is a contract change upstream, so that is what was tested.

2. **The invalidation chain is precise, not over-eager.** Verified from the target definitions:
   `langgraph:generate-schemas` inputs are `cordia_agent/**/*.py` + `langgraph.json` + the exporter
   script — **not** `tests/**`. `shared-schemas:generate-csharp` inputs are
   `{projectRoot}/schemas/**/*`, and `libs/shared-schemas/schemas/` is **tracked** (only
   `generated/` is gitignored), so its hash follows real JSON-Schema content. Net effect: a langgraph
   test edit does **not** rebuild the .NET API; a Python edit that does not change the emitted schema
   does **not** rebuild it either. Only an actual contract change cascades.

3. **A description-only change does not reach `v1.json`.** The perturbation appeared in
   `SharedTypes.cs` but not in the OpenAPI document — Swashbuckle does not emit XML doc summaries
   into the schema. `api:build` still recompiled, which is what the gate is about, but this means
   `v1.json` is a weaker drift signal than it looks: it catches shape changes, not documentation.

4. **`OpenApiExport__Enabled` was kept, not deleted.** It has zero readers in `apps/api` today, so
   deleting it as dead config is tempting. It is the flag Wave 1's PR C needs when it gates
   `app.UseSwagger()` outside Development (#94 D9) — deleting it now would mean the exporter breaks
   when that gate lands. A comment in the script says so.

5. **Task-chain length is the cheap regression signal for this fix.** `api-contract:generate-types`
   went from *"and 3 tasks it depends on"* to *"and 7 tasks it depends on"*; `web:typecheck` went
   from **zero** dependencies to 8. If a future change drops those back, the hole is back.

6. **`web:lint` deliberately excluded.** It runs `oxlint src/ --deny-warnings` over
   `src/**/*.{ts,tsx,js,jsx}` only — it does not typecheck, so generated types cannot affect its
   result. `build`, `test`, `test-coverage` and `typecheck` all key on the `typescript` namedInput
   and did get the dependency.

7. **Two pre-existing environment gaps found, neither caused by this wave, neither fixed here.**
   - `apps/web` dependencies were **not installed** — `@happy-dom/global-registrator` is declared at
     `apps/web/package.json:28` but absent from `node_modules`. Verified pre-existing by stashing
     this phase's change and reproducing the identical 45 failures on pristine `main`.
     `bun install` fixed it and left `bun.lock` **unchanged**, so this was purely an uninstalled
     workspace, not a lockfile problem.
   - **`bun nx test web` cannot run on this machine.** `apps/web/src/test-setup.bun.ts` hard-throws
     below `MIN_BUN_VERSION = ">=1.4.0"` (a deliberate guard from the #98/#99 bun-test migration,
     protecting against a `mock.module` isolation bug). Local Bun is **1.3.14**. CI pins
     `bun-version: latest` (`ci-cd.yml:64-66, 111-113`) so CI is unaffected. Consequence: the
     `web:test` half of this phase's change is **correct by construction but not verified locally** —
     it is the same one-line mechanism proven on `api:build` and on `web:typecheck` (exit 0, 8
     dependency tasks). Flagged rather than claimed as verified.

---

### Phase 2 — Scope the test claims per tenant (#91 a, b)

`test(api): scope outbox and scheduled-job claims per tenant`

Research corrected the roadmap here. Of the 17 files referencing `NeutralizeExistingUnpublishedAsync`:
**5 are already migrated** (helper deleted, comment remains, already call `ScopeOutboxClaimToTenant`)
— no work. **1** is an unrelated comment (`NotificationReconcileTests:166`). **11** still
define-and-call it, of which **`OutboxPublisherTests` must be excluded** — its Test 1
(`ClaimBatchAsync_SetsLease_AndIsCrossTenant`) exists to pin production's cross-tenant claim and
would fail by construction.

**Real scope: 10 conversions.** `EndToEndSubstrateTests`, `DepositAndUserActionTests`,
`FinalizeHappyPathTests`, `MenuIngestionTests`, `MenuLifecycleEndToEndTests`, `PoisonDeadLetterTests`,
`StateMachineCapstoneTests`, `RejectionTests`, `NotificationSenderTests`, `FinalizeAgainstHoldTests`.

Plus:
- New `IntegrationTests/Messaging/ScheduledJobTenantScope.cs` — sibling of `OutboxTenantScope.cs`,
  mirroring `ScheduledJobRepository.ClaimDueAsync:47-97` with an added `tenant_id` predicate.
  Ships **with a parity test** modelled on `OutboxTenantScopeParityTests`, and a reciprocal
  "keep in sync" comment on the production method — or it drifts.
- `RetryAndFailureTests` — remove `NeutralizeExistingScheduledJobsAsync` (`:208`, called `:359`,
  `:580`), the last remaining neutralize helper, once the scope wrapper covers it.
- Fold in `IntegrationTests/Security/ConsentEndpointTests.cs:107` CS8631 (`string?[]` vs
  `Assert.Equal<T>`) — an orphan warning owned by no PR.

**Do not touch:** `OutboxRepository.ClaimBatchAsync`, `ScheduledJobRepository.ClaimDueAsync`,
`OutboxPublisherTests`, the 5 already-migrated classes.

**Automated Verification**
- [ ] `bun nx test-integration api` green (needs `docker compose up -d postgres`)
- [ ] `bun nx test-integration api` green when run **twice concurrently is NOT tested** — instead assert zero `testhost` before each run
- [ ] `grep -rl NeutralizeExisting apps/api/test` returns **only** `OutboxPublisherTests.cs`
- [ ] `bun nx lint api` green · zero CS8631 in build output

**Manual Verification**
- [ ] Regression: `OutboxPublisherTests.ClaimBatchAsync_SetsLease_AndIsCrossTenant` still asserts both tenants and passes.
- [ ] Regression: `OutboxTenantScopeParityTests` still passes; new scheduled-job parity test passes.

**Discoveries and Notable Information**
_(implementer fills in)_

---

### Phase 3 — Run the integration suite in CI (#91 c)

`ci: run the integration suite against a postgres service container`

Lands as **`continue-on-error: true` / non-required**, promoted to required only at the end of
Wave 1. Turning a knowingly-contaminated suite into a required gate buys a red gate that gets
disabled permanently.

Constraints found in research:
- `PostgresFixture.TestConnStr` (`Persistence/PostgresFixture.cs:20`), `AcceptanceApiFixture.TestConnStr`,
  `TenantResolutionApiFixture.TestConnStr` are three byte-identical **`const`** strings
  (`localhost:5432`, `cordia`/`localdev`). 26 call sites across 24 files for the first alone.
  **Match the service container to the constants** — do not refactor 26 call sites in Wave 0.
- **Three** concurrent first-run migrators, not two: `PostgresFixture:29` `MigrateAsync()`,
  `AcceptanceApiFixture:48` and `TenantResolutionApiFixture:42` `MigrateOnStartup=true`.
  All 39 migrations apply to an empty database for the first time ever.
- `test-integration` has no `dependsOn: build` and no `--no-restore` — the job pays a full
  restore+build. Leave the target alone; account for it in the job.
- Record #91's explicit ask in the issue: **integration is now a CI gate, not local-only.**

**Automated Verification**
- [ ] the new job runs `bun nx run api:test-integration` and reports a result on a PR
- [ ] all 39 migrations apply cleanly to an empty `Cordia_API_Test` (assert in the job log)
- [ ] existing `ci-dotnet`, `ci-web`, `ci-python`, `generate-schemas` jobs unchanged and green

**Manual Verification**
- [ ] Suite wall-clock recorded in the plan (currently unknown anywhere in the repo).
- [ ] Job is non-required; a red run does not block merge.

**Discoveries and Notable Information**
_(implementer fills in)_

---

### Phase 4 — Collapse the duplicate langgraph config, add redis (#94 D2, D12)

`chore(langgraph): add redis to compose and collapse the duplicate production config`

`langgraph.json` and `langgraph.production.json` are **byte-identical** (verified by diff).
Delete the production copy and update its four references: `nx.json:42`,
`apps/langgraph/project.json:86`, `apps/langgraph/scripts/build_image.sh:13`,
`docs/deployment.md:68,121`. Add a `redis` service to `docker-compose.yml` alongside `postgres`.

**Scope honesty:** this gets the production image from `KeyError: REDIS_URI` to
`ValueError: License verification failed`. It does **not** make langgraph start — that is #94 D1,
procurement, and it is not in this wave. The exit criterion is written accordingly.

`docs/deployment.md` is current, honest and well-maintained — **update only the two file-path
references, change nothing else.**

**Automated Verification**
- [ ] `bun nx test langgraph` green · `bun nx lint langgraph` green
- [ ] `grep -rn "langgraph.production.json" .` returns **zero** hits
- [ ] `docker compose config` parses; `docker compose up -d redis` reaches healthy

**Manual Verification**
- [ ] `bun nx run langgraph:serve-docker` gets past `KeyError: REDIS_URI` and fails **only** at the licence check.

**Discoveries and Notable Information**
_(implementer fills in)_

---

### Phase 5 — A unit-test harness for `FinalizeBookingCommandHandler`

`test(api): add a FinalizeBookingCommandHandlerTests harness`

17 constructor dependencies and **no unit test that calls `Handle()`** — the two existing unit tests
cover `internal static` helpers only. Four Wave 2 issues cite this gap.

Copy `ReconcileOperationCommandHandlerTests.cs`: Moq fields, a `CreateHandler()` factory, shared
defaults in the constructor (`_uow.ExecuteInTransactionAsync` executing the work function inline,
`_adapter.ProviderName`, `_retryPolicy.NextEligibleAt` non-null).

**The trap, resolved in research and must be encoded in the harness:** `FinalizeOutcome` declares
`Finalized, HoldNoLongerValid, NotTracked`, so the **zero value is `Finalized`** — an unconfigured
`Mock<ICapacityReservation>` silently returns "proceed to Booked". Every capacity-sensitive test
must set it explicitly, and the harness should make the default explicit rather than implicit.

Cover the guard branches only (`:91` null/wrong-tenant → `Wait`; `:94` wrong-status → `Continue`;
`:196` Inv-7 dispatching/ambiguous → `Reconciling`). **Do not** attempt the TXN1/TXN2 branches —
those are Wave 2's, and writing assertions for them now would pin behaviour Wave 2 intends to change.

**This phase gates Wave 2's PRs L and M, not K** (K is the Reconcile handler, which already has a
test file). It may slip a wave without blocking Wave 2's start.

**Automated Verification**
- [ ] `bun nx test api` green, new tests included
- [ ] `bun nx lint api` green (new `.cs` file **UTF-8 with BOM**)

**Manual Verification**
- [ ] Each new test fails if its guard is removed (verify by temporarily inverting one condition).

**Discoveries and Notable Information**
_(implementer fills in)_

---

## Blast radius

Derived from research B1–B8.

**Intentionally changing:** `nx.json` (`dotnet` namedInput + consumer `dependsOn`),
`tools/scripts/export_openapi.mjs`, `CLAUDE.md`, 10 integration test classes,
`RetryAndFailureTests` scheduled-job helper, `ConsentEndpointTests:107`, new
`ScheduledJobTenantScope.cs` + parity test, `.github/workflows/ci-cd.yml` (new job only),
`docker-compose.yml` (+redis), `langgraph.production.json` (deleted) and its four references,
new `FinalizeBookingCommandHandlerTests.cs`.

**Must stay byte-for-byte:**
- **All of `apps/api/src/**`** — Wave 0 changes no production code.
- `OutboxRepository.ClaimBatchAsync`, `ScheduledJobRepository.ClaimDueAsync`.
- `OutboxPublisherTests` (pins cross-tenant claiming).
- The 5 already-migrated test classes.
- `apps/api/project.json` `test-integration` (uncached, no `dependsOn` — deliberate).
- `docs/deployment.md` beyond two path references.
- `nx.json` `python` and `typescript` input *sets* (only the hashing mechanism changes).
- The generated `v1.json` document contents.

**Type-generation consumers:** none change. `v1.json` must regenerate identically — a verification,
not a change.

## What we're NOT doing

- No production code changes in `apps/api/src`. No handler, repository, or DI change.
- **No migration.** Spec 12 owns DDL; nothing here needs one.
- Not fixing #75/#73/#96/#95 or any Wave 1+ issue.
- Not refactoring the three `TestConnStr` constants into env vars (26+ call sites; not Wave 0).
- Not making the CI integration job required (that is Wave 1's exit criterion).
- Not making langgraph actually start (that is #94 D1, procurement).
- Not un-gitignoring `libs/*/generated/` — the convention stays; the hash flows via `dependsOn`.
- Not adding `xunit.runner.json` / `MaxParallelThreads` to serialize collections — scoping the
  claim is the targeted fix; disabling parallelism would mask the class of bug and slow the suite.
- Not writing Wave 2 assertions in the Phase 5 harness.

## Cross-cutting requirements

- **Tenant scope (Inv-13):** the new `ScheduledJobTenantScope` must mirror production exactly plus
  the tenant predicate, and ship with a parity test. Production claim behaviour must not change.
- **Queue safety (Inv-5):** untouched — no consumer is modified.
- **Migrations:** none in this wave.
- **Generated types:** never hand-edited; Phase 1 changes only how they are *hashed*.
- **File encoding:** new `.cs` files UTF-8 **with BOM** or `bun nx lint api` fails.
- **One nx command at a time** (daemon lock). Assert zero `testhost` processes before trusting any
  test count. Tee to `.tmp/`, never pipe to `tail`.
- **Commit hygiene:** `SKIP=api-contract-generate` is safe on intermediate test-only commits with
  one regenerating commit at the end. `SKIP=check-migrations-immutable` is **not** safe and is not
  needed here.

## Package notes

**No new packages** in any lane. The redis compose service uses a stock image; no client library is
added (nothing in `apps/api` or `apps/langgraph` gains a redis dependency in this wave).

## Environment preconditions

- Docker Desktop must be running for Phases 2, 3, 4 (`docker compose up -d postgres`).
  Currently **down**; nothing on 5432 or 6379.
- Toolchain verified: bun 1.3.14, .NET SDK 10.0.302, node v24.14.1.
