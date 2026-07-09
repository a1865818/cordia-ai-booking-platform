# Spec-14 /code-review max — conformance + defect pass (2026-07-10)

**Scope:** full `feature/spec-14` branch vs main, reviewed requirement-by-requirement against
`docs/specs/14-security-and-privacy.md` (§1–§12) plus a fresh bug hunt on the previous review's
fix commit. Method: 3 Sonnet conformance sweeps + 4 finder angles (Sonnet manual / Opus
reasoning, with empirical measurements) → Fable final verdict → fixes.
**Result:** 13 findings fixed; 4 accepted with rationale. Final state: langgraph 787,
api unit 796, api integration 331, both lints green.

## Fixed

1. 🔴 **Checkpoint identifier corruption (empirically reproduced, ~0.25–0.4%/key):** the
   whole-state scrub masked chance Luhn-valid digit runs inside `idem_<uuid4hex>`/`op_<uuid4hex>`
   (and could hit hex `*_hash` fields), permanently failing the Inv-2 pre-submit durability
   check and blocking submission. `redact_payload` now exempts control-plane keys
   (`*_id/_key/_hash/_ref/_version`); free-text fields still scrub. Regression test uses the
   observed colliding key.
2. 🔴 **Stashed decline flipped to granted by an unrelated affirmative** ("yes please" answering
   a different question on the retry turn). Fresh replies now override the stash only in the
   fail-safe direction (decline). Test added.
3. 🔴 **Stale pending notice retained when policy resolution fails during a reply retry** —
   routed to END re-awaiting the already-answered notice. Branch now clears the notice and
   continues non-recorded. Test added.
4. 🟡 **§3.1 step 5 — grant DENIALS were never audited** anywhere: `GrantAuthorizationService`
   now writes a `verification.grant_denied` deny audit (fresh DI scope, surviving the caller's
   rollback) before throwing — covering every manage-booking deny path centrally.
5. 🟡 **§3.1/§5.1 — sensitive READS were never audited:** `GetBookingDetails` now writes the
   spec's exact `read_sensitive_booking` allow audit on the grant-validated path.
6. 🟡 **§4.2 — consent not provably linked to submissions:** the acceptance transaction now
   stamps the session's consent snapshot (state/policy/jurisdiction/recording_allowed/
   recorded_at) into the immutable `operation.accepted` audit metadata (one PK read in-txn).
7. 🟡 **§4.1 — RedactionPolicy was a dead contract:** added the loading seam —
   `security/policy.py` registry (agent lane; hosting layer installs the tenant policy, all
   call sites default to it) and `Redaction:SensitiveFields` config → enricher fragments
   (API lane).
8. 🟡 **§5.3/§3.2 — tool decisions were not logged:** `is_tool_invocation_permitted` now logs
   allow/deny decisions (tool + trust + reason only; never content).
9. 🟡 **§11 — missing test families:** added `QueueConsumerTenantIsolationTests` (real inbox
   consumer via the full pipeline against real Postgres, tenant-A scope cannot touch tenant-B
   rows; admission recorded under A only) and `test_job_event_schema_hygiene` (every job-event
   schema is structurally unable to declare credential-shaped fields — with a justified
   allowlist; it immediately caught `payment_link_token`, correctly allowlisted as a
   customer-deliverable tokenized link).
10. 🟡 **Cross-language drift risk:** shared corpus `tools/config/redaction-test-vectors.json`
    now asserted by BOTH pytest and xUnit — the two lane filters can no longer diverge silently
    (the corpus itself caught a hand-typed vector error on first run).
11. 🟡 **Precision regression (measured 96.9%):** two adjacent phone numbers one space apart
    merged into one fused "PAN" candidate and were masked; spaced date-times masked ~10%.
    Candidate runs containing a space-separated group >6 digits (impossible in real PAN
    grouping) are now evaluated per-group. Both languages + corpus vectors.
12. 🟡 **Adversarial CPU (measured ~1s/100k digits):** digit runs >50 now mask unconditionally
    (strictly more fail-closed, O(1)) instead of window-scanning. Both languages.
13. 💭 Python digit-count fast path now short-circuits (parity with C#); `NoticeText!` got the
    explanatory comment (null is a required wire value — "None ⇒ agent default"); C# mask doc
    states its deliberate asymmetries (always-on gov-id; ASCII-only digits).

## Accepted with rationale

- **§5.2 HumanHandoff branch on decline** — consciously simplified to the non-recorded
  alternative (documented in the plan); the spec's own diagram marks it policy-conditional.
- **C# lane always masks SSN/TFN regardless of tenant opt-out** — deliberate conservatism on
  the audit/log lane, now documented on the class.
- **C# lane doesn't detect Unicode-digit PANs** ([0-9] anchoring) — the foreground filter
  catches them before text reaches the API lane; documented asymmetry.
- **Checkpoint byte-identical idempotency** — verified empirically (definition-order stable
  dump + deterministic scrub); noted as informational, no dedicated byte-equality test added.

## Conformance verdict (Fable, final)

Spec 14 §1–§12 requirement-by-requirement: **conformant** after the fixes above, with these
documented, deliberate deviations: `SessionConsentState` naming (C# pg-enum collision);
spec §4.2's C# AuditEvent shape superseded by spec 12's ratified row (registry wins);
consent proof implemented as an acceptance-audit stamp rather than a submission-envelope
field (spec-04 owns that wire contract; the audit stamp provides the immutable, queryable
link); operator identity remains the X-Actor shim + mandatory reason code (approved gate
decision; role taxonomy is the tracked 08/14 open decision); encryption-at-rest/object-store
descoped (no object store exists; deployment concern, documented); STT audio-egress consent
enforcement is spec 01/02 follow-up (documented limitation).
