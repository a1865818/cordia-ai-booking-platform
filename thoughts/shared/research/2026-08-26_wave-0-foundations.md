---
date: 2026-08-26
researcher: Claude (spec-loop Stage 1 — six parallel agents)
git_commit: 30f46fd
branch: chore/wave-0-foundations
repository: Cordia
topic: "Wave 0 foundations — exporter key, nx cache inputs, tenant-scoped test claims, CI integration gate, langgraph config collapse, Finalize unit-test harness"
tags: [research, baseline-audit, tooling, nx, ci, apps/api, apps/langgraph, integration-tests, tenant-scope, inv-13, issue-91, issue-94]
status: complete
issues: [91, 94]
roadmap: https://claude.ai/code/artifact/e67f1cc3-4539-43bd-b844-553b6d0267e1
---

# Research — Wave 0 Foundations

Baseline for the first wave of the Cordia Remediation Roadmap. Wave 0 exists for one reason:
**nothing downstream is falsifiable without it.** Nine of the open issues name an integration
test as their primary regression guard, and the integration suite has never run in CI.

This document is the regression baseline Stage 6 diffs against.

## Scope

| Lane | Touched | Contract change? |
|---|---|---|
| tooling (`tools/scripts/`, `nx.json`) | yes | no — changes the *machinery* that generates the api-contract, not the contract |
| api (`apps/api/test/**`) | yes | no — test-only, plus one new test-side DI wrapper |
| ci (`.github/workflows/`) | yes | n/a |
| langgraph (config + compose) | yes | no |
| web, shared-schemas, api-contract | **no** | — |

**Spec registry position:** Wave 0 owns no shared contract. It is a *referencer* throughout.
The one invariant in play is **Inv-13 (tenant scope established before data access)**, owned by
spec 15 — and Wave 0 does not change production tenant scoping, it closes a *test-harness* hole
where cross-tenant production behaviour leaks between concurrently-running test collections.

**Frozen decisions:** none are touched. D11 (`scheduled_jobs` uses `run_after`) is *relied on*
by the scheduled-job claim analysis below but not modified.

---

## Existing Feature Behavior (Baseline Audit)

### B1 — OpenAPI export → api-contract → CI drift gate

**How it behaves today.**
`libs/api-contract/project.json` chains `generate-contract → generate-types → export-openapi → api:build`.
`export_openapi.mjs:243-254` spawns the built API DLL with five environment variables:

| Line | Variable | Status |
|---|---|---|
| 247 | `ASPNETCORE_ENVIRONMENT=Development` | live |
| 248 | `ASPNETCORE_URLS` | live |
| 249 | `ConnectionStrings__MAgeDBConnection` | **dead — nothing reads this key** |
| 250 | `DatabaseBootstrap__MigrateOnStartup=false` | live (`Program.cs:176`) |
| 251 | `OpenApiExport__Enabled=true` | **dead — zero readers in `apps/api`** |

The only connection-string key the API reads is `CordiaDBConnection`
(`InfrastructureServiceCollectionExtensions.cs:50`; also `DesignTimeDbContextFactory.cs:20`).
`MAgeDBConnection` occurs exactly once in the entire repo — at `export_openapi.mjs:249`.
`OpenApiExport` likewise occurs exactly once, at `export_openapi.mjs:251`.

Consequence: the spawned API ignores the throwaway `openapi_export` database the script author
intended and silently binds `appsettings.json:7`'s committed loopback value
(`Host=localhost;...;Username=cordia;Password=localdev`). `appsettings.Development.json` and
`appsettings.Test.json` have **no `ConnectionStrings` section at all**, so there is no override layer.

The connection string is passed straight into `UseNpgsql(connectionString, …)`
(`InfrastructureServiceCollectionExtensions.cs:60`), which defers connection until first use.

**Who consumes this:** the pre-commit hook `api-contract-generate`
(`.pre-commit-config.yaml:157-165`, `files: ^apps/api/.*\.(cs|csproj|sln|props|targets)$`,
`pass_filenames: false`) which stages `libs/api-contract/openapi/v1.json`; and the CI drift gate
(`ci-cd.yml:164-174`) which fails the PR when `git status --porcelain libs/shared-schemas/ libs/api-contract/`
is non-empty.

**Invariant upheld:** none directly. This is build machinery.

**Must stay the same:** the exporter must keep producing a byte-identical `v1.json` for an
unchanged API surface, or every open branch conflicts on a generated file.

### B2 — nx cache inputs for the .NET lane

`nx.json:12-33` defines the `dotnet` namedInput: `src/**/*.cs`, `*.csproj`, `*.props`, `*.targets`,
`test/**/*.cs`, `test/**/Inputs/**/*`, `Cordia.sln`, `global.json`, `.editorconfig`, the generated
`SharedTypes.cs`, and `{workspaceRoot}/tools/config/*.json`.

**There is no `appsettings*.json` glob.** `apps/api/project.json` `build` (`:28-47`),
`test` (`:62-79`) and `lint` (`:140-153`) all declare `inputs: ["dotnet"]` with `cache: true`.
So a commit that changes only `appsettings.Development.json` or `appsettings.Test.json` gets a
**replayed cache hit** on `bun nx test api`.

**Correction to the roadmap text:** this does *not* bite through pre-commit. All four
apps/api-triggered hooks (`dotnet-format`, `dotnet-test`, `dotnet-build`, `api-contract-generate`)
are scoped to `\.(cs|csproj|sln|props|targets)$`, so an appsettings-only commit does not trigger
them at all. The hole bites on a **manual** `bun nx test api` after a config-only edit — which is
exactly what Wave 3's PR I (#75, six new config flags) will be doing repeatedly.

**Already closed, do not re-do:** `apps/web/project.json` `test.inputs` already declares
`bunfig.toml` and `.env.test` (closed by PR #98/#99). `tools/config/*.json` is already in the
`dotnet`, `python` and `typescript` named inputs (closed during the ui-wire pass).

`test-integration` (`apps/api/project.json:80-87`) declares **no `inputs`, no `cache`, no
`dependsOn`, and no `--no-restore`**. Uncached is correct for a target that talks to a shared
mutable database — this must stay that way.

### B3 — Cross-tenant claim contamination in the integration suite

**Production behaviour (unchanged by this wave).** Two repositories claim work cross-tenant *by design*:

- `OutboxRepository.ClaimBatchAsync` (`OutboxRepository.cs:55-98`) —
  `WHERE published_at IS NULL AND (lease_owner IS NULL OR lease_expires_at < {0}) ORDER BY occurred_at LIMIT {1} FOR UPDATE SKIP LOCKED`,
  with `.IgnoreQueryFilters()`. No tenant predicate. Comment at `:66` marks it deliberate (§5.1):
  one worker drains every tenant.
- `ScheduledJobRepository.ClaimDueAsync` (`ScheduledJobRepository.cs:47-97`) — the same shape:
  `WHERE status::text <> 'done' AND status::text <> 'dead' AND run_after <= {0} AND (lease_owner IS NULL OR lease_expires_at < {0}) ORDER BY run_after LIMIT {1} FOR UPDATE SKIP LOCKED`,
  `.IgnoreQueryFilters()`, comment at `:54` marking it the §5.1 mirror.

Both are correct in production. Both are hazardous in a test suite that shares one database.

**Test-harness behaviour.** The whole integration suite runs against a single database
(`Cordia_API_Test`) and xUnit runs the three collections in parallel:

| Collection | Fixture | Classes |
|---|---|---|
| `AcceptanceApi` | `Acceptance/AcceptanceApiFixture.cs:128` | ~46 |
| `TenantResolutionApi` | `TenantResolution/TenantResolutionApiFixture.cs:118` | 1 |
| `Postgres` | `Persistence/PostgresFixture.cs:101` | ~70 |

There is **no** `xunit.runner.json`, **no** `AssemblyInfo.cs`, **no** `[assembly: CollectionBehavior]`,
and **no** `MaxParallelThreads` anywhere under `apps/api/test`. Parallelism is xUnit v3 default.

So while the `Postgres` collection runs, the `AcceptanceApi` collection is a live producer of
unpublished `booking.command.requested.v1` rows. A publisher tick driven through a container
holding a *fake* `IBookingProviderAdapter` claims those foreign rows, inflates that test's
`CreateCallCount`/`CancelCallCount`, and writes the fake's provider reference onto another
tenant's operation. That is issue #90/#91.

**Two mitigations exist, and they are not equivalent:**

1. `NeutralizeExistingUnpublishedAsync` — a per-class helper that marks the *pre-existing* backlog
   published. It cannot close the race: it only covers rows existing at that instant, never rows a
   concurrent writer inserts before the tick. It also **stamps other tests' live rows as published**,
   which is itself contamination.
2. `ScopeOutboxClaimToTenant` (`IntegrationTests/Messaging/OutboxTenantScope.cs:43-53`) — a
   post-`AddInfrastructure` DI override re-registering `IOutboxRepository` as
   `TenantScopedOutboxRepository`, whose `ClaimBatchAsync` is the production SQL plus
   `tenant_id = {0}`. Every other member passes through to the real repository. This *does* close it.

**Exact current state — 17 files reference `NeutralizeExistingUnpublishedAsync`:**

| Group | Count | Files |
|---|---|---|
| **Already migrated** — helper deleted, "there used to be…" comment remains, calls `ScopeOutboxClaimToTenant` | 5 | `CancelFulfilmentTests` (:93), `CompensationAndCancellationTests` (:127), `ManualRecoveryTests` (:133), `ReconciliationTests` (:132), `RetryAndFailureTests` (:174) |
| **Not migrated** — still define + call the helper | 11 | `EndToEndSubstrateTests`, `DepositAndUserActionTests`, `FinalizeHappyPathTests`, `MenuIngestionTests`, `MenuLifecycleEndToEndTests`, `OutboxPublisherTests`, `PoisonDeadLetterTests`, `StateMachineCapstoneTests`, `RejectionTests`, `NotificationSenderTests`, `FinalizeAgainstHoldTests` |
| **Comment-only, unrelated** | 1 | `NotificationReconcileTests:166` — name-drops `RejectionTests`' helper as an analogy for its own *notification-table* cleanup |

**Correction to the roadmap:** the 5 already-migrated files need **no work at all** — the roadmap's
"G1 = deletion-only for 5 files" was wrong; that deletion already happened. Real scope is
**10 conversions** (11 not-migrated minus `OutboxPublisherTests`).

**`OutboxPublisherTests` must be excluded.** Its Test 1
(`ClaimBatchAsync_SetsLease_AndIsCrossTenant`, `:87-129`) constructs a raw unwrapped
`new OutboxRepository(workerCtx)` and asserts `Assert.Contains(tenantA, …)` **and**
`Assert.Contains(tenantB, …)`. Scoping it would make the test fail by construction and destroy the
only direct pin on production's cross-tenant claim — which `OutboxTenantScope.cs:34` explicitly
names as the remaining guard.

**`NeutralizeExistingScheduledJobsAsync` exists in exactly one file:** `RetryAndFailureTests.cs`
(defines `:208`, calls `:359`, `:580`). No `ScheduledJobTenantScope` equivalent exists anywhere.
Every other class that drives the poller has *no* mitigation at all.

**Drift guard already in place:** `OutboxTenantScopeParityTests` runs both implementations over the
same rows in real Postgres and fails unless they select the same `message_id`s and leave every
column identical. `OutboxRepository.cs:62-64` carries the reciprocal "keep in sync" comment.
**Any new `ScheduledJobTenantScope` must ship with a sibling parity test or it will drift the same way.**

**Invariant upheld today:** Inv-13 in *production* (EF query filters + `TenantContext`). The test
harness does not uphold it, which is the gap.

### B4 — CI coverage

Four workflows: `ci-cd.yml`, `app-deploy.yml`, `claude.yml`, `claude-code-review.yml`.

The **only** .NET test invocation anywhere is `ci-cd.yml:302-304`:
```yaml
- name: Test
  run: bun nx run api:test
```
which resolves to `dotnet test … --filter "Category=Unit"`.

Verified absent across all of `.github/`: any `test-integration` run, any `services:` block, any
postgres, any `dotnet ef` or migration command. So **39 migrations have never been applied by CI**,
and the integration suite has never run there.

**Fixture facts that constrain a CI job:**
- `PostgresFixture.TestConnStr` (`Persistence/PostgresFixture.cs:20-21`) is a **`const`**:
  `Host=localhost;Port=5432;Database=Cordia_API_Test;Username=cordia;Password=localdev`.
  26 occurrences across 24 files. `AcceptanceApiFixture.TestConnStr` (`:28-31`) and
  `TenantResolutionApiFixture.TestConnStr` (`:27-29`) are byte-identical `const`s of the same value.
  A service container must match those credentials exactly, or all three constants need an
  env-var refactor rippling through every call site.
- **Three concurrent first-run migrators**, not two: `PostgresFixture:29` calls `MigrateAsync()`
  directly; `AcceptanceApiFixture:48` and `TenantResolutionApiFixture:42` both set
  `DatabaseBootstrap:MigrateOnStartup=true`.
- `docker-compose.yml` provisions `POSTGRES_DB: Cordia_Agent` on `pgvector/pgvector:pg16` with
  user `cordia` / `localdev`. `Cordia_API_Test` is created at fixture-init by
  `EnsureTestDatabaseExistsAsync()`, not by compose.
- `test-integration` has no `dependsOn: build` and no `--no-restore`, so a CI job pays a full
  restore + build on top of the suite runtime. No timing is recorded anywhere in the repo.

**Open decision #91 explicitly asked for and this plan must record:** either add a Postgres service
and run the suite in CI, or document that integration is a local-only gate. **This plan chooses CI.**

### B5 — LangGraph runtime configuration

`apps/langgraph/langgraph.json` and `apps/langgraph/langgraph.production.json` are **byte-identical**
(12 lines each, verified by diff). Nothing distinguishes "production" except which path is passed
on the command line.

Four live references to the production file:
- `nx.json:42` (`python` namedInput)
- `apps/langgraph/project.json:86` (`serve-docker` target)
- `apps/langgraph/scripts/build_image.sh:13` (`CONFIG=`)
- `docs/deployment.md:68,121`

There is **no Dockerfile for langgraph** — the image is produced by the LangGraph CLI
(`langgraph build … -c langgraph.production.json`) from `image_distro: "wolfi"`. Nothing in this
repo describes what that image contains.

`docker-compose.yml` declares exactly **one service, `postgres`**. No redis. `REDIS_URI` appears
in exactly one place repo-wide — `docs/deployment.md:68`, as prose documenting the crash.

`docs/deployment.md` is current and honest: it states up front that no infrastructure exists, that
the langgraph container cannot start at all, and it records the two sequential hard failures
(`KeyError: REDIS_URI`, then `ValueError: License verification failed` in
`langgraph_runtime_postgres/lifespan.py:75`). **RC5's "stale docs" finding does not apply to this
file** — it is the best-maintained doc in the repo and needs no correction.

**Confirms the roadmap's crash-loop warning, with the precise mechanism:**
`InfrastructureServiceCollectionExtensions.cs:226-281` binds `LangGraphOptions` with six
`.Validate(…)` gates and `.ValidateOnStart()` at `:281`. One of them is
`o.UseStubExtractor || o.HasUsableHost`. `appsettings.json:21-25` deliberately omits `Host`;
`deploy-api` (`ci-cd.yml:578-635`) sets only `Version`, `WEBSITES_PORT`,
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`, `Cordia__EnvironmentCode` — never `LangGraph__Host` or
`LangGraph__UseStubExtractor`. So a deploy today **throws at startup before the host listens**.
`app-deploy.yml:186-202` has the same never-populated `TODO(cordia)` placeholder.

### B6 — `FinalizeBookingCommandHandler` test coverage

**17 constructor dependencies** (`FinalizeBookingCommandHandler.cs:64-83`, C# primary constructor):
`IBookingOperationRepository`, `IWorkflowStepRepository`, `IWorkflowAttemptRepository`,
`IBookingIntentRepository`, `IOutboxRepository`, `IAuditEventRepository`, `IBookingProviderAdapter`,
`IBookingRepository`, `IProviderCircuitBreaker`, `RetryClassifier`, `IRetryPolicy`, `RetryScheduler`,
`IUnitOfWork`, `ICapacityReservation`, `IGuestContactRepository`, `ILogger<…>`, `Metrics`.
(`RetryClassifier`, `RetryScheduler`, `Metrics` are concrete classes.)

**No unit test calls `Handle()`.** Six test files mention the handler; only two are unit tests, and
both exercise `internal static` helpers only:
- `FinalizeBookingProvenanceTests` — `BuildBookingAggregate` (2 facts, attributed-source copy-through).
- `HardeningRegressionTests` — `ReadFirstString` (canonical-payload key precedence).

`FinalizeAgainstHoldTests` (Integration) is the only test that actually drives the handler, via
`IMediator` at `:402`.

**Model to copy:** `ReconcileOperationCommandHandlerTests.cs`. Its `CreateHandler()` factory
(`:36-39`) builds the handler from eight `Mock<T>` fields (`:22-29`) plus a real `new Metrics()`
plus a `CreateEscalator()` helper (`:32-34`). Shared defaults are set in the constructor (`:81-99`):
`_adapter.ProviderName`, `_uow.ExecuteInTransactionAsync` executing the work function immediately,
`_retryPolicy.NextEligibleAt` returning non-null. **11 source call sites** across 12 test methods.

**Resolved trap:** `FinalizeOutcome` (`Cordia.Domain/Capacity/FinalizeOutcome.cs:6-27`) declares
`Finalized, HoldNoLongerValid, NotTracked` in that order — so the **zero value is `Finalized`**.
An unconfigured `Mock<ICapacityReservation>` therefore returns "hold finalized, proceed to Booked".
That is benign but *silent*: a harness that forgets to configure it tests the happy path while
appearing to test the branch. Every capacity-sensitive test must set it explicitly.

**Dead decision table confirmed:** `RetryClassifier.Classify` (`RetryClassifier.cs:22-44`) returns a
`RetryDecision(Classification, Action)` and maps `NotFound → RetryAction.ManualReview` with the
comment *"a NotFound cannot prove the create did not take effect."* All four call sites
(`FinalizeBookingCommandHandler.cs:610, 648, 738, 774`) read only `.Classification`.
**`RetryDecision.Action` has zero readers in `apps/api/src`.** This is Wave 2's raw material,
recorded here so Wave 0's harness is built with it in mind.

### B7 — Known warning, unowned

`IntegrationTests/Security/ConsentEndpointTests.cs:107` emits **CS8631** on every build:
`Assert.Equal(["allergy_notes", "loyalty_number"], fields)` where `fields` is `string?[]` because
`JsonElement.GetString()` returns `string?`. `Cordia.Tests.csproj:5` has `<Nullable>enable</Nullable>`,
no `NoWarn`, no pragma. Live and unsuppressed. Belongs to no PR in the roadmap — folded into
Wave 0 here.

---

## Blast radius summary

| Item | Intentionally changing | Must stay byte-for-byte |
|---|---|---|
| `tools/scripts/export_openapi.mjs` | yes (2 dead env keys) | the generated `v1.json` output |
| `nx.json` `dotnet` namedInput | yes (add appsettings glob) | `python`, `typescript` inputs |
| `apps/api/project.json` `test-integration` | **no** — stays uncached, no `dependsOn` | all of it |
| `OutboxRepository.ClaimBatchAsync` | **no** | all of it — parity test pins it |
| `ScheduledJobRepository.ClaimDueAsync` | **no** | all of it |
| 10 integration test classes | yes (helper → `ScopeOutboxClaimToTenant`) | their assertions |
| `OutboxPublisherTests` | **no** | all of it — pins cross-tenant claim |
| 5 already-migrated classes | **no** | already done |
| `.github/workflows/ci-cd.yml` | yes (new job) | existing jobs |
| `langgraph.production.json` | yes (delete) | `langgraph.json` |
| `docker-compose.yml` | yes (add redis) | the postgres service |
| `docs/deployment.md` | **no** | it is current and correct |
| Any `apps/api/src/**` production code | **no** | Wave 0 is test + tooling only |

**Type-generation blast radius:** none. No Pydantic contract, no controller signature, no DTO
changes. `v1.json` must regenerate identically — that is a verification step, not a change.

## Existing automated coverage that must stay green

- `bun nx test api` — `Category=Unit`, the only suite CI runs today.
- `bun nx test-integration api` — `Category=Integration&Category!=Manual`, local-only today.
  Notably includes `OutboxTenantScopeParityTests` (pins the claim mirror) and
  `OutboxPublisherTests` (pins cross-tenant claiming).
- `bun nx lint api` — `dotnet format --verify-no-changes`.
- `bun nx test langgraph`, `bun nx lint langgraph`.
- `bun nx test web` — bun test, unaffected.

## Environment preconditions discovered

- Docker Desktop is **not running**; nothing listens on 5432 or 6379. The integration suite and any
  compose work need `docker compose up -d postgres` first.
- Toolchain verified: bun 1.3.14, .NET SDK 10.0.302, node v24.14.1. Zero stray `testhost` processes.

---

## B8 — MEASURED: the nx cache hole is in the generated types, not the appsettings

Two experiments were run on this branch at `30f46fd`. Both changed the plan.

### Experiment 1 — does the exporter need a database?

Ran `bun nx run api-contract:generate-types` with Docker down and nothing on 5432.

**Result: it succeeds.** The API starts, all ten hosted services start and log their intervals,
`GET /swagger/v1/swagger.json` returns **200 in 453 ms**, and the document is written. The database
is never dialed during a Swagger scrape.

**This falsifies the roadmap's "only true cycle" claim.** Deleting `appsettings.json:7`'s connection
string would *not* break the exporter or the CI drift gate — `UseNpgsql(null)` defers, and nothing
in the export path connects. The exporter fix is therefore **hygiene, not a blocker for #73**:
worth doing (two dead env keys, and the script silently depends on a committed value it did not
intend to use), but it does not gate Wave 3. Wave 0 keeps it because it is 30 minutes; the
dependency arrow in the roadmap should be deleted.

### Experiment 2 — why did `v1.json` drift?

Regenerating produced a 4-line diff: `resource_id` removed from the `BookingRequest` schema.
Chased to ground:

1. Local `libs/shared-schemas/generated/csharp/SharedTypes.cs` was dated **Aug 10**; `main` is Aug 22.
   Generated dirs are gitignored, so a stale checkout carries stale types silently.
2. Ran the full correct-order chain — `langgraph:generate-schemas` → `shared-schemas:generate-types`
   → `api-contract:generate-types`. `SharedTypes.cs` regenerated and **did** gain
   `[JsonPropertyName("resource_id")] public string ResourceId` at `:1618-1621`.
3. **`v1.json` still dropped `resource_id`.** The log shows why:
   `> nx run api:build [local cache]` — nx replayed a cached build even though `SharedTypes.cs`
   had just changed, so `export_openapi.mjs` scraped a **stale DLL**.
4. Re-ran with `--skip-nx-cache`. Drift check came back **empty**.

**Root cause.** `nx.json`'s `dotnet` namedInput lists
`{workspaceRoot}/libs/shared-schemas/generated/csharp/**/*.cs`, and `typescript` lists both
`libs/shared-schemas/generated/typescript/**/*` and `libs/api-contract/generated/typescript/**/*`.
All three paths are **gitignored** (`.gitignore:17-18`, proved with `git check-ignore -v`).
Nx builds its file map from git and skips ignored files, so **those three namedInput entries
contribute nothing to any hash.** They look like coverage and are inert.

**Consequences, all live today:**

| Effect | Impact |
|---|---|
| `api:build` / `api:test` / `api:lint` do not invalidate when `SharedTypes.cs` changes | a contract change can report a **replayed green** across the whole .NET lane |
| `web:test` / `web:lint` / `web:build` do not invalidate when either generated TS package changes | same, for web |
| `export-openapi` can run against a stale DLL | the tracked `v1.json` can silently disagree with the code, in **either** direction |
| the CI drift gate can false-pass or false-fail depending on cache state | the gate meant to catch this is itself downstream of it |

This is the same *family* as the missing `appsettings*.json` glob but strictly more serious: it sits
on the repo's central cross-app coupling, and it is the mechanism behind "the test suites were
reporting stale passes" recorded in `thoughts/shared/reviews/2026-08-04_frontend-api-wiring.md`.
That review fixed one instance (`tools/config/*.json`, which is **not** gitignored, so its entry
does work); the generated-types instance was never found.

**`v1.json` on `main` is correct.** There is no committed drift. The working tree is clean.

## Open questions carried into Stage 2

1. ~~Does removing the connection string break the exporter?~~ **Answered: no.** See B8 Experiment 1.
2. What is the integration suite's wall-clock runtime? Unrecorded anywhere; needed to size the CI job.
   Must be measured in Phase 3 before the CI job is written.
3. **New, needs a decision:** does Wave 0 absorb the generated-types cache hole (B8)? It is not in
   the roadmap. It is small to fix but changes what "green" means for every subsequent wave.
