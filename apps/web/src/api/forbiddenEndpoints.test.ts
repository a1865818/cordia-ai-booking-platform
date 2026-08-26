import { describe, expect, it } from "../test/vitest-compat";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { relative, resolve } from "node:path";

/**
 * Enforces `docs/frontend-api-wiring.md` §13-C — the six `apps/api` endpoints the console must
 * never call.
 *
 * Until this file, §13-C was enforced by **prose alone**: two doc comments, no lint rule, no CI
 * check. Nothing would have failed if a component started calling
 * `POST /api/conversation-sessions/{id}/consent` tomorrow. The list is not style guidance — most of
 * the six are trust boundaries:
 *
 * - **C1/C2** (`/api/channels/{messaging,telephony}`) *mint* a signed `TenantContext` from a
 *   transport-attested signal. The tenant is the endpoint's output, not its input; a browser
 *   calling one would issue itself a context for any tenant it named.
 * - **C3** (delivery receipts) is authenticated by `X-Webhook-Secret`. Calling it from the console
 *   means shipping that secret to the browser.
 * - **C4/C5** assert what only the guest or the channel may assert — transcript turns are evidence
 *   (Inv-1), and consent is the guest's own act (spec 14 §3.1).
 * - **C6** simply cannot carry a change payload; a reschedule goes through the acceptance front
 *   door instead.
 *
 * The check is textual on purpose. A stricter AST rule would be easy to satisfy accidentally (build
 * the URL from a variable), and this is a guardrail rather than a sandbox: its job is to make an
 * accidental wiring loud, and a deliberate one require deleting a test with this comment on it.
 */

/** Source root scanned by every case below. */
const WEB_SRC = resolve(__dirname, "..");

/**
 * Full-path patterns, anchored so neighbouring legitimate routes cannot trip them.
 *
 * The prefix collisions are the whole difficulty:
 * - `/api/notification-webhooks/delivery-receipts` (C3) shares `/api/notification` with
 *   `/api/notifications`, the delivery-state read issue #83 will add. A loose prefix would
 *   false-positive the day that lands, and the guard would be deleted as noise.
 * - `/api/conversation-sessions/{id}/consent` (C5) shares the word `consent` with
 *   `GET /api/consent-policies`, which Settings legitimately calls (`features/settings/api.ts`).
 *   That is a different resource — the venue's consent *policy*, not a guest's consent *state*.
 */
const FORBIDDEN: ReadonlyArray<{ id: string; label: string; pattern: RegExp }> = [
  { id: "C1", label: "POST /api/channels/messaging", pattern: /\/api\/channels\/messaging/ },
  { id: "C2", label: "POST /api/channels/telephony", pattern: /\/api\/channels\/telephony/ },
  {
    id: "C3",
    label: "POST /api/notification-webhooks/delivery-receipts",
    pattern: /\/api\/notification-webhooks\/delivery-receipts/,
  },
  {
    id: "C4",
    label: "POST /api/conversation-sessions/{id}/transcript-turns",
    pattern: /\/api\/conversation-sessions\/[^\s"'`]*\/transcript-turns/,
  },
  {
    id: "C5",
    label: "POST /api/conversation-sessions/{id}/consent",
    pattern: /\/api\/conversation-sessions\/[^\s"'`]*\/consent\b/,
  },
  {
    id: "C6",
    label: "POST /api/Bookings/{id}/modifications",
    pattern: /\/api\/Bookings\/[^\s"'`]*\/modifications/i,
  },
];

/**
 * Mentions that are known, reviewed and legitimate: doc comments explaining why the endpoint must
 * not be called, or a unit test that never reaches a server.
 *
 * Adding an entry is the deliberate act this guard exists to force. Do not widen it to a directory.
 */
const ALLOWED = new Map<string, string>([
  [
    "components/ui/ConsentBadge.tsx",
    "doc comment on a read-only badge, explaining why it has no setter",
  ],
  [
    "features/bookings/submit.ts",
    "doc comment explaining why reschedule uses the acceptance front door",
  ],
  ["api/client.test.ts", "error-parsing unit test against a mocked fetch; no server is reached"],
  ["api/forbiddenEndpoints.test.ts", "this guard"],
]);

const SOURCE_EXT = /\.(ts|tsx|js|jsx)$/;

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const full = resolve(dir, entry);
    if (statSync(full).isDirectory()) return walk(full);
    return SOURCE_EXT.test(entry) ? [full] : [];
  });
}

interface Hit {
  file: string;
  line: number;
  text: string;
}

function findHits(pattern: RegExp): Hit[] {
  const hits: Hit[] = [];

  for (const file of walk(WEB_SRC)) {
    const rel = relative(WEB_SRC, file).split("\\").join("/");
    if (ALLOWED.has(rel)) continue;

    readFileSync(file, "utf8")
      .split("\n")
      .forEach((text, i) => {
        if (pattern.test(text)) hits.push({ file: rel, line: i + 1, text: text.trim() });
      });
  }

  return hits;
}

describe("§13-C — endpoints the console must never call", () => {
  // A plain loop rather than `it.each`, and assertions that carry their own message rather than
  // using vitest's `expect(value, message)` second argument: this suite runs on bun:test (see
  // src/test/vitest-compat, which re-exports it), and neither of those vitest conveniences is
  // guaranteed there.
  for (const { id, label, pattern } of FORBIDDEN) {
    it(`${id} ${label} is not referenced outside the reviewed allowlist`, () => {
      const hits = findHits(pattern);

      const report =
        hits.length === 0
          ? ""
          : `§13-C ${id} (${label}) is referenced in apps/web/src:\n` +
            hits.map((h) => `  ${h.file}:${h.line}  ${h.text}`).join("\n") +
            "\n\nThis endpoint is not the console's to call — see docs/frontend-api-wiring.md " +
            "§13-C for why, and for the different endpoint that serves the legitimate adjacent " +
            "need. If this is a reviewed non-call (a comment, or a test that reaches no server), " +
            "add the file to ALLOWED in this test with a one-line reason.";

      expect(report).toBe("");
    });
  }

  // ── Guards on the guard ────────────────────────────────────────────────────────

  /**
   * C5 must not fire on `GET /api/consent-policies`, which Settings legitimately calls. Without
   * this, the first person to touch that file would "fix" the guard by deleting it.
   */
  it("does not mistake the consent-policy read for the guest-consent write", () => {
    const c5 = FORBIDDEN.find((f) => f.id === "C5")!;

    expect(c5.pattern.test('fetchApi("/api/consent-policies?channel=sms")')).toBe(false);
    expect(c5.pattern.test("fetchApi(`/api/conversation-sessions/${id}/consent`)")).toBe(true);
  });

  /**
   * C3 must not fire on `/api/notifications`, the delivery-state read issue #83 adds. Reading
   * delivery state is a different capability from asserting a receipt.
   */
  it("does not mistake the notifications read for the delivery-receipt webhook", () => {
    const c3 = FORBIDDEN.find((f) => f.id === "C3")!;

    expect(c3.pattern.test('fetchApi("/api/notifications?bookingId=b1")')).toBe(false);
    expect(c3.pattern.test('fetchApi("/api/notification-webhooks/delivery-receipts")')).toBe(true);
  });

  /** A guard whose patterns match nothing would pass forever. */
  it("actually matches a real call-site shape for every entry", () => {
    const samples: Record<string, string> = {
      C1: 'fetchApi("/api/channels/messaging", { method: "POST" })',
      C2: 'fetchApi("/api/channels/telephony", { method: "POST" })',
      C3: 'fetchApi("/api/notification-webhooks/delivery-receipts", { method: "POST" })',
      C4: "fetchApi(`/api/conversation-sessions/${id}/transcript-turns`)",
      C5: "fetchApi(`/api/conversation-sessions/${id}/consent`)",
      C6: "fetchApi(`/api/Bookings/${id}/modifications`)",
    };

    const unmatched = FORBIDDEN.filter((f) => !f.pattern.test(samples[f.id])).map((f) => f.id);

    expect(unmatched).toEqual([]);
  });
});
