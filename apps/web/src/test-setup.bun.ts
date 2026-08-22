// Bun's test runner has no DOM by default — register happy-dom's globals (document,
// window, etc.) before anything else in the suite touches them. This is vitest's
// `environment: "jsdom"` equivalent for `bun test`.
//
// This MUST be the only static `import` in this file: ESM hoists every `import`
// declaration to execute before a file's own top-level statements, regardless of where
// it's written relative to plain code. `@testing-library/dom`'s `screen` singleton binds
// to `document` at its own import time — if it were pulled in (even transitively, via
// jest-dom or React Testing Library) before `register()` runs, `document` wouldn't exist
// yet and `screen`'s query methods would be permanently replaced with stubs that always
// throw, no matter what happens afterwards. Every other module here is loaded with a
// dynamic `import()` instead, which — unlike `import` declarations — runs exactly where
// it's written, so `register()` above it is guaranteed to happen first.
import { GlobalRegistrator } from "@happy-dom/global-registrator";

GlobalRegistrator.register();

// `require`, not `await import(...)`: jest-dom's own types (types/index.d.ts) are a pure
// ambient side-effect file with no real exports — see src/test/jest-dom-bun.d.ts, which is
// how its `bun:test` matcher augmentation actually gets wired in — and `import()`'s type
// needs an actual module shape to resolve, which `require()`'s `any` return sidesteps.
// Still runs synchronously exactly here, after `register()`, same as the awaited imports.
require("@testing-library/jest-dom");
const { afterEach } = await import("bun:test");
const { cleanup } = await import("@testing-library/react");

// @testing-library/react normally self-registers this via an ambient `afterEach` at
// import time — but Bun shares one module cache across every test file in the run, so
// that side effect only fires once, for whichever file happens to import it first. Every
// other file's renders would leak into the shared `document.body` for the rest of the
// run. Registering it here, in the preload script, makes it a real per-test root hook.
afterEach(() => {
  cleanup();
});

afterEach(() => {
  sessionStorage.removeItem("cordia.operator_session");
});

// happy-dom implements no layout, so `scrollIntoView` is simply absent — any component
// that scrolls a ref into view (the conversation thread does on every new message) throws
// in a test although it works in a browser. Stubbed once here rather than in each test.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = function scrollIntoView() {
    /* no layout in happy-dom — nothing to scroll */
  };
}
