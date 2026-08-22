/**
 * Wires `@testing-library/jest-dom`'s matchers onto `bun:test`'s `expect`.
 *
 * jest-dom ships this augmentation (`types/bun.d.ts`) but doesn't expose it as a public
 * subpath export — only `.`, `./jest-globals`, `./matchers`, and `./vitest` are in its
 * package.json `exports` map, and none of those targets `bun:test`. Its own test fixture
 * only gets away with a plain `import '../../bun'` because that's a same-package relative
 * import, which package `exports` maps don't govern. We're an external consumer, so we
 * reach the same file the same way `@testing-library/jest-dom/types/index.d.ts` reaches
 * its sibling `jest.d.ts`: a triple-slash path reference, which is a raw filesystem
 * include and — unlike a module specifier — isn't subject to `exports` restrictions at all.
 */
/// <reference path="../../node_modules/@testing-library/jest-dom/types/bun.d.ts" />
