/**
 * A minimal `vi`-shaped shim over `bun:test`'s jest-compatible primitives.
 *
 * This exists purely so the vitest -> bun:test migration is a one-line import swap
 * (see `./vitest-compat`) instead of touching every `vi.*` call site across the suite.
 * It is NOT a general-purpose vitest polyfill — it covers exactly the `vi.*` surface
 * this codebase actually uses.
 */
import { mock, jest, spyOn, type Mock } from "bun:test";

type Mocked<T> = T extends (...args: infer A) => infer R
  ? Mock<(...args: A) => R>
  : T extends object
    ? { [K in keyof T]: Mocked<T[K]> }
    : T;

/** Type-only cast, matching vitest's `vi.mocked` — the mock is real at runtime because
 * the module (or object) it came from was already replaced via `vi.mock`/`vi.spyOn`. */
function mocked<T>(item: T): Mocked<T> {
  return item as unknown as Mocked<T>;
}

const stubbedGlobals = new Map<PropertyKey, unknown>();

// happy-dom (unlike jsdom) defines some globals — `navigator` among them — as
// non-writable, so a plain assignment throws. `defineProperty` overrides them the same
// way regardless, and restoring the original descriptor on unstub puts them back exactly
// as they were (writable or not).
function stubGlobal(name: PropertyKey, value: unknown): void {
  if (!stubbedGlobals.has(name)) {
    stubbedGlobals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
  }
  Object.defineProperty(globalThis, name, {
    value,
    writable: true,
    configurable: true,
    enumerable: true,
  });
}

function unstubAllGlobals(): void {
  for (const [name, original] of stubbedGlobals) {
    if (original === undefined) {
      delete (globalThis as Record<PropertyKey, unknown>)[name as string];
    } else {
      Object.defineProperty(globalThis, name, original);
    }
  }
  stubbedGlobals.clear();
}

// Bun's `mock.module()` replaces the module registry immediately and retroactively —
// there's no vitest-style hoisting, but there's also no need for it: by the time any
// `it()` body runs, every top-level `vi.mock()` call in the file has already executed.
// A query-suffixed dynamic import bypasses whatever mock is registered for the bare
// specifier and resolves the real, unmocked module — safe to call from a test body.
//
// NOT safe to call from inside a `mock.module` factory, which is why `vi.mock()` here is
// a plain alias with no `importOriginal` support: `mock.module` mutates the target
// module's exports object in place, so a factory that imported "the real thing" itself
// would — the instant its own mock registered — recurse into itself and blow the stack.
// A query-suffixed import sidesteps that only when it's the file's lone `mock.module`
// call; with a second one anywhere in the file (this codebase always has several),
// calling `import()` from inside a factory deadlocks Bun 1.3's resolver entirely —
// confirmed empirically, not documented, and not worked around by deferring the capture
// to a microtask, a `beforeAll`, or an awaited async helper. The only shape that reliably
// avoids it is a literal `await import(...)` expression at true top-level module scope —
// so vitest's `vi.mock(path, async (importOriginal) => ({ ...(await importOriginal()), foo: vi.fn() }))`
// partial-mock idiom was rewritten, file by file, into:
//   const __actual_x = { ...(await import(path)) };
//   vi.mock(path, () => ({ ...__actual_x, foo: vi.fn() }));
// which needs nothing from this shim beyond the plain alias below.
async function importActual<T>(specifier: string): Promise<T> {
  return (await import(`${specifier}?vi-import-actual`)) as T;
}

export const vi = {
  fn: mock,
  mock: mock.module,
  mocked,
  spyOn,
  clearAllMocks: (): void => {
    jest.clearAllMocks();
  },
  restoreAllMocks: (): void => {
    jest.restoreAllMocks();
  },
  resetAllMocks: (): void => {
    jest.resetAllMocks();
  },
  useFakeTimers: (): void => {
    jest.useFakeTimers();
  },
  useRealTimers: (): void => {
    jest.useRealTimers();
  },
  advanceTimersByTime: (ms: number): void => {
    jest.advanceTimersByTime(ms);
  },
  stubGlobal,
  unstubAllGlobals,
  importActual,
};
