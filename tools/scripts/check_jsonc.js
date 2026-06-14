#!/usr/bin/env node
// Validates JSONC files (JSON with comments) passed as arguments.
// Used by the check-jsonc pre-commit hook.
const fs = require('fs')
const strip = require('strip-json-comments')

const files = process.argv.slice(2)
let ok = true

for (const f of files) {
  try {
    JSON.parse(strip(fs.readFileSync(f, 'utf8')))
  } catch (e) {
    console.error(`${f}: ${e.message}`)
    ok = false
  }
}

if (!ok) process.exit(1)
