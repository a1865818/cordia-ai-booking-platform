using System.Text.Json.Nodes;
using Cordia.Infrastructure.Services;

namespace Cordia.Tests.UnitTests.Idempotency;

/// <summary>
/// Pins canon.v1 (spec 04 §5) across the Python↔C# boundary.
///
/// <para>The agent computes <c>confirmation.intent_payload_hash</c> with
/// <c>cordia_agent/revisions/canonical.py</c>; this service re-derives it from the
/// submitted <c>revision.canonical_payload</c> and rejects the submission on any
/// mismatch (§8.8, Inv-1). Each fixture below is duplicated verbatim — payload AND
/// expected digest — in
/// <c>apps/langgraph/tests/unit_tests/revisions/test_canonical_hash.py</c>. If either
/// implementation drifts, exactly one of the two suites goes red.</para>
/// </summary>
[Trait("Category", "Unit")]
public class CanonicalHashCrossLanguageParityTests
{
    private readonly CanonicalRequestHasher _hasher = new();

    [Theory]
    // A real agent submission: nulls omitted, timestamp normalized to millisecond UTC.
    [InlineData(
        """
        {"operation":"create","service_id":"svc_001","location_id":"loc_001",
         "starts_at":"2026-08-10T19:00:00Z","party_size":4,"duration_minutes":null,
         "special_requests":["high chair"],"preorder":null,"target_booking_id":null,
         "target_booking_version":null}
        """,
        "9efa756fc92721ac095d700d7467e30161781146f53f7295a24db182009ec45e")]
    // Offset folding plus sub-millisecond TRUNCATION (never rounding).
    [InlineData(
        """
        {"starts_at":"2026-06-15T20:30:00.9999+10:00","requested_at":"2026-06-15T10:30:00.000Z"}
        """,
        "3d639d40fbfb2c2593276fa26c93b42592b99ce406b56539b13d0cbc651c2960")]
    // JavaScriptEncoder.Default escaping: non-ASCII, HTML-sensitive chars, surrogate pairs.
    [InlineData(
        """
        {"note":"café &  bar <b>  ","emoji":"😀"}
        """,
        "04bf740fcec16aa06f12e2b8cd5b1f67f09c614763825ac57dbdb665ca9d619d")]
    // 500 == 500.0; fractional values keep shortest round-trip form.
    [InlineData(
        """
        {"a":500,"b":500.0,"c":500.5,"d":0}
        """,
        "30397cf077921abd5085b51d9f0c78ffb2eebf3d1ebe86acf46246b59a1a2ed8")]
    // List ordering by natural key, and empty collections omitted.
    [InlineData(
        """
        {"lines":[{"item_id":"b","modifier_id":"2"},{"item_id":"a","modifier_id":"1"}],
         "tags":["z","a"],"empty":[]}
        """,
        "bf1d43afd874fef6a23e7e4cd31f1664cbc3b2a13e0266f7bd84b419070b92e8")]
    public void Hash_MatchesTheAgentSideCanonV1Digest(string payloadJson, string expectedHash)
    {
        var hash = _hasher.Hash(JsonNode.Parse(payloadJson));

        Assert.Equal("canon.v1", hash.Version);
        Assert.Equal(expectedHash, hash.Value);
    }
}
