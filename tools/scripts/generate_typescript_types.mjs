#!/usr/bin/env node
/**
 * Generate TypeScript types from JSON Schema files using quicktype.
 *
 * This script reads JSON Schema files from libs/shared-schemas/schemas/
 * and generates TypeScript interfaces in libs/shared-schemas/generated/typescript/index.ts
 *
 * Usage:
 *   node generate_typescript_types.mjs
 *
 * Prerequisites:
 *   - quicktype must be installed: npm install quicktype
 */

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { main as quicktypeMain } from "quicktype";

// Paths relative to repository root
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, "../..");
const SCHEMAS_DIR = path.join(REPO_ROOT, "libs/shared-schemas/schemas");
const OUTPUT_DIR = path.join(REPO_ROOT, "libs/shared-schemas/generated/typescript");
const OUTPUT_FILE = path.join(OUTPUT_DIR, "index.ts");
const ZOD_OUTPUT_FILE = path.join(OUTPUT_DIR, "zod.ts");

/**
 * Recursively find all JSON files in a directory.
 * @param {string} dir - Directory to search
 * @returns {string[]} Array of JSON file paths
 */
function findJsonFiles(dir) {
  const files = [];

  if (!fs.existsSync(dir)) {
    console.warn(`Directory not found: ${dir}`);
    return files;
  }

  const entries = fs.readdirSync(dir, { withFileTypes: true });

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...findJsonFiles(fullPath));
    } else if (entry.isFile() && entry.name.endsWith(".json")) {
      files.push(fullPath);
    }
  }

  return files;
}

/**
 * Recursively collect the `title` of every object node that declares
 * `additionalProperties: false` across the given JSON Schema files.
 *
 * quicktype derives a generated type's name from its schema `title`, so these
 * titles map 1:1 onto the emitted `export const <title>Schema` consts. We use
 * them to re-apply closed-object semantics that quicktype's `typescript-zod`
 * target drops: it emits plain `z.object({…})`, which in Zod 3 *strips* unknown
 * keys and still succeeds — so `additionalProperties: false` would otherwise not
 * be enforced at web runtime. See {@link applyStrictToClosedSchemas}.
 *
 * @param {string[]} absoluteSchemaFiles - Absolute paths to every schema JSON
 *   file (including shared-def files, whose closed `$defs` should also be caught).
 * @returns {Set<string>} Titles of closed object schemas.
 */
function collectClosedSchemaTitles(absoluteSchemaFiles) {
  const titles = new Set();

  const walk = (node) => {
    if (Array.isArray(node)) {
      for (const item of node) walk(item);
      return;
    }
    if (node && typeof node === "object") {
      if (node.additionalProperties === false && typeof node.title === "string") {
        titles.add(node.title);
      }
      for (const value of Object.values(node)) walk(value);
    }
  };

  for (const file of absoluteSchemaFiles) {
    try {
      walk(JSON.parse(fs.readFileSync(file, "utf8")));
    } catch (err) {
      console.warn(`Warning: could not parse ${file} for closed-schema detection: ${err.message}`);
    }
  }

  return titles;
}

/**
 * Append `.strict()` to the generated Zod object schemas whose source JSON
 * Schema declared `additionalProperties: false`, so closed contracts are
 * actually enforced at web runtime (quicktype's `typescript-zod` emits plain
 * `z.object({…})`, which silently strips unknown keys instead of rejecting them).
 *
 * Only `z.object(...)` consts are touched — `z.enum(...)` consts have no strict
 * concept. Enums and open (`additionalProperties` absent) schemas are left as
 * plain `z.object`, matching their permissive JSON Schema semantics.
 *
 * @param {string} content - The (alias-stripped) zod.ts source.
 * @param {Set<string>} closedTitles - Titles of closed object schemas.
 * @returns {{content: string, applied: number, unmatched: string[]}}
 */
function applyStrictToClosedSchemas(content, closedTitles) {
  let applied = 0;
  const unmatched = [];

  for (const title of closedTitles) {
    const escaped = title.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    // Match the full `export const <title>Schema = z.object({ … });` block,
    // non-greedily up to the first line-leading `});`, and insert `.strict()`.
    // Stopping at the FIRST line-leading `})` is safe because quicktype hoists
    // every nested object type into its own top-level `export const` — emitted
    // z.object blocks are always flat, with inner lines indented, so a
    // line-leading `})` only ever occurs at the end of the block.
    const re = new RegExp(
      String.raw`(export const ${escaped}Schema = z\.object\(\{[\s\S]*?\n\}\))(;)`,
      "m"
    );
    const next = content.replace(re, "$1.strict()$2");
    if (next !== content) {
      applied += 1;
      content = next;
    } else {
      unmatched.push(title);
    }
  }

  return { content, applied, unmatched };
}

/**
 * Replace quicktype's `Date` interface type (emitted in the `--just-types`
 * `index.ts` for every `format: date-time` / `date` / `time` field) with
 * `string`, so the canonical TS types match both the JSON wire (datetimes are
 * strings — there is no reviver) AND the Zod schemas, which validate/return a
 * `string` via `z.string().datetime(...)` (see {@link relaxCoercedDates}).
 *
 * Without this, `index.ts` would declare `timestamp: Date` while `zod.ts`
 * returns `string` — two disagreeing generated contracts for the same schema.
 *
 * Only type positions are touched (`: Date`, `: Date[]`, `: Date | null`) via the
 * `:`-anchored match, so JSDoc prose and property names containing "Date" (e.g.
 * `createdDate`) are left intact.
 *
 * @param {string} content - The index.ts source.
 * @returns {{content: string, applied: number}}
 */
function convertDateTypesToString(content) {
  const re = /(:\s*)Date\b/g;
  const applied = (content.match(re) || []).length;
  return { content: content.replace(re, "$1string"), applied };
}

/**
 * Replace quicktype's coercive `z.coerce.date()` (emitted for every
 * `format: date-time` field) with a strict, non-coercive
 * `z.string().datetime({ offset: true })`.
 *
 * Two problems with the default `z.coerce.date()` for *wire* validation:
 *   1. It coerces — `z.coerce.date()` runs `new Date(input)`, so non-string
 *      inputs like `0` or `1700000000000` (numbers) PASS, and a parsed payload's
 *      datetime is silently transformed from the wire string into a `Date`.
 *      That defeats the closed-contract intent: a malformed frame with a numeric
 *      timestamp would be accepted instead of dropped by `useJobStream`.
 *   2. `z.string().datetime({ offset: true })` instead requires a string, accepts
 *      both `Z` and numeric offsets (e.g. `+00:00`) with arbitrary sub-second
 *      precision — matching the API's STJ wire output — and keeps the value a
 *      string, so the validated payload mirrors the wire exactly.
 *
 * Applied to every composition quicktype emits (`z.coerce.date()`,
 * `z.coerce.date().optional()`, `z.union([z.coerce.date(), z.null()])`) via a
 * plain substring swap of the base expression.
 *
 * @param {string} content - The zod.ts source.
 * @returns {{content: string, applied: number}}
 */
function relaxCoercedDates(content) {
  const needle = "z.coerce.date()";
  const replacement = "z.string().datetime({ offset: true })";
  const applied = content.split(needle).length - 1;
  return { content: content.split(needle).join(replacement), applied };
}

/**
 * Generate Zod schemas (`*Schema` consts) from JSON Schema files into a
 * separate `zod.ts` module, alongside the `index.ts` interfaces/enums.
 *
 * Why a separate module rather than concatenating into `index.ts`:
 * quicktype's `typescript-zod` target emits BOTH `export const XSchema = z.object({…})`
 * AND `export type X = z.infer<typeof XSchema>`. The `--just-types` output in
 * `index.ts` already declares `export interface X` / `export enum X` for the same
 * names, so merging the two files would produce duplicate identifiers and a TS
 * compile error. Instead we keep `index.ts` byte-identical (the canonical types
 * stay the interfaces/enums) and emit only the `*Schema` consts here, stripping
 * the duplicate `z.infer` type aliases.
 *
 * Must be called with cwd already chdir'd into SCHEMAS_DIR (for $ref resolution),
 * mirroring the `index.ts` generation in generateTypeScriptTypes().
 *
 * @param {string[]} relativeSchemaFiles - Schema paths relative to SCHEMAS_DIR
 *   (already alphabetically sorted for deterministic naming).
 */
async function generateZodSchemas(relativeSchemaFiles) {
  console.log("\nGenerating Zod schemas (zod.ts)...");

  const relativeZodOutputFile = path.relative(SCHEMAS_DIR, ZOD_OUTPUT_FILE).split(path.sep).join("/");

  const quicktypeArgs = [
    "--lang", "typescript-zod",
    "--src-lang", "schema",
    "--out", relativeZodOutputFile,
    ...relativeSchemaFiles,
  ];

  await quicktypeMain(quicktypeArgs);

  // Strip the duplicate `export type X = z.infer<typeof XSchema>;` aliases.
  // The canonical TS types remain the `index.ts` interfaces/enums; keeping the
  // z.infer aliases here would collide with them when both modules are imported.
  let content = fs.readFileSync(ZOD_OUTPUT_FILE, "utf8");
  content = content.replace(/^export type \w+ = z\.infer<typeof \w+>;\n/gm, "");

  // Replace coercive `z.coerce.date()` with strict `z.string().datetime({offset:true})`
  // so date-time wire fields are validated, not coerced (numbers rejected, value
  // kept as the wire string). See {@link relaxCoercedDates}.
  const { content: dateContent, applied: datesRelaxed } = relaxCoercedDates(content);
  content = dateContent;
  console.log(`Relaxed ${datesRelaxed} z.coerce.date() → z.string().datetime({ offset: true }).`);

  // Re-apply closed-object semantics that `typescript-zod` drops: append
  // `.strict()` to schemas whose source JSON Schema set `additionalProperties: false`.
  // Scan every schema file (including shared defs) so closed nested $defs are caught.
  const closedTitles = collectClosedSchemaTitles(findJsonFiles(SCHEMAS_DIR));
  const { content: strictContent, applied, unmatched } = applyStrictToClosedSchemas(content, closedTitles);
  content = strictContent;
  console.log(
    `Applied .strict() to ${applied} closed object schema(s) ` +
      `(${closedTitles.size} closed title(s) detected).`
  );
  if (unmatched.length > 0) {
    console.warn(
      `Warning: .strict() NOT applied to ${unmatched.length} closed schema title(s) ` +
        `with no matching z.object const in zod.ts: ${unmatched.join(", ")}. ` +
        `Each closed schema's "title" must match quicktype's generated type name, ` +
        `otherwise its closed contract is not enforced at web runtime.`
    );
  }

  const header = `// <auto-generated>
// This file was generated by quicktype from JSON Schema files.
// Do not modify this file directly - changes will be overwritten.
//
// Zod runtime-validation schemas (the *Schema consts) for the shared types.
// The canonical TypeScript types live in ./index.ts; the z.infer aliases that
// quicktype emits here are stripped to avoid duplicate-identifier collisions.
//
// Source schemas: libs/shared-schemas/schemas/
// </auto-generated>

`;

  fs.writeFileSync(ZOD_OUTPUT_FILE, header + content);
  console.log(`Zod schemas generated successfully!`);
  console.log(`Output: ${path.relative(REPO_ROOT, ZOD_OUTPUT_FILE)}`);
}

/**
 * Generate TypeScript types from JSON Schema files.
 */
async function generateTypeScriptTypes() {
  console.log("Generating TypeScript types from JSON schemas...\n");

  // Find all JSON schema files
  const allSchemaFiles = findJsonFiles(SCHEMAS_DIR);

  // Exclude shared/PydanticSharedDefs.json from root inputs.
  // quicktype will follow $ref pointers to this file automatically when
  // processing wrapper schemas. Including it as a root would cause duplicate
  // type generation since it only contains $defs (not a root schema).
  const SHARED_DEFS_FILE = path.join(SCHEMAS_DIR, "shared", "PydanticSharedDefs.json");
  const schemaFiles = allSchemaFiles
    .filter((f) => f !== SHARED_DEFS_FILE)
    // Sort alphabetically for deterministic quicktype type naming.
    // Without sorting, filesystem ordering varies across platforms and can
    // change which file "wins" for collision-variant naming.
    .sort((a, b) => a.localeCompare(b, "en"));

  if (schemaFiles.length === 0) {
    console.error("No JSON schema files found in:", SCHEMAS_DIR);
    console.error(
      "\nPlease run 'nx run langgraph:generate-schemas' first."
    );
    process.exit(1);
  }

  console.log(`Found ${schemaFiles.length} root schema files (excluding shared defs):`);
  schemaFiles.forEach((f) => console.log(`  - ${path.relative(REPO_ROOT, f)}`));

  // Ensure output directory exists
  if (!fs.existsSync(OUTPUT_DIR)) {
    fs.mkdirSync(OUTPUT_DIR, { recursive: true });
    console.log(`\nCreated output directory: ${OUTPUT_DIR}`);
  }

  // Use paths relative to SCHEMAS_DIR for $ref resolution.
  // Normalise to forward slashes so that quicktype's URI-based $ref resolver
  // works correctly on Windows (path.sep produces backslashes on Win32).
  const toForwardSlash = (p) => p.split(path.sep).join("/");
  const relativeSchemaFiles = schemaFiles.map((f) => toForwardSlash(path.relative(SCHEMAS_DIR, f)));
  const relativeOutputFile = toForwardSlash(path.relative(SCHEMAS_DIR, OUTPUT_FILE));

  const quicktypeArgs = [
    "--lang", "typescript",
    "--src-lang", "schema",
    "--just-types",
    "--out", relativeOutputFile,
    ...relativeSchemaFiles,
  ];

  console.log("\nRunning quicktype...");

  const originalCwd = process.cwd();
  process.chdir(SCHEMAS_DIR);

  try {
    await quicktypeMain(quicktypeArgs);

    console.log(`\nTypeScript types generated successfully!`);
    console.log(`Output: ${path.relative(REPO_ROOT, OUTPUT_FILE)}`);

    // Align date-time fields with the wire + Zod: represent them as `string`,
    // not quicktype's `Date` (the JSON wire has no reviver; zod.ts returns string).
    let content = fs.readFileSync(OUTPUT_FILE, "utf8");
    const { content: dateContent, applied: datesConverted } = convertDateTypesToString(content);
    content = dateContent;
    console.log(`Converted ${datesConverted} Date interface field(s) → string.`);

    const header = `// <auto-generated>
// This file was generated by quicktype from JSON Schema files.
// Do not modify this file directly - changes will be overwritten.
//
// Source schemas: libs/shared-schemas/schemas/
// </auto-generated>

`;

    fs.writeFileSync(OUTPUT_FILE, header + content);
    console.log("Added auto-generated header to output file.");

    // Emit Zod schemas into a separate module (see generateZodSchemas docs).
    await generateZodSchemas(relativeSchemaFiles);
  } catch (error) {
    console.error("\nError running quicktype:", error.message);
    process.exit(1);
  } finally {
    process.chdir(originalCwd);
  }
}

// Main execution
await generateTypeScriptTypes();
