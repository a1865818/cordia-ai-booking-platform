/**
 * Drop-in replacement for `from "vitest"` when running under Bun's native test runner.
 *
 * Every test file in this suite was written against vitest's API. This module re-exports
 * `bun:test`'s equivalents under the same names, plus a `vi` shim (see `./vi`), so migrating
 * a file is exactly one edit: change the import specifier from "vitest" to this module.
 */
export * from "bun:test";
export { vi } from "./vi";
