import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { CANON_VERSION, canonicalHash, canonicalJson } from "./canonicalHash";

/**
 * Shared cross-language canon.v1 corpus (spec 04 §5).
 *
 * The expected hashes were produced by `CanonicalRequestHasher.cs`, which is the
 * authoritative implementation, and are asserted on the C# side by
 * `CanonV1GoldenVectorTests.cs` and on the Python side by
 * `tests/unit_tests/test_canonical_hash.py`. If this port drifts, this suite fails —
 * which matters because a drifted client is rejected by the acceptance endpoint with a
 * 422 and cannot create a booking at all.
 */
interface Vector {
  name: string;
  equivalence: string;
  /** RAW JSON text — number forms and Unicode normalisation only survive as text. */
  payload: string;
  expected: string;
}

const corpus = JSON.parse(
  readFileSync(resolve(__dirname, "../../../../tools/config/canon-v1-test-vectors.json"), "utf8")
) as { version: string; vectors: Vector[] };

describe("canon.v1 shared corpus", () => {
  it("loads the corpus", () => {
    expect(corpus.version).toBe(CANON_VERSION);
    expect(corpus.vectors.length).toBeGreaterThan(40);
  });

  it.each(corpus.vectors.map((v) => [v.name, v.payload, v.expected] as const))(
    "%s",
    async (_name, payload, expected) => {
      const { version, value } = await canonicalHash(JSON.parse(payload));
      expect(version).toBe(CANON_VERSION);
      expect(value).toBe(expected);
    }
  );

  it("agrees within every equivalence group", async () => {
    const groups = new Map<string, { name: string; hash: string }>();

    for (const vector of corpus.vectors) {
      const { value } = await canonicalHash(JSON.parse(vector.payload));
      const first = groups.get(vector.equivalence);
      if (first) {
        expect(
          value,
          `equivalence '${vector.equivalence}': ${vector.name} vs ${first.name}`
        ).toBe(first.hash);
      } else {
        groups.set(vector.equivalence, { name: vector.name, hash: value });
      }
    }

    expect(groups.size).toBeGreaterThan(0);
  });
});

describe("canonicalJson", () => {
  it("sorts object keys by code unit and drops nulls", () => {
    expect(canonicalJson({ z: 1, a: 2, m: null })).toBe('{"a":2,"z":1}');
  });

  it("drops empty objects and arrays, recursively", () => {
    expect(canonicalJson({ a: 1, b: {}, c: [], d: { e: [] } })).toBe('{"a":1}');
  });

  it("keeps nulls inside arrays", () => {
    expect(canonicalJson({ a: [null, 1] })).toBe('{"a":[1,null]}');
  });

  it("escapes non-ASCII as uppercase \\uXXXX, unlike JSON.stringify", () => {
    expect(canonicalJson({ name: "café" })).toBe('{"name":"caf\\u00E9"}');
    expect(JSON.stringify({ name: "café" })).not.toBe(canonicalJson({ name: "café" }));
  });

  it("escapes the HTML-sensitive ASCII set the way .NET does", () => {
    expect(canonicalJson({ s: "a<b>c&d'e+f\"g`h" })).toBe(
      '{"s":"a\\u003Cb\\u003Ec\\u0026d\\u0027e\\u002Bf\\u0022g\\u0060h"}'
    );
  });

  it("escapes a backslash as \\\\ rather than \\u005C", () => {
    expect(canonicalJson({ s: "a\\b" })).toBe('{"s":"a\\\\b"}');
  });

  it("emits surrogate pairs as two escapes", () => {
    expect(canonicalJson({ s: "😀" })).toBe('{"s":"\\uD83D\\uDE00"}');
  });

  it("normalises NFC and collapses whitespace but preserves case", () => {
    expect(canonicalJson({ s: "  Window   Seat  " })).toBe('{"s":"Window Seat"}');
  });

  it("treats U+0085 as whitespace and U+FEFF as not, matching .NET", () => {
    expect(canonicalJson({ s: "xy" })).toBe('{"s":"x y"}');
    expect(canonicalJson({ s: "x﻿y" })).toBe('{"s":"x\\uFEFFy"}');
  });

  it("canonicalises integer forms identically", () => {
    expect(canonicalJson({ n: 500 })).toBe('{"n":500}');
    expect(canonicalJson({ n: 500.0 })).toBe('{"n":500}');
    expect(canonicalJson({ n: 5e2 })).toBe('{"n":500}');
    expect(canonicalJson({ n: -0 })).toBe('{"n":0}');
  });

  it("emits fractions without an exponent", () => {
    expect(canonicalJson({ n: 1e-7 })).toBe('{"n":0.0000001}');
    expect(canonicalJson({ n: 500.5 })).toBe('{"n":500.5}');
    expect(canonicalJson({ n: -12.25 })).toBe('{"n":-12.25}');
  });

  it("emits large integers without an exponent", () => {
    expect(canonicalJson({ n: 1e21 })).toBe('{"n":1000000000000000000000}');
  });

  it("normalises a timestamp offset to UTC with millisecond truncation", () => {
    expect(canonicalJson({ t: "2026-08-01T20:30:00+02:00" })).toBe(
      '{"t":"2026-08-01T18:30:00.000Z"}'
    );
    expect(canonicalJson({ t: "2026-08-01T18:30:59.9995Z" })).toBe(
      '{"t":"2026-08-01T18:30:59.999Z"}'
    );
  });

  it("leaves TriState markers and bare dates as plain strings", () => {
    expect(canonicalJson({ t: "absent" })).toBe('{"t":"absent"}');
    expect(canonicalJson({ d: "2026-06-15" })).toBe('{"d":"2026-06-15"}');
    expect(canonicalJson({ t: "2026-08-01T18:30:00" })).toBe('{"t":"2026-08-01T18:30:00"}');
  });

  it("sorts arrays of objects by item_id then modifier_id", () => {
    const unsorted = {
      lines: [
        { item_id: "b", quantity: 1 },
        { item_id: "a", modifier_id: "z" },
        { item_id: "a", modifier_id: "a" },
      ],
    };
    expect(canonicalJson(unsorted)).toBe(
      '{"lines":[{"item_id":"a","modifier_id":"a"},{"item_id":"a","modifier_id":"z"},{"item_id":"b","quantity":1}]}'
    );
  });

  it("hashes null and an empty payload identically", async () => {
    const a = await canonicalHash(null);
    const b = await canonicalHash({});
    expect(a.value).toBe(b.value);
    expect(a.value).toHaveLength(64);
  });
});

describe("timestamp trimming matches .NET, not JavaScript", () => {
  /**
   * The server hands the untrimmed string to `DateTimeOffset.TryParse`, which trims by
   * `char.IsWhiteSpace`. `String.prototype.trim()` uses JavaScript's class instead — it omits
   * U+0085 and adds U+FEFF, the exact two code points this module's header names as the runtimes'
   * disagreement. Either divergence changes `payload_hash` and the submit 422s with a
   * `ConfirmationHashMismatchException` the operator cannot act on.
   */
  const TS = "2026-06-15T18:00:00Z";

  it("does NOT treat a U+FEFF-padded value as a timestamp — .NET does not trim it", () => {
    // JS `trim()` strips U+FEFF, so this used to normalise to a UTC instant while the server kept
    // a plain string.
    expect(canonicalJson({ at: `\uFEFF${TS}` })).not.toBe(canonicalJson({ at: TS }));
  });

  it("DOES treat a U+0085-padded value as a timestamp — .NET trims it", () => {
    // JS `trim()` does not strip U+0085, so this used to fall through to the string path while the
    // server parsed it as an instant.
    expect(canonicalJson({ at: `${TS}\u0085` })).toBe(canonicalJson({ at: TS }));
    expect(canonicalJson({ at: `\u0085${TS}` })).toBe(canonicalJson({ at: TS }));
  });

  it("still trims the ordinary spaces both runtimes agree on", () => {
    expect(canonicalJson({ at: `  ${TS}  ` })).toBe(canonicalJson({ at: TS }));
  });
});
