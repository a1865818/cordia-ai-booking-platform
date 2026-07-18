#!/usr/bin/env bash
# Committed EF migrations are immutable (docs/dotnet-api-patterns.md: "never
# edit applied ones"). A silent rewrite of a migration that databases have
# already run desyncs schema history irrecoverably — and it CAN happen without
# anyone intending it (2026-07-11: an IDE-side rewrite truncated a column name
# inside a committed Designer file; caught only by manual diff review).
#
# This hook fails the commit when any staged change MODIFIES, DELETES, or
# RENAMES a file under the migrations directory that already exists in HEAD.
# Allowed: ADDING a new migration, and updating ApplicationDbContextModelSnapshot.cs
# (the snapshot legitimately evolves with every added migration).
#
# Intentional exception (e.g. removing a committed-but-never-deployed
# migration): SKIP=check-migrations-immutable git commit ...
set -euo pipefail

MIGRATIONS_DIR='apps/api/src/Cordia.Infrastructure/EntityFramework/Migrations'
SNAPSHOT="$MIGRATIONS_DIR/ApplicationDbContextModelSnapshot.cs"

# name-status lines: "M\tpath", "D\tpath", or "R100\told\tnew" — flag the line
# unless every path field on it is the model snapshot.
violations=$(git diff --cached --name-status --diff-filter=MDR -- "$MIGRATIONS_DIR" \
  | awk -F'\t' -v snap="$SNAPSHOT" '{ keep = 0; for (i = 2; i <= NF; i++) if ($i != snap) keep = 1; if (keep) print }')

if [ -n "$violations" ]; then
  {
    echo 'ERROR: committed EF migrations are immutable, but this commit changes:'
    echo "$violations"
    echo
    echo "Undo with:    git restore --staged --worktree -- '$MIGRATIONS_DIR/<file>'"
    echo 'If intentional: SKIP=check-migrations-immutable git commit ...'
  } >&2
  exit 1
fi
