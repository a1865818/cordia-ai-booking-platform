# Bug report material: `bun test` hangs when a `mock.module()` factory `import()`s a
module that's also statically imported elsewhere in the file

Ready to paste into a GitHub issue against `oven-sh/bun`. Confirmed present on **1.3.14
and 1.4.0** (latest at time of writing) — not fixed between those versions. Not a
duplicate of the mocking issues already filed upstream (checked #10428, #10437, #12823,
#6751 — none describe this specific interaction).

## Summary

`bun test` hangs indefinitely (spins at ~100% CPU, no timeout recovers it) when a
`mock.module()` factory performs a dynamic `import()` of the same module specifier it is
mocking, **if and only if** a plain static `import` of that specifier also exists anywhere
else in the file. Neither condition alone triggers it. There is no real circular
dependency in the repro below — `moduleA.ts` has zero imports.

This breaks a completely standard pattern: partially mocking a module by importing its
real implementation (to spread most of it through unchanged) while overriding one or two
exports — vitest's built-in `vi.mock(path, async (importOriginal) => ({
...(await importOriginal()), someExport: vi.fn() }))` idiom, which is the only fully
general way to do this if `mock.module()` doesn't accept "just call the real module for
whatever I don't override" natively.

## Environment

- Bun 1.4.0 (also reproduced on 1.3.14) — `bun --version`
- macOS (Darwin), but nothing in the repro is platform-specific
- Plain `bun test`, no config file needed to reproduce

## Minimal repro

```
mkdir bun-mock-hang-repro && cd bun-mock-hang-repro
```

`moduleA.ts`:
```ts
export function a() { return "real-a"; }
```

`repro.test.ts`:
```ts
import { describe, it, expect, mock } from "bun:test";

mock.module("./moduleA", () => {
  return (async () => {
    const real = await import("./moduleA?actual"); // reentrant import of the mocked spec
    return { a: mock(() => "mocked-" + real.a()) };
  })();
});

import { a } from "./moduleA"; // <-- THIS static import is the other half of the trigger

describe("minimal", () => {
  it("test", () => {
    expect(a()).toBe("mocked-real-a");
  });
});
```

```
$ time timeout 6 bun test repro.test.ts
bun test v1.4.0 (34cbb9a40)
<hangs for the full 6s, then killed by timeout, 99% CPU throughout>
```

**Expected**: either resolves and the assertion runs, or fails fast with an error (e.g. a
circular-resolution error) — anything but an unbounded hang.

**Actual**: hangs indefinitely. `timeout 6 bun test repro.test.ts` exits 124 (timeout),
`user` CPU time in `time`'s output is ~equal to the wall clock — it's actively spinning,
not blocked/idle.

## Isolating the two necessary conditions

Each of these resolves instantly (confirmed, not assumed):

**A. Remove the static `import` — keep everything else identical** (query the module only
via dynamic `import()`, e.g. from inside a test body):
```ts
import { describe, it, expect, mock } from "bun:test";

mock.module("./moduleA", () => {
  return (async () => {
    const real = await import("./moduleA?actual");
    return { a: mock(() => "mocked-" + real.a()) };
  })();
});

describe("no static import anywhere", () => {
  it("resolves fine", async () => {
    const { a } = await import("./moduleA"); // dynamic, from the test body — fine
    expect(a()).toBe("mocked-real-a");
  });
});
```
→ passes in ~4ms.

**B. Keep the static import — remove the factory's internal `import()`** (capture the real
module as a snapshot *before* `mock.module()` is called, hand a plain object to a
synchronous factory instead):
```ts
import { describe, it, expect, mock } from "bun:test";

const realA = { ...(await import("./moduleA")) }; // plain top-level await, not inside a factory
mock.module("./moduleA", () => ({ a: mock(() => "mocked-" + realA.a()) }));

import { a } from "./moduleA";

describe("factory has no import() at all", () => {
  it("resolves fine", () => {
    expect(a()).toBe("mocked-real-a");
  });
});
```
→ passes in ~5ms.

Only the combination of *both* — a static import of `spec`, and `spec`'s
`mock.module()` factory itself calling `import()` (on `spec` or a query-suffixed variant of
it) — produces the hang. Registration *count* doesn't matter: a second, entirely unrelated
`mock.module()` call for a different module doesn't change the outcome either way (adding
one to case A or B above doesn't introduce or remove the hang).

## Why this matters in practice

Any test file that does both of these — which is normal, not exotic — hits this:
1. Statically imports a named export for some tests to exercise the mocked/overridden
   behavior (`import { thing } from "./module"`, then asserts against the mock).
2. Uses vitest's partial-mock idiom (`vi.mock(path, async (importOriginal) => ({
   ...(await importOriginal()), thing: vi.fn() }))`) — or the Bun-native equivalent, a
   `mock.module()` factory calling `import()` on its own spec — to keep every *other*
   export of that module real while overriding just `thing`.

We hit this migrating ~40 real test files from Vitest to `bun test` across two codebases;
it's the single largest correctness blocker in that migration, since the failure mode is a
silent CI hang, not a clean test failure.

## Suspected area

Given the two necessary conditions both center on "the same specifier resolved through two
different paths within one module's lifetime" (an already-hoisted static import binding,
and a factory-time dynamic `import()` — with or without a query-string variant), this
smells like a resolver-side re-entrancy issue: something in how Bun tracks "a resolution
for this specifier is already in flight for this file" doesn't account for the mock
registry lookup itself needing to resolve the same specifier again while the original
resolution (triggered by the hoisted static import) is still pending. No access to Bun's
internal resolver source was used to confirm this — treat as a hypothesis for whoever
picks this up, not a diagnosis.
