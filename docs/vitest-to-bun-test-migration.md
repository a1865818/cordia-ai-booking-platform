# Migrating a Vite/React test suite from Vitest to `bun test`

Reference implementation: `apps/web` in this repo (PR #98, #99). This doc distills what
was actually required — not the happy-path pitch — so the same migration can be applied
to another Vite + Bun-as-package-manager repo without re-discovering each gotcha by hand.
Every claim below was verified empirically against Bun 1.3.14 and/or 1.4.0; version deltas
are called out explicitly.

**Required Bun version: >= 1.4.0.** `bun test --parallel` produced silent false failures on
1.3.14 (2 of 8 repeated runs, no error — just wrong pass/fail counts) and was clean across
23 consecutive runs on 1.4.0. Don't attempt this migration on an older Bun.

## 1. Why this isn't a drop-in

Three structural differences from Vitest, each with a concrete consequence:

1. **Bun's default test run shares ONE module registry across every file in the process.**
   Vitest isolates each test file (its own module graph per worker). Bun's bare `bun test`
   does not — a `mock.module()` call in file A can silently leak into file B if they mock
   the same specifier differently. This alone makes migration a correctness risk, not just
   a config swap. **Fix: always run with `--parallel`** (implies `--isolate`, fresh global
   object + separate worker process per file). Confirmed: `--isolate` alone is safe but
   single-process; `--parallel` is both safe and faster.

2. **`vi.mock(path, async (importOriginal) => ...)` — vitest's partial-mock idiom — hangs
   Bun's resolver.** Specifically: a `mock.module()` factory that itself calls `import()`
   deadlocks once a *second* `mock.module()` registration exists anywhere in the same file.
   Confirmed on both 1.3.14 and 1.4.0 — not fixed in 1.4, not documented upstream. See §4.

3. **No hoisting.** Vitest hoists every `vi.mock()` call to the top of the file at
   transform time, regardless of where it's written. Bun's `mock.module()` runs exactly
   where it's called, top-to-bottom. In practice this rarely matters (module-level
   `mock.module()` calls still all run before any `it()` body, since test execution is
   deferred past the whole module's synchronous evaluation) — except for the rewrite in
   §4, which depends on running *before* Bun would hoist it, and Vitest's hoisting is
   exactly what breaks that same rewrite if you ever need to fall back to Vitest (§6).

## 2. The shim: one-line import swap for (almost) every file

Don't hand-edit every `vi.*` call site. Build a `vi`-shaped module that re-exports
`bun:test`'s primitives under vitest's names, and swap one import line per file.

**`src/test/vi.ts`** — covers exactly the `vi.*` surface your suite actually uses, not a
general polyfill:

```ts
import { mock, jest, spyOn, type Mock } from "bun:test";

type Mocked<T> = T extends (...args: infer A) => infer R
  ? Mock<(...args: A) => R>
  : T extends object ? { [K in keyof T]: Mocked<T[K]> } : T;

function mocked<T>(item: T): Mocked<T> {
  return item as unknown as Mocked<T>; // type-only cast, matching vitest's vi.mocked
}

const stubbedGlobals = new Map<PropertyKey, PropertyDescriptor | undefined>();

// happy-dom (unlike jsdom) defines some globals (`navigator`) as non-writable — a plain
// assignment throws. defineProperty overrides regardless; restoring the saved descriptor
// on unstub puts it back exactly as it was (writable or not).
function stubGlobal(name: PropertyKey, value: unknown): void {
  if (!stubbedGlobals.has(name)) {
    stubbedGlobals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
  }
  Object.defineProperty(globalThis, name, { value, writable: true, configurable: true, enumerable: true });
}

function unstubAllGlobals(): void {
  for (const [name, original] of stubbedGlobals) {
    if (original === undefined) delete (globalThis as Record<PropertyKey, unknown>)[name as string];
    else Object.defineProperty(globalThis, name, original);
  }
  stubbedGlobals.clear();
}

// Safe to call from a TEST BODY. NOT safe to call from inside a mock.module factory — see §4.
async function importActual<T>(specifier: string): Promise<T> {
  return (await import(`${specifier}?vi-import-actual`)) as T;
}

export const vi = {
  fn: mock,
  mock: mock.module, // plain alias — no importOriginal support, see §4
  mocked,
  spyOn,
  clearAllMocks: () => jest.clearAllMocks(),
  restoreAllMocks: () => jest.restoreAllMocks(),
  resetAllMocks: () => jest.resetAllMocks(),
  useFakeTimers: () => jest.useFakeTimers(),
  useRealTimers: () => jest.useRealTimers(),
  advanceTimersByTime: (ms: number) => jest.advanceTimersByTime(ms),
  stubGlobal,
  unstubAllGlobals,
  importActual,
};
```

**`src/test/vitest-compat.ts`** — the actual swap target:

```ts
export * from "bun:test";
export { vi } from "./vi";
```

Then in every test file: `from "vitest"` → `from "<relative path>/test/vitest-compat"`.
Nothing else changes for files that only use `describe`/`it`/`expect`/`beforeEach`/
`afterEach`/`vi.fn`/`vi.spyOn`/`vi.stubGlobal`/`vi.mocked` — which in a typical suite is
most of them. Files using vitest's `importOriginal` mocking idiom need the rewrite in §4.

## 3. The preload: DOM, matchers, and per-test cleanup

Bun has no DOM by default. `bunfig.toml`:

```toml
[test]
preload = ["./src/test-setup.bun.ts"]
```

**`src/test-setup.bun.ts`** — order matters more than it looks like it should:

```ts
// happy-dom registration MUST be the only *static* `import` in this file. ESM hoists
// every import declaration to run before a file's own top-level statements, regardless of
// where it's written. @testing-library/dom's `screen` singleton binds to `document` at ITS
// OWN import time — if it's pulled in (even transitively, via jest-dom or RTL) before
// register() runs, `document` doesn't exist yet, and `screen`'s query methods are
// permanently replaced with stubs that always throw. Every other import below is dynamic
// (`await import(...)`), which — unlike a static import — runs exactly where it's written.
import { GlobalRegistrator } from "@happy-dom/global-registrator";

const MIN_BUN_VERSION = ">=1.4.0"; // engines.bun in package.json is NOT enforced by Bun —
if (!Bun.semver.satisfies(Bun.version, MIN_BUN_VERSION)) {                // confirmed empirically,
  throw new Error(`This suite requires Bun ${MIN_BUN_VERSION} (found ${Bun.version}).`); // so check it here too.
}

GlobalRegistrator.register();

// require(), not `await import(...)`: jest-dom's types/index.d.ts is a pure ambient
// side-effect file with no real exports (see §5's jest-dom fix) — import()'s type needs an
// actual module shape to resolve; require()'s `any` return sidesteps that. Still runs
// synchronously exactly here, after register(), same guarantee as an awaited import.
require("@testing-library/jest-dom");
const { afterEach } = await import("bun:test");
const { cleanup } = await import("@testing-library/react");

// @testing-library/react normally self-registers this cleanup via an ambient `afterEach`
// at import time — but Bun shares ONE module cache across every file in the run, so that
// side effect only fires once, for whichever file happens to import RTL first. Every other
// file's renders leak into the next test's DOM otherwise. Registering it here, in the
// preload, makes it a real per-test root hook for the whole run.
afterEach(() => { cleanup(); });
```

Symptom if you skip the explicit `afterEach(cleanup)`: running a single test file in
isolation passes; running the full suite produces `getByText` "multiple elements match"
failures on tests that look unrelated to each other, because earlier files' unmounted-but-
never-cleaned-up DOM piles up in the shared `document.body`.

Symptom if `GlobalRegistrator.register()` and any RTL/jest-dom-touching import end up in
the same file mixed with regular statements (not carefully ordered per the comment above):
`TypeError: For queries bound to document.body a global document has to be available` on
literally the first test in the file that runs into it, even though `document` genuinely
exists by the time the test runs — because `@testing-library/dom`'s `screen` object baked
in its "no document" fallback at import time and never re-checks.

## 4. The actual Bun bug: `mock.module()` + `import()` deadlock

**Symptom**: `bun test` hangs indefinitely (100% CPU, no timeout helps) on a file using
vitest's `vi.mock(path, async (importOriginal) => ({ ...(await importOriginal()), foo:
vi.fn() }))` partial-mock idiom — the common case, since most files mock more than one
module and often need the real implementation for part of it.

**Precise trigger** (confirmed by minimal repro — see the filing-ready version in
`docs/bun-mock-module-import-deadlock-repro.md`): a `mock.module(spec, factory)` where
`factory` itself performs a dynamic `import()` of `spec` (including the query-suffix trick
that otherwise safely bypasses an individual mock when called from a test body) hangs Bun's
resolver **if and only if there is also a plain static `import` of that same `spec`
anywhere else in the file**. Registration count is a red herring — earlier investigation
attributed this to "2+ `mock.module()` calls in the file," which was a confound: the actual
minimal repro is a *single* module, a *single* `mock.module()` call, and one static
`import` statement elsewhere in the same file. Remove either the static import or the
factory-internal `import()` and it resolves instantly; keep both and it hangs every time,
regardless of registration count. This is a real Bun defect (a hang is never correct
behavior for valid, non-circular code), not a design smell in the target code.

**The fix** — capture the real module as a plain top-level `await import(...)` *before*
any `mock.module()` call, never from inside a factory:

```ts
// Before (vitest idiom — hangs Bun with 2+ mock.module() calls in the file):
vi.mock("./api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./api")>()),
  someFn: vi.fn(),
}));

// After (works on Bun, top-level await, plain script code — not inside any factory):
const __actual_api = { ...(await import("./api")) };
vi.mock("./api", () => ({ ...__actual_api, someFn: vi.fn() }));
```

Constraints that matter and were each independently verified — deviating from any one of
them reintroduces the hang or a silent correctness bug:

- **Must be a literal `{ ...(await import(path)) }` shallow copy**, not a bare reference.
  `mock.module()` mutates the target module's exports object *in place*; a live reference
  captured before the override runs would get mutated too, so `__actual_api.someFn()`
  inside the factory would recurse into the mock and blow the stack.
- **Must be genuine top-level-await syntax**, not a call to an `async` helper function that
  is itself awaited, and not deferred via `queueMicrotask`/`beforeAll`. All three of those
  alternatives were tried and all three still hang; only an inline `await import(...)`
  expression at true module scope reliably avoids the deadlock. (Cause not fully root-
  caused — treat as an empirical constraint, not a theory.)
- **The rewritten `vi.mock` factory must be synchronous** — no `importOriginal` parameter
  needed once the snapshot is pre-captured, so the shim's `vi.mock` can stay a plain
  `mock.module` alias with no special-casing.

Find every file needing this rewrite with:
```
grep -rl "vi\.mock(.*async.*importOriginal\|await vi\.importActual" src
```
In this repo that was 5 files out of 46. Scale this number before committing to the
migration on a larger suite — it was 36 files (plus the shared setup file) out of 559 in a
sibling repo evaluated for the same migration, a proportionally much bigger manual lift.

`vi.importActual(path)` called directly from a **test body** (not from inside a
`mock.module` factory) is unaffected by this bug and needs no rewrite — the query-suffix
trick in §2's shim works fine there, confirmed even with several other `mock.module()`
calls present in the same file.

## 5. TypeScript, jest-dom, and other loose ends

- **Install `@types/bun`.** Without it, `bun:test` has no type declarations and every file
  importing from your shim fails typecheck with `has no exported member 'describe'` etc.
  This is easy to miss if you validate the migration by running tests but never run
  `tsc`/typecheck afterward — do both.
- **TypeScript 6 needs `"types"` set explicitly** in `tsconfig.json`:
  `"types": ["bun", "react", "react-dom"]` (or your framework's equivalents). TypeScript
  5.8 auto-discovers everything under `node_modules/@types` with no `"types"` option set;
  TypeScript 6 was observed to stop doing that for `@types/bun` specifically. If you're on
  TS 5.x you likely don't need this yet, but it costs nothing to add proactively.
- **jest-dom's `bun:test` matcher augmentation isn't a public export.** `@testing-library/
  jest-dom` ships `types/bun.d.ts` (declaring `expect(...).toBeInTheDocument()` etc. on
  `bun:test`'s `Matchers` interface), but its `package.json` `exports` map only exposes
  `.`, `./jest-globals`, `./matchers`, `./vitest` — not `./bun`. A plain `import
  "@testing-library/jest-dom"` only pulls in the Jest-namespace augmentation. Fix: a small
  local `.d.ts` file with a triple-slash path reference, which is a raw filesystem include
  and isn't subject to the `exports` restriction the way a module specifier is:
  ```ts
  /// <reference path="../../node_modules/@testing-library/jest-dom/types/bun.d.ts" />
  ```
  (Adjust the relative path to wherever this file lives relative to `node_modules`.)
- **`import.meta.env.BASE_URL` and friends are Vite-injected build-time constants** — bun
  test has no bundler pass, so any code reading them needs the values supplied another way.
  Bun auto-loads `.env.test` for `bun test`; put `BASE_URL=/` (or whatever your app needs)
  there rather than mutating `process.env` at runtime in the preload — the latter doesn't
  reliably survive `--isolate`'s fresh-global-per-file semantics, confirmed by testing both.
- **`engines.bun` in `package.json` is decorative.** Verified empirically: `bun install`
  and `bun run` neither warn nor fail on an unmet `engines.bun` constraint. Set it anyway
  for tooling that does read it (Renovate, GitHub's dependency graph), but pair it with the
  runtime `Bun.semver.satisfies(...)` guard in §3's preload if you actually want a failure.
- **nx (or any build-cache) inputs**: if a preload/config file lives outside the directories
  your cache's default file-set already tracks (e.g. `bunfig.toml`, `.env.test` sitting at
  the project root rather than under `src/`), add them to the test target's `inputs`
  explicitly. Verified the gap is real: editing `bunfig.toml` alone, without touching
  anything nx's default input glob covers, replayed a stale cached test result.
- **Coverage**: `bun test --coverage` needs `bunfig.toml` keys at the flat `[test]` level,
  not nested — `coverageReporter = ["text", "lcov"]`, `coverageDir = "coverage"`,
  `coveragePathIgnorePatterns = [...]`. Test files (`*.test.ts(x)`) are excluded from the
  report automatically; your own test-support files and any generated/vendored source are
  not — list them in `coveragePathIgnorePatterns` explicitly.

## 6. What does NOT come along for free

- **No working Vitest fallback.** If you keep a `vitest run` script "just in case," verify
  it actually works — don't assume. In this repo it didn't: the shim's `export * from
  "bun:test"` makes every file fail immediately under Vite/Vitest (`bun:test` is a
  Bun-runtime-only virtual module; Vite externalizes the unresolvable specifier and errors
  on first access), and even a fixed shim would hit a second wall — vitest's `vi.mock()`
  hoisting is fundamentally incompatible with §4's top-level-await rewrite (vitest hoists
  the mock call above the `const __actual_x = await import(...)` line it depends on,
  producing a TDZ `ReferenceError`). Building a genuine dual-runtime shim is a real,
  separate effort — budget for it explicitly or drop the fallback claim from your docs.
- **No `vitest --changed`-equivalent.** If your CI plans dynamic test shards from a
  git-diff-based affected-file list (`vitest list --changed <ref>`), there is nothing
  built into `bun test` that replicates it. Either keep vitest around solely for CI
  planning (defeats a lot of the point) or build custom changed-file detection.
- **No per-process heap-size flag.** Vitest/Node exposes `execArgv: ["--max-old-space-
  size=..."]` for forks that need more headroom under a large jsdom suite. Bun uses
  JavaScriptCore, not V8 — there's no equivalent knob, and no data point yet on how Bun's
  memory behavior compares at scale. If your current suite is sharded specifically to avoid
  OOM (not just for speed), measure Bun's memory footprint on a representative slice before
  trusting it in CI, rather than assuming it inherits V8's headroom characteristics.
- **jsdom → happy-dom is not guaranteed transparent** for suites depending on jsdom-specific
  polyfills (canvas `getContext`, Web Worker mocking for embedded editors, etc.). Cordia's
  suite didn't exercise any of these; check yours before assuming a clean swap.

## 7. Migration checklist

1. Confirm Bun >= 1.4.0 (`bun --version`); upgrade with `bun upgrade` if not.
2. Add `src/test/vi.ts`, `src/test/vitest-compat.ts` (§2), `src/test-setup.bun.ts`,
   `bunfig.toml` (§3).
3. Codemod every test file's `from "vitest"` → `from "<relative>/test/vitest-compat"`
   (mechanical, one-line-per-file; a script over `find ... -name "*.test.ts*"` computing
   relative depth is enough).
4. `grep -rl "vi\.mock(.*async.*importOriginal\|await vi\.importActual" src` — rewrite each
   hit per §4. Also check any shared setup file for the same pattern.
5. Install `@types/bun`; fix typecheck (§5) — don't skip validating this separately from
   running tests.
6. `bun test --parallel` (never bare `bun test` — see §1.1). Run it 10-20+ times in a row
   before trusting it; flakiness here has historically been silent (wrong pass/fail counts,
   no error) and version-dependent.
7. Wire into your build tool as `bun test --parallel` (add `--coverage` for a coverage
   target, per §5's bunfig keys). Add `bunfig.toml`/`.env.test` to any build-cache's input
   tracking for that target.
8. Decide explicitly on §6's gaps (Vitest fallback, `--changed`-equivalent, memory ceiling)
   rather than silently inheriting Cordia's answers — they were sized to a 45-file suite.
