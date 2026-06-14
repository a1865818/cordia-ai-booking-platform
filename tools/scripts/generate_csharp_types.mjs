#!/usr/bin/env node
/**
 * Generate C# types from JSON Schema files using quicktype.
 *
 * Reads JSON Schema files from libs/shared-schemas/schemas/ and generates C#
 * classes in libs/shared-schemas/generated/csharp/SharedTypes.cs.
 *
 * Usage:
 *   node generate_csharp_types.mjs
 *
 * Prerequisites:
 *   - quicktype must be installed (workspace devDependency).
 */

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { main as quicktypeMain } from "quicktype";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, "../..");
const SCHEMAS_DIR = path.join(REPO_ROOT, "libs/shared-schemas/schemas");
const OUTPUT_DIR = path.join(REPO_ROOT, "libs/shared-schemas/generated/csharp");
const OUTPUT_FILE = path.join(OUTPUT_DIR, "SharedTypes.cs");
const ENUM_MAPPINGS_FILE = path.join(REPO_ROOT, "tools/config/ef-enum-mappings.json");

const NAMESPACE = "Cordia.Domain.Generated";

/** Recursively find all JSON files in a directory. */
function findJsonFiles(dir) {
  const files = [];
  if (!fs.existsSync(dir)) {
    console.warn(`Directory not found: ${dir}`);
    return files;
  }
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
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
 * Post-process the generated C# file to add EF Core attributes to
 * database-mapped enums declared in ef-enum-mappings.json:
 *   1. Inject `using NpgsqlTypes;`.
 *   2. Expand each target enum from quicktype's single-line declaration to a
 *      multi-line form with [JsonConverter], [JsonStringEnumMemberName], and
 *      [PgName] attributes on every member.
 */
function postProcessEnums() {
  const config = JSON.parse(fs.readFileSync(ENUM_MAPPINGS_FILE, "utf8"));
  if (!config.enums || Object.keys(config.enums).length === 0) {
    console.log("\nNo EF enum mappings configured; skipping enum post-processing.");
    return;
  }

  console.log("\nPost-processing enums for EF Core...");
  let content = fs.readFileSync(OUTPUT_FILE, "utf8");

  if (!content.includes("using NpgsqlTypes;")) {
    content = content.replace(
      "    using System.Globalization;",
      "    using System.Globalization;\n    using NpgsqlTypes;"
    );
  }

  for (const [enumName, enumConfig] of Object.entries(config.enums)) {
    const singleLineRegex = new RegExp(
      String.raw`([ \t]*)public enum ${enumName}\s*\{([^}]+)\};?`,
      "m"
    );
    const match = content.match(singleLineRegex);
    if (!match) {
      console.warn(`Warning: Could not find enum ${enumName} in generated file.`);
      continue;
    }

    const indent = match[1];
    const innerIndent = indent + "    ";
    const members = match[2]
      .split(",")
      .map((m) => m.trim())
      .filter((m) => m.length > 0);

    const memberLines = members
      .map((member, i) => {
        const pgValue = enumConfig.members[member];
        if (!pgValue) {
          throw new Error(`No mapping found for member '${member}' in enum '${enumName}'`);
        }
        const comma = i < members.length - 1 ? "," : "";
        return [
          `${innerIndent}[JsonStringEnumMemberName("${pgValue}")]`,
          `${innerIndent}[PgName("${pgValue}")]`,
          `${innerIndent}${member}${comma}`,
        ].join("\n");
      })
      .join("\n\n");

    const expanded = [
      `${indent}[JsonConverter(typeof(JsonStringEnumConverter))]`,
      `${indent}public enum ${enumName}`,
      `${indent}{`,
      memberLines,
      `${indent}}`,
    ].join("\n");

    const idx = content.indexOf(match[0]);
    if (idx !== -1) {
      content =
        content.substring(0, idx) + expanded + content.substring(idx + match[0].length);
    }
  }

  fs.writeFileSync(OUTPUT_FILE, content);
  console.log("EF Core enum attributes added successfully.");
}

/**
 * Change the generated Converter class from internal to public so the API can
 * use Converter.Settings for JSON serialization options.
 */
function postProcessConverterVisibility() {
  let content = fs.readFileSync(OUTPUT_FILE, "utf8");
  const target = "internal static class Converter";
  const replacement = "public static class Converter";

  const occurrences = content.split(target).length - 1;
  if (occurrences === 0) {
    console.warn("Converter class not found; skipping visibility post-processing.");
    return;
  }
  if (occurrences > 1) {
    throw new Error(
      `Post-processing failed: '${target}' found ${occurrences} times (expected 1).`
    );
  }

  content = content.replace(target, replacement);
  fs.writeFileSync(OUTPUT_FILE, content);
  console.log("Converter class changed from internal to public.");
}

async function generateCSharpTypes() {
  console.log("Generating C# types from JSON schemas...\n");

  const allSchemaFiles = findJsonFiles(SCHEMAS_DIR);
  const SHARED_DEFS_FILE = path.join(SCHEMAS_DIR, "shared", "PydanticSharedDefs.json");
  const schemaFiles = allSchemaFiles
    .filter((f) => f !== SHARED_DEFS_FILE)
    .sort((a, b) => a.localeCompare(b, "en"));

  if (schemaFiles.length === 0) {
    console.error("No JSON schema files found in:", SCHEMAS_DIR);
    console.error("\nPlease run 'bun nx run langgraph:generate-schemas' first.");
    process.exit(1);
  }

  console.log(`Found ${schemaFiles.length} root schema files (excluding shared defs):`);
  schemaFiles.forEach((f) => console.log(`  - ${path.relative(REPO_ROOT, f)}`));

  if (!fs.existsSync(OUTPUT_DIR)) {
    fs.mkdirSync(OUTPUT_DIR, { recursive: true });
    console.log(`\nCreated output directory: ${OUTPUT_DIR}`);
  }

  const toForwardSlash = (p) => p.split(path.sep).join("/");
  const relativeSchemaFiles = schemaFiles.map((f) => toForwardSlash(path.relative(SCHEMAS_DIR, f)));
  const relativeOutputFile = toForwardSlash(path.relative(SCHEMAS_DIR, OUTPUT_FILE));

  const quicktypeArgs = [
    "--lang", "csharp",
    "--src-lang", "schema",
    "--framework", "SystemTextJson",
    "--namespace", NAMESPACE,
    "--array-type", "list",
    "--any-type", "object",
    "--check-required",
    "--out", relativeOutputFile,
    ...relativeSchemaFiles,
  ];

  console.log("\nRunning quicktype...");
  const originalCwd = process.cwd();
  process.chdir(SCHEMAS_DIR);

  try {
    await quicktypeMain(quicktypeArgs);
    console.log(`\nC# types generated successfully!`);
    console.log(`Output: ${path.relative(REPO_ROOT, OUTPUT_FILE)}`);

    postProcessEnums();
    postProcessConverterVisibility();

    const content = fs.readFileSync(OUTPUT_FILE, "utf8");
    const header = `// <auto-generated>
// This file was generated by quicktype from JSON Schema files.
// Do not modify this file directly - changes will be overwritten.
//
// Source schemas: libs/shared-schemas/schemas/
// </auto-generated>

`;
    fs.writeFileSync(OUTPUT_FILE, header + content);
    console.log("Added auto-generated header to output file.");
  } catch (error) {
    console.error("\nError running quicktype:", error.message);
    process.exit(1);
  } finally {
    process.chdir(originalCwd);
  }
}

await generateCSharpTypes();
