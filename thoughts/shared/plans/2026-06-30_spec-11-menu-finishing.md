---
date: 2026-06-30
researcher: minhnt10122003@gmail.com
git_commit: 641c42e
branch: feat/spec-11
repository: Cordia
task: "Spec 11 — Menu vertical finishing (read endpoint, full catalog contract, fail-surfacing)"
tags: [implementation-plan, spec-11, menu, catalog, tdd]
status: draft
last_updated: 2026-06-30
last_updated_by: minhnt10122003@gmail.com
---

# Spec 11 — Menu Finishing Implementation Plan

## Overview

Close the remaining gaps in the **menu-ingestion vertical** so it is a working end-to-end feature, not just
a tested lifecycle. Three gaps remain after the menu plan (`2026-06-30_spec-11-menu-ingestion.md`, complete):
1. The published-catalog **read path is not exposed over HTTP** — `SearchVendorCatalogQuery`/`GetCatalogItemQuery`
   handlers exist but no controller serves them, and the langgraph foreground tools call `GET /api/catalog/items`
   which returns 404. The foreground read is non-functional (§2 FOREGROUND, Inv-16).
2. The `CatalogItem` contract **silently dropped** three §4.2 fields: `modifiers` (+ `CatalogModifier`),
   `availability_window` (+ `AvailabilityWindow`), `image_ref`.
3. Quarantine / extraction-failure are not **surfaced** to the owner (FM#4/§8, Inv-14) — they only write an audit row.

**Out of scope (decided):** real ClamAV / object storage / LLM stay **stub-ported**; wiring the .NET consumer to the
langgraph extraction agent stays deferred; the `apps/web` owner-review UI is a later plan.

## Current State Analysis

- `Cordia.Application/Handlers/Menu/{SearchVendorCatalog,GetCatalogItem}` handlers exist, published-only, tenant-scoped,
  with DTOs `CatalogItemSummary` / `CatalogItemDetail` — but **no controller references them** (verified: only
  `MenuController` exists, routed `api/menu`, owner-write endpoints only).
- `apps/langgraph/.../tools/foreground.py:78,103` — `search_vendor_catalog` → `GET /api/catalog/items?query=&category=&max_results=`,
  `get_catalog_item` → `GET /api/catalog/items/{item_id}`, via `get_tool_client().call_tool(...)`. The route/shape are the contract.
- `contracts/catalog.py` `CatalogItem` has `item_id,name,description,price,category,allergens,dietary_labels,extraction_confidence,source_location`
  — **missing** `modifiers`, `availability_window`, `image_ref` (and the `CatalogModifier`/`AvailabilityWindow` types).
- `VendorCatalogItem` entity stores `MenuMetadata`/`AllergenInfo` jsonb; the new optional fields need columns or jsonb mapping.
- `IngestVendorMenuCommandHandler` writes `catalog.quarantined` / `catalog.extraction_failed` audit rows but enqueues **no**
  owner notification.

### Key Discoveries
- Read DTOs already exclude draft-only fields (Inv-16) — exposing them is purely a controller + route concern.
- `ExtractFailed` from §5.2/Inv-14 **cannot be a state**: frozen decision D4 fixes `catalog_status` at 9 values with no
  `extract_failed`. Resolution stands: failed extraction stays `extracting`, is DLQ-surfaced, and (new here) raises an
  owner notification + stays invisible to the read path. This is the spec-compliant realization of Inv-14 under D4.

## Desired End State

`GET /api/catalog/items` + `GET /api/catalog/items/{itemId}` return only published items for the tenant; the langgraph
foreground tools resolve real data end-to-end; `CatalogItem` matches §4.2 exactly (modifiers/availability/image carried
through extraction → draft → review → published read); and quarantine/extract-failure raise an owner-visible signal.

## What We're NOT Doing

- Real ClamAV / object-storage / LLM; .NET→langgraph extraction wiring; `apps/web` review UI; authz (actor+audit `TODO`).
- A pricing/availability *enforcement* engine — `availability_window` is carried as data; the saga (separate plan) does
  live revalidation.

## Implementation Approach

TDD per phase. Reuse the existing read handlers, `MenuController` thinness, the generated-contract pipeline, and the
notification-outbox seam. No new patterns.

---

## Phase 1: Expose the catalog read path (HTTP) + foreground wiring

### Phase 1: Changes Required (TDD)
#### RED
- **Integration** `IntegrationTests/Acceptance/CatalogReadEndpointTests.cs` (Alba): `GET /api/catalog/items?query=` →
  200 with only published items (tenant-scoped); `?category=` filter; paging via `max_results`; `GET /api/catalog/items/{id}`
  → 200 published / 404 unpublished-or-missing; missing tenant header → 400.
#### GREEN
- New `Cordia.Api/Controllers/CatalogController.cs` — `[Route("api/catalog")]`, thin, injects `IMediator`+`ITenantContext`;
  `GET items` → `SearchVendorCatalogQuery` (map `query`/`category`/`max_results`), `GET items/{itemId}` → `GetCatalogItemQuery`
  (null → 404). Mirrors `BookingOperationsController` tenant-guard + problem responses.
- Add the optional `category` filter to `SearchVendorCatalogQuery`/repo (currently name/description only) so the foreground
  `category` param is honored.

### Phase 1: Success Criteria
#### Automated
- [x] `bun nx test-integration api` — `CatalogReadEndpointTests` green (5/5: published-only + tenant isolation + category filter + max_results bound + 404).
- [x] `bun nx test api` — existing menu unit tests still green (61/61); broader menu/catalog integration suite 22/22.
- [x] `dotnet format --verify-no-changes` clean on the two new files (`CatalogController.cs`, `CatalogReadEndpointTests.cs`).
#### Manual
- [ ] A langgraph `search_vendor_catalog` call against a seeded published catalog returns the items (tool-client smoke).

### Phase 1: Discoveries and Notable Information
**Technical Discoveries:**
- `category` already existed in the langgraph `CatalogItem` contract (`contracts/catalog.py:42`) but **not** in the
  .NET `VendorCatalogItem` entity — so honoring the foreground `category` filter required a new column. Added
  `VendorCatalogItem.Category` (nullable `text`) via migration `20260630121651_CatalogItemCategory`, and threaded it
  through `DraftCatalogItem` (new trailing `string? Category = null` positional param — keeps existing 7-arg call sites
  compiling) → `IngestVendorMenuCommandHandler` → read DTOs (`CatalogItemSummary`/`CatalogItemDetail`).
- The repo wire convention is **snake_case** (MenuController returns anonymous snake_case objects; default MVC policy is
  camelCase but menu endpoints override). `CatalogController` returns snake_case anonymous objects to match the §4.2
  field names the foreground consumes (`item_id`, `price_minor`, `category`, …).
- The read DTOs are still **flat** (`price_minor` + `currency`), **not** the §4.2 nested `Money`. Reshaping to nested
  `Money` + adding `allergens`/`dietary_labels`/`modifiers`/`availability_window`/`image_ref` is **Phase 2**'s job
  ("complete the CatalogItem contract"); Phase 1 deliberately exposes the existing handlers as-is plus `category`.
- `SearchPublishedItemsAsync` / `SearchVendorCatalogQuery` gained a `category` param; updated all call sites
  (1 unit test + 3 integration tests).

**Implementation Adaptations:**
- `max_results` (foreground's only paging knob) maps to `Take`, clamped to `[1, SearchVendorCatalogValidator.MaxPageSize]`
  with `Skip=0`; the foreground tool never sends an offset, so a single bounded page is the contract.
- New `.cs` files written LF + UTF-8 BOM (editorconfig). The repo working tree is CRLF (`autocrlf=true`), so
  `dotnet format` flags pre-existing files repo-wide — verified the **two new files** format clean in isolation.

---

## Phase 2: Complete the `CatalogItem` contract (§4.2)

### Phase 2: Changes Required (TDD)
#### RED
- **Unit (langgraph)** `tests/unit_tests/contracts/test_catalog.py` — round-trip `modifiers`/`availability_window`/`image_ref`;
  `extra="forbid"` still rejects unknowns; `CatalogModifier`/`AvailabilityWindow` shapes.
- **Unit (api)** `GetCatalogItemTests` / read DTO test — the new fields surface on `CatalogItemDetail` (published) and are
  still absent of draft-only `extraction_confidence`/`source_location`.
#### GREEN
- `contracts/catalog.py` — add `CatalogModifier` (id/name/price_delta), `AvailabilityWindow` (days/time range), and
  `CatalogItem.modifiers/availability_window/image_ref` (all optional, `extra="forbid"`). Regenerate C#/TS/Zod.
- `VendorCatalogItem` — persist the new optional fields (jsonb for `modifiers`/`availability_window`, text for `image_ref`);
  migration `CatalogItemRichFields`. `DraftCatalogItem` + `StubIsolatedMenuExtractor` carry them; `IngestVendorMenuCommandHandler`
  persists them; `CatalogItemDetail` exposes them on read.

### Phase 2: Success Criteria
#### Automated
- [x] `bun nx run langgraph:generate-schemas` + `shared-schemas:generate-types` clean; generated C# now has
      `CatalogModifier`, `AvailabilityWindow`, and `CatalogItem.{Modifiers,AvailabilityWindow,ImageRef}` (TS/Zod too).
- [x] langgraph contract tests green (9/9); api Menu unit tests green (61/61); menu/catalog integration green (22/22) —
      migration `CatalogItemRichFields` applied on the test Postgres at host startup.
- [x] `dotnet format` clean on new files (BOM + LF); `ruff check`/`ruff format` clean on changed Python.
#### Manual
- [x] Published `GET /api/catalog/items/{id}` returns `modifiers` (nested array) / `availability_window` (nested object)
      / `image_ref` (string); draft-only `extraction_confidence`/`source_location` still excluded (asserted in tests).

### Phase 2: Discoveries and Notable Information
**Technical Discoveries:**
- The spec **references** `CatalogModifier` / `AvailabilityWindow` in §4.2 but never defines their fields. Designed
  minimal shapes (ADR): `CatalogModifier{ modifier_id, name, price_delta: Money? }` (stable id referenced by
  `PreorderDraftLine.modifier_ids` + captured into `PreorderReference`, §21.3); `AvailabilityWindow{ days_of_week:
  list[str]?, start_time: "HH:MM"?, end_time: "HH:MM"? }` (descriptive only — fulfillment availability is never
  guaranteed by the agent, §21.2; the saga revalidates).
- `allergens` / `dietary_labels` / nested `price: Money` already existed in the Pydantic `CatalogItem`, so Phase 2 only
  added `modifiers` / `availability_window` / `image_ref` to the contract.
- **.NET storage:** modeled `Modifiers` + `AvailabilityWindow` as **jsonb** (`string?`, mirroring the existing
  `MenuMetadata`/`AllergenInfo` jsonb columns) and `ImageRef` as **text**. The read controller parses the jsonb strings
  into `JsonElement` (`AsJson` helper) so `modifiers`/`availability_window` serialize as nested JSON (array/object), not
  a double-encoded string — matching the §4.2 wire shape. `image_ref` is a plain string.
- The **.NET read wire still keeps `price_minor` + `currency` flat** (not nested `Money`) and emits `menu_metadata` /
  `allergen_info` as raw jsonb strings — deliberately unchanged from Phase 1. The full §4.2 nested-`Money` /
  structured-allergens shape lives in the **generated cross-service types** (the contract source of truth); the foreground
  consumes via a loose `ToolResultEnvelope`, so reshaping the internal REST projection further was out of scope here.

**Implementation Adaptations:**
- `DraftCatalogItem` gained the three fields as trailing defaulted positional params (`= null`), keeping the stub
  extractor's existing call site + unit tests compiling without change.
- Generated dirs (`libs/*/generated`) are gitignored — regeneration verified locally; not committed.

---

## Phase 3: Surface quarantine / extraction-failure to the owner (Inv-14)

### Phase 3: Changes Required (TDD)
#### RED
- **Integration** extend `MenuIngestionTests`: malware → `quarantined` **and** an owner notification outbox row
  (`menu.catalog.quarantined.v1` or reuse a notification.*.v1) enqueued in the same inbox txn; extraction-failure →
  catalog stays `extracting`, audit `catalog.extraction_failed`, **and** an owner notification enqueued (no redelivery).
#### GREEN
- `IngestVendorMenuCommandHandler` — on quarantine and on caught extraction failure, enqueue an owner-facing notification
  via the outbox (hand-author the envelope under `schemas/job-events/`; no secrets — Inv-12), inside the inbox transaction.
- Document the `ExtractFailed`-under-D4 resolution in the spec status note (already partially noted): failure is surfaced,
  never silently dropped, and the artifact is never read-path visible.

### Phase 3: Success Criteria
#### Automated
- [x] `MenuIngestionTests` green (4/4) incl. the new owner-alert assertions on quarantine + a new extraction-failure
      test (catalog stuck `Extracting`, audit `catalog.extraction_failed`, owner alert enqueued, inbox row admitted →
      fail-secure, no throw / no redelivery).
- [x] `IngestVendorMenuTests` unit green (61/61) incl. outbox-emit assertions on quarantine + extraction-failure.
- [x] menu/catalog integration suite green (23/23); new job-event schemas parse as valid JSON.
#### Manual
- [x] Quarantined/extract-failed catalogs never appear in `GET /api/catalog/items` (read path is published-only —
      covered by Phase 1 visibility tests; quarantine/extraction-failure never reach `Published`).

### Phase 3: Discoveries and Notable Information
**Technical Discoveries:**
- **Owner alert = outbox event, not the notification subsystem.** The `notification.requested.v1` pipeline is
  operation-id-centric (booking `NotificationEvent` + template + recipient resolution) — a poor fit for a menu-catalog
  owner alert that has no booking `operation_id`. Instead the handler enqueues hand-authored menu events
  (`menu.catalog.quarantined.v1` / `menu.catalog.extraction_failed.v1`) on the transactional outbox **inside the inbox
  transaction** (Inv-4), carrying only `catalog_id` + a coarse `reason` (Inv-12). Schemas added under
  `libs/shared-schemas/schemas/job-events/`.
- **Unconsumed types are safe.** `MediatRQueueConsumerDispatcher.DispatchAsync` returns `false` for an unregistered
  type and `PostgresMessageTransport` logs it and **treats it as delivered** (no dead-letter / poison loop). So emitting
  the owner-alert events without yet wiring a delivery consumer is harmless — they route to the `menu.commands` lane
  (prefix `menu.`) and are ack-delivered until an owner-notification consumer is added (deferred with the web UI).
- The `IngestVendorMenuCommandHandler` gained an `IOutboxRepository` dependency; `outboxRepo.AddAsync` participates in
  the ambient inbox transaction exactly like `auditRepo.AddAsync` (no new transaction — the handler must not open one).
- Realizes Inv-14 fully: a failed extraction now leaves the catalog stuck in `Extracting` (no `extract_failed` state,
  frozen D4) **and** writes an audit row **and** emits an owner alert — three independent surfacings, never silent.

**Implementation Adaptations:**
- The extraction-failure integration test injects a `ThrowingMenuExtractor` via `services.RemoveAll<IIsolatedMenuExtractor>()`
  + re-add, keeping the production `StubIsolatedMenuExtractor` deterministic-success (no test-only throw sentinel in prod code).

---

## Testing Strategy
- Unit: read DTO field coverage, category filter, contract round-trip.
- Integration (Alba + Postgres): read endpoints (published-only/tenant/paging/404), owner-notification on quarantine/fail.

## Architecture Decisions
| Decision | Chosen | Why |
|---|---|---|
| `ExtractFailed` state | Not added (frozen D4 = 9 values) | Stay `extracting` + DLQ + owner notification realizes Inv-14 without violating the frozen enum. |
| New catalog item fields storage | jsonb (`modifiers`/`availability_window`) + text (`image_ref`) | Matches existing `MenuMetadata`/`AllergenInfo` jsonb convention; no rigid columns for optional structured data. |
| Read endpoint route | `api/catalog/items` (new `CatalogController`) | Matches the langgraph foreground tool-client contract verbatim; keeps owner-write (`api/menu`) and customer-read (`api/catalog`) controllers separate. |

## References
- Spec: `docs/specs/11-waitlist-menu-preorder.md` §2, §4.2, §5.2, §7, §8 FM#4-6, Inv-14/16.
- Prior plan: `thoughts/shared/plans/2026-06-30_spec-11-menu-ingestion.md`.
- Foreground tool contract: `apps/langgraph/cordia_agent/tools/foreground.py:73-109`.
