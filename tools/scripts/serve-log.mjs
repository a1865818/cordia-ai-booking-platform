#!/usr/bin/env node
/**
 * Runs a dev server and tees its output to a log file — cross-platform.
 *
 * Why this exists: the `serve-log` targets used to be
 * `mkdir -p ../../.tmp && <server> 2>&1 | tee ../../.tmp/<name>.log`, which is POSIX shell.
 * Nx's `run-commands` executes through `cmd.exe` on Windows, where `mkdir -p` is not a flag and
 * there is no `tee`, so all three targets died with "The syntax of the command is incorrect."
 * before any server started. Doing the redirection in Node removes the shell from the picture
 * entirely rather than maintaining two spellings of the same command.
 *
 * Usage:
 *   node tools/scripts/serve-log.mjs <log-name> <command> [...args]
 *
 * Output is written to `<repo>/.tmp/<log-name>-dev.log` **and** streamed to the terminal, so
 * `bun run dev:log` still shows the NX TUI while leaving a file to grep afterwards.
 */

import { spawn } from "node:child_process";
import { createWriteStream, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const [logName, command, ...args] = process.argv.slice(2);

if (!logName || !command) {
  console.error("usage: serve-log.mjs <log-name> <command> [...args]");
  process.exit(2);
}

// Resolved from this file, not from cwd: each target sets its own `cwd` to its app directory.
const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const logDir = resolve(repoRoot, ".tmp");
mkdirSync(logDir, { recursive: true });

const logPath = resolve(logDir, `${logName}-dev.log`);
const log = createWriteStream(logPath, { flags: "w" });

log.write(`=== ${logName}: ${command} ${args.join(" ")}\n`);

/**
 * Quotes one argument for `cmd.exe`.
 *
 * Needed because Windows has to go through a shell (see below) and Node's `spawn` merely
 * *concatenates* args in that mode — it does not escape them (DEP0190). Without this, any
 * argument containing a space silently splits into two, which is the kind of thing that works
 * on a developer's machine and breaks on a path like `C:\Program Files\...`.
 */
function quoteForCmd(arg) {
  if (!/[\s"^&|<>()%!]/.test(arg)) return arg;
  return `"${arg.replace(/"/g, '""')}"`;
}

const onWindows = process.platform === "win32";

// Windows must use a shell to resolve the `.cmd` shims that `bunx`, `uv` and `dotnet` install;
// passing a pre-joined string (rather than an args array) keeps the quoting ours and avoids
// DEP0190. POSIX needs no shell at all, so it gets the args array directly — no quoting, and no
// chance of a metacharacter being reinterpreted.
const child = onWindows
  ? spawn([command, ...args].map(quoteForCmd).join(" "), {
      shell: true,
      stdio: ["inherit", "pipe", "pipe"],
    })
  : spawn(command, args, { stdio: ["inherit", "pipe", "pipe"] });

/** Fan one stream out to the terminal and the log file. */
function tee(source, sink) {
  source.on("data", (chunk) => {
    sink.write(chunk);
    log.write(chunk);
  });
}

tee(child.stdout, process.stdout);
// stderr is teed to stdout deliberately — it is the `2>&1` the old command had, and it keeps
// the log a single ordered transcript rather than two interleaved-but-separate files.
tee(child.stderr, process.stdout);

// A dev server is normally ended by Ctrl-C. Forward it so the child shuts down cleanly instead
// of being orphaned, and let the child's own exit drive ours.
for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => {
    if (!child.killed) child.kill(signal);
  });
}

child.on("error", (error) => {
  const message = `serve-log: failed to start '${command}': ${error.message}\n`;
  process.stderr.write(message);
  log.write(message);
  log.end(() => process.exit(1));
});

child.on("close", (code, signal) => {
  log.end(() => {
    // Signal termination is the normal Ctrl-C path, not a failure.
    process.exit(signal ? 0 : (code ?? 0));
  });
});
