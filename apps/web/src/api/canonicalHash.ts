/**
 * `canon.v1` canonical request hasher — TypeScript port (spec 04 §5).
 *
 * This is a **mirror**, not a second algorithm. The authoritative implementation is
 * `apps/api/src/Cordia.Infrastructure/Services/CanonicalRequestHasher.cs`; both are pinned
 * to `tools/config/canon-v1-test-vectors.json` (see `canonicalHash.test.ts`).
 *
 * Why the client needs it at all: `POST /api/bookings` requires the caller to supply
 * `revision.payload_hash` and `confirmation.intent_payload_hash`, and the server recomputes
 * the hash and rejects a mismatch with 422 `ConfirmationHashMismatchException`. A drifted
 * client cannot create a booking at all.
 *
 * Algorithm (applied recursively):
 *   Object  — drop null-valued properties and properties whose canonical value is an empty
 *             object or empty array; NFC-normalise keys; sort keys by UTF-16 code unit.
 *   String  — NFC, trim, collapse internal whitespace runs to one space, preserve case.
 *             ISO-8601 date-times **with an offset** are converted to UTC and re-emitted as
 *             `yyyy-MM-ddTHH:mm:ss.fffZ`, truncated (floored) to milliseconds — never rounded.
 *   Number  — 500, 500.0 and 5e2 all canonicalise to `500`; fractions use the shortest
 *             round-trip form with no exponent and no trailing zeros.
 *   Array   — canonicalise every element, then sort: arrays of objects by `item_id`, then
 *             `modifier_id`, then the element's full canonical JSON; other arrays by that
 *             canonical JSON. Nulls are **kept** in arrays (only object properties drop).
 *   Encode  — compact JSON, then SHA-256 → lowercase hex.
 *
 * Two details are easy to get wrong and are load-bearing:
 *   1. The JSON encoding is .NET's default `JavaScriptEncoder`, which escapes every
 *      non-ASCII code unit and the HTML-sensitive ASCII set as `\uXXXX` with UPPERCASE hex.
 *      `JSON.stringify` does not — using it would break every accented customer name.
 *   2. The whitespace class is .NET's `char.IsWhiteSpace`, which includes U+0085 (NEL) and
 *      excludes U+FEFF. JavaScript's `\s` is the opposite on both counts.
 */

export const CANON_VERSION = "canon.v1";

export interface CanonicalHash {
  version: typeof CANON_VERSION;
  /** SHA-256 of the canonical encoding, lowercase hex (64 chars). */
  value: string;
}

// ---------------------------------------------------------------------------
// Canonicalisation
// ---------------------------------------------------------------------------

interface CanonNode {
  /** Compact canonical JSON text for this node. */
  text: string;
  isObject: boolean;
  /** Present on objects: the values used to order arrays of objects. */
  itemId: string;
  modifierId: string;
}

const OMIT = Symbol("omit");
type Canonicalized = CanonNode | typeof OMIT;

/**
 * Exactly .NET's `char.IsWhiteSpace`: U+0009-U+000D, U+0020, U+0085, and the Zs/Zl/Zp
 * categories. Deliberately NOT JavaScript's whitespace class, which omits U+0085 and
 * adds U+FEFF - the two code points where the runtimes disagree.
 */
const WHITESPACE_CLASS =
  "[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]";
const WHITESPACE_RUN = new RegExp(`${WHITESPACE_CLASS}+`, "g");
const WHITESPACE_EDGES = new RegExp(`^${WHITESPACE_CLASS}+|${WHITESPACE_CLASS}+$`, "g");

/** ASCII characters .NET's default JavaScriptEncoder escapes even though they are printable. */
const ESCAPED_ASCII = new Set(['"', "&", "'", "+", "<", ">", "`"]);

/**
 * Control characters System.Text.Json writes as two-character escapes rather than \uXXXX.
 *
 * Verified by running the real encoder on net10.0: BS/TAB/LF/FF/CR come out as \b \t \n \f
 * \r, while VT () and other control characters stay \uXXXX. Emitting  where the
 * server emits \b yields a different SHA-256, and the submission is rejected with 422.
 *
 * Only backspace can reach here from a value — `canonicalizeString` collapses whitespace and
 * `char.IsWhiteSpace('')` is false — but object KEYS are not whitespace-collapsed, so
 * all five matter.
 */
const SHORT_ESCAPES = new Map<number, string>([
  [0x08, "\\b"],
  [0x09, "\\t"],
  [0x0a, "\\n"],
  [0x0c, "\\f"],
  [0x0d, "\\r"],
]);

/** Compares by UTF-16 code unit, matching .NET's `StringComparison.Ordinal`. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function encodeString(value: string): string {
  let out = '"';
  for (const unit of value) {
    // Iterating a string yields whole code points; surrogate pairs must be emitted as two
    // \uXXXX escapes, matching how .NET encodes them per UTF-16 code unit.
    const code = unit.codePointAt(0)!;

    if (unit === "\\") {
      out += "\\\\";
    } else if (SHORT_ESCAPES.has(code)) {
      out += SHORT_ESCAPES.get(code)!;
    } else if (code >= 0x20 && code <= 0x7e && !ESCAPED_ASCII.has(unit)) {
      out += unit;
    } else if (code > 0xffff) {
      for (let i = 0; i < unit.length; i++) {
        out += `\\u${unit.charCodeAt(i).toString(16).toUpperCase().padStart(4, "0")}`;
      }
    } else {
      out += `\\u${code.toString(16).toUpperCase().padStart(4, "0")}`;
    }
  }
  return out + '"';
}

/**
 * ISO-8601 date-time carrying an offset. Month and day accept one or two digits because
 * .NET's `DateTimeOffset.TryParse` does.
 *
 * The date/time separator is **uppercase `T` only**. That is not a typo: the C# gate is
 * `raw.IndexOf('T', StringComparison.Ordinal)`, so a lowercase `t` never reaches
 * `TryParse` and the value stays a plain string. Accepting `t` here would normalise a
 * string the server does not, producing a different hash.
 *
 * Likewise, hour/minute/second must be **two digits**: .NET tolerates a missing leading
 * zero on month and day but not on the time components, so `T5:30:00Z` is a plain string
 * to the server and must be one here too.
 */
const ISO_WITH_OFFSET =
  /^(\d{4})-(\d{1,2})-(\d{1,2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?(?:([Zz])|([+-])(\d{2})(?::?([0-5]\d))?)$/;

/** .NET's DateTimeOffset offset range is +/-14:00 inclusive; beyond that TryParse fails. */
const MAX_OFFSET_MINUTES = 14 * 60;

function tryNormalizeTimestamp(raw: string): string | undefined {
  // Cheap pre-filter mirroring the C# one exactly: an uppercase 'T' and an offset marker
  // after it. Lowercase 't' is deliberately not accepted — see ISO_WITH_OFFSET.
  const tIndex = raw.indexOf("T");
  if (tIndex < 0) return undefined;
  if (!/[Zz+-]/.test(raw.slice(tIndex + 1))) return undefined;

  // Matched against the TRIMMED value: `DateTimeOffset.TryParse` tolerates leading and
  // trailing whitespace (verified on net10.0), so " 2026-06-15T18:00:00Z" is a timestamp to
  // the server. An anchored match on the raw string would miss it, fall through to the
  // general string path, and emit a different canonical value — a 422 on submit.
  const match = ISO_WITH_OFFSET.exec(raw.trim());
  if (!match) return undefined;

  const [, y, mo, d, h, mi, s, frac, , sign, offH, offM] = match;

  // The regex's two-digit offset groups have no upper bound, but .NET rejects anything past
  // +/-14:00. Accepting "+15:00" here would normalise to UTC while the server kept the raw
  // string, so the two hashes would differ.
  if (sign) {
    const offsetMinutes = Number(offH) * 60 + Number(offM ?? 0);
    if (offsetMinutes > MAX_OFFSET_MINUTES) return undefined;
  }

  const year = Number(y);
  const month = Number(mo);
  const day = Number(d);
  const hour = Number(h);
  const minute = Number(mi);
  const second = Number(s ?? "0");

  // Truncate (floor) sub-millisecond precision. Rounding could cross a second boundary and
  // silently merge two distinct instants.
  const millis = frac ? Number((frac + "000").slice(0, 3)) : 0;

  // `Date.UTC` maps years 0-99 to 1900+year, so build the date explicitly:
  // `setUTCFullYear` has no such remapping.
  const local = new Date(0);
  local.setUTCFullYear(year, month - 1, day);
  local.setUTCHours(hour, minute, second, millis);

  // `Date` overflows impossible dates (Feb 30 becomes Mar 2, hour 24 becomes the next
  // day). `DateTimeOffset.TryParse` rejects them, leaving the value as a plain string —
  // so anything that did not round-trip is not a timestamp here either.
  if (
    local.getUTCFullYear() !== year ||
    local.getUTCMonth() !== month - 1 ||
    local.getUTCDate() !== day ||
    local.getUTCHours() !== hour ||
    local.getUTCMinutes() !== minute ||
    local.getUTCSeconds() !== second
  ) {
    return undefined;
  }

  const offsetMinutes =
    sign === undefined ? 0 : (Number(offH) * 60 + Number(offM ?? "0")) * (sign === "-" ? -1 : 1);

  const utcMs = local.getTime() - offsetMinutes * 60_000;
  if (!Number.isFinite(utcMs)) return undefined;

  // Applying the offset can carry the instant outside `DateTimeOffset`'s range (year 1 to
  // 9999). .NET's TryParse fails there, leaving the value a plain string, so anything that
  // lands outside must not be normalised here either — JS `Date` would happily represent
  // year 0 and produce a hash the server never agrees with.
  const utc = new Date(utcMs);
  const utcYear = utc.getUTCFullYear();
  if (utcYear < 1 || utcYear > 9999) return undefined;

  const iso = utc.toISOString();
  // toISOString emits exactly yyyy-MM-ddTHH:mm:ss.fffZ for four-digit years; anything else
  // (expanded-year form) is outside the contract and stays a plain string.
  return iso.length === 24 ? iso : undefined;
}

function canonicalizeString(raw: string): string {
  const timestamp = tryNormalizeTimestamp(raw);
  if (timestamp !== undefined) return timestamp;

  return raw
    .normalize("NFC")
    .replace(WHITESPACE_EDGES, "")
    .replace(WHITESPACE_RUN, " ");
}

/** Expands exponent notation so the canonical form never contains `e`. */
function withoutExponent(text: string): string {
  const match = /^(-?)(\d+)(?:\.(\d+))?[eE]([+-]?\d+)$/.exec(text);
  if (!match) return text;

  const [, sign, intPart, fracPart = "", expText] = match;
  const exponent = Number(expText);
  const digits = intPart + fracPart;
  const pointIndex = intPart.length + exponent;

  if (pointIndex <= 0) {
    return `${sign}0.${"0".repeat(-pointIndex)}${digits}`.replace(/0+$/, "").replace(/\.$/, "");
  }
  if (pointIndex >= digits.length) {
    return sign + digits + "0".repeat(pointIndex - digits.length);
  }
  const result = `${sign}${digits.slice(0, pointIndex)}.${digits.slice(pointIndex)}`;
  return result.replace(/0+$/, "").replace(/\.$/, "");
}

function canonicalizeNumber(value: number): string {
  if (!Number.isFinite(value)) {
    // Not producible by JSON.parse; treated defensively rather than emitting `null`.
    return "0";
  }

  if (Number.isInteger(value)) {
    // `Object.is(-0, 0)` is false, but the canonical integer form of -0 is "0" (BigInteger
    // has no negative zero), so normalise before formatting.
    const normalized = value === 0 ? 0 : value;
    return Math.abs(normalized) >= 1e21
      ? BigInt(normalized).toString()
      : String(normalized);
  }

  return withoutExponent(String(value));
}

function makeNode(text: string): CanonNode {
  return { text, isObject: false, itemId: "", modifierId: "" };
}

/**
 * Reads a natural-key field from an already-canonicalised object the way the C# comparer
 * does: a string yields its value, anything else yields its canonical JSON text, and a
 * missing key yields the empty string.
 */
function naturalKeyOf(entries: Map<string, CanonNode>, key: string, rawValues: Map<string, string | undefined>): string {
  const node = entries.get(key);
  if (node === undefined) return "";
  const raw = rawValues.get(key);
  return raw !== undefined ? raw : node.text;
}

function canonicalize(value: unknown): Canonicalized {
  if (value === null || value === undefined) return OMIT;

  if (typeof value === "boolean") return makeNode(value ? "true" : "false");
  if (typeof value === "number") return makeNode(canonicalizeNumber(value));
  if (typeof value === "string") return makeNode(encodeString(canonicalizeString(value)));

  if (Array.isArray(value)) return canonicalizeArray(value);
  if (typeof value === "object") return canonicalizeObject(value as Record<string, unknown>);

  // Functions / symbols cannot appear in parsed JSON; drop them rather than guess.
  return OMIT;
}

/** Array elements keep JSON `null` — only object properties drop it. */
function canonicalizeElement(value: unknown): CanonNode {
  const canonical = canonicalize(value);
  return canonical === OMIT ? makeNode("null") : canonical;
}

function canonicalizeObject(source: Record<string, unknown>): CanonNode {
  const entries = new Map<string, CanonNode>();
  /** Canonicalised *unescaped* string values, needed for natural-key comparison. */
  const rawStrings = new Map<string, string | undefined>();

  for (const key of Object.keys(source)) {
    const raw = source[key];
    const canonical = canonicalize(raw);
    if (canonical === OMIT) continue;
    if (canonical.text === "{}" || canonical.text === "[]") continue;

    const normalizedKey = key.normalize("NFC");

    // Two distinct keys can normalise to the same one. The server's `JsonObject.Add`
    // throws on that, so silently keeping the last would produce a hash the server can
    // never agree with — fail here, where the message is actionable.
    if (entries.has(normalizedKey)) {
      throw new Error(
        `canon.v1: object has two keys that normalise to "${normalizedKey}"; ` +
          "the payload cannot be canonicalised unambiguously."
      );
    }

    entries.set(normalizedKey, canonical);
    rawStrings.set(normalizedKey, typeof raw === "string" ? canonicalizeString(raw) : undefined);
  }

  const sortedKeys = [...entries.keys()].sort(ordinalCompare);
  const text = `{${sortedKeys.map((k) => `${encodeString(k)}:${entries.get(k)!.text}`).join(",")}}`;

  return {
    text,
    isObject: true,
    itemId: naturalKeyOf(entries, "item_id", rawStrings),
    modifierId: naturalKeyOf(entries, "modifier_id", rawStrings),
  };
}

function canonicalizeArray(source: unknown[]): CanonNode {
  const items = source.map(canonicalizeElement);

  const allObjects = items.length > 0 && items.every((i) => i.isObject);

  items.sort(
    allObjects
      ? (a, b) =>
          ordinalCompare(a.itemId, b.itemId) ||
          ordinalCompare(a.modifierId, b.modifierId) ||
          ordinalCompare(a.text, b.text)
      : (a, b) => ordinalCompare(a.text, b.text)
  );

  return makeNode(`[${items.map((i) => i.text).join(",")}]`);
}

/**
 * The canonical JSON encoding of a payload — exported for tests and debugging so a hash
 * mismatch can be diagnosed by comparing text rather than digests.
 */
export function canonicalJson(payload: unknown): string {
  if (payload === null || payload === undefined) return "{}";
  const canonical = canonicalize(payload);
  return canonical === OMIT ? "{}" : canonical.text;
}

// ---------------------------------------------------------------------------
// Hashing
// ---------------------------------------------------------------------------

function toHex(buffer: ArrayBuffer): string {
  return [...new Uint8Array(buffer)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * Computes the `canon.v1` hash of a payload.
 *
 * Uses Web Crypto, which requires a secure context — `localhost` and any https origin
 * qualify, so dev and production both work.
 */
export async function canonicalHash(payload: unknown): Promise<CanonicalHash> {
  const json = canonicalJson(payload);
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(json));
  return { version: CANON_VERSION, value: toHex(digest) };
}
