# My contributions to Cordia — Andrew (Tuan Minh) Nguyen

Cordia was built by two engineers between June and September 2026:

- **Harry Nguyen ([@Hazzng](https://github.com/Hazzng))**: project owner. He built the repo
  foundations and the Nx/CI setup, the streaming voice pipeline (Azure STT/TTS and the media
  plane), the foreground conversational agent, tenant resolution and the JWS gateway (spec 15),
  manage-booking and verification (spec 08), observability (spec 13), the Azure model factory,
  and the real menu extractor.
- **Andrew (Tuan Minh) Nguyen ([@a1865818](https://github.com/a1865818))**, me: the work described below.

The original repository is private. This public copy, published with Harry's permission, keeps
the **full commit history with original authors**. One real Azure resource name was redacted
throughout, so commit hashes differ from the original. PR numbers refer to the original
repository; the matching `Merge pull request #…` commits are in this history.

**In numbers:** I authored 9 merged pull requests and 214 commits (205 excluding merges). Mostly in
`apps/api` (.NET), plus `apps/web` (React) and `apps/langgraph` (Python). I wrote roughly half of
the project's commits.

## Pull requests I authored

| PR | What it delivered | Size |
|---|---|---|
| #65 | **Spec 11: waitlist, menu and pre-order.** An isolated LLM menu-extraction agent and catalog contracts (LangGraph). Menu catalog domain, persistence and ingestion, owner approval lifecycle, a published catalog read path, ingestion-failure alerts, and a pre-order saga integrated across specs | +45.6k / −0.3k |
| #67 | **Spec 14: security and privacy for an AI agent.** A consent gate node and a pre-emit PII redaction filter in the agent. Jurisdiction-aware consent recording. An audit writer and an operator-action spine with reason codes. Log scrubbing, a secret-custody seam, scheduled privacy-retention sweeps, queue payload sanitisation, an encrypted object store with TTL retention, a tool gate and prompt channels. Transcripts readable only with consent, behind owner/operator role taxonomy | +15.5k / −0.5k |
| #61 | **Landing page** (React) | +7.5k |
| #82 | **Operator console wired to the live API.** Bookings, waitlist offers and notifications, with typed clients generated from OpenAPI and Zod | +73.1k / −2.6k (includes regenerated contracts) |
| #100 | **Wave 0: make the test signal trustworthy** before fixing anything on top of it | +2.1k / −0.3k |
| #101 | **Wave 1: six correctness fixes.** Four of them turned out not to be the bug their issue described | +2.5k |
| #109 | **Wave 2: engine correctness.** Fenced the dispatch write against concurrent cancels. Made the recovery shortcut honour cancellation. Respected an exhausted retry budget when the circuit is open | +4.3k / −0.2k |
| #111 | Test isolation for the console scan-ceiling test | +36 |
| #112 | **Waves 3–5: security and correctness backlog.** Identity proven, not asserted, for operator actions. HMAC-authenticated delivery receipts. Bounded heartbeat renewal for outbox leases. A circuit breaker on reconciliation lookups. Keyset pagination for the console. Random verification codes with lockout. Startup refusal of committed dev credentials. Per-tenant capacity ladder configuration | +16.7k / −0.4k |

## Themes that matter for production AI and data work

1. **AI governance in code.** Consent, redaction, audit, retention and secret custody are
   enforced at choke points such as the agent's emit path, the `DbContext` and the queue
   boundary, not left to convention.
2. **Reliability of data movement.** Transactional outbox/inbox leases, circuit breakers,
   reconciliation, idempotency and concurrency fences, each with a test that proves the failure
   mode is closed.
3. **Diagnosis before fixing.** Each wave began by checking whether the reported issue was the
   real bug. In waves 1 and 2, the issue description was wrong 8 times out of 12. The research,
   plan and review records are in `thoughts/shared/`.
4. **Working across the whole stack.** Python agent, C# API, React console, and shared schemas
   that keep them consistent.

## Every commit I authored

| Date | Commit | Message |
|---|---|---|
| 2026-06-29 | [`94042dc`](https://github.com/a1865818/cordia-ai-booking-platform/commit/94042dc1a2684569444282a5a359ed06a07138c9) | feat(landing page): Add landing page UI |
| 2026-06-29 | [`c8114f7`](https://github.com/a1865818/cordia-ai-booking-platform/commit/c8114f78a217a6da41a36b08751ba452b92d7b3a) | fix(landing page): fix UI based on review |
| 2026-06-29 | [`a2223ac`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a2223ac6c9252807084da48a7be368cc7b25ff2d) | fix(review): fix cubic review findings |
| 2026-06-29 | [`6bc89d4`](https://github.com/a1865818/cordia-ai-booking-platform/commit/6bc89d41671b422ad24ab6733d70f249b328b9c1) | Fix Cubic review findings |
| 2026-06-30 | [`364d587`](https://github.com/a1865818/cordia-ai-booking-platform/commit/364d5874b52dc1820ed3b72de54eac36b48bd026) | chore: ignore local .claude workspace directory |
| 2026-06-30 | [`d488884`](https://github.com/a1865818/cordia-ai-booking-platform/commit/d4888843c6f15a47c1a8f52f797a4ab742e367bd) | feat(langgraph): add isolated menu-extraction agent and catalog contracts |
| 2026-06-30 | [`9db7c76`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9db7c76b1219676e95b8a0142b1ecd50f38f9c5b) | feat(api): add menu catalog domain and persistence |
| 2026-06-30 | [`4e7ca3f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/4e7ca3f720b07ab4fbe0a883fe8b6218efa7f7cd) | feat(api): add menu ingestion and owner lifecycle handlers with MenuController |
| 2026-06-30 | [`e402fe8`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e402fe8e63f0c89ea504a49be68d9025717b867d) | test(api): add menu ingestion, lifecycle, and read-path coverage |
| 2026-06-30 | [`febbd95`](https://github.com/a1865818/cordia-ai-booking-platform/commit/febbd95e7f0b6db6b31fe448bb065b5e3415ffeb) | docs: document spec 11 menu-ingestion vertical |
| 2026-06-30 | [`25421e4`](https://github.com/a1865818/cordia-ai-booking-platform/commit/25421e45135c799869c969fc2ff64bfdf661c101) | feat(langgraph): add modifiers, availability window, image ref to catalog contract |
| 2026-06-30 | [`977b5bf`](https://github.com/a1865818/cordia-ai-booking-platform/commit/977b5bfe85435a9f6ee5753fcd8ed68bafc8ab0d) | feat(api): expose published catalog read path, rich fields, and ingestion-failure alerts |
| 2026-06-30 | [`265ae09`](https://github.com/a1865818/cordia-ai-booking-platform/commit/265ae09f41c2e38c94219d319b5ae6e6fa1444b3) | test(api): cover catalog read endpoint, rich fields, and ingestion-failure alerts |
| 2026-06-30 | [`8c6fef3`](https://github.com/a1865818/cordia-ai-booking-platform/commit/8c6fef30168217db3df861b2a3fa3398e0b35c58) | docs: add spec 11 menu-finishing, waitlist, and preorder-saga plans |
| 2026-07-01 | [`9855cb4`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9855cb48099b0f4ae1b226e9040b413078798468) | feat(api): spec 11 waitlist, pre-order saga, and cross-spec booking integration |
| 2026-07-01 | [`6b16c00`](https://github.com/a1865818/cordia-ai-booking-platform/commit/6b16c002dd938614c1026e3717b4e95cd36b083d) | feat(api): close spec-11 remaining gaps (auto-book front door, standalone conversion, preorder draft) |
| 2026-07-01 | [`f3ea113`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f3ea1133558d2331034a1386bb977ddc75fd957a) | feat(api): enforce spec-08 customer grant on preorder standalone conversion (spec 11) |
| 2026-07-02 | [`5e3d413`](https://github.com/a1865818/cordia-ai-booking-platform/commit/5e3d413e3359c762cb3025b782d2f2311d0b0773) | fix(api): harden spec 11 waitlist/menu/preorder per review |
| 2026-07-02 | [`2e38ff2`](https://github.com/a1865818/cordia-ai-booking-platform/commit/2e38ff2ede837ed72b4dff5cddcb4e40ec868f1d) | docs: consolidate spec 11 plans and correct handoff research |
| 2026-07-02 | [`557a374`](https://github.com/a1865818/cordia-ai-booking-platform/commit/557a3748da48029513365e28bb9ea068b665a788) | test(langgraph): fix date-rollover in verify_identity grant fixture |
| 2026-07-02 | [`e94efe7`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e94efe793c59c3e9c9e6fdea97314cb01d078103) | fix(api): reconcile waitlist accept when offer claim lost after booking mint |
| 2026-07-02 | [`57229ec`](https://github.com/a1865818/cordia-ai-booking-platform/commit/57229ecd6be1ac7699f00bba7cdf2ae9b20b5956) | docs(api): document waitlist accept-reconcile residuals |
| 2026-07-02 | [`6641cca`](https://github.com/a1865818/cordia-ai-booking-platform/commit/6641ccac0d063ca383eaeff0af0bde6c043215af) | fix(api): deliver waitlist offer and expiry notifications to customers |
| 2026-07-02 | [`6364d7f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/6364d7fec80c4f0a9f8b4bb1e628163faf5720cc) | fix(api): escape ILIKE metacharacters in catalog search query |
| 2026-07-02 | [`2fdaf0a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/2fdaf0a67e6f77fb75ba4212ee9afab02cafd3b9) | fix(api): keep accept replay idempotent when enrollment is fulfilled |
| 2026-07-02 | [`0fc4b90`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0fc4b90942a090b84d618353feffedcb97d731a3) | fix(api): reject unknown waitlist verification levels at the edge |
| 2026-07-02 | [`273f189`](https://github.com/a1865818/cordia-ai-booking-platform/commit/273f1890e5f3c869585b8f550b55325d7aab1f56) | fix(api): settle enrollment fulfillment on the accept claim-won path |
| 2026-07-02 | [`1dabbf9`](https://github.com/a1865818/cordia-ai-booking-platform/commit/1dabbf9eaed4cfcf1fe2ec3022a8b2edd6c8d2d1) | fix(api): reject waitlisting a pre-order operation in alternatives response |
| 2026-07-02 | [`00b01f8`](https://github.com/a1865818/cordia-ai-booking-platform/commit/00b01f8620194e4b048262d6aa709c498c5dcdd6) | fix(api): surface preorder revalidation failure to the customer |
| 2026-07-02 | [`42f7e37`](https://github.com/a1865818/cordia-ai-booking-platform/commit/42f7e373b49289f307dd645f664f4dab4969c80d) | fix(api): wire the ManualReview park for a stuck preorder commit |
| 2026-07-02 | [`33d1a28`](https://github.com/a1865818/cordia-ai-booking-platform/commit/33d1a2871aead094c275eedb0409a3acd4a744e1) | fix(api): dead-letter routable outbox types with no registered consumer |
| 2026-07-02 | [`188c092`](https://github.com/a1865818/cordia-ai-booking-platform/commit/188c092480e733e8d4e6b4fcd85ee8dfec2323cf) | perf(api): batch preorder revalidation and bound waitlist/sweeper reads |
| 2026-07-03 | [`f5677b9`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f5677b9b4530c88429d8376d1d0bcf2f9e5bc80a) | docs: add spec 11 code-review and progress-review reports |
| 2026-07-03 | [`36b56fb`](https://github.com/a1865818/cordia-ai-booking-platform/commit/36b56fb1af5ae591182c1833f654113375ccf5fa) | fix(api): allow operation-less notifications past the booking FK |
| 2026-07-03 | [`9252c79`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9252c79797b79234a394a45d07a40fc4ed045f2b) | fix(api): route all preorder terminal outcomes through the saga |
| 2026-07-03 | [`631a79c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/631a79ce90348823f515e6ac1fce793cb37dbc0b) | fix(api): terminalize the operation when a preorder saga compensates |
| 2026-07-03 | [`c84cac5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/c84cac56821b7d3bd69e4d3a8394ee1b92f66ae2) | fix(api): make menu review-with-edits work and validate edit prices |
| 2026-07-03 | [`8e8cc83`](https://github.com/a1865818/cordia-ai-booking-platform/commit/8e8cc839ac6dccd19c9ba81a7d422b8abccb46b9) | fix(api): harden queue poison handling, cancel re-match, and consent |
| 2026-07-03 | [`b07ff6f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/b07ff6fedeec6fded40f0f3a73aaea05d2097042) | fix(api): trigram search indexes, enroll identity fields, wire-shape fixes |
| 2026-07-03 | [`1e4eb4e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/1e4eb4e98e140fd37e1914e736ca257503536604) | fix(api): notification identity labels, decline-after-stale outcome |
| 2026-07-03 | [`7896aee`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7896aee3037642c4e788fd06075380f0c86b9687) | fix(api): fall back to a deterministic label in NotificationIdentity |
| 2026-07-03 | [`fedd5c5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/fedd5c55d3d9aa534b54efc95419da3609ca71db) | test(api): prove the decline-after-stale preorder emits one outcome |
| 2026-07-09 | [`367b363`](https://github.com/a1865818/cordia-ai-booking-platform/commit/367b363d5cb3ba8a15864c7991a6c842425f8c33) | feat(langgraph): add spec-14 security contracts |
| 2026-07-09 | [`82e5357`](https://github.com/a1865818/cordia-ai-booking-platform/commit/82e5357f2e241b90de27cb266a31449f4f6f7707) | docs(thoughts): spec-14 research baseline and implementation plan |
| 2026-07-09 | [`783e060`](https://github.com/a1865818/cordia-ai-booking-platform/commit/783e060e7e4eeffd531f1c6852f695afa43d64c7) | feat(langgraph): consent gate node and pre-emit redaction filter |
| 2026-07-09 | [`3878cc2`](https://github.com/a1865818/cordia-ai-booking-platform/commit/3878cc2a70cf4b9713e1d9dfd29c1c1609e2756c) | feat(api): consent vertical - jurisdiction policy and consent recording |
| 2026-07-09 | [`0e69d6e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0e69d6ee97cf4de5e38866e0b5bb03b2a3c9fc97) | feat(api): audit writer and reason-coded operator-action spine |
| 2026-07-09 | [`e2c514f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e2c514f00bb8792399a276e0346aadd56652bf83) | feat(api): log scrubbing, secret custody, and privacy retention sweeps |
| 2026-07-09 | [`0064368`](https://github.com/a1865818/cordia-ai-booking-platform/commit/00643689c177ed7396d3cc84f8e6a1b0bf23aa4d) | test(api,langgraph): spec-14 cross-cutting security suite (27.6) |
| 2026-07-09 | [`ce65099`](https://github.com/a1865818/cordia-ai-booking-platform/commit/ce65099e9446768924c851721d05a11947011cf7) | docs(thoughts): spec-14 validation report - all phases complete |
| 2026-07-09 | [`1f4d713`](https://github.com/a1865818/cordia-ai-booking-platform/commit/1f4d713ae1a1fe540f43fc58b631ef05cd695f32) | fix(api,langgraph): close spec-14 review findings from the xhigh pass |
| 2026-07-10 | [`7191b3f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7191b3f3f327f8f90c027d8ed68dd45d4aab11c4) | fix(api,langgraph): close spec-14 max-review conformance and defect findings |
| 2026-07-10 | [`0746a4d`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0746a4d00f0a874654059edc107a5563d1fc3a06) | feat(api): schedule the privacy retention sweeps |
| 2026-07-10 | [`9066277`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9066277639126a4951a321b74057510432859df9) | feat(api): sanitize every audit row at the DbContext choke point |
| 2026-07-10 | [`4f28684`](https://github.com/a1865818/cordia-ai-booking-platform/commit/4f28684faa1d5e9c5c1c00400299f5aabe834b47) | feat(api): spec-14 gap closure phase 3 - operator spine across all staff commands |
| 2026-07-10 | [`e01e830`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e01e8309ee32807cdd5181c4935e1727024266fb) | feat(api): spec-14 gap closure phase 4 - secret custody seam and typed security exceptions |
| 2026-07-10 | [`8743681`](https://github.com/a1865818/cordia-ai-booking-platform/commit/874368193e4e5733161023d7ab05be4c7d6b71f5) | feat(api): spec-14 gap closure phase 5 - queue payload sanitization |
| 2026-07-10 | [`4671c20`](https://github.com/a1865818/cordia-ai-booking-platform/commit/4671c20dcbfa8a6026fe76dde1b585db43e72c48) | feat(api): spec-14 gap closure phase 6 - encrypted object store with TTL retention |
| 2026-07-10 | [`90e23f5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/90e23f532eadb382d4e55c00ed2b7e0d51b08c6f) | feat(langgraph,api): spec-14 gap closure phase 7 - tool gate, prompt channels, redaction transport |
| 2026-07-10 | [`b5ea5b6`](https://github.com/a1865818/cordia-ai-booking-platform/commit/b5ea5b6d69b6f019b8cf1e0445bd0056f3815b45) | docs(thoughts): spec-14 gap closure validation report - phases 1-8 complete |
| 2026-07-10 | [`f4e0477`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f4e0477eeae7a285502c91032cde53352503b0b3) | docs(thoughts): correct residual-item spec attributions in gap-closure report |
| 2026-07-10 | [`a5ddc46`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a5ddc46685b697abdad3191dc392e2e429068c76) | feat(api): spec-14 linkage phase 1 - owner/operator role taxonomy on the authorization spine |
| 2026-07-10 | [`7824eab`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7824eab3a85ebc1613b5d1b6c8b6175f23241b4b) | feat(api): spec-14 linkage phase 2 - consent-gated transcript producer |
| 2026-07-10 | [`ff601fd`](https://github.com/a1865818/cordia-ai-booking-platform/commit/ff601fd3d63a392d874aee46091f8c536329d6ba) | feat(api): spec-14 linkage phase 3 - consent-gated transcript reader on the full authorization spine |
| 2026-07-10 | [`8f6d88b`](https://github.com/a1865818/cordia-ai-booking-platform/commit/8f6d88b01f5da4c2c438d195553d09963bb716ac) | feat(langgraph): spec-14 linkage phase 4 - transcript producer tool and pipeline persister port |
| 2026-07-10 | [`fd455e3`](https://github.com/a1865818/cordia-ai-booking-platform/commit/fd455e35202a3192d4773cad1cc32a4aed5ca83a) | feat(api,langgraph): spec-14 linkage phase 5 - model-trace sink on the stricter telemetry tier |
| 2026-07-10 | [`b66c76c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/b66c76c47c32f7f47f19f36851010d2fd6bfc43b) | docs(thoughts): spec-14 sibling-linkage validation report - phases 1-6 complete |
| 2026-07-10 | [`cde526c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/cde526c6c28955d62444ac80462c315cd64a00d0) | fix(api,langgraph): close spec-14 sibling-linkage xhigh review findings |
| 2026-07-10 | [`837ae05`](https://github.com/a1865818/cordia-ai-booking-platform/commit/837ae0588873b742ead07ab8418ddd825a9e4af0) | fix(api,langgraph): fail-closed PAN window scan + ReDoS-safe CVV regex (M1, M2) |
| 2026-07-10 | [`a02bb46`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a02bb462030adb5b3f35c6ad707fe1dcf72fb893) | fix(api): fail-closed object-store meta + atomic write (M3) |
| 2026-07-10 | [`e36e0c9`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e36e0c9cff4e3264d011d5247791ff8bf5989380) | fix(api): fresh DI scope per tenant in RetentionSweeper (M4) |
| 2026-07-10 | [`dcdd136`](https://github.com/a1865818/cordia-ai-booking-platform/commit/dcdd136c2f960d164d83e660221045d37cf13c65) | test(api): update audit-sanitization vector for M1 fail-closed PAN semantics |
| 2026-07-10 | [`66599da`](https://github.com/a1865818/cordia-ai-booking-platform/commit/66599da6c575ef6e5b8051dcd1f7e42837456bc2) | fix(api): close /code-review high findings on the M1-M4 hardening |
| 2026-07-10 | [`d345678`](https://github.com/a1865818/cordia-ai-booking-platform/commit/d345678dff104a7f2114bdaedb50b192df0efd2b) | fix(api,langgraph): mask PANs with doubled separators + hyphenated security-code CVV |
| 2026-07-10 | [`366150a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/366150a4b79afd04e526fc10d9855e383b779915) | fix(api): ListIds tolerates over-long prefix (L4) + document write-once store contract (L3) |
| 2026-07-10 | [`37f0464`](https://github.com/a1865818/cordia-ai-booking-platform/commit/37f046493f17372c4e6c6455dbaf34682b994f4a) | fix(api): audit object prunes before deleting the bytes (L1) |
| 2026-07-10 | [`5a8131c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/5a8131ca0f9c8cf517cf2157e9c47d7be0cf35cd) | fix(api): close GetTranscript existence oracle (L5) + audit missing-identity operator denials (L6) |
| 2026-07-10 | [`cf7ddac`](https://github.com/a1865818/cordia-ai-booking-platform/commit/cf7ddacb695b0a1dd0c2a3fbadf815ff94fea959) | fix(api): close 7 spec-14 fail-closed and audit-durability bugs |
| 2026-07-10 | [`3e81ac0`](https://github.com/a1865818/cordia-ai-booking-platform/commit/3e81ac0b6e2fcc60a2a95d1168cf2b501ffa2f5b) | fix(api): close /code-review high findings on the spec-14 bug-fix commit |
| 2026-07-10 | [`1b65b6f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/1b65b6ff7b8e0d4f9fdc085ab0a4d17c1277e7aa) | fix(api,langgraph): redaction parity, audit attribution, and stale docs |
| 2026-07-10 | [`465e152`](https://github.com/a1865818/cordia-ai-booking-platform/commit/465e1522c9dc4a2fb4a22391d96a1794bd9e1fc3) | fix(api,langgraph): close /code-review high findings on the parity/audit commit |
| 2026-07-10 | [`458987a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/458987aec35603ca37c17c0dfc1c92fba234e662) | fix(api): close four spec-14 audit/authz/log-hygiene defects |
| 2026-07-10 | [`524105c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/524105c8cb451bb40f1064dbcf59ad256ef93606) | fix(api): fail-closed 403 (not NRE/500) on anomalous empty-ref grant in GetTranscript |
| 2026-07-11 | [`079f6b9`](https://github.com/a1865818/cordia-ai-booking-platform/commit/079f6b96386cc5fd39b3f69af2cfa839adac055a) | fix(api,langgraph): close three spec-14 masking/transcript defects |
| 2026-07-11 | [`ca3f34e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/ca3f34e08465502636c088958ab1566006e4f0ff) | chore(api): block edits to committed EF migrations at pre-commit |
| 2026-07-11 | [`0b572c3`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0b572c3f6dddc7894acb43b4b1b7b319da4f55e1) | fix(api): mask numeric log PANs and the audit actor column |
| 2026-07-11 | [`1b130cf`](https://github.com/a1865818/cordia-ai-booking-platform/commit/1b130cf0047f79649db9b144356054ad59b94b6e) | fix(api,langgraph): mask audit subject/correlation ids and log dict keys |
| 2026-08-03 | [`0abeac3`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0abeac3cc803a887493e9b6dc5b75b018ef19471) | feat(web,api): wire the console to the live API |
| 2026-08-03 | [`bc4d015`](https://github.com/a1865818/cordia-ai-booking-platform/commit/bc4d015b3691f160f6f93bd118d8aebfc142b94d) | fix(web): date the calendar from the live schedule window |
| 2026-08-03 | [`e351e4c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e351e4cf273c4ddea6e8a615e2450b727d461d1c) | fix(web): stop asserting queue and conversation facts the API omits |
| 2026-08-03 | [`be96cbe`](https://github.com/a1865818/cordia-ai-booking-platform/commit/be96cbe753d93d63c206ef94916d779b32997d35) | fix(web): badge or disarm every remaining unbacked control |
| 2026-08-03 | [`5d42126`](https://github.com/a1865818/cordia-ai-booking-platform/commit/5d421262ac26fd698c6464b996bb55c64396fbca) | fix(web): carry verification grants through every gated call |
| 2026-08-03 | [`304a7ff`](https://github.com/a1865818/cordia-ai-booking-platform/commit/304a7fff87151d5eb92e756da579d30054732053) | feat(web,api): make waitlist offers answerable from the console |
| 2026-08-04 | [`0a8d138`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0a8d1383c063637086ac2f547e94a4312248756b) | fix(web): stop writing a guest's name into the minimised customer ref |
| 2026-08-04 | [`8c71bbe`](https://github.com/a1865818/cordia-ai-booking-platform/commit/8c71bbe8bd4b58838a8175431cf1f32d66362233) | fix(api): gate the X-Tenant-Id dev shim to non-production (Inv-13) |
| 2026-08-04 | [`9615495`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9615495b65b0b538da9c14e0719a784464da4a98) | fix(api): stop random test ids tripping the PAN detector |
| 2026-08-04 | [`a54d17c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a54d17cc804ba5174ddf8bc8f9c7a6f2a083bf93) | docs: record the gap-closure pass in the wiring guide |
| 2026-08-04 | [`7b443ba`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7b443baaa9ca8dee6a88656be63363bc09da6c47) | docs: record the Stage 6 regression diff against the baseline audit |
| 2026-08-04 | [`5f6c45f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/5f6c45f12e0a8671cd14b82be780e41cadaa800b) | fix(web,api): close the 15 findings from the xhigh review |
| 2026-08-04 | [`45db33e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/45db33eb7ec5f7c644d9f1dcf9ed7ae8b23a3633) | docs: record the xhigh review and its outcome |
| 2026-08-06 | [`78528fa`](https://github.com/a1865818/cordia-ai-booking-platform/commit/78528fa004d509343dcd411c2794673f9cc0a03f) | feat(web,api,langgraph): complete the console-to-API wiring pass |
| 2026-08-06 | [`31b6244`](https://github.com/a1865818/cordia-ai-booking-platform/commit/31b624485bf6731bc6c25bde9131fc3b9cca52c2) | docs: re-map the wiring guide and consolidate the thoughts records |
| 2026-08-07 | [`bda4d0e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/bda4d0ec223ee4cb8515342885ff0446af5f5c29) | fix(web,api,langgraph): close the post-PR hardening review findings |
| 2026-08-07 | [`f460ef7`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f460ef750c924b6bb1d0dcf167f8fe743b03bc4d) | docs: record grant single-use and the hardening semantics in the wiring guide |
| 2026-08-07 | [`f1ddbbe`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f1ddbbecfd8b0ca049a1faaeb4238d0d9730e7a0) | fix(api,web): global capacity lock order, single-source workflow state |
| 2026-08-07 | [`f03e775`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f03e77592bb7124fe0442b9cf1273eabb7edb621) | docs: record the capacity lock order and consolidate the wiring records |
| 2026-08-07 | [`5f93b2a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/5f93b2a39b46b05b958530bdee2be5c71090be00) | docs: sync the PR description record with the hardening update |
| 2026-08-09 | [`a37332b`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a37332b0b837a115e72acfde46e589cdfde05be9) | fix(api): gate the change pre-flight on the asserted-role flag and announce it at startup |
| 2026-08-09 | [`63625f0`](https://github.com/a1865818/cordia-ai-booking-platform/commit/63625f0d30500c1c478f0ee22b78588ff3c1f9aa) | fix(api): settle modify capacity from what the payload actually says |
| 2026-08-09 | [`f630eaf`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f630eaf1c819c433722a3daef7b8737c354d633d) | fix(api): clear a booking's pending lifecycle on every terminal failure |
| 2026-08-09 | [`ae6486b`](https://github.com/a1865818/cordia-ai-booking-platform/commit/ae6486bf09a5396c744fddadd15eea7a0055cac0) | fix(langgraph): treat the customer as a material change so the revision keeps up |
| 2026-08-09 | [`1e9b17c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/1e9b17cfc1a54344c40d3188f87f5a3d47daa91f) | fix(web): stop duplicate operations, unaudited transcript reads and silent refusals |
| 2026-08-09 | [`e3d023c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e3d023c1d9ce648e9794cf36bb6f87046c347fb1) | docs: record the asserted-role gate and review batch in the PR description |
| 2026-08-09 | [`88a1142`](https://github.com/a1865818/cordia-ai-booking-platform/commit/88a114205b64ad6596ca1a042693e3fb4bed9c8c) | fix(api): make manual recovery refuse what it cannot honestly claim |
| 2026-08-09 | [`3114357`](https://github.com/a1865818/cordia-ai-booking-platform/commit/311435794067732ee6f2df89559f63d962f005de) | fix(web): move the console's "today" with the clock, and revert a refused move |
| 2026-08-09 | [`ce7b480`](https://github.com/a1865818/cordia-ai-booking-platform/commit/ce7b48083cb6be0d253c12254c695b90bcd41427) | docs: record the terminal-path precondition fixes in the PR description |
| 2026-08-09 | [`f2a9835`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f2a983567551f2afd68d7b10cbfc92640caf3d48) | Update apps/web/src/components/platform/drawers/CreateBookingDrawer.tsx |
| 2026-08-09 | [`7654b69`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7654b6902fe8ed1aa75d0951a9189067fab4cb78) | fix(api): let an operation retract only the pending state it parked itself |
| 2026-08-09 | [`555e067`](https://github.com/a1865818/cordia-ai-booking-platform/commit/555e067401bc42d9d63bd5ccffea13d92d963209) | fix(web): unblock the create drawer and give a gated reschedule a way through |
| 2026-08-09 | [`7346e1c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7346e1c672b2ba4fdb167d7c5b620cdc27dccc91) | fix(api): refuse a modify while a cancellation is in flight |
| 2026-08-09 | [`f6ce103`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f6ce1036e9204d534ac123b515ddcf0baa03978e) | fix(web): stop the console asserting outcomes it cannot know |
| 2026-08-09 | [`0d54af4`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0d54af46b60718cc7deffffc929b49d863321795) | fix(api): minimise customer_ref on the ungated console reads |
| 2026-08-09 | [`a9271cf`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a9271cf8bff5da7669e5003777fd68b39f65071f) | docs: correct the wiring guide where the code had moved past it |
| 2026-08-09 | [`bb15d0e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/bb15d0e21d4fd6200d64be25c933fa28f86a36cb) | docs: record the PII minimisation pass in the PR description |
| 2026-08-09 | [`c34f089`](https://github.com/a1865818/cordia-ai-booking-platform/commit/c34f08974f076796020b92616797edd50375b166) | fix(api): create workflow steps through an insert the database can absorb |
| 2026-08-09 | [`17d65b2`](https://github.com/a1865818/cordia-ai-booking-platform/commit/17d65b2a3ac73185a9433be083e2258144f3de4e) | fix(api): keep the grant strip applied when confirmation.identity is not a string |
| 2026-08-09 | [`9df4fa5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9df4fa5c9739bd8f647a08d059430071cc54b2ff) | fix(api): report a console scan that reached the ceiling but examined everything |
| 2026-08-09 | [`87d44f3`](https://github.com/a1865818/cordia-ai-booking-platform/commit/87d44f3ca16327a1a95ca5503088b2b273014113) | fix(web): tell a failed catalog read apart from a tenant with no catalogs |
| 2026-08-09 | [`2e0cca4`](https://github.com/a1865818/cordia-ai-booking-platform/commit/2e0cca40926bc502e2d02575669061088c96c2d8) | docs: correct four claims in the wiring guide |
| 2026-08-09 | [`c3d3c3c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/c3d3c3c05578dd3da02010b37e74169a8d44bd27) | fix(web): stop ErrorState telling operators to verify when they lack the role |
| 2026-08-09 | [`affed32`](https://github.com/a1865818/cordia-ai-booking-platform/commit/affed328aa5693bf12b9484ed2da42268188f79a) | fix(web): gate a catalog's Save and its lifecycle actions against each other |
| 2026-08-09 | [`d0ea0d4`](https://github.com/a1865818/cordia-ai-booking-platform/commit/d0ea0d45fdd5ca4a1949e15501ed9416c4ac1806) | fix(web): let an owner see and correct an extracted description |
| 2026-08-09 | [`bd6b21e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/bd6b21e8498fab51db4bc3900fb042296fe550fa) | docs: record the last three review rounds in the PR description |
| 2026-08-09 | [`abb683a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/abb683aee2cb95cf23055efd958a4d08eadd6125) | fix(web): window the draft diff onto the text that actually changed |
| 2026-08-09 | [`19b2490`](https://github.com/a1865818/cordia-ai-booking-platform/commit/19b24905a5ba32d5f91ec934c4da4549741fe8a1) | docs: record the diff-window fix in the PR description |
| 2026-08-10 | [`9e466c7`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9e466c771739f274c086cbd348bc24fd103b5a43) | fix(web): revert a reschedule the platform terminally refused |
| 2026-08-10 | [`92dc8ac`](https://github.com/a1865818/cordia-ai-booking-platform/commit/92dc8ac1a74493459ac73b9f4d4d6293eff62f2b) | fix(api): fence a pending-state release on the operation that parked it |
| 2026-08-10 | [`24ff7a2`](https://github.com/a1865818/cordia-ai-booking-platform/commit/24ff7a2a4f5d4fab2765f05de174f7f758b8d661) | fix(api): make the pending-state ownership check atomic with its release |
| 2026-08-26 | [`d881956`](https://github.com/a1865818/cordia-ai-booking-platform/commit/d88195693992a6e958d466b9b2a1bfab62092248) | fix(tooling): hash generated types through the producing task |
| 2026-08-26 | [`776227f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/776227fcf1b0493a98aa849f445e5a0798c7ccb4) | test(api): scope outbox and scheduled-job claims per tenant |
| 2026-08-26 | [`055ec55`](https://github.com/a1865818/cordia-ai-booking-platform/commit/055ec5521e004796eaddc60d469c4ae83a2c27eb) | fix(test): serialise test-schema setup and pin the outbox claim ordering |
| 2026-08-26 | [`8914cd5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/8914cd59ab2983eff89c2f8b331d0fb83328d41c) | ci: run the .NET integration suite against Postgres |
| 2026-08-26 | [`660573a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/660573a2ba74240aaae7cc19ce4f0016b1d48b95) | chore(langgraph): collapse the duplicate production config |
| 2026-08-26 | [`fce2a47`](https://github.com/a1865818/cordia-ai-booking-platform/commit/fce2a478da4f2af06e47528ff412a1585470cb9a) | test(api): add a FinalizeBookingCommandHandler unit harness |
| 2026-08-26 | [`c4fd25f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/c4fd25f0909afdd42fc61bce3dac16a4fbfa9f0d) | docs: record Wave 0 validation and the regression diff |
| 2026-08-26 | [`4b35ea1`](https://github.com/a1865818/cordia-ai-booking-platform/commit/4b35ea1e7b813a51154900160acad68113b6df51) | fix(ci): hash generated-type sources instead of depending on the generator |
| 2026-08-26 | [`bd15fe5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/bd15fe57af9bed3a8636316391617cddb8bc3766) | fix(api): publish outbox lanes concurrently and size the lease to the slowest consumer |
| 2026-08-26 | [`c9cd2f9`](https://github.com/a1865818/cordia-ai-booking-platform/commit/c9cd2f9415b3252ef006631ff854c92e4eb8233f) | fix(security): mask content-bearing artifact refs in both redaction lanes |
| 2026-08-26 | [`b6a8477`](https://github.com/a1865818/cordia-ai-booking-platform/commit/b6a8477abed856598e94385cfe103eadf46548df) | fix(api): report the real outcome of a cancellation request |
| 2026-08-26 | [`100ee50`](https://github.com/a1865818/cordia-ai-booking-platform/commit/100ee50a76a4eb48c40d4c58018b60867c4093bc) | fix(api): tighten CORS, correlation provenance and Swagger exposure |
| 2026-08-26 | [`88748b7`](https://github.com/a1865818/cordia-ai-booking-platform/commit/88748b78b1416210b974cd0edf9fe568ea774d58) | chore(docs): correct the stale gap inventory and enforce the §13-C guardrail |
| 2026-08-26 | [`56f5084`](https://github.com/a1865818/cordia-ai-booking-platform/commit/56f5084ff89b592abd469e03863609373001261a) | chore(api): retag deferred TODOs to issues |
| 2026-08-26 | [`75a489c`](https://github.com/a1865818/cordia-ai-booking-platform/commit/75a489c5254349a7a8ae1dc8b83c27bd03634e70) | docs: record Wave 1 validation and the regression diff |
| 2026-08-26 | [`46d2c75`](https://github.com/a1865818/cordia-ai-booking-platform/commit/46d2c75690d85a449ceb8c442f4fdb9f923c0f5d) | fix(api,langgraph): mask sensitive data inside encoded data: artifact refs |
| 2026-08-26 | [`2e2c413`](https://github.com/a1865818/cordia-ai-booking-platform/commit/2e2c4133e80508e3a8e6e658c66ff3e8982e5c4e) | test(api): assert the CORS policy refuses a method the API does not use |
| 2026-08-26 | [`86f5e36`](https://github.com/a1865818/cordia-ai-booking-platform/commit/86f5e361cd9701424b9569b59ff4703e9933ed4b) | docs(api): correct the outbox lease justification for multi-row lanes |
| 2026-08-26 | [`45c5663`](https://github.com/a1865818/cordia-ai-booking-platform/commit/45c5663bab9153dc653937e187d5e46dcc3799a0) | docs: fill the wave-1 plan discoveries and correct two closed-gap entries |
| 2026-08-26 | [`c8273c7`](https://github.com/a1865818/cordia-ai-booking-platform/commit/c8273c7970031edf89f13cc2207d08c07192c410) | docs(api): fix a stale LeaseSeconds cross-reference |
| 2026-08-31 | [`0ad9b61`](https://github.com/a1865818/cordia-ai-booking-platform/commit/0ad9b616232d80260ff71be27a4d6540f9887caa) | test(api): give the console scan-ceiling test its own command budget |
| 2026-08-26 | [`fdc1332`](https://github.com/a1865818/cordia-ai-booking-platform/commit/fdc1332445ab9183a22d9c4739bdbabd2fd20ff9) | docs: research and plan Wave 2 engine correctness |
| 2026-08-31 | [`a0a55c3`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a0a55c328c94e1b08dff21eb4337d625339ea6f6) | fix(api): stop Compensate declaring "no effect" over an in-flight attempt |
| 2026-08-31 | [`2feee02`](https://github.com/a1865818/cordia-ai-booking-platform/commit/2feee029b4a1f5804ac1239f4a7cf609c3967be5) | fix(api): require proof of absence before reconcile re-creates a booking |
| 2026-08-31 | [`12513d1`](https://github.com/a1865818/cordia-ai-booking-platform/commit/12513d19c05115f3e15ed62dbf0f963d808309ed) | fix(api): settle first-party capacity when reconcile books an operation |
| 2026-08-31 | [`6ef1223`](https://github.com/a1865818/cordia-ai-booking-platform/commit/6ef12237b089ed88037395337c2c3baac47b2492) | refactor(api): move the booking-aggregate factory out of the Finalize handler |
| 2026-08-31 | [`ad88b02`](https://github.com/a1865818/cordia-ai-booking-platform/commit/ad88b021b7bfa426ae89a525c80bdd74c8152992) | fix(api): give a reconciled operation its bookings row (U1) |
| 2026-08-31 | [`410d473`](https://github.com/a1865818/cordia-ai-booking-platform/commit/410d473c2425b839ac3aed5553edf58465fcb8b8) | fix(api): make the recovery shortcut honour cancellation and emit an outcome |
| 2026-08-31 | [`214da3a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/214da3a7f643a7311a9a4d05a00296405f5d51ea) | fix(api): fence the dispatch write against a concurrent cancel |
| 2026-08-31 | [`7127ca0`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7127ca083e6ba57e3b48537eaf5730d73ff63494) | fix(api): honour an exhausted retry budget when the circuit is open |
| 2026-08-31 | [`4081d45`](https://github.com/a1865818/cordia-ai-booking-platform/commit/4081d452e5fa1f33fc978f2e2de5dee98a399a64) | fix(api): give NotFound from a create its own arm, and close the default trap |
| 2026-08-31 | [`288b7b5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/288b7b593f019b8fa448b659f385df7dcfb661fb) | docs: record the Wave 2 outcome and where the plan was wrong |
| 2026-08-31 | [`96a88cc`](https://github.com/a1865818/cordia-ai-booking-platform/commit/96a88cce0028802564099c0c735ed480a7e49798) | fix(api): close four review gaps in the Wave 2 engine fixes |
| 2026-08-31 | [`6e18f72`](https://github.com/a1865818/cordia-ai-booking-platform/commit/6e18f723571b6a5606b2879473f7acc1acb53671) | docs: research and plan the remaining waves |
| 2026-08-31 | [`f1ac7b5`](https://github.com/a1865818/cordia-ai-booking-platform/commit/f1ac7b57a6adc5c972434bbd5b87aebdb2c1c29f) | fix(api): require a proven identity for operator actions, not an asserted one |
| 2026-08-31 | [`d312390`](https://github.com/a1865818/cordia-ai-booking-platform/commit/d3123907ba3d30e02b75860e0edd848c3a59fde0) | fix(api): require a signed-in operator for console PII and money reads |
| 2026-09-15 | [`ace07a8`](https://github.com/a1865818/cordia-ai-booking-platform/commit/ace07a85ceedd4eb18dd8986bb6daeb782f71632) | fix(api): stamp the tenant once per scope and refuse to re-point it |
| 2026-09-15 | [`df2ace1`](https://github.com/a1865818/cordia-ai-booking-platform/commit/df2ace1be1d45b4761c195a7a5e6344829da4231) | fix(api): stop reconciliation lookups while the provider circuit is open |
| 2026-09-15 | [`9ac7b00`](https://github.com/a1865818/cordia-ai-booking-platform/commit/9ac7b0069256f4b7c02a0a5903fd7f867bba711f) | fix(api): refuse the committed verification pepper outside Development |
| 2026-09-15 | [`54d8ed2`](https://github.com/a1865818/cordia-ai-booking-platform/commit/54d8ed27eddaf98985b75a263a96d1cf9b73911d) | fix(api): refuse to start on committed credentials outside Development |
| 2026-09-15 | [`52656c8`](https://github.com/a1865818/cordia-ai-booking-platform/commit/52656c83a7557b0fa40ab0557c33b37b6a65d3a7) | docs(spec-07): record that slots are provisioned by the availability endpoint |
| 2026-09-15 | [`b18479a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/b18479a0b8334544481bd176e9b2ba840a39498c) | feat: show notification delivery state in the booking drawer |
| 2026-09-15 | [`601c48a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/601c48a1be69ad18abb93dc42d883b551003ba67) | fix(api): renew outbox leases while rows wait in a lane |
| 2026-09-15 | [`1982d44`](https://github.com/a1865818/cordia-ai-booking-platform/commit/1982d44e76f81ab2ca85aa62f0b51e51f7a88e4c) | fix: issue random verification codes and lock a challenge after five misses |
| 2026-09-15 | [`115751b`](https://github.com/a1865818/cordia-ai-booking-platform/commit/115751b4e589f2b2d1ea5713d1aa52a67ef35440) | feat(api): configure the capacity ladder rung per tenant and provider |
| 2026-09-15 | [`3d7e533`](https://github.com/a1865818/cordia-ai-booking-platform/commit/3d7e53384ad893fb5953d0b5ed9a06c1e3f15eda) | feat(api): name every stubbed port at startup and refuse impossible opt-outs |
| 2026-09-15 | [`7a02106`](https://github.com/a1865818/cordia-ai-booking-platform/commit/7a02106ed2566a8dbf06180f296c629da52e8efc) | fix(api): authenticate delivery receipts with an HMAC over the body |
| 2026-09-15 | [`2164f46`](https://github.com/a1865818/cordia-ai-booking-platform/commit/2164f460cd3cf098ca0312c197f988ffb33ca8ac) | fix(api): serialise the dispatch fence and cancel-before-create with a lock |
| 2026-09-15 | [`bbd3d2a`](https://github.com/a1865818/cordia-ai-booking-platform/commit/bbd3d2afeab69d719e6212c7f86c8f29fa167e8f) | perf(api): page the console scan by keyset over a supporting index |
| 2026-09-15 | [`e6b6033`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e6b6033468af3dbf34d3add688689888ede91533) | fix(api): verify receipts against the signed tenant header and a non-blank secret |
| 2026-09-15 | [`749f5e3`](https://github.com/a1865818/cordia-ai-booking-platform/commit/749f5e376b00ce1d9fdd542d22ec3263563edc4f) | fix(api): record a lookup-circuit failure when a reconcile lookup throws |
| 2026-09-15 | [`3244c7e`](https://github.com/a1865818/cordia-ai-booking-platform/commit/3244c7e0638303707c07f7ab5504d997bbcf750e) | fix(api): guard the remaining console reads and leave mutations to the audited behaviour |
| 2026-09-15 | [`3b0113f`](https://github.com/a1865818/cordia-ai-booking-platform/commit/3b0113f2c2de92030b3e8223e4c3911af79dfac8) | fix(api): keep a lane publishing when a lease renewal fails, and share one lock |
| 2026-09-15 | [`a8be659`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a8be6595d5b3eb243216b50bf0f2efca15322eca) | fix(api): list the notification adapters among the stubs named at startup |
| 2026-09-15 | [`b4d3a29`](https://github.com/a1865818/cordia-ai-booking-platform/commit/b4d3a2956edb13111a273402eca58ea559d35a3f) | fix(api): keep the newest notifications when the drawer read hits its cap |
| 2026-09-15 | [`d50f083`](https://github.com/a1865818/cordia-ai-booking-platform/commit/d50f083918ce677cc49500fbe3a291949a8e8278) | docs: record the xhigh review findings and their outcomes |
| 2026-09-15 | [`e9dc415`](https://github.com/a1865818/cordia-ai-booking-platform/commit/e9dc415d8599f5ab03b08df303dc528777d8cafa) | docs: accept the blocking index build for the undeployed Phase 15 migration |
| 2026-09-15 | [`a52b5c1`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a52b5c1f33fd005cfbc26ab9b73801a582c9318a) | docs: add the waves 3-5 PR description |
| 2026-09-17 | [`568b5b4`](https://github.com/a1865818/cordia-ai-booking-platform/commit/568b5b4e55850674f1612fd1234e92f0e8297daf) | docs: correct the phase count, webhook wire format, pepper wording and contract note |
| 2026-09-17 | [`71967d8`](https://github.com/a1865818/cordia-ai-booking-platform/commit/71967d8434ad7a7d53a3780c42af66a67881fe89) | fix(api): declare the 403 every operator-guarded console read can return |
| 2026-09-17 | [`dfc7aca`](https://github.com/a1865818/cordia-ai-booking-platform/commit/dfc7aca67cb02d4149d18d2c0d05e8bf84434c61) | fix(api): close four guard edge cases and align the waitlist read contract |
| 2026-09-17 | [`2bc5ddc`](https://github.com/a1865818/cordia-ai-booking-platform/commit/2bc5ddcd370a5f538179c7b57a81db1a6c540887) | fix(api): renew outbox leases on a bounded heartbeat, not before every row |
| 2026-09-17 | [`8f81ad7`](https://github.com/a1865818/cordia-ai-booking-platform/commit/8f81ad738e2d0e295550862de780da0753444d62) | fix(api): do not count our own cancellation as a lookup-circuit failure |
| 2026-09-17 | [`51ecced`](https://github.com/a1865818/cordia-ai-booking-platform/commit/51ecced07a5f000b83bd470bbdc3b1f81be2ebab) | perf(api): index booking_operations.booking_id for the notification drawer read |
| 2026-09-17 | [`a2f8d30`](https://github.com/a1865818/cordia-ai-booking-platform/commit/a2f8d30b8a0901876f554a3adcf5dff05ee3ce90) | fix(web): clear the query cache at sign-out and sign-in |
| 2026-09-17 | [`124a9da`](https://github.com/a1865818/cordia-ai-booking-platform/commit/124a9da76a894f58f01e1a43b1d1aed51a7ea416) | test(api): prove the verification code is hidden by default outside Development |
| 2026-09-17 | [`180d591`](https://github.com/a1865818/cordia-ai-booking-platform/commit/180d591cd63ffe8af64ba3755b97029cb62a1c45) | docs: record PR review round 2 and refresh the PR description |
