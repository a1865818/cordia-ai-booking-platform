# Cordia UI/UX Blueprint — Pages, Components & Design System

> Generated 2026-07-10 (`feature/spec-14`, commit `7c92e23`) from a full audit of the
> implemented backend (`apps/api`, `apps/langgraph`), all 15 spec docs (`docs/specs/`),
> and the existing frontend (`apps/web/src`). Companion to the audit report at
> `thoughts/shared/research/2026-07-10_00-20-27_backend-ui-audit.md`.
>
> **Rev 2 (2026-07-10):** re-audited with the backend nearly fully landed. The
> `Handlers/BookingFulfillment/` workflow engine (payments, SLA, escalation, step-level
> recovery, retry scheduling), `Security/SensitiveDataMask`, and the spec-15 endpoint
> registry contract added the components in §4.2/§4.3 marked **(rev 2)**; the deposits
> and tenant-registry surfaces moved out of the spec-only fence.
>
> **Rev 3 (2026-07-10):** gap review. Added the missing **auth/login + role surface**
> (spec 14 authorization spine), **queue & job health** (spec 05/13), **capacity &
> holds** visibility (spec 07), mapped the previously unlisted Analytics/Campaigns/More
> screens, and the **(rev 3)** primitives/patterns below.

This document is the canonical inventory of the **pages** and **components** Cordia's
web app needs, how they **relate and communicate**, and the **UI/UX principles** each
one enforces — so the frontend grows as a coherent, reusable design system rather than
one-off screens.

---

## 1. Where we are today

| Layer | Exists in `apps/web/src` | Verdict |
|---|---|---|
| Primitives (`components/ui/`) | Avatar, Button, Chip, CommandPalette, Drawer, Empty, Icon, KpiCard, Logo, Panel, Pills, SectionLabel, Segmented, Skeleton, Spinner, StatusTags, Toast, Toggle | Good base; gaps listed in §4.2 |
| Console shell (`platform/console/`) | Header, Sidebar, BottomNav (mobile), StatusBar, nav config | Exists |
| Screens (`platform/screens/`) | Today, Queue, Waitlist, Conversations, Customers, Deposits, Campaigns, Analytics, Settings, More | **Mock-backed** — all data from `data/console.ts` |
| Drawers (`platform/drawers/`) | BookingDetail, CreateBooking, Reschedule | Mock-backed |
| Data layer | `api/client.ts` (typed fetch, ApiError), `hooks/useToast.ts` | No tenant header, no idempotency key, no 409 handling, no query cache |
| Routes (`router.tsx`) | `/` (landing), `/dashboard` (console), `*` | Console is one route; screens are internal tabs |

**The core job is not building screens from scratch — it is (a) filling primitive gaps,
(b) adding a real data/communication layer, and (c) rewiring the mock screens to the
live API while adding the missing feature surfaces.**

---

## 2. UI/UX principles → concrete rules for Cordia

Every component/page in this blueprint is tagged with the principles it enforces.
These are the industry principles adopted, each translated into a Cordia-specific rule:

### 2.1 Nielsen's 10 usability heuristics (applied)

| # | Heuristic | Cordia rule |
|---|---|---|
| N1 | Visibility of system status | Every async action shows Skeleton → content or Spinner → Toast. Long-running booking operations render the `OperationStatus` state machine as a visible `StatusTimeline`, never a blank wait. Streaming conversations show partial transcript immediately. |
| N2 | Match system ↔ real world | Domain enums are never shown raw: `CompensationRequired` → "Needs attention — refund pending". A single `statusCopy.ts` map owns human labels for all enums. |
| N3 | User control & freedom | Destructive/operator actions are two-step (`ConfirmDialog`), cancellable while pending, and barge-in is honoured in voice UI (`BargeInFrame`). Drawers close with Esc and never trap focus. |
| N4 | Consistency & standards | One `StatusBadge`, one `DataTable`, one `EntityDrawer` everywhere. Design tokens (§4.1) forbid ad-hoc colors/spacing. Same filter bar pattern on every list. |
| N5 | Error prevention | Booking changes call `POST changes:validate` (`BookingsController.cs:70`) **before** enabling submit. Forms use Zod schemas from `libs/shared-schemas/generated/typescript/zod.ts` so client validation always matches the backend. Reason codes are required *in the dialog*, not after failure. |
| N6 | Recognition over recall | Filters are visible chips, not hidden menus; active filters live in the URL. `CommandPalette` (exists) surfaces every action by name. Status colors are consistent app-wide. |
| N7 | Flexibility & efficiency | Command palette + keyboard shortcuts for power users; bulk-select on tables (e.g. DLQ replay); beginners get the guided default path (§6). |
| N8 | Aesthetic & minimalist design | Progressive disclosure: list rows show 4–6 fields; everything else lives in the `EntityDrawer`. Advanced/ops surfaces are separate routes, not mixed into daily screens. |
| N9 | Help users recognize, diagnose, recover from errors | `ProblemDetails`-aware `ErrorState` renders the API's `title/detail`, plus a recovery action: 409 → "Reload & retry", 403 RequiresVerification → mounts `VerificationGate`, network → retry button. |
| N10 | Help & documentation | Contextual `HelpHint` on every non-obvious field; empty states teach (§6.2); first-run checklist. |

### 2.2 Other adopted standards

- **Atomic design** (Frost): tokens → primitives → patterns → feature modules → pages
  (§4). A component may only import from layers **below** it — this is the reusability
  guarantee.
- **WCAG 2.2 AA**: 4.5:1 contrast, full keyboard operability (drawers/dialogs focus-trap
  + restore), `aria-live="polite"` for toasts and streaming transcript, status conveyed
  by icon+text not color alone, 44px touch targets (BottomNav is mobile).
- **Progressive disclosure**: default view = today's work; complexity opt-in.
- **Optimistic UI with truth reconciliation**: optimistic updates only for reversible
  actions; state-machine transitions (booking ops) always render server truth.
- **URL as state**: filters, selected entity, open drawer → search params. Every screen
  state is deep-linkable and shareable (also aids support).
- **Jakob's law / familiarity**: console follows the standard SaaS pattern (left nav,
  top header with tenant switcher + search, content, right drawer) that operators
  already know from other tools.
- **Hick's law**: state-aware `ActionBar` shows only the actions *legal in the current
  state* (derived from the `OperationStatus` transition table), never all 6 buttons.
- **Doherty threshold (<400 ms perceived)**: cached list data via query cache,
  skeletons render instantly, streaming partials render as they arrive.

---

## 3. Personas & routes

Three surfaces, one design system:

```
apps/web
├─ /                     Landing (exists)
├─ /console/*            Venue operator/staff  (evolves from /dashboard)
├─ /admin/*              Platform admin / ops
└─ /manage/*, /pay/*,    End customer (public, link-driven, mobile-first)
   /waitlist/offers/*
```

### 3.1 Route map

| Route | Persona | Screen | Status | Backend evidence |
|---|---|---|---|---|
| `/console` (Today) | Operator | TodayScreen | partial (mock) | bookings + operations queries |
| `/console/bookings` + `?booking=:id` drawer | Operator | Bookings list/detail | partial (mock drawers exist) | `BookingsController.cs:24,44` |
| `/console/operations` + `?op=:id` | Operator | Operations board | missing (QueueScreen is closest) | `BookingOperationsController.cs:30–200`, `OperationStatus` (16 states) |
| `/console/waitlist` | Operator | Waitlist board | partial (mock) | `WaitlistController.cs:30–96` |
| `/console/conversations` + `?session=:id` | Operator | Live conversations | partial (mock) | `streaming_models.py`, `ConversationSessionsController.cs:19` |
| `/console/menu` + `/console/menu/:catalogId` | Operator | Menu publishing wizard | missing | `MenuController.cs:32–130`, `CatalogStatus` |
| `/console/preorders` | Operator | Preorder sagas | missing | `PreorderController.cs:29–51`, `PreorderSagaState` |
| `/console/deposits` | Operator | Deposits board + settings toggle | partial (mock; **backend landed**) | `Handlers/BookingFulfillment/RequestPayment/RequestPaymentCommand.cs`, `DepositEvent/ProcessDepositEventCommand.cs`, `DepositStatus`; `deposit_enabled` per-restaurant toggle (spec 09 §2) |
| `/console/customers` | Operator | Customers | partial (mock; no list API yet) | spec 08 |
| `/console/settings` | Operator | Venue settings + onboarding checklist | partial (mock) | spec 15 (channels), `ConsentPoliciesController.cs:18` |
| `/admin/dlq` | Admin | Dead-letter replay | missing | `DeadLetterMessagesController.cs:21`, `DlqReplayState` |
| `/admin/notifications` | Admin | Delivery monitor | missing | `NotificationWebhooksController.cs:39`, `NotificationState`, `EndpointStatus` |
| `/admin/queues` | Admin | Queue & job health: outbox lag, inbox dup rate, DLQ depth, scheduled-job lateness | missing | spec 05 (metrics named in §interfaces→13), `ScheduledJobStatus`, `LogicalQueue`, `RetryClassification` |
| `/admin/tenants` + `:tenantId/endpoints` | Admin | Tenant & channel-endpoint registry | missing (contract landed) | spec 15 §5: `tenant_channel_endpoints` (`signal_type`: phone_number / messaging_recipient / widget_key / email_recipient / web_origin / directory_slug; exactly-one-active uniqueness) |
| `/console/settings/hours` | Operator | Operating hours & blackout periods | missing | spec 15 §7 tenant configuration (gates availability before provider calls) |
| `/console/capacity` | Operator | Capacity slots + active holds panel | missing | spec 07: `capacity_slots` inventory, `capacity_holds` + scheduled expiry release (`ReleaseExpiredHoldsHandler`), `HoldStatus`, availability modes |
| `/console/analytics` | Operator | Analytics / KPIs | partial (mock) | AnalyticsScreen.tsx exists; metrics backend is spec 13 — wire to real KPIs when a read API lands |
| `/console/campaigns` | Operator | Campaigns | **mock with no backend or spec — park or descope**; do not invest until a spec owns it | CampaignsScreen.tsx |
| `/login` (+ auth callback) | All staff | Sign-in → role- & tenant-scoped session | **missing — blocks every operator/admin surface** | spec 14 §4 authorization spine: authenticated identity + role + tenant scope + reason code (Inv-11); owner/operator role taxonomy (§24.5) |
| `/admin/slo` | Admin | SLO dashboard (or link to Grafana) | spec-only | spec 13 |
| `/manage` | Customer | Find my booking + OTP | missing | `BookingsController.cs:24`, `VerificationsController.cs:15–42` |
| `/manage/:bookingId` | Customer | View/modify/cancel | missing | `BookingsController.cs:44–156` |
| ~~`/pay/:linkId`~~ **no in-app payment page** | Customer | Payment happens on the **Stripe-hosted** checkout page — Cordia never renders card UI (spec 09 §2, Inv-12). Optional thin `/deposit/return` thank-you/status page only. | n/a | spec 09; `DepositSession.payment_link_url` |
| `/waitlist/offers/:offerId` | Customer | Accept/decline offer | missing | `WaitlistController.cs:78–96` |
| `/chat` or embedded widget | Customer | Voice/chat booking agent | partial (ConsolePage shell) | specs 01–02, streaming frames |

---

## 4. Design system — layered component inventory

Dependency rule: **a layer imports only from layers above it in this list.** This is
what makes components reusable — a `DataTable` knows nothing about bookings; a
`BookingsTable` composes it.

### 4.1 Layer 0 — Design tokens (`styles/tokens`)

Single source for: color scales (incl. a **semantic status ramp**: success / progress /
waiting-on-user / warning / danger / neutral — every domain enum maps into these six),
spacing, radii, typography, elevation, motion durations, breakpoints. Dark mode via
CSS variables. *(Principles: N4 consistency, WCAG contrast.)*

### 4.2 Layer 1 — Primitives (`components/ui/`)

**Existing (keep, harden a11y):** Avatar, Button, Chip, CommandPalette, Drawer, Empty,
Icon, KpiCard, Logo, Panel, Pills, SectionLabel, Segmented, Skeleton, Spinner,
StatusTags, Toast, Toggle.

**Missing — build these next (each generic, domain-free):**

| Primitive | Why (evidence) | Principles |
|---|---|---|
| `DataTable` | every list endpoint; sortable, selectable rows, sticky header, virtualized | N4, N7 |
| `Input`, `Select`, `Textarea`, `Field` (label+error+hint wrapper) | all forms | N5, N10 |
| `DatePicker` / `TimeSlotPicker` | booking create/modify (party size, time) | N5 |
| `PhoneInput` (E.164) | `phone_e164` params (`BookingsController.cs:28`) | N5 |
| `OtpInput` | verification codes (`VerificationsController.cs:42`) | N5 |
| `Dialog` / `ConfirmDialog` | destructive actions | N3, N5 |
| `Tabs` | drawer sections | N4 |
| `Stepper` | menu publish workflow, onboarding | N1, N8 |
| `Tooltip` / `HelpHint` | contextual help | N10 |
| `Banner` / `Alert` | page-level notices (degraded provider, expiring holds) | N1, N9 |
| `Pagination` | list endpoints | N4 |
| `Countdown` | hold expiry, waitlist offer expiry | N1 |
| `Timeline` | audit trails, operation history | N1 |
| `CopyButton` | booking references, IDs | N7 |
| `MaskedField` (rev 2) | renders PII masked by default with reveal-on-purpose (click → unmask + audited reason), auto-remask on blur — `Cordia.Domain/Security/SensitiveDataMask.cs`, spec-14 minimization | N5, WCAG |
| `MoneyAmount` (rev 2) | safe `amount_minor` + `currency` rendering, never floats — `DepositSession` (spec 09 §5) | N2, N4 |
| `DiffView` (rev 2) | menu review/edit comparison (`MenuController.cs:59,85`) | N1, N5 |
| `AudioMeter` / `AudioPlayer` (rev 2) | voice widget: mic level + TTS playback of `TtsAudioDeltaFrame` / `AudioStartedFrame` (spec 01) | N1 |
| `LocalTime` (rev 3) | renders timestamps in the **venue's** timezone (spec 15 §7 tenant configuration) with viewer-timezone tooltip — prevents the classic wrong-timezone booking error | N2, N5 |
| `JsonViewer` (rev 3) | collapsible payload viewer with `SensitiveDataMask` redaction applied — DLQ message bodies, webhook receipts | N8, spec 14 |
| `LocaleSwitcher` (rev 3) | customer-page locale selection; matches notification template locales (spec 10 `(template_version, channel, locale)`) | N4, i18n |
| `RouteErrorBoundary` (rev 3) | per-route crash fallback with CorrelationIdChip + "report" action — a component crash never blanks the whole console | N9 |

### 4.3 Layer 2 — Patterns (`components/patterns/`) — the reuse engine

These compose primitives + the data layer and are shared by **all** feature modules:

| Pattern | Composition | Consumed by | Principles |
|---|---|---|---|
| `StatusBadge` | StatusTags + `statusCopy.ts` + status ramp. **One component, enum-driven**: takes any generated enum value (`BookingLifecycleStatus`, `OperationStatus`, `WaitlistOfferStatus`, `CatalogStatus`, `PreorderSagaState`, `NotificationState`, `DepositStatus`, `DlqReplayState`), renders color+icon+plain-language label+tooltip. | everything | N1, N2, N6 |
| `FilterBar` | Chips + Segmented + Select, **reads/writes URL search params** | all lists | N6, URL-as-state |
| `SearchableList` | FilterBar + DataTable + Pagination + Skeleton + Empty + ErrorState, driven by a query hook | all lists | N1, N4 |
| `EntityDrawer` | Drawer + Tabs + route param (`?booking=…`); detail-on-the-side keeps list context | bookings, operations, sessions, catalogs | N3, N8 |
| `ActionBar` | Buttons whose visibility comes from a **transition map** (state → legal actions); disabled states carry tooltips explaining *why* | operation detail, booking detail, catalog wizard | Hick, N5, N10 |
| `ReasonCodeDialog` | ConfirmDialog + required reason Select + optional note; used for every reason-coded operator action (resolve `BookingOperationsController.cs:140`, DLQ replay `DeadLetterMessagesController.cs:21`, cancels) | operator actions | N3, N5, spec-14 audit spine |
| `VerificationGate` | wraps children; on 403 `RequiresVerification` (`BookingsController.cs:64`) swaps in the OTP flow (start → OtpInput → verify), then retries with grant | customer manage flow, sensitive drawers | N9 |
| `ConflictBoundary` | catches `ApiError(409)` from mutations → toast "Someone else updated this" + refetch + re-open form with fresh version token | all mutations | N9, optimistic-with-truth |
| `StreamingTranscript` | renders `StreamFrameEnvelope` frames: partial→final swap, agent text deltas, `ToolProgressFrame` chips, barge-in/turn-cancelled markers, `aria-live` | conversations screen, customer chat widget | N1, WCAG |
| `AuditTimeline` | Timeline + actor/reason rendering | admin audit, operation history | N1, spec-14 |
| `WizardShell` | Stepper + per-step ActionBar + draft persistence | menu publishing, onboarding | N1, N8 |
| `KpiRow` | KpiCard grid with loading/error states | Today, Analytics | N1 |
| `OfferCard` | Countdown + StatusBadge + accept/decline actions | waitlist (operator + customer) | N1, N3 |
| `DepositStatusCard` (rev 2) | MoneyAmount + StatusBadge for `deposit_status` (`not_required` / `link_sent` / `paid` / `expired`), Countdown to `expires_at`, resend-link action | booking & operation drawers, Deposits board (`RequestPaymentCommand`, `ProcessDepositEventCommand`, spec 09 §5 `OperationStatusView.deposit_status`) | N1, N2 |
| `StepTimeline` (rev 2) | Timeline of workflow steps (`WorkflowStepStatus`) with per-step retry info (`RetryScheduler.cs`, `RetryClassification`) and **resume-at-step** action (`ManualRecovery/ResumeWorkflowStepCommand.cs`) via ReasonCodeDialog | operation detail drawer | N1, N9 |
| `SlaBadge` + `EscalationBanner` (rev 2) | SLA state on operations (`Sla/EvaluateOperationSlaCommand.cs`); Banner when `ManualReviewEscalator.cs` escalates — links straight to the triage queue | operations board, Today screen | N1, N9 |
| `NotificationTimeline` (rev 2) | per-operation notification history: `NotificationState`, channel, `template_version`, `suppressed_duplicate` dedup markers (spec 10 Inv-9) | booking/operation drawers, admin monitor | N1 |
| `TemplatePreview` (rev 2) | renders a `RenderedMessage` for a `(template_version, channel, locale)` with redaction check indicator (spec 10 §5) | admin notifications | N1, N5 |
| `EndpointRegistryTable` + `ChannelEndpointForm` (rev 2) | signal type/value rows with `active`/`disabled` status; form enforces the exactly-one-active `(channel, signal_type, signal_value)` uniqueness with an inline `ambiguous_endpoint` conflict explainer (spec 15 §5, fail-closed) | admin tenants | N5, N9 |
| `OperatingHoursEditor` + `BlackoutPeriodPicker` (rev 2) | weekly hours grid + date-range blackouts; feeds availability gating (spec 15 §7) | console settings | N5, N6 |
| `ConsentCaptureBanner` (rev 2) | records `ConsentDecision` on a session (`ConversationSessionsController.cs:19`); state shown via `ConsentState` badge | conversations screen, customer chat widget | N3, spec 14 |
| `CorrelationIdChip` (rev 2) | CopyButton + truncated `correlation_id`; the handle support uses to join UI ↔ logs ↔ traces (specs 13, 15) | all drawers, error states | N9, N7 |
| `PermissionGate` (rev 3) | renders children only when the session's role + tenant scope allow the action (spec 14 Inv-11); hides—or disables with an explaining tooltip—operator/admin actions. Nav config (`nav.ts`) becomes role-aware so staff never see dead links | every ActionBar, Sidebar, admin routes | N8, Hick, spec 14 |
| `HoldsPanel` (rev 3) | active `capacity_holds` list with `HoldStatus` badge + Countdown to expiry; release action via ReasonCodeDialog | capacity page, Today screen | N1, spec 07 |
| `AvailabilityModeBadge` (rev 3) | shows which of the four modes (`informational` / `live` / `held` / `confirmed`) backs a stated availability — bounds what staff may promise a guest, mirroring what the agent may say (spec 07 §6.5) | booking create/modify forms, conversations | N1, N2 |
| `PreorderDraftCard` (rev 3) | pre-order line items + `MoneyAmount` prices with a live-revalidation notice ("prices re-checked before commit", spec 11 `LiveItemRevalidationService`) | chat widget read-back, preorder saga drawer | N1, N5 |
| `QueueHealthCards` + `ScheduledJobsTable` (rev 3) | KpiCard row for outbox lag / inbox dup rate / DLQ depth + jobs table (`ScheduledJobStatus`, lateness, `RetryClassification`) | `/admin/queues` | N1, spec 05/13 |
| `UnsavedChangesGuard` (rev 3) | route-leave / drawer-close prompt when a form or wizard has dirty state; WizardShell drafts persist instead of vanishing | all forms, WizardShell | N3, N5 |
| `UndoToast` (rev 3) | Toast variant with an undo action for reversible operations (e.g. waitlist un-enroll before offer sent) — prefer undo over confirm where the backend permits | reversible mutations | N3 |

### 4.4 Layer 3 — Feature modules (`features/<domain>/`)

Each module = its query/mutation hooks + thin domain components composing Layer 2.
E.g. `features/bookings/` → `useManageableBookings()`, `useBookingDetails()`,
`useValidateChange()`, `BookingsTable`, `BookingDetailDrawer` (rewire existing),
`ModifyBookingForm` (validate-before-submit), `CancelBookingDialog` (ReasonCodeDialog).
Same shape for `operations/` (incl. `StepTimeline`, `SlaBadge`, `AlternativesPicker` —
`BookingOperationsController.cs:200` + `RespondToAlternativesCommand`), `waitlist/`,
`menu/`, `preorder/`, `conversations/`, `verification/`, `notifications/`, `dlq/`,
`deposits/` (rev 2 — now real: settings toggle, board, `DepositStatusCard`),
`tenants/` (rev 2 — endpoint registry + operating hours), `auth/` (rev 3 — login,
session, roles), `capacity/` (rev 3 — slots + `HoldsPanel`), and `queues/` (rev 3 —
`QueueHealthCards`, `ScheduledJobsTable`).

Note on existing mock screens: **MoreScreen** is mobile nav overflow (stays a shell
component, not a feature); **AnalyticsScreen** waits on a spec-13 read API;
**CampaignsScreen** has no backend or spec — park it and remove from nav until one
exists (don't ship dead UI).

### 4.5 Layer 4 — Pages & layouts

`RootLayout` (exists) → `ConsoleLayout` (Header + Sidebar + BottomNav + StatusBar,
exists as parts), `AdminLayout`, `PublicLayout` (customer: single-column, mobile-first,
no nav chrome). Pages are thin: layout + one or two feature components + route wiring.

---

## 5. How components communicate

### 5.1 Data flow (one pattern everywhere)

```
generated types (libs/shared-schemas, libs/api-contract)
        │  compile-time contract
        ▼
api/client.ts  ──►  attach X-Tenant-Id (TenantContext)
                    attach Idempotency-Key on POST (spec 04)
                    parse ProblemDetails → ApiError
        ▼
query layer (TanStack Query)          keys: [tenantId, domain, filters]
  useXQuery / useXMutation            invalidation: mutation → invalidates its
        ▼                             domain list + entity keys
feature hooks (features/*)
        ▼
patterns (SearchableList, EntityDrawer, ActionBar…)
        ▼
pages
```

- **Server state lives in the query cache** (TanStack Query), never in Redux-style
  stores. Components subscribe via hooks; refetch-on-focus keeps operator boards fresh.
- **UI state lives in the URL** (filters, open drawer, selected tab) — search params
  are the bus between FilterBar, SearchableList, and EntityDrawer. Two components
  never pass filter state through props more than one level.
- **Ephemeral state** (toast queue, command palette open) → small contexts
  (`useToast` exists).
- **TenantContext** wraps the console; the switcher in Header writes it; the client
  reads it; query keys include it so switching tenants can't leak cached data
  (`BookingsController.cs:33` — every endpoint requires `X-Tenant-Id`).
- **AuthContext (rev 3)** sits above TenantContext: holds the authenticated session
  (identity, role, tenant scopes — spec 14 authorization spine). The client attaches
  the auth token; `PermissionGate` and role-aware nav read it; 401 → redirect to
  `/login`, 403 `forbidden` → inline "you don't have access" state, 403
  `verification_required` → `VerificationGate` (customer flows). Logout clears the
  query cache.

### 5.2 Realtime / streaming

- Conversations: WS/SSE → a per-session frame store → `StreamingTranscript`.
  Frames arrive as `StreamFrameEnvelope` (`streaming_models.py`); partial frames
  replace in place, final frames commit. `TurnCancelledFrame`/`BargeInFrame` render
  as inline markers (N1: the operator sees *why* text stopped).
- Operator boards: poll via query `refetchInterval` first; upgrade to push later.
  `StatusBar` (exists) surfaces connection health (N1).

### 5.3 Mutation etiquette (shared by every write)

1. Form validates with the generated Zod schema (N5) — same rules as server.
2. POST carries an idempotency key; double-click safe by construction (spec 04).
3. Mutations that the backend versions send the version token; `ConflictBoundary`
   handles 409 (N9).
4. On success: toast + targeted cache invalidation; on operation-starting endpoints
   (booking create/modify), navigate to the operation detail so the state machine is
   visible (N1) rather than pretending the write is done.

### 5.4 Relationship sketch

```
FilterBar ──(URL params)──► SearchableList ──(row click → URL)──► EntityDrawer
                                                                    │
                              ┌─────────────────────────────────────┤
                              ▼                                     ▼
                          ActionBar ──(legal actions)──► ReasonCodeDialog / ConfirmDialog
                              │                                     │
                              ▼                                     ▼
                        useXMutation ──► ConflictBoundary ──► Toast + cache invalidation
                              │
                              ▼ (long-running)
                        OperationStatus Timeline (polls until terminal state)
```

---

## 6. Beginner-friendly by design

1. **First-run onboarding checklist** on `/console` (Settings-linked `WizardShell`):
   connect channel (spec 15) → review consent policy (`ConsentPoliciesController.cs:18`)
   → upload menu (`MenuController.cs:32`) → test booking. Progress persists; dismissible.
2. **Empty states that teach** (extend existing `Empty`): every list's empty state
   says what the thing is, why it's empty, and has one primary CTA
   ("No waitlist entries yet — enroll a guest when service is full → *Enroll guest*").
3. **Plain-language everywhere** (N2): `statusCopy.ts` is the single dictionary;
   jargon (saga, DLQ, compensation) only appears in `/admin`.
4. **Progressive complexity**: Today screen = what needs attention now (`ManualReview`,
   `AlternativesRequired`, expiring holds via `Countdown`); full boards one click away.
5. **Safe exploration** (N3, N5): destructive actions always confirm; validate-before-
   submit shows consequences ("This change requires a new deposit") before commitment.
6. **Help affordances** (N10): `HelpHint` on non-obvious fields; CommandPalette
   doubles as searchable action/help index; short guided tour on first console visit.
7. **Customer flows are single-purpose** (one column, one primary action per screen,
   `PublicLayout`): lookup → verify → manage; offer → accept/decline; pay link → pay.

---

## 7. Accessibility & quality gates (definition of done for every component)

- Keyboard: full operability; Drawer/Dialog focus-trap + focus-restore; visible focus ring.
- Screen readers: labelled fields (`Field` wrapper), `aria-live` on Toast +
  StreamingTranscript, table semantics in DataTable.
- Color: status never color-only (icon + label); AA contrast from tokens.
- Motion: respect `prefers-reduced-motion` (skeleton shimmer, drawer slide).
- States: every data component ships loading / empty / error / partial variants
  (Storybook or Ladle story per state recommended).
- i18n-ready: all strings through `statusCopy.ts` / message catalogs, no literals in JSX.

---

## 8. Gap summary & build order

| # | Work package | Contents | Unblocks |
|---|---|---|---|
| 1 | **Auth + data & communication layer** | `/login` + AuthContext + `PermissionGate` (rev 3), TenantContext, client upgrades (auth token, tenant header, idempotency key, ProblemDetails, version tokens), TanStack Query, `ConflictBoundary`, `statusCopy.ts`, `StatusBadge`, `RouteErrorBoundary` | everything |
| 2 | **Primitive gaps** | DataTable, Field/Input/Select, Dialog/ConfirmDialog, DatePicker, PhoneInput, OtpInput, Tabs, Stepper, Tooltip, Banner, Pagination, Countdown, Timeline | all patterns |
| 3 | **Patterns** | SearchableList, FilterBar, EntityDrawer (refactor existing drawers onto it), ActionBar, ReasonCodeDialog, VerificationGate, WizardShell | all features |
| 4 | **Bookings live** | rewire BookingsTable + BookingDetailDrawer + Create/Reschedule drawers to real API; validate-before-submit | daily operator value |
| 5 | **Operations board** | 16-state board, triage queues (ManualReview / CompensationRequired), state-aware ActionBar, alternatives picker | reliability payoff |
| 6 | **Customer manage flow** | `/manage` lookup + OTP (VerificationGate) + modify/cancel | self-service |
| 7 | **Deposits live** (rev 2) | settings toggle, Deposits board on real data, `DepositStatusCard` in drawers, `MoneyAmount` | spec 09 complete |
| 8 | **Waitlist** (operator board + customer offer page), **Menu wizard** (+ `DiffView`), **Preorders** | | spec 11 complete |
| 9 | **Admin & tenant ops** | DLQ replay (ReasonCodeDialog + bulk select + `JsonViewer`), notifications monitor (+ `NotificationTimeline`, `TemplatePreview`), queue & job health (rev 3), tenant endpoint registry, operating-hours editor, capacity slots + `HoldsPanel` (rev 3) | ops + onboarding |
| 10 | **Conversations & voice** | StreamingTranscript + session viewer, `ConsentCaptureBanner`; customer chat/voice widget (`AudioMeter`/`AudioPlayer`) | biggest lift |

**Remaining thin spots (build behind interfaces, wire when the HTTP surface lands):**
several rev-2 capabilities exist as MediatR handlers without controllers yet — deposits
(`RequestPaymentCommand`), step-level resume (`ResumeWorkflowStepCommand`), SLA state
(`EvaluateOperationSlaCommand`) — so their feature hooks should isolate the endpoint
shape. Still genuinely pending: consent policy *editing* (GET only), the audit-trail
read API, and SLO dashboards (spec 13 — likely Grafana, not web app; surface only a
`CorrelationIdChip` deep-link).
