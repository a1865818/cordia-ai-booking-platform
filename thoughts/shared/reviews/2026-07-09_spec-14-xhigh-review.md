# Spec-14 /code-review xhigh — findings and dispositions (2026-07-09)

**Scope:** `git diff 2f592d4...HEAD` (the 7 spec-14 commits, 86 files).
**Method:** 10 finder angles (Sonnet for mechanical sweeps, Opus for reasoning angles) →
dedup → verification (empirical for the regex findings) → Opus gap sweep → Fable final verdict.
**Result:** 12 findings fixed in the follow-up commit; 7 accepted with rationale. All suites
green after fixes (langgraph 747, api unit 780, api integration 330, web 2, both lints).

## Fixed (🔴/🟡)

1. 🔴 **PAN redaction failed open on ≥20-digit runs** (`redaction.py`, `SensitiveDataMask.cs`).
   The lookaround-anchored regex could not match a card number embedded in a longer digit run —
   the PAN escaped entirely (empirically verified). Now: any ≥13-digit run is a candidate;
   13–19-digit runs keep whole-run Luhn (precision — non-Luhn references survive); longer runs
   are window-scanned and masked wholesale (fail closed). Both languages + tests.
2. 🔴 **Compact CVV (`cvv123`) escaped masking** — the post-keyword `\b` required a separator.
   Fixed in both languages + tests.
3. 🔴 **Stale stashed consent grant could override a later decline** (introduced by this
   review's own first-round consent fix): the retry branch now reclassifies the fresh
   transcript first — the customer's latest statement always wins. Test added.
4. 🔴 **Consent record-failure-after-grant re-emitted the notice and discarded the reply**
   (soft loop under API outage). Now the classified reply is stashed (`consent_reply`) and the
   persist retried next turn without re-asking. Test added.
5. 🔴 **Checkpoint scrubbed only `transcript_text`** — a PAN spoken into a special request was
   durably stored unredacted. The checkpointer now runs `redact_payload` over the whole durable
   copy. Test added.
6. 🔴 **LogScrubbingEnricher skipped destructured values** — strings inside
   `StructureValue`/`SequenceValue`/`DictionaryValue` (`{@Object}` logging) bypassed both masks.
   Now recursive. Test added.
7. 🟡 **`SensitiveDataMask` lacked CVV/SSN/TFN parity with the Python filter** despite docs
   claiming identical masking; and `\d` matched Unicode digits while Luhn assumed ASCII. Now
   `[0-9]`-anchored with full detector parity.
8. 🟡 **ConsentController could NRE→500 on a null JSON body** (the exact trap the sibling
   replay endpoint documents). Null guard added.
9. 🟡 **Enricher masked any property whose NAME contains "pan"** (`SpanId`, `CompanyName`) —
   bare fragment removed; PAN values are still caught by the value sweep.
10. 🟡 **Double jurisdiction-policy resolution + version skew**: the reply turn re-resolved the
    policy (extra hot-path HTTP call; a rotated policy could bind consent to a version the
    customer never saw). The first-pass policy is now stashed in state and reused.
11. 🟡 **Consent audit actor said "customer"** — an unauthenticated identity implying the
    customer performed the write. Now `agent` (the true acting principal); the customer's
    decision stays in the metadata outcome.
12. 🟡 Conventions/cleanup: controller split into `ConsentPoliciesController` /
    `ConversationSessionsController` with class-level `[Route]`; enum→wire mapping moved into
    `RecordConsentCommand.FromContract` (Application) — controllers are thin again; deny-audit
    now writes through a fresh DI scope (a rolled-back handler's tracked entities can never be
    flushed by the audit's SaveChanges); `AuditRecord.OccurredAt` lets the replay audit share
    the envelope's clock; Extra metadata keys can no longer overwrite canonical envelope keys;
    "not sure"/"maybe" consent replies now classify as None (never granted); digit-count fast
    path before the regex sweeps on both hot paths; stale "11/17 tools" comments corrected.

## Accepted with rationale (💭)

- **`ConversationSessionRepository.RecordConsentAsync` bypasses the Version concurrency token**
  (ExecuteUpdate). Deliberate, documented last-write-wins: consent re-recording is legal (gate
  re-runs on reconnect) and per-session writes are serialized by the conversation.
- **Replay audit metadata key `decision` → `replay_decision`**: intended — `decision` now
  uniformly means allow/deny across all spec-14 audit rows; no existing consumer reads the old key.
- **STT keeps streaming after consent declined** — enforcement sits at the persistence/emit
  boundary; stopping raw audio at the STT edge needs session-level consent plumbed into the
  voice pipeline (spec 01/02 work). Recorded as a known limitation.
- **Retention sweeps are N+1 and copy-paste-shaped** — mirrors the pre-existing idempotency
  sweep (baseline behavior); consolidation is follow-up refactoring, not a spec-14 defect.
- **Two yes/no classifiers exist** (consent vs await_confirmation) — different vocabularies are
  intentional today (consent adds recording-specific phrases); unifying NLU is spec-02 work.
- **Omitted value-typed fields in the consent body bind to defaults** (omitted `outcome` →
  `alternative_offered` + `recording_allowed=false`): fail-SAFE (most restrictive outcome, never
  fail-open); missing string fields are 400-rejected. Root cause is the tracked quicktype
  nullability gap (documented in Program.cs); documented on the validator.
- **X-Actor read per-controller** rather than via an IActorContext middleware — matches the
  Stage-3 gate decision to keep the documented shim until the role taxonomy lands (08/14 open
  decision).
