# End-to-end handoff: running Cordia on the text and voice channels

Status as of 2026-08-09, updated after the voice channel was built and run.
Everything marked ✅ below was verified against live Azure and a live Postgres,
not against mocks. Everything marked ❌ has never run outside a test.

Companion docs: [`text-booking-e2e.md`](text-booking-e2e.md) has the step-by-step
text repro; [`deployment.md`](deployment.md) owns **deployed** configuration — every
setting each service needs, what breaks without it, which values are secrets, and
the traps. This document is the umbrella for running Cordia *locally*: both
channels, what is wired, what is missing, and the order to build it in.

---

## 1. Where things actually stand

| Capability | State | Evidence |
|---|---|---|
| Chat model (gpt-5.6-luna, Responses surface) | ✅ live | 2.77s round trip through `llm/factory` + `ainvoke_with_timeout` |
| Model-backed slot extraction | ✅ live | extracted `party_size=4, starts_at=2026-08-10T19:00Z, special_requests=['high chair']` from free text |
| TTS (gpt-4o-mini-tts) | ✅ live | 51 chunks, 4.30s of 24 kHz PCM, first chunk at 1.18s |
| STT (gpt-realtime-whisper, realtime WS) | ✅ live | accurate transcript, onset partial + final, via the real provider class |
| Text booking → `booked` row | ✅ live | `op_1cebdff2a97249728ab9751276146910`, `booked`, capacity consumed |
| **Voice call → `booked` row** | ✅ live | `op_188ba85b02cf4c71a313595430c459ef`, `booked`, from audio in to agent speech out |

**Both channels produce real bookings today.** The voice channel runs
`scripts/run_voice_booking_e2e.py`: caller audio → Azure realtime STT → the §8.3
graph → the .NET API → Postgres, and the agent's reply back out through
`gpt-4o-mini-tts` to 8 kHz mu-law WAVs on the media plane.

### What "booked" does and does not mean

Booking references look like `ext_pik_c7d54bb5…`. That is
`StubBookingProviderAdapter` minting a deterministic reference. The operation
genuinely traversed outbox → fulfillment → capacity finalisation → `booked`, and
`capacity_slots.booked_capacity` really moved. But **no downstream provider was
contacted.** No restaurant is holding a table. See §7.

There is a sharper version of that caveat on the voice channel, found by running
it: an operation reaches `booked` even when the `service_id` names nothing that
exists. See §8, "a spoken identifier does not survive ASR".

---

## 2. The two channel paths

Both channels converge on the same graph. They differ only in what produces the
turn.

```
TEXT                                    VOICE
────                                    ─────
scripts/run_text_booking_e2e.py         caller audio  ✅ media plane (file-backed)
  │ injects directly                      │
  │                                       ▼
  │                                     STTProvider  ✅ AzureRealtimeSTTProvider
  │                                       │ TranscriptFinalFrame   (session pre-warmed)
  │                                       ▼
  │                                     AgentPort  ✅ GraphAgentPort
  │                                       │
  ▼                                       ▼
        AgentInputState { signed_tenant_context, session_id, source,
                          transcript_text, transcript_is_final, current_turn_id }
                                 │
                                 ▼
   resolve_tenant → consent_gate → load_context → identify_intent →
   capture_fields ⇄ clarify → check_candidates → read_back →
   await_confirmation → submit_intent
                                 │
                                 ▼
                     .NET API → Postgres  ✅
                                 │
                          agent text reply
                                 ▼
                     TTSChunker → TTSProvider  ✅ AzureTtsProvider
                                 │ opaque tts:// refs
                                 ▼
                          AudioSink  ✅ TranscodingAudioSink
                                 │ resampled + companded
                                 ▼
                          media plane  ✅ WavFileMediaPlane / MediaDelivery
```

The remaining difference between the channels is the top box: the text runner
injects a transcript, the voice runner produces one from audio. Everything below
`AgentInputState` is byte-identical code on both paths.

### The graph ends its own turns now

The §8.3 topology had no END on the unconfirmed path, so a turn could not
terminate inside the graph — the text runner broke out of `astream` at the
confirmation gate instead. Three edges were cycles rather than recoveries
(`clarify → capture_fields`, `check_candidates → capture_fields`,
`await_confirmation → capture_fields`): each re-entered a node with an
*unchanged* transcript, so it reproduced the state that sent it there and span
until `GraphRecursionError`.

A turn now ENDs at the four points where only the customer can supply what is
missing: the consent notice, `clarify`, an unconfirmed read-back, and an expired
verification grant. Both runners are plain "one utterance, one invocation" loops
as a result, and neither contains a driver hack.

## 3. Prerequisites

### Azure deployments

All on `<your-ai-services-resource>` (AIServices, australiaeast):

| Purpose | Deployment | Notes |
|---|---|---|
| Conversation + extraction | `gpt-5.6-luna` | **must** use the Responses surface — it rejects function tools + flat `reasoning_effort` on Chat Completions |
| Cheap extraction (optional) | `gpt-5.4-mini` | via the `_SLOT_EXTRACTOR` role suffix |
| TTS | `gpt-4o-mini-tts` | emits 24 kHz 16-bit mono PCM |
| STT | `gpt-realtime-whisper` | **realtime WebSocket only** — returns `OperationNotSupported` on the batch `audio/transcriptions` route |

Endpoint must be the resource form (`https://<your-ai-services-resource>.cognitiveservices.azure.com/`),
**not** the regional `australiaeast.api.cognitive.microsoft.com` — the latter
404s on the deployments path.

### Local stack

- Docker (Postgres via `docker compose`)
- .NET 10 SDK, `bun`, `uv`

---

## 4. Environment

Copy `apps/langgraph/.env.example` → `apps/langgraph/.env` and fill in secrets.
`.env` is gitignored; `.env.example` is tracked, so never put a real key there.

**Required — nothing works without these:**

| Var | Why |
|---|---|
| `AZURE_OPENAI_ENDPOINT` / `AZURE_OPENAI_API_KEY` | model + audio, shared |
| `AZURE_OPENAI_DEPLOYMENT` | `gpt-5.6-luna` |
| `AZURE_OPENAI_API_SURFACE=responses` | luna rejects tools + flat effort on chat completions |
| `AZURE_OPENAI_REASONING_EFFORT=medium` | |
| `CORDIA_API_BASE_URL` | unset ⇒ **no ToolClient registered** ⇒ dies at `consent_gate` |
| `CORDIA_TENANT_CONTEXT_SIGNING_KEYS` | unset ⇒ **no verifier registered** ⇒ `resolve_tenant` fails closed every run |

The signing-key contract with the API is byte-level:
`base64decode(apps/api value) == utf8(langgraph value)`. The API stores it
base64-encoded under `Secrets:TenantContextSigningKeys:Keys:<kid>`; the dev pair
is already in `.env.example` and `appsettings.Development.json`.

**Optional:** everything under `CORDIA_AUDIO_*` (see `.env.example`, which
documents each knob inline), `LANGSMITH_*`, `OTEL_EXPORTER_OTLP_ENDPOINT`.

**Menu extraction** — all optional *locally*, and each one is a deployment-level
trap. [`deployment.md`](deployment.md) §2 and §4 own them:
`AZURE_OPENAI_DEPLOYMENT_MENU_EXTRACTOR`, `CORDIA_MENU_EXTRACTION_API_KEY`,
`CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS`,
`CORDIA_MENU_EXTRACTION_DEFAULT_CURRENCY` and
`CORDIA_LLM_BASE_TIMEOUT_SECONDS_MENU_EXTRACTOR`. `.env.example` documents each one
inline as well.

Locally you need none of them: `bun nx serve langgraph` sets
`CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS=1` so the extraction route serves on an
unauthenticated `:2024`, and the API opts into the stub extractor in Development.

Any `AZURE_OPENAI_*` or `CORDIA_AUDIO_*` var takes a `_<ROLE>` suffix override,
so one process can drive several deployments:
`AZURE_OPENAI_DEPLOYMENT_SLOT_EXTRACTOR=gpt-5.4-mini`.

### The seam runs both ways now

Until 2026-08-17 only one direction existed. There are two, and they share
nothing — different auth, different failure semantics, different owners.

**Agent → API** (the booking lane, every foreground tool call):
`CORDIA_API_BASE_URL` → `ToolClient` → the .NET controllers, authenticated by a
**signed `TenantContext`** the graph mints and `JwsTenantContextVerifier`
checks. Unset ⇒ no `ToolClient` is registered and the graph dies at
`consent_gate`.

**API → agent** (the menu-ingestion lane, issue #87): `LangGraph:Host`
(`http://localhost:2024` in `appsettings.Development.json` — plain HTTP, because
`langgraph dev` does not serve TLS; **absent from base `appsettings.json`, so a
deployment that forgets it fails to start**) + `LangGraph:ExtractPath` →
`POST /internal/menu/extract`, called by `LangGraphMenuExtractorClient`. **This is
the API's first outbound HTTP client**, so it is also the first place the
`AddHttpClient` conventions in [`dotnet-api-patterns.md`](dotnet-api-patterns.md)
apply. The deployed shape, and everything that can go wrong with it, is
[`deployment.md`](deployment.md) §3-§4.

- Auth is a **shared secret**, not a signed context: the client sends
  `X-Cordia-Internal-Key` when `Secrets:LangGraphMenuExtractionKey` is
  configured; the route requires it when `CORDIA_MENU_EXTRACTION_API_KEY` is set.
  Set both or neither. **Neither side fails open any more**: the API refuses to
  start without the secret outside Development, and the route answers
  `503 auth_not_configured` rather than serving anonymously unless
  `CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS` says so — which is how the
  unauthenticated local `:2024` keeps working.
- `X-Correlation-Id` rides along so the client's "HTTP 502" line can be joined
  to the route's own log line — and the route now logs it, so that join is real
  rather than aspirational. **Mind the charset**: it is logged verbatim only when
  it is `[A-Za-z0-9-_]` and at most 64 characters, otherwise the route records
  `correlation=opaque` and the join is lost. The .NET ids are `cat_{Guid:N}` and
  `corr_…` shapes, which qualify — but only because `_` is on that list, so do
  not switch the id format to one that uses `:`, `.` or `/`.
  `artifact_ref` is sent for correlation only and is **omitted when long**,
  because its canonical `inline:` form *is* the whole menu document.
- `LangGraph:ExtractTimeoutSeconds` (default 120) is owned deliberately, not
  inherited from `HttpClient`'s 100 s default. **It stays at 120** — the call runs
  inside the ingestion inbox transaction, so it is a **transaction-hold budget**
  too, and raising it is not a way to buy the agent more time. It is paired with
  the agent's own 105 s ceiling (95 s worst case at the default), which leaves
  15 s for DNS, connect, request transfer, body read and the response — all of
  which the caller's budget covers and the agent's does not.
- The route's `MAX_REQUEST_BODY_BYTES` is **4 ×** the 256 KiB text cap (1 MiB), a
  transport guard sized to absorb JSON escaping of one copy of the document. It is
  not a second budget for the text: `MAX_MENU_TEXT_BYTES` is still the contract,
  and an at-cap document plus an echoed `inline:` ref would spend over half of it
  on a field the route only reduces to a digest.

---

## 5. Running the text channel

Full detail in [`text-booking-e2e.md`](text-booking-e2e.md). Summary:

```bash
docker compose up -d

cd apps/api && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:8000 \
  ConnectionStrings__CordiaDBConnection="Host=localhost;Port=5432;Database=Cordia_API;Username=cordia;Password=localdev" \
  dotnet run --project src/Cordia.Api/Cordia.Api.csproj

docker exec -i cordia-postgres psql -U cordia -d Cordia_API < apps/api/scripts/seed-dev-tenant.sql

cd apps/langgraph && CORDIA_API_BASE_URL=http://localhost:8000 \
  uv run python scripts/run_text_booking_e2e.py
```

Verify:

```sql
select operation_id, status from booking_operations order by created_at desc limit 1;
-- expect: booked
select booked_capacity from capacity_slots where booked_capacity > 0;
-- expect: +4 per run
```

**Two traps that will cost you an hour each:**

1. **`dotnet user-secrets` overrides the connection string** to a remote Neon DB.
   First boot fails with `password authentication failed for user 'neondb_owner'`.
   The `ConnectionStrings__CordiaDBConnection` env var above outranks it — do not
   drop it from the command.
2. **The seed is load-bearing beyond ingress.** Without `capacity_slots` rows the
   fulfillment engine cannot finalise, so an operation accepted at the API never
   reaches `booked`. `seed-dev-tenant.sql` is idempotent (`ON CONFLICT DO NOTHING`)
   and deliberately does not resurrect consumed capacity on re-run.

---

## 6. Running the voice channel

```bash
# same stack as §5: docker compose up -d, the API on :8000, the seed applied
cd apps/langgraph && CORDIA_API_BASE_URL=http://localhost:8000 \
  uv run python scripts/run_voice_booking_e2e.py
```

The runner mints a `TenantContext` from the seeded DID, then drives three turns
of a call. Verify exactly as in §5 — the operation lands in `booking_operations`
the same way — and listen to what the agent said:

```bash
afplay .tmp/voice-e2e/<session>-turn-2-<id>.wav   # 8 kHz mu-law, decoded to linear
```

Useful flags: `--egress {pstn,wideband,native}` picks the carriage format,
`--caller-audio turn.wav` drives a turn from a real recording instead of a
synthesized one, `--silence-ms` tunes endpointing, `--no-realtime` stops pacing
audio to wall-clock.

### 6.1 The media plane

`AudioSink` now has an implementation, and the sample-rate conversion sits in it
rather than in a provider — which is where §6.2 of the old work list said it
belonged.

- `providers/audio_codec.py` — a polyphase rational resampler with a
  windowed-sinc anti-aliasing filter, plus G.711 mu-law companding. Pure
  `array`/`int`, no new dependency, ~90x realtime. A 10 kHz tone downsampled to
  8 kHz comes out at 1/1700th of its input amplitude instead of aliasing to an
  audible 2 kHz buzz, and the same stream cut into different chunk sizes
  resamples to identical bytes.
- `providers/media_plane.py` — `TranscodingAudioSink` (the `AudioSink`),
  `MediaDelivery` (the carriage seam), and `WavFileMediaPlane` (the reference
  delivery, one WAV per assistant turn).

**Choosing a telephony vendor is still a product call, and it is now a small
one.** Twilio Media Streams or a WebRTC track is one `MediaDelivery`
implementation — two methods, `deliver` and `fetch`. No provider, chunker,
pipeline or graph code changes. The file-backed plane is what makes the channel
runnable and audible today without committing to a vendor.

`TranscodingAudioSink.resolve(ref)` closes the other half of the gap: the port
yielded opaque `tts://` refs precisely so audio stayed off the frame contract,
and nothing could redeem one.

### 6.2 `AgentPort`

`streaming/graph_agent_port.py`. `GraphAgentPort` maps a `TranscriptFinalFrame`
onto `AgentInputState`, runs one graph turn, and streams the reply back as
`agent.text.delta` frames so TTS starts on the first sentence rather than the
last.

Two things worth knowing:

- **It never generates text.** Every word it emits was written by a node.
  `streaming/agent_reply.py` maps node → the state key that node writes
  (`pending_consent_notice`, `pending_clarify_prompt`, `read_back_text`,
  `submission_message`) and is a function of the node UPDATES, not of final
  state — `read_back_text` stays set for the rest of the session, so reading
  final state would replay the read-back on every subsequent turn.
- **Progress is measured, not fabricated.** LangGraph's `tasks` stream mode
  reports node entry, so the port emits `ToolProgressFrame`s on a timer with the
  elapsed time actually observed. `ProgressFiller` turns those into "just a
  moment" / "checking availability" at the §6.4 latency bands. On a live run the
  model-backed slot extractor took 12s and the caller heard a filler instead of
  silence.

### 6.3 `disclosures_presented`

Closed. `consent_gate` records `recording_notice` when the resolved policy
required a notice, and `read_back` records `booking_terms` (plus
`availability_advisory` when availability is only informational — Inv-16). The
vocabulary is `models/disclosures.py`, and nothing else may append to it: a
disclosure id asserts "the customer was shown this."

Neither runner injects the field any more.

### 6.4 The read-back the customer has not heard

Found by the first text run after the turn boundary landed, and worth its own
note because it was live for as long as the driver hack was hiding it.

`read_back` runs earlier in the same turn as `await_confirmation`. Turn 2's
transcript — *"Yes, that's fine. I'd like to book a table…"* — answers the
recording-consent notice, and `is_explicit_confirmation` saw the "yes". The
booking was confirmed and submitted **in the same breath as the request**,
without the read-back ever having been spoken.

`read_back_first_presented_turn_id` now records the turn a read-back version was
first rendered on, and `await_confirmation` refuses a confirmation from that same
turn. Re-rendering an unchanged read-back on a later turn leaves it alone, so a
"yes" on the following turn confirms normally.

### 6.5 Socket pre-warm

`AzureRealtimeSTTProvider.prewarm()` opens the transcription session once at call
setup and every `transcribe` reuses it; `aclose()` (or `async with`) releases it.
Measured at ~1.2s on live runs — paid once, while a real caller would still be
hearing the greeting, instead of on the first barge-in of every turn. A session
that dies mid-call is detected and replaced rather than degrading silently.

Reuse carries one hazard a fresh socket does not, and a live run hit it: a
consumer takes ONE final per stream, so a sentence the server-side VAD split in
two leaves a second transcription queued. On a per-stream socket that dies with
the connection; on a reused one the tail of turn 2 ("and we'll need a high
chair") arrived as the FIRST transcript of turn 3, and the caller's "yes, go
ahead" was never seen. The end of every stream over a shared session now clears
the input buffer and drains what is left.

### 6.6 The zero-candidate router cycle

Fixed. `route_after_check_candidates` sends a full slot to `clarify`, which asks
"that time isn't available — would another time work?" and ENDs the turn.
`availability_checked` distinguishes "genuinely full" from "we have not looked
yet", so clarify does not re-ask for a field the customer already gave.

### 6.7 `request_human_handoff`'s endpoint

Built. `POST /api/handoffs` → `HandoffWorkItem`, keyed on the session rather than
on an operation (a caller the agent cannot help has no operation and never will).
Idempotent per `handoff_idempotency_key` with a unique index behind it, so a
reconnect racing a retry across two processes still queues one caller. Verified
live: two POSTs with the same key returned `created` then `existing` with the
same `work_item_id`, and the agent's typed tool client parses the response.

## 7. What is still a stub

Ports and their real implementations. **Anything ❌ silently does nothing.**

### LangGraph

| Port | Real impl? |
|---|---|
| `TenantContextVerifier` | ✅ `JwsTenantContextVerifier` |
| `ModelTraceSink` | ✅ `ApiModelTraceSink` |
| `TranscriptPersister` | ✅ `ToolTranscriptPersister` |
| `SlotExtractor` | ✅ `ModelSlotExtractor` |
| `STTProvider` | ✅ `AzureRealtimeSTTProvider` (+ `AzureWhisperSTTProvider` batch fallback) |
| `TTSProvider` | ✅ `AzureTtsProvider` |
| `VoiceActivityDetector` | ✅ `EnergyVoiceActivityDetector` (batch STT only) |
| `AudioSink` | ✅ `TranscodingAudioSink` (+ `WavFileMediaPlane` delivery) |
| `AgentPort` | ✅ `GraphAgentPort` |
| `S2SProvider` | ❌ deferred by design (spec 01 §6.2) |
| `ExtractionModel` | ✅ `AzureExtractionModel` (role `menu_extractor`; served over `POST /internal/menu/extract`) |
| `SessionLookup` / `AgentResumeHook` / `HandoffHook` | ❌ reconnect path |

### API — stubs registered in DI, i.e. running in production today

`InfrastructureServiceCollectionExtensions.cs:139-247`:

| Interface | Bound to | Effect |
|---|---|---|
| `IBookingProviderAdapter` | `StubBookingProviderAdapter` | **nothing books downstream** |
| `IVerificationService` | `NoUserActionRequiredService` | **identity verification always passes** |
| `IValidationPolicy` | `AlwaysValidPolicy` | **validation never rejects** |
| `IArtifactScanner` | `StubArtifactScanner` | no malware scanning |
| `IExternalInventoryAdapter` | `StubExternalInventoryAdapter` | |
| `IIsolatedMenuExtractor` | `LangGraphMenuExtractorClient` (real; `StubIsolatedMenuExtractor` only when `LangGraph:UseStubExtractor=true`) | real extraction, gated |
| `IPreorderItemReservation` | `StubPreorderItemReservation` | |
| `IDepositPolicy` | `NoDepositPolicy` | no deposits taken |

Plus `SmsNotificationAdapter` / `EmailNotificationAdapter` / `ChannelCallbackAdapter`
— all self-described dev stubs with no network I/O. **No SMS or email leaves the
process.**

The three in bold are security-relevant and currently silent. `AlwaysValidPolicy`
and `NoUserActionRequiredService` in particular mean the Inv-11 grant gate that is
supposed to guard booking mutations is a pass-through. Fine in dev; not fine in
front of a customer.

`IIsolatedMenuExtractor` is the one row that has left this table (2026-08-17,
issue #87): the default is now the real `LangGraphMenuExtractorClient`, and the
stub is an explicit `LangGraph:UseStubExtractor=true` opt-in set in
`appsettings.Development.json` and `appsettings.Test.json` only. `Program.cs`
logs a warning when it is on outside Development, for the same reason
`Tenancy:AllowHeaderShim` does. `IArtifactScanner` has **not** moved — uploads
are still unscanned.

---

## 8. Known bugs worth their own tickets

**A spoken identifier does not survive ASR — and the stubs hide it.** ⛔ the
biggest open issue on the voice channel. The scripted caller says
"service svc_001"; `gpt-realtime-whisper` returns "Service SVC zero zero one";
the extractor faithfully records `SVC001` or `SVC_001`, because it is
(correctly) forbidden from inventing an id. No `capacity_slots` row matches, so
the booking consumes no capacity — **and still reaches `booked`**, because
`AlwaysValidPolicy` and `StubBookingProviderAdapter` (§7) never reject anything.
Confirmed live: `op_188ba85b02cf4c71a313595430c459ef` is `booked` against a
`service_id` that does not exist.

Two separate tickets, and the second is the alarming one:

1. Real callers do not speak internal ids at all — they say "a table for six".
   Resolving that to `svc_001` is catalog lookup (`search_vendor_catalog`),
   which the graph does not currently call. Until it does, the voice channel
   cannot carry a service id end-to-end.
2. An operation naming a nonexistent service should be REJECTED. That it is not
   is exactly what §7 warns about, now with a reproduction.

**`asr_confidence` is a constant on the realtime path.** `gpt-realtime-whisper`
returns no logprobs, so finals always report `fallback_asr_confidence` (0.9).
Anything branching on a confidence threshold is branching on a constant.

**Delta stability is invented.** The realtime surface supplies no per-delta score;
`0.5` is a documented policy choice, not data.

**Missing API routes:** `/api/tenant-context`, `/api/customers/context` and
`/api/booking-drafts/validate` still 405. All degrade gracefully today.
`/api/handoffs` is built (§6.7).

### Fixed since the last revision

**Submit recovery misclassified definitive rejections.** `_do_submit` treated
*any* `env.ok == False` as a lost response, and `map_http_error` maps an untyped
422 to the generic `request_error` — so a semantic rejection triggered
`GET /booking-operations/{id}` → 404 → **resubmit of the same durable mutation**
→ the same 422 → `nonrecoverable` → `human_handoff`, for a case spec 03 §4.3
classifies as recoverable. Recovery is now reserved for genuinely inconclusive
outcomes (`unavailable`, `rate_limited`); definitive 4xx map onto
`_CAPTURE_FIELDS_STATUSES`.

**Realtime teardown was non-deterministic for early-stopping consumers.**
`STTAdapter` returns at the first final, which left the provider generator to
the event loop's async-generator finaliser. Both it and `GraphAgentPort` now use
`contextlib.aclosing`, so abandoning a turn mid-stream releases the socket and
cancels the graph pump immediately rather than eventually.

## 9. Conventions you must follow

- **No test doubles in `cordia_agent/`.** All fakes live in `tests/_stubs/`. This
  is enforced by convention; `grep -rn "^class \(Fake\|Stub\)" apps/langgraph/cordia_agent`
  must return nothing.
- **Ports get constructed and injected, never globally registered**, except the
  four env-guarded bootstraps in `graph.py`: `init_metrics()`,
  `bootstrap_verifier()`, `bootstrap_tool_client()`, `bootstrap_slot_extractor()`.
  All four are unset ⇒ no-op, set-but-broken ⇒ crash at startup.
- **Every network call is bounded.** Follow `llm/invoke.py::ainvoke_with_timeout`
  (progressive budget, retry only what is safe to retry) and
  `providers/audio_http.py`.
- **The suite runs fully offline.** Inject the transport — `httpx.MockTransport`
  for HTTP, the `RealtimeConnect` factory for WebSockets. Live calls belong in
  opt-in scripts only.
- **Two implementations of a shared algorithm must be pinned by shared fixtures.**
  Learned the hard way: `canonical_payload_hash` was a Python stub while the API
  implemented canon.v1 per spec 04 §5. Both sides' tests passed; only running them
  against each other exposed it. See `tests/unit_tests/test_canonical_hash.py` and
  `UnitTests/Idempotency/CanonicalHashCrossLanguageParityTests.cs` — same payloads,
  same digests, both languages.

---

## 10. Verification

Never run two `nx` commands at once (daemon lock).

```bash
bun nx run langgraph:format
bun nx lint langgraph
bun nx test langgraph              # 1428
bun nx test-integration langgraph  # 51
bun nx test api                    # 1799
bun nx test-integration api        # 662 passed, 1 skipped
bun nx lint api
cd apps/langgraph && uv run ruff check tests/ scripts/   # 4 pre-existing findings in observability/
```

Counts measured 2026-08-20 on `feature/extractor`.

> **Known flake in `test-integration api`.** The booking lane has a
> pre-existing, order-dependent failure: `ManualRecoveryTests.Resume_ManualReview_
> ReconcileFindEffect_Booked_Inv15` and `CancelFulfilmentTests.CancelOperation_…`
> alternate — whichever runs second can fail, and each passes in isolation. It is
> unrelated to menu extraction. A red run there is worth re-running the single
> test before treating it as a regression.

After changing Pydantic models, regenerate the shared types:

```bash
bun nx run langgraph:generate-schemas
bun nx run shared-schemas:generate-types
```

---

## 11. Suggested order of work

Items 1-5 of the previous revision are done: the media plane, `AudioSink` with
resampling, `AgentPort`, `disclosures_presented`, the per-turn boundary, the
socket pre-warm, the zero-candidate cycle and the handoff endpoint. The first
live call happened, and — as predicted — it broke somewhere that was not on the
list (§6.4, §6.5, §8).

What is left, in order:

1. **Catalog resolution on the voice channel** (§8) — a caller cannot speak a
   machine identifier, so the agent has to resolve "a table for six" itself.
   This is the one thing blocking a genuinely realistic voice booking.
2. **Replace the security-relevant API stubs** (§7) before anything touches a
   real customer. `AlwaysValidPolicy` and `NoUserActionRequiredService` are why
   a booking against a nonexistent service still reaches `booked`.
3. **Pick a telephony vendor** and write the `MediaDelivery` for it (§6.1). Two
   methods; nothing above it changes.
4. **The remaining 405 routes** (§8) and a real `IBookingProviderAdapter`.
