# Deploying Cordia: required configuration, per service

Operator reference. Every setting a deployed service needs, what breaks when it is
missing, and which values are secrets.

**Scope, stated up front.** This documents *configuration*. It does not describe
infrastructure that exists, because none does: there is no IaC in this repo, no
Redis anywhere (dev or prod), and no LangGraph Platform licence. Those are
[#94](https://github.com/Hazzng/Cordia/issues/94), and until they are resolved the
langgraph container **cannot start at all** — see §5. Setting everything below
correctly is necessary and not sufficient.

What *is* true today: a misconfiguration of the menu-extraction seam now fails at
**startup** rather than silently at runtime. §4 is the list of things that used to
boot green and then fail on every request.

---

## 1. The API (`apps/api`, .NET, App Service)

Bound from `IConfiguration`, so any key can be supplied as an environment variable
with `__` for `:` — `LangGraph:Host` is `LangGraph__Host`. Anything under
`Secrets:` reaches code through `ISecretStore` and is never logged or serialized.

### Required — the process will not start, or will fail on every request

| Setting | Secret | Missing ⇒ |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | no | Defaults to `Production`, which is the correct posture. Set it deliberately anyway: every gate in §4 keys off `IsDevelopment()`. |
| `ConnectionStrings__CordiaDBConnection` | **yes** (carries a password) | Every request that touches the database fails. `appsettings.json` ships a *localhost dev* connection string, so a deployment that omits this does not fail loudly — it tries to reach a Postgres on its own loopback interface. |
| `LangGraph__Host` | no | **Refuses to start** when the real extractor is selected (the default). Scheme + authority only — see §4.2 and §4.3. |
| `Secrets__LangGraphMenuExtractionKey` | **yes** | **Refuses to start** outside Development when the real extractor is selected. Must equal `CORDIA_MENU_EXTRACTION_API_KEY` on the agent, byte for byte, and must be printable ASCII (§4.4). |
| `Secrets__TenantContextSigningKeys__ActiveKid`<br>`Secrets__TenantContextSigningKeys__KeyIds`<br>`Secrets__TenantContextSigningKeys__Keys__<kid>` | **yes** | `JwsTenantContextSigner` throws `SecretNotFoundException` when it is constructed, so the agent→API booking lane fails closed on every call. `ActiveKid` must appear in `KeyIds`, and each key is **base64**. |
| `Secrets__ObjectStoreEncryptionKey` | **yes** | `EncryptedFileObjectStore` resolves it lazily, so the process starts and then throws on the first object write. Base64, 32 bytes (AES-256-GCM). |

The signing-key contract with the agent is byte-level:
`base64decode(Secrets__TenantContextSigningKeys__Keys__<kid>) == utf8(CORDIA_TENANT_CONTEXT_SIGNING_KEYS value)`.

### Optional — defaults are the safe direction

| Setting | Default | Notes |
|---|---|---|
| `LangGraph__ExtractPath` | `/internal/menu/extract` | Mirrors `EXTRACT_PATH` in `cordia_agent/menu/http_app.py`. Change both or neither. Must be relative — an absolute value would make `HttpClient` ignore `BaseAddress` entirely and is refused at startup. |
| `LangGraph__ExtractTimeoutSeconds` | `120` | **Also a transaction-hold budget** — the call runs inside the ingestion inbox transaction. Do not raise it to buy the agent more time; the agent has its own 105 s ceiling, and the 15 s difference is DNS, connect, request transfer and body read. |
| `LangGraph__UseStubExtractor` | `false` | `true` means **uploaded menus are never read**: every ingestion settles `DraftReady` carrying the same fabricated sample item, which an owner cannot tell from a real extraction. Startup logs a warning when it is on outside Development. Set only in Development and the test host. |
| `Cors__AllowedOrigins` | `[]` | No browser origin can call the API. The bundled SPA is served same-origin from `wwwroot/`, so this is only needed for a separately hosted front end. |
| `DatabaseBootstrap__MigrateOnStartup` | `false` | `true` applies EF migrations at startup. On in Development only; a deployment applies migrations as its own step. |
| `Tenancy__AllowHeaderShim` | `false` outside Development | `true` lets **any caller name the tenant** in `X-Tenant-Id` (Inv-13). Startup logs a warning when it is on outside Development. |
| `Authorization__AllowAssertedStaffRole` | `false` outside Development | `true` lets any caller who knows a booking id assert a staff role and mutate it with no verification grant (Inv-11). Startup logs a warning when it is on outside Development. |
| `Secrets__NotificationsWebhookSecret` | absent | Absent ⇒ the provider-callback webhook cannot verify a signature. |
| `ObjectStore__Root` | `<cwd>/.objectstore` | A container-local path, so objects do not survive a restart. |
| `TenantContext__TtlSeconds`, `OperatorSession__TtlSeconds` | 300 / 28800 | |
| `Redaction__SensitiveFields` | none | Extra log-scrubbing selectors on top of the built-in default-deny passes. |
| `Messaging__*__Enabled` | `true` | Per-lane hosted-service switches. The outbox publisher is a `BackgroundService`, so the host must not idle out. |

---

## 2. The agent (`apps/langgraph`, LangGraph Platform, Container App)

All of these are environment variables. `langgraph.json` loads `.env`, and
`.env.example` documents every knob inline — treat that file as the authority for
the optional ones.

### Required by the LangGraph runtime itself

| Var | Secret | Missing ⇒ |
|---|---|---|
| `REDIS_URI` | **yes** (URI carries credentials) | **Crash loop at import** — `KeyError: "Config 'REDIS_URI' is missing, and has no default."`. Proven by building the image from `langgraph.production.json`. There is no Redis in this repo. |
| `DATABASE_URI` (a.k.a. `POSTGRES_URI`) | **yes** | Crash loop. This is the *agent's own* Postgres — checkpoints and runs — **not** the API's `Cordia_API` database. |
| `LANGGRAPH_CLOUD_LICENSE_KEY` (or `LANGSMITH_API_KEY`) | **yes** | Lifespan abort: `License verification failed`. `langgraph dev` never hits this because the in-memory runtime ships a no-op licence shim; `langgraph build` produces the licensed variant. Procurement, not configuration. |

### Required by Cordia

| Var | Secret | Missing ⇒ |
|---|---|---|
| `AZURE_OPENAI_ENDPOINT` | no | Process starts fine, then `ModelNotConfigured` on the first request. Must be the resource form (`https://<name>.cognitiveservices.azure.com/`); the regional `*.api.cognitive.microsoft.com` form 404s on the deployments path. |
| `AZURE_OPENAI_API_KEY` | **yes** | As above. |
| `AZURE_OPENAI_DEPLOYMENT` | no | As above. Extraction falls back to this when the role-specific deployment is unset. |
| `AZURE_OPENAI_API_SURFACE=responses` | no | `gpt-5.6-luna` rejects function tools and a flat `reasoning_effort` on Chat Completions. |
| `CORDIA_API_BASE_URL` | no | **No `ToolClient` is registered** and the graph dies at `consent_gate` on every conversation. |
| `CORDIA_TENANT_CONTEXT_SIGNING_KEYS` | **yes** | No verifier is registered; `resolve_tenant` fails closed on every run. Paired with the API's `Secrets__TenantContextSigningKeys` (§1). |
| `CORDIA_MENU_EXTRACTION_API_KEY` | **yes** | `POST /internal/menu/extract` answers **503 `auth_not_configured`** on every request. It used to serve unauthenticated; see §4.4. |

### Optional, but read §4 before leaving them unset

| Var | Default | Notes |
|---|---|---|
| `AZURE_OPENAI_DEPLOYMENT_MENU_EXTRACTOR` | shared `AZURE_OPENAI_DEPLOYMENT` | Extraction fails closed if neither is set. |
| `CORDIA_MENU_EXTRACTION_DEFAULT_CURRENCY` | `AUD` | §4.6 — silent, and deployment-level rather than per-tenant. |
| `CORDIA_LLM_BASE_TIMEOUT_SECONDS_MENU_EXTRACTOR` | `40.0` | Extraction's first-attempt budget. §4.5. |
| `CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS` | unset | **Local loop only.** Do not set it in a deployment — see §4.4. |
| `CORDIA_AUDIO_*`, `LANGSMITH_*`, `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Documented inline in `.env.example`. |

Any `AZURE_OPENAI_*` or `CORDIA_AUDIO_*` var takes a `_<ROLE>` suffix override, so
one process can drive several deployments:
`AZURE_OPENAI_DEPLOYMENT_SLOT_EXTRACTOR=gpt-5.4-mini`.

---

## 3. The two directions of the API↔agent seam

They share nothing — different auth, different failure semantics.

| | Agent → API (booking lane) | API → agent (menu ingestion) |
|---|---|---|
| Address | `CORDIA_API_BASE_URL` | `LangGraph__Host` + `LangGraph__ExtractPath` |
| Auth | signed `TenantContext` (JWS, HS256) | shared secret in `X-Cordia-Internal-Key` |
| Key material | `CORDIA_TENANT_CONTEXT_SIGNING_KEYS` ↔ `Secrets__TenantContextSigningKeys` | `CORDIA_MENU_EXTRACTION_API_KEY` ↔ `Secrets__LangGraphMenuExtractionKey` |
| Unconfigured | graph dies at `consent_gate` | **both sides refuse to start / serve** |

---

## 4. Traps

Each of these was found by auditing the seam against a real deployment shape, and
each is now either a startup failure or a loud log line.

### 4.1 The agent listens on **8000** in production, not 2024

`langgraph dev` binds `:2024`. The image `langgraph build` produces from
`langgraph.production.json` serves **8000**. `LangGraph__Host` must name the port
the deployed container actually listens on; `http://…:2024` there is a connection
refusal on every ingestion.

### 4.2 `LangGraph__Host` must have **no path**

The client POSTs the root-relative `ExtractPath` against `HttpClient.BaseAddress`,
and RFC 3986 resolution replaces the base path wholesale. So
`LangGraph__Host=https://gw.internal/langgraph` does not reach
`/langgraph/internal/menu/extract` — it reaches
`https://gw.internal/internal/menu/extract`, returns 404, and surfaces to the owner
as "the agent returned HTTP 404". Addressing the agent through a reverse-proxy or
Front Door *route* is exactly how a deployment would do it, so this is the likely
mistake, not an exotic one.

**Now:** startup failure, in every environment. Give the agent its own hostname or
put the route at the root.

### 4.3 A loopback `LangGraph__Host` used to be the shipped default

`appsettings.json` carried `http://localhost:2024` and nothing in any deploy config
overrode it. It passed validation — it *is* an absolute http URI — and startup
logged nothing about the resolved host, so a deployed API POSTed every uploaded
menu at its own loopback interface and every ingestion settled `ExtractFailed`
with a generic reason.

**Now:** the base `appsettings.json` no longer carries a `Host` at all (the dev
value lives in `appsettings.Development.json`), a loopback or unspecified address
(`localhost`, `127.0.0.0/8`, `::1`, `0.0.0.0`, `[::]`) is a startup failure outside
Development, and startup logs the resolved host at Information whenever the real
client is selected:

```
Menu extraction is served by the REAL LangGraph agent (Production):
host http://langgraph.internal:8000, route /internal/menu/extract, timeout 120s.
```

### 4.4 The shared secret must be ASCII, and set on **both** sides or neither

- **Both or neither.** The API sends `X-Cordia-Internal-Key` only when
  `Secrets__LangGraphMenuExtractionKey` is configured; the route requires it only
  when `CORDIA_MENU_EXTRACTION_API_KEY` is set. Configuring one side gives you a
  permanent 401.
- **ASCII, printable.** Starlette decodes header values as latin-1 while the
  agent reads its environment as UTF-8; the two agree exactly on ASCII and diverge
  above it. A non-ASCII secret is a permanent 401 that neither side's logs
  explain. Control characters are refused too — the client adds the header without
  validation, so a CR/LF in the secret would be header injection.
  `openssl rand -base64 32` is a fine generator.
- **Whitespace is not a secret.** The route strips its expected value and the
  client treats a blank secret as "send no header", so a key configured to spaces
  reads as absent on both sides.

This route used to **fail open on both sides at once**: the key was configured
nowhere, LangGraph Platform auth does not cover custom routes unless
`enable_custom_route_auth` is on (it is not), and the route is published in the
deployed `/openapi.json` and `/docs`.

**Now:** the API refuses to start without the secret outside Development, and the
route answers `503 {"error": "auth_not_configured"}` rather than serving
anonymously. The local loop opts out by name with
`CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS=1`, which `bun nx serve langgraph` sets on
the serve target — **never set it in a deployment.** A configured key always wins,
so a stale opt-out cannot disarm a real secret.

Ingress should still be pinned **internal**: `/docs`, `/openapi.json` and the
platform's own surface remain unauthenticated whatever the route does.

### 4.5 `CORDIA_LLM_BASE_TIMEOUT_SECONDS` set process-wide breaks extraction

Extraction honours the **shared** form of the variable when the
`_MENU_EXTRACTOR`-suffixed one is unset. A conversational value — `8.0` is
reasonable for a turn of dialogue — makes every extraction attempt time out, while
conversation looks perfectly healthy. Set
`CORDIA_LLM_BASE_TIMEOUT_SECONDS_MENU_EXTRACTOR` explicitly whenever the shared
value is tuned for conversation.

The extractor's own worst case is `2 × base + 15`, capped at **105 s**, so the
highest accepted base is `45.0` — values above it are rejected, not clamped. That
ceiling sits deliberately below the caller's 120 s, which additionally covers DNS,
connect, up to ~1 MiB of request transfer, body read and the response.

### 4.6 `CORDIA_MENU_EXTRACTION_DEFAULT_CURRENCY` is silent and deployment-wide

Unset, it is `AUD`. It applies to a row the document priced but never attributed a
currency to — common, since a menu printing bare `$22.00` carries no locale marker.
It is **one value for the whole process, not per tenant**, so in a multi-tenant
deployment a US vendor's bare-`$` menu extracts as AUD, in a draft catalogue an
owner is about to approve. Nothing warns. A real per-tenant default has to come
from tenant context (`AzureExtractionModel` already accepts one per call).

### 4.7 An oversize upload is refused at ingress now

`SourceArtifactRef` is bounded at 1 MiB of characters. Above it the upload is a
400 instead of a `202 Accepted` that commits a catalog row and an outbox message
and then fails asynchronously. The authoritative document cap is unchanged: 256 KiB
of UTF-8 bytes, enforced on both sides of the wire.

For today's `inline:` refs the reference **is** the uploaded document, so it is
stored unmasked in `outbox_messages.payload`
([#95](https://github.com/Hazzng/Cordia/issues/95)). The bound limits the damage;
the fix is #84 moving artifacts into object storage.

### 4.8 Diagnosing a failed ingestion

- Startup log: the §4.3 line reports the resolved host, route and timeout.
- Application log: transport-level failures — unreachable agent, timeout, any
  non-2xx — log at **Error**. A malformed response for a single document logs at
  **Warning**. So "every ingestion is failing" is visible to a pipeline that alerts
  on Error, which it was not before.
- Durable evidence: the `catalog.extraction_failed` audit row carries
  `failure_category` — one of `unreachable`, `timeout`, `http_error`,
  `model_error`, `malformed_response`, `artifact_unresolvable`, `unknown`. That is
  the difference between "`LangGraph__Host` is wrong" and "the model returned
  garbage" without a log search.
- Joining the two sides: the client sends `X-Correlation-Id` and the route logs
  `correlation=` on every outcome. The id is logged verbatim only when it is
  `[A-Za-z0-9-_]` and at most 64 characters, else `correlation=opaque`.

Neither side ever logs the menu text, the artifact reference, a response body or
the shared secret.

---

## 5. Prerequisites that are not configuration

Tracked in [#94](https://github.com/Hazzng/Cordia/issues/94), out of scope for any
amount of settings work:

- **Redis** for the langgraph runtime. There is none in this repo — `docker-compose.yml`
  provisions Postgres only.
- **A second Postgres** for the langgraph runtime, separate from `Cordia_API`.
- **A LangGraph Platform self-hosted licence.** Procurement, and the production
  runtime has never been exercised on any machine.
- **Infrastructure as code.** There is no bicep/terraform/helm in the repo, so it
  cannot answer whether ingress is internal, whether a Key Vault exists, or what
  the ingress timeout is. Note that `az containerapp up` defaults to **external**
  ingress if it ever creates the app rather than updating a pre-provisioned one.
- **Deploy hygiene:** both images push to the mutable tag `vars.ENVIRONMENT` rather
  than a commit SHA (no rollback), and `deploy-api` / `deploy-langgraph` are
  independent unordered jobs — with `JsonUnmappedMemberHandling.Disallow` on the
  .NET side, an old API meeting a new agent response hard-fails for the length of
  the deploy window.

---

## 6. Local development

Nothing above is needed to run locally. See
[`end-to-end-handoff.md`](end-to-end-handoff.md) for the runnable channels and
[`text-booking-e2e.md`](text-booking-e2e.md) for the step-by-step repro. The short
version:

- `appsettings.Development.json` sets `LangGraph:UseStubExtractor: true` and
  `LangGraph:Host: http://localhost:2024`, so the API never dials out and no shared
  secret is needed.
- `appsettings.Test.json` does the same for the integration suite, which runs fully
  offline.
- `bun nx serve langgraph` sets `CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS=1`, so the
  extraction route serves on an unauthenticated `:2024` with no key configured.
- To drive the *real* agent from the API locally, set
  `LangGraph:UseStubExtractor: false` and leave both secrets unset — Development is
  the one environment where the §4.3 and §4.4 gates do not apply.
