using System.Text.Json.Nodes;
using Cordia.Infrastructure.Idempotency;

namespace Cordia.Tests.UnitTests.Idempotency;

/// <summary>
/// Unit tests for <see cref="CanonicalRequestHasher"/> — canon.v1 equivalence matrix (spec 04 §5, §12).
/// All inputs are built via <see cref="JsonNode.Parse"/> to mirror the production code path.
/// </summary>
[Trait("Category", "Unit")]
public class CanonicalRequestHasherTests
{
    private readonly CanonicalRequestHasher _hasher = new();

    // ------------------------------------------------------------------
    // 1. Reordered object keys → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void ReorderedObjectKeys_ProduceEqualHash()
    {
        var a = JsonNode.Parse("""{"z":1,"a":2}""");
        var b = JsonNode.Parse("""{"a":2,"z":1}""");
        Assert.Equal(_hasher.Hash(a).Value, _hasher.Hash(b).Value);
    }

    // ------------------------------------------------------------------
    // 2. Explicit null vs absent → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void ExplicitNullVsAbsent_ProduceEqualHash()
    {
        var withNull = JsonNode.Parse("""{"a":1,"b":null}""");
        var withoutB = JsonNode.Parse("""{"a":1}""");
        Assert.Equal(_hasher.Hash(withNull).Value, _hasher.Hash(withoutB).Value);
    }

    // ------------------------------------------------------------------
    // 3. NFC equivalence: composed (U+00E9) vs decomposed (e + U+0301) → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void NfcComposedVsDecomposed_ProduceEqualHash()
    {
        // "café" — é is the composed form (U+00E9)
        var composed = JsonNode.Parse("{\"name\":\"café\"}");
        // decomposed: e (U+0065) + combining acute accent (U+0301)
        var decomposed = JsonNode.Parse("{\"name\":\"café\"}");
        Assert.Equal(_hasher.Hash(composed).Value, _hasher.Hash(decomposed).Value);
    }

    // ------------------------------------------------------------------
    // 4a. Internal whitespace collapse: "a  b" vs "a b" → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void InternalWhitespaceCollapse_ProduceEqualHash()
    {
        var twoSpaces = JsonNode.Parse("""{"v":"a  b"}""");
        var oneSpace = JsonNode.Parse("""{"v":"a b"}""");
        Assert.Equal(_hasher.Hash(twoSpaces).Value, _hasher.Hash(oneSpace).Value);
    }

    // ------------------------------------------------------------------
    // 4b. Leading/trailing whitespace trim: "  a b  " vs "a b" → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void LeadingTrailingWhitespace_ProduceEqualHash()
    {
        var padded = JsonNode.Parse("""{"v":"  a b  "}""");
        var trimmed = JsonNode.Parse("""{"v":"a b"}""");
        Assert.Equal(_hasher.Hash(padded).Value, _hasher.Hash(trimmed).Value);
    }

    // ------------------------------------------------------------------
    // 5a. Monetary minor units: same value → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void SameMonetaryMinorUnits_ProduceEqualHash()
    {
        var a = JsonNode.Parse("""{"amount":500}""");
        var b = JsonNode.Parse("""{"amount":500}""");
        Assert.Equal(_hasher.Hash(a).Value, _hasher.Hash(b).Value);
    }

    // ------------------------------------------------------------------
    // 5b. Integer vs decimal representation → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void IntegerVsDecimalRepresentation_ProduceEqualHash()
    {
        var integer = JsonNode.Parse("""{"amount":500}""");
        var decimal_ = JsonNode.Parse("""{"amount":500.0}""");
        Assert.Equal(_hasher.Hash(integer).Value, _hasher.Hash(decimal_).Value);
    }

    // ------------------------------------------------------------------
    // 5c. Different monetary values → NOT EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void DifferentMonetaryValues_ProduceDifferentHash()
    {
        var a = JsonNode.Parse("""{"amount":500}""");
        var b = JsonNode.Parse("""{"amount":501}""");
        Assert.NotEqual(_hasher.Hash(a).Value, _hasher.Hash(b).Value);
    }

    // ------------------------------------------------------------------
    // 6a. Reordered array of objects by item_id → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void ReorderedArrayOfObjects_ProduceEqualHash()
    {
        var ab = JsonNode.Parse("""{"items":[{"item_id":"b"},{"item_id":"a"}]}""");
        var ba = JsonNode.Parse("""{"items":[{"item_id":"a"},{"item_id":"b"}]}""");
        Assert.Equal(_hasher.Hash(ab).Value, _hasher.Hash(ba).Value);
    }

    // ------------------------------------------------------------------
    // 6b. Reordered scalar array → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void ReorderedScalarArray_ProduceEqualHash()
    {
        var a = JsonNode.Parse("""{"vals":[2,1]}""");
        var b = JsonNode.Parse("""{"vals":[1,2]}""");
        Assert.Equal(_hasher.Hash(a).Value, _hasher.Hash(b).Value);
    }

    // ------------------------------------------------------------------
    // 7a. Same instant in different timezones → EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void SameInstantDifferentTimezone_ProduceEqualHash()
    {
        var utc = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000Z"}""");
        var plus2 = JsonNode.Parse("""{"ts":"2026-06-15T12:30:00.000+02:00"}""");
        Assert.Equal(_hasher.Hash(utc).Value, _hasher.Hash(plus2).Value);
    }

    // ------------------------------------------------------------------
    // 7b. Different instants (1 ms apart) → NOT EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void DifferentInstants_ProduceDifferentHash()
    {
        var t0 = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000Z"}""");
        var t1 = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.001Z"}""");
        Assert.NotEqual(_hasher.Hash(t0).Value, _hasher.Hash(t1).Value);
    }

    // ------------------------------------------------------------------
    // 8. Determinism: same payload hashed twice → EQUAL.
    //    This test verifies two successive Hash() calls on identical input
    //    produce identical output (pure, no shared mutable state).
    // ------------------------------------------------------------------
    [Fact]
    public void Determinism_SamePayloadHashedTwiceProduceEqualHash()
    {
        var payload = JsonNode.Parse("""{"party_size":2,"service_id":"svc-1","requested_at":"2026-06-15T10:30:00.000Z"}""");
        var h1 = _hasher.Hash(payload);
        // Parse again to ensure no shared mutable state.
        var payload2 = JsonNode.Parse("""{"party_size":2,"service_id":"svc-1","requested_at":"2026-06-15T10:30:00.000Z"}""");
        var h2 = _hasher.Hash(payload2);
        Assert.Equal(h1.Value, h2.Value);
    }

    // ------------------------------------------------------------------
    // 9. Material change (party_size 2 → 4) → NOT EQUAL
    // ------------------------------------------------------------------
    [Fact]
    public void MaterialChange_ProducesDifferentHash()
    {
        var partyOf2 = JsonNode.Parse("""{"party_size":2}""");
        var partyOf4 = JsonNode.Parse("""{"party_size":4}""");
        Assert.NotEqual(_hasher.Hash(partyOf2).Value, _hasher.Hash(partyOf4).Value);
    }

    // ------------------------------------------------------------------
    // 10. Version is stamped as "canon.v1"
    // ------------------------------------------------------------------
    [Fact]
    public void Hash_StampsVersionAsCanonV1()
    {
        var payload = JsonNode.Parse("""{"x":1}""");
        var result = _hasher.Hash(payload);
        Assert.Equal("canon.v1", result.Version);
    }

    // ------------------------------------------------------------------
    // Bonus: null payload hashes the canonical empty object (deterministic)
    // ------------------------------------------------------------------
    [Fact]
    public void NullPayload_HashesAsEmptyObject()
    {
        var nullResult = _hasher.Hash(null);
        var emptyResult = _hasher.Hash(JsonNode.Parse("{}"));
        Assert.Equal(nullResult.Value, emptyResult.Value);
        Assert.Equal(64, nullResult.Value.Length); // SHA-256 hex is always 64 chars
    }

    // ==================================================================
    // Fix 1 — Numeric item_id/modifier_id must not crash the comparer
    // ==================================================================

    /// <summary>
    /// Fix 1: item_id is a JSON number (not a string). The comparer must read it defensively
    /// and fall back to canonical JSON text rather than calling GetValue&lt;string&gt;() which throws.
    /// The two orderings must sort to the same canonical form and produce the same hash.
    /// </summary>
    [Fact]
    public void NumericItemId_DoesNotThrowAndProducesEqualHashForReorderedArray()
    {
        // item_id values are numbers (2 and 1). Sort must not throw inside List.Sort.
        var higher = JsonNode.Parse("""{"a":[{"item_id":2},{"item_id":1}]}""");
        var lower = JsonNode.Parse("""{"a":[{"item_id":1},{"item_id":2}]}""");

        // Must not throw.
        var h1 = _hasher.Hash(higher);
        var h2 = _hasher.Hash(lower);

        // Both orderings must produce the same canonical hash.
        Assert.Equal(h1.Value, h2.Value);
    }

    /// <summary>
    /// Fix 1: modifier_id as a number also must not throw.
    /// </summary>
    [Fact]
    public void NumericModifierId_DoesNotThrowAndProducesEqualHashForReorderedArray()
    {
        var a = JsonNode.Parse("""{"items":[{"item_id":"x","modifier_id":2},{"item_id":"x","modifier_id":1}]}""");
        var b = JsonNode.Parse("""{"items":[{"item_id":"x","modifier_id":1},{"item_id":"x","modifier_id":2}]}""");

        var h1 = _hasher.Hash(a);
        var h2 = _hasher.Hash(b);

        Assert.Equal(h1.Value, h2.Value);
    }

    // ==================================================================
    // Fix 2 — Timestamp truncation (floor), not rounding
    // ==================================================================

    /// <summary>
    /// Fix 2a: microsecond input (6 fractional digits, as emitted by Python/Pydantic) normalizes
    /// deterministically — the microseconds are truncated (floored), not rounded.
    /// </summary>
    [Fact]
    public void MicrosecondTimestamp_NormalizesToMillisecondTruncated()
    {
        // .123456 microseconds → truncate to .123 (floor), not .123 rounded
        var micro = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.123456Z"}""");
        var milli = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.123Z"}""");
        Assert.Equal(_hasher.Hash(micro).Value, _hasher.Hash(milli).Value);
    }

    /// <summary>
    /// Fix 2b: sub-millisecond values that are less than 0.5 ms floor to .000, not round to .001.
    /// Both ...00.0005Z and ...00.000Z floor to ...00.000Z (equal hash).
    /// </summary>
    [Fact]
    public void SubMillisecondBelow0Point5_FloorsToZeroMilliseconds()
    {
        // 0.0005Z = 0.5 microseconds → still floors to .000, does not round to .001
        var almostZero = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.0005Z"}""");
        var exactZero = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000Z"}""");
        Assert.Equal(_hasher.Hash(almostZero).Value, _hasher.Hash(exactZero).Value);
    }

    /// <summary>
    /// Fix 2c: .000Z and .001Z are 1 ms apart and must produce DIFFERENT hashes.
    /// </summary>
    [Fact]
    public void OneMillisecondApart_ProduceDifferentHashes()
    {
        var t0 = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000Z"}""");
        var t1 = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.001Z"}""");
        Assert.NotEqual(_hasher.Hash(t0).Value, _hasher.Hash(t1).Value);
    }

    /// <summary>
    /// Fix 2d: a sub-millisecond value near a second boundary (.9995Z) must NOT roll over
    /// into the next second — truncation keeps it at ...59.999Z.
    /// </summary>
    [Fact]
    public void SubMillisecondNearSecondBoundary_DoesNotRollOverToNextSecond()
    {
        // Without truncation, ToString("fff") rounds .9995 → "000" of the next second.
        // With truncation, it stays at .999 of the same second.
        var nearBoundary = JsonNode.Parse("""{"ts":"2026-06-15T10:30:59.9995Z"}""");
        var expectedFloor = JsonNode.Parse("""{"ts":"2026-06-15T10:30:59.999Z"}""");
        var nextSecond = JsonNode.Parse("""{"ts":"2026-06-15T10:31:00.000Z"}""");

        Assert.Equal(_hasher.Hash(nearBoundary).Value, _hasher.Hash(expectedFloor).Value);
        Assert.NotEqual(_hasher.Hash(nearBoundary).Value, _hasher.Hash(nextSecond).Value);
    }

    // ==================================================================
    // Fix 3 — Lowercase 'z' UTC designator
    // ==================================================================

    /// <summary>
    /// Fix 3: lowercase 'z' and uppercase 'Z' UTC designators must normalize to the same hash.
    /// The pre-filter was only checking for uppercase 'Z', causing lowercase timestamps to
    /// fall through to plain string normalization and diverge.
    /// </summary>
    [Fact]
    public void LowercaseZDesignator_ProducesEqualHashToUppercaseZ()
    {
        var lowercase = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000z"}""");
        var uppercase = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000Z"}""");
        Assert.Equal(_hasher.Hash(lowercase).Value, _hasher.Hash(uppercase).Value);
    }

    // ==================================================================
    // Fix 4 — Empty array / empty object treated as absent
    // ==================================================================

    /// <summary>
    /// Fix 4a: {"items":[]} and {} must produce the same hash (empty array == absent).
    /// </summary>
    [Fact]
    public void EmptyArray_EqualToAbsent()
    {
        var withEmpty = JsonNode.Parse("""{"items":[]}""");
        var withoutKey = JsonNode.Parse("""{}""");
        Assert.Equal(_hasher.Hash(withEmpty).Value, _hasher.Hash(withoutKey).Value);
    }

    /// <summary>
    /// Fix 4b: {"meta":{}} and {} must produce the same hash (empty object == absent).
    /// </summary>
    [Fact]
    public void EmptyObject_EqualToAbsent()
    {
        var withEmpty = JsonNode.Parse("""{"meta":{}}""");
        var withoutKey = JsonNode.Parse("""{}""");
        Assert.Equal(_hasher.Hash(withEmpty).Value, _hasher.Hash(withoutKey).Value);
    }

    /// <summary>
    /// Fix 4c: {"items":[],"party_size":2} and {"party_size":2} must produce the same hash.
    /// Empty array alongside a material field is treated as if absent.
    /// </summary>
    [Fact]
    public void EmptyArrayAlongsideMaterialField_EqualToAbsent()
    {
        var withEmptyArray = JsonNode.Parse("""{"items":[],"party_size":2}""");
        var withoutEmpty = JsonNode.Parse("""{"party_size":2}""");
        Assert.Equal(_hasher.Hash(withEmptyArray).Value, _hasher.Hash(withoutEmpty).Value);
    }

    // ==================================================================
    // Fix 5 — Large integers beyond decimal range
    // ==================================================================

    /// <summary>
    /// Fix 5a: 5e2 == 500 == 500.0 (existing guarantee must keep passing).
    /// </summary>
    [Fact]
    public void ExponentFormInteger_EqualToPlainInteger()
    {
        var exp = JsonNode.Parse("""{"n":5e2}""");
        var plain = JsonNode.Parse("""{"n":500}""");
        var decimal_ = JsonNode.Parse("""{"n":500.0}""");
        Assert.Equal(_hasher.Hash(exp).Value, _hasher.Hash(plain).Value);
        Assert.Equal(_hasher.Hash(plain).Value, _hasher.Hash(decimal_).Value);
    }

    /// <summary>
    /// Fix 5b: 1e30 and its full decimal literal 1000000000000000000000000000000 must hash equally.
    /// This value exceeds decimal range, so the old code kept raw text and the two diverged.
    /// </summary>
    [Fact]
    public void LargeIntegerExponentForm_EqualToDecimalLiteral()
    {
        var exp = JsonNode.Parse("""{"n":1e30}""");
        var literal = JsonNode.Parse("""{"n":1000000000000000000000000000000}""");
        Assert.Equal(_hasher.Hash(exp).Value, _hasher.Hash(literal).Value);
    }

    /// <summary>
    /// Fix 5c: in-range 1e15 == 1000000000000000 (keep passing).
    /// </summary>
    [Fact]
    public void InRangeLargeInteger_ExponentEqualToLiteral()
    {
        var exp = JsonNode.Parse("""{"n":1e15}""");
        var literal = JsonNode.Parse("""{"n":1000000000000000}""");
        Assert.Equal(_hasher.Hash(exp).Value, _hasher.Hash(literal).Value);
    }

    // ==================================================================
    // Fix 6 — NFC-normalize object KEYS
    // ==================================================================

    /// <summary>
    /// Fix 6: composed and decomposed forms of the same key must produce equal hashes.
    /// Today all keys are ASCII, but this removes a latent asymmetry.
    /// </summary>
    [Fact]
    public void NfcComposedVsDecomposedKey_ProduceEqualHash()
    {
        // Key "café" composed (U+00E9 é)
        var composed = JsonNode.Parse("{\"café\":1}");
        // Key "café" decomposed (e + U+0301 combining acute)
        var decomposed = JsonNode.Parse("{\"café\":1}");
        Assert.Equal(_hasher.Hash(composed).Value, _hasher.Hash(decomposed).Value);
    }

    // ==================================================================
    // Fix 7 — Additional timestamp forms + edge cases
    // ==================================================================

    /// <summary>
    /// Fix 7a: negative UTC offset -05:00 represents the same instant as UTC+0 equivalent.
    /// </summary>
    [Fact]
    public void NegativeOffset_NormalizesToSameInstantAsUtc()
    {
        // 2026-06-15T05:30:00.000-05:00 == 2026-06-15T10:30:00.000Z
        var neg = JsonNode.Parse("""{"ts":"2026-06-15T05:30:00.000-05:00"}""");
        var utc = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000Z"}""");
        Assert.Equal(_hasher.Hash(neg).Value, _hasher.Hash(utc).Value);
    }

    /// <summary>
    /// Fix 7a: +0000 (no colon) is accepted and normalizes to UTC.
    /// </summary>
    [Fact]
    public void ZeroOffsetNoColon_NormalizesCorrectly()
    {
        var noColon = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000+0000"}""");
        var utc = JsonNode.Parse("""{"ts":"2026-06-15T10:30:00.000Z"}""");
        Assert.Equal(_hasher.Hash(noColon).Value, _hasher.Hash(utc).Value);
    }
}
