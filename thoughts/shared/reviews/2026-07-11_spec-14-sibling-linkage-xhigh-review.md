# xhigh review — spec-14 sibling linkage (460f9e4..d6c5ef6), 2026-07-11

Four parallel finder passes (line-scan api, line-scan+pitfalls langgraph, removed-behavior +
cross-file tracer, wrapper/reuse/efficiency/conventions), verified and adjudicated at the
final tier. 12 findings survived; 9 fixed in the follow-up commit, 3 dispositioned.

## Fixed (🔴/🟡)

1. **Pipeline persist ordering (langgraph, 🔴)** — the persist call was awaited inline BEFORE
   `_run_agent`, (a) adding a blocking HTTP round-trip of dead air in front of every agent
   response and (b) running before `resolve_tenant` set the tool client's tenant context, so
   the session's FIRST turn always got a silent `tenant_not_resolved` envelope. Fix: persist
   moved AFTER the agent's frames are yielded — no dead air, tenant gate satisfied.
2. **Silent envelope discard (langgraph, 🔴)** — `ToolTranscriptPersister` ignored the returned
   envelope; non-ok rejections are normal returns, not exceptions, so drops were invisible.
   Fix: non-ok → warning log; consent-suppressed (`persisted=false`) → debug log (this also
   un-deads `parse_transcript_turn_persisted`).
3. **camelCase wire drift (api, 🔴)** — the five new response records lacked
   `[JsonPropertyName]`, serializing camelCase while the Python contracts (`extra="forbid"`,
   snake_case) could never parse them. Fix: snake_case attributes on
   `TranscriptTurnPersistedResult`, `TranscriptView`, `TranscriptTurnView`,
   `ModelTraceRecordedResult`, `ModelTraceView`; api-contract regenerated.
4. **Reader 500/phantom-splice on dot-bearing session ids (api, 🔴)** — `int.Parse` of the id
   suffix threw on sibling sessions matching the prefix (ids may legally contain dots). Fix:
   strict `{6 digits}` suffix filter with `int.TryParse(NumberStyles.None, Invariant)`; ids
   not matching are skipped, killing both the 500 and the phantom-turn splice.
5. **Deny-audit role mislabel (api, 🔴)** — `OperatorActionBehavior`'s `DomainException` deny
   path still hardcoded `ActorType: "operator"`, so an owner's failed actions couldn't be
   reconciled with their allow rows. Fix: asserted-role stamping (IsKnown ? role : operator),
   matching the role-mismatch path and the handlers.
6. **Hostile ids → 500 instead of 400 (api, 🟡)** — `trace_id`/`session_id` flowed into store
   path segments with only NotEmpty validation; the store's `ArgumentException` surfaced as
   500. Fix: new Domain `ObjectStoreSegment` (single-source allow-list regex, now also used by
   the store) + `.Must(ObjectStoreSegment.IsValid)` rules on all four validators → 400.
7. **Unknown-role audit fallback inconsistency (api, 🟡)** — transcript denials attributed
   unknown asserted roles as "customer" while the other two sites said "operator". Fix:
   asserted-but-unknown → "operator"; no role asserted at all → "customer" (documented).
8. **ListIds full-directory scan (api, 🟡)** — prefix filtering happened in managed code after
   enumerating every object in the purpose folder. Fix: prefix pushed down into
   `Directory.EnumerateFiles(dir, prefix + "*.bin")` (safe — segment allow-list admits no
   wildcards); managed filter kept as belt-and-braces.
9. **First-turn audit race (api, 🟡)** — check-then-act between the emptiness check and the
   put can duplicate `transcript.recording_started` under concurrent first turns. Fixed by
   contract: documented as AT-LEAST-ONCE (consistent with the platform's audit posture);
   consumers must not assume exactly one. Culture-invariant `D6` formatting also applied.

## Dispositioned (not fixed, reasons recorded)

- **Global tool-client single slot (langgraph, architectural)** — tenant-context clobbering
  across concurrent in-process sessions is pre-existing (spec-15 territory); the ordering fix
  removes the newly-added exposure window on the persist path. Tracked: per-session client or
  contextvar keying belongs to the spec-15 gateway work.
- **Serial N+1 turn decrypt in the reader** — acceptable at current transcript sizes;
  `Task.WhenAll` is the named cheaper alternative when profiles demand it.
- **GetModelTrace hand-rolled spine** — deliberate: `OperatorActionBehavior` does not audit
  the missing-identity case, while spec 13 §4.3 requires auditing every view attempt; the
  divergence is documented in the handler.
- **Hand-rolled controller DTOs vs generated types** — kept: the request body deliberately
  excludes `session_id` (path param), which the generated `TranscriptTurnInput` requires, so
  binding the generated type would change the wire shape.

Verification after fixes: api 872 unit / 354 integration green, langgraph 810 green, both
lints clean, api-contract regenerated.
