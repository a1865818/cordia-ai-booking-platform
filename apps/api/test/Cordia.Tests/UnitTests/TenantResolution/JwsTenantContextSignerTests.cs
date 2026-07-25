using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Security;
using Cordia.Infrastructure.TenantResolution;
using Microsoft.Extensions.Configuration;

namespace Cordia.Tests.UnitTests.TenantResolution;

/// <summary>
/// Unit tests for <see cref="JwsTenantContextSigner"/> (spec-15 Phase 2): round-trip,
/// tamper rejection, key rotation, expiry/skew, unknown kid, wrong typ, message hygiene,
/// and the frozen cross-impl vector shared with the langgraph verifier (Phase 5).
/// </summary>
[Trait("Category", "Unit")]
public class JwsTenantContextSignerTests
{
    // -----------------------------------------------------------------------------------------
    // CROSS-IMPL VECTOR — shared with Phase 5 test (apps/langgraph test_jws_verifier).
    // Captured verbatim from the real Python stub (apps/langgraph/tests/_stubs/tenant_context_stub.py):
    //   secret string    = "spec15-cross-impl-vector-secret"  (HMAC key = UTF-8 bytes of this string)
    //   C# key bytes     = base64-decode(VectorSecretBase64)   == UTF-8("spec15-cross-impl-vector-secret")
    //   kid              = "vector-kid-1"
    // The claim set is built by VectorClaims(); the canonical payload includes a non-ASCII 'ü'
    // (U+00FC) in a tool-policy id to exercise ensure_ascii (=> ü). Phase 5 must reuse the
    // identical secret + claims + token.
    // -----------------------------------------------------------------------------------------
    private const string VectorSecretString = "spec15-cross-impl-vector-secret";
    private const string VectorSecretBase64 = "c3BlYzE1LWNyb3NzLWltcGwtdmVjdG9yLXNlY3JldA==";
    private const string VectorKid = "vector-kid-1";

    private const string VectorToken =
        "eyJhbGciOiJIUzI1NiIsImtpZCI6InZlY3Rvci1raWQtMSIsInR5cCI6InRlbmFudGN0eCtqd3MifQ" +
        ".eyJhbGxvd2VkX2FnZW50X3BvbGljeV9pZHMiOlsiYWdlbnQtcG9saWN5LWEiLCJhZ2VudC1wb2xpY3kt" +
        "YiJdLCJhbGxvd2VkX3Rvb2xfcG9saWN5X2lkcyI6WyJ0b29sLXBvbGljeS1cdTAwZmMiXSwiY2hhbm5l" +
        "bCI6InZvaWNlIiwiY2hhbm5lbF9lbmRwb2ludF9pZCI6ImRpZDorMTU1NTAwMDExMTEiLCJjb25maWd1" +
        "cmF0aW9uX3ZlcnNpb24iOiJjZmctdjEiLCJjb3JyZWxhdGlvbl9pZCI6ImNvcnJfMDAwMDAwMDAwMDAw" +
        "MDAwMDAwMDAwMDAwMDAwMDAwMDEiLCJleHBpcmVzX2F0IjoiMjAyNi0wNy0yNVQwMDowNTowMFoiLCJp" +
        "c3N1ZWRfYXQiOiIyMDI2LTA3LTI1VDAwOjAwOjAwWiIsInNlc3Npb25faWQiOiJzZXNzXzAwMDAwMDAw" +
        "MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAxIiwidGVuYW50X2lkIjoidGVuYW50LWFiYyJ9" +
        ".-6d8E_84lKWbIUdh8zCrMrqx76DAPX9UNW6axXhKT0M";

    private const string VectorPayloadHex =
        "7b22616c6c6f7765645f6167656e745f706f6c6963795f696473223a5b226167656e742d706f6c6963" +
        "792d61222c226167656e742d706f6c6963792d62225d2c22616c6c6f7765645f746f6f6c5f706f6c69" +
        "63795f696473223a5b22746f6f6c2d706f6c6963792d5c7530306663225d2c226368616e6e656c223a" +
        "22766f696365222c226368616e6e656c5f656e64706f696e745f6964223a226469643a2b3135353530" +
        "303031313131222c22636f6e66696775726174696f6e5f76657273696f6e223a226366672d7631222c" +
        "22636f7272656c6174696f6e5f6964223a22636f72725f30303030303030303030303030303030303030" +
        "30303030303030303030303031222c22657870697265735f6174223a22323032362d30372d32355430" +
        "303a30353a30305a222c226973737565645f6174223a22323032362d30372d32355430303a30303a3030" +
        "5a222c2273657373696f6e5f6964223a22736573735f3030303030303030303030303030303030303030" +
        "303030303030303030303031222c2274656e616e745f6964223a2274656e616e742d616263227d";

    // -----------------------------------------------------------------------------------------
    // Round-trip
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SignThenVerify_RoundTripsClaims()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var claims = SampleClaims();

        var token = signer.Sign(claims);
        var verified = signer.Verify(token);

        AssertClaimsEqual(claims, verified);
    }

    // -----------------------------------------------------------------------------------------
    // Tamper
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Verify_TamperedPayloadByte_Throws()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var token = signer.Sign(SampleClaims());

        // Flip a byte inside the payload segment (simulates tampering the tenant_id claim);
        // the recomputed HMAC no longer matches the signature.
        var tampered = FlipCharInSegment(token, segment: 1, index: 6);

        Assert.Throws<TenantContextVerificationException>(() => signer.Verify(tampered));
    }

    // -----------------------------------------------------------------------------------------
    // Rotation (zero-downtime overlap)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Rotation_ZeroDowntimeOverlap()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();
        var clock = new FixedClock(Utc("2026-07-25T12:00:00Z"));

        // kid-a is the active signer; a token is minted with kid-a.
        var signerA = MakeSigner("kid-a", [("kid-a", keyA)], clock);
        var tokenA = signerA.Sign(SampleClaims());
        AssertClaimsEqual(SampleClaims(), signerA.Verify(tokenA)); // verifies while kid-a active

        // Overlap window: activate kid-b but keep kid-a published -> the kid-a token still verifies.
        var signerOverlap = MakeSigner("kid-b", [("kid-a", keyA), ("kid-b", keyB)], clock);
        AssertClaimsEqual(SampleClaims(), signerOverlap.Verify(tokenA));
        var tokenB = signerOverlap.Sign(SampleClaims()); // now signed with kid-b

        // Retire kid-a: a signer without kid-a rejects the old token but verifies the kid-b token.
        var signerRetired = MakeSigner("kid-b", [("kid-b", keyB)], clock);
        Assert.Throws<TenantContextVerificationException>(() => signerRetired.Verify(tokenA));
        AssertClaimsEqual(SampleClaims(), signerRetired.Verify(tokenB));
    }

    // -----------------------------------------------------------------------------------------
    // Temporal
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Verify_ExpiredToken_Throws()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)])
            .Sign(ClaimsWindow("2026-07-25T00:00:00Z", "2026-07-25T00:05:00Z"));

        // Clock is past expires_at.
        var late = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T01:00:00Z")));

        Assert.Throws<TenantContextVerificationException>(() => late.Verify(token));
    }

    [Fact]
    public void Verify_FutureIssuedAtBeyondSkew_Throws()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)])
            .Sign(ClaimsWindow("2026-07-25T12:00:00Z", "2026-07-25T13:00:00Z"));

        // Clock is well before issued_at (beyond the 60s skew).
        var early = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T11:00:00Z")));

        Assert.Throws<TenantContextVerificationException>(() => early.Verify(token));
    }

    [Fact]
    public void Verify_FutureIssuedAtWithinSkew_Succeeds()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)])
            .Sign(ClaimsWindow("2026-07-25T12:00:30Z", "2026-07-25T13:00:00Z"));

        // issued_at is 30s in the future — inside the 60s skew tolerance.
        var slightlyEarly = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));

        var verified = slightlyEarly.Verify(token);
        Assert.Equal(Utc("2026-07-25T12:00:30Z"), verified.IssuedAt);
    }

    // -----------------------------------------------------------------------------------------
    // Header validation
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Verify_UnknownKid_Throws()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)]).Sign(SampleClaims());

        // A verifier that does not know kid-a.
        var other = MakeSigner("kid-z", [("kid-z", RandomKey())], new FixedClock(Utc("2026-07-25T12:00:00Z")));

        Assert.Throws<TenantContextVerificationException>(() => other.Verify(token));
    }

    [Fact]
    public void Verify_WrongTyp_Throws()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var payloadB64 = signer.Sign(SampleClaims()).Split('.')[1];

        // Correctly signed, but the header typ is wrong.
        var forged = ForgeToken(key, "{\"alg\":\"HS256\",\"kid\":\"kid-a\",\"typ\":\"wrong+typ\"}", payloadB64);

        Assert.Throws<TenantContextVerificationException>(() => signer.Verify(forged));
    }

    [Fact]
    public void Verify_UnsupportedAlg_Throws()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var payloadB64 = signer.Sign(SampleClaims()).Split('.')[1];

        var forged = ForgeToken(key, "{\"alg\":\"none\",\"kid\":\"kid-a\",\"typ\":\"tenantctx+jws\"}", payloadB64);

        Assert.Throws<TenantContextVerificationException>(() => signer.Verify(forged));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jws")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.@@@.###")]
    public void Verify_MalformedToken_Throws(string jws)
    {
        var signer = MakeSigner("kid-a", [("kid-a", RandomKey())], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        Assert.Throws<TenantContextVerificationException>(() => signer.Verify(jws));
    }

    // -----------------------------------------------------------------------------------------
    // Message hygiene — no secret / key id / claim value in any thrown message.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Verify_ExceptionMessages_NeverLeakSecretKidOrClaims()
    {
        var keyString = "super-secret-hmac-key-material-do-not-leak";
        var key = Encoding.UTF8.GetBytes(keyString);
        var keyBase64 = Convert.ToBase64String(key);
        const string kid = "kid-hygiene-secret";
        var clock = new FixedClock(Utc("2026-07-25T12:00:00Z"));

        var signer = MakeSigner(kid, [(kid, key)], clock);
        var claims = SampleClaims();
        var goodToken = signer.Sign(claims);
        var payloadB64 = goodToken.Split('.')[1];

        // A basket of distinct failure modes.
        var failures = new List<string>
        {
            FlipCharInSegment(goodToken, segment: 2, index: 3),                              // bad signature
            ForgeToken(key, "{\"alg\":\"HS256\",\"kid\":\"kid-hygiene-secret\",\"typ\":\"x\"}", payloadB64), // wrong typ
            "malformed.token",                                                               // malformed
        };

        // Unknown-kid failure from a verifier that doesn't hold this kid.
        var otherVerifier = MakeSigner("kid-other", [("kid-other", RandomKey())], clock);

        foreach (var bad in failures)
        {
            var ex = Assert.Throws<TenantContextVerificationException>(() => signer.Verify(bad));
            AssertMessageClean(ex.Message, keyString, keyBase64, kid, claims);
        }

        var unknownKidEx = Assert.Throws<TenantContextVerificationException>(() => otherVerifier.Verify(goodToken));
        AssertMessageClean(unknownKidEx.Message, keyString, keyBase64, kid, claims);
    }

    // -----------------------------------------------------------------------------------------
    // Cross-impl vector (guards Phase 5 interop)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Verify_AcceptsPythonCrossImplVector()
    {
        var signer = MakeSigner(
            VectorKid,
            [(VectorKid, Convert.FromBase64String(VectorSecretBase64))],
            new FixedClock(Utc("2026-07-25T00:02:00Z"))); // between issued_at and expires_at

        var claims = signer.Verify(VectorToken);

        AssertClaimsEqual(VectorClaims(), claims);
        Assert.Equal(VectorSecretString, Encoding.UTF8.GetString(Convert.FromBase64String(VectorSecretBase64)));
    }

    [Fact]
    public void Sign_ReproducesPythonCrossImplToken()
    {
        var signer = MakeSigner(VectorKid, [(VectorKid, Convert.FromBase64String(VectorSecretBase64))]);

        // Full 3-part token equality proves header + canonical payload + signature parity with Python.
        Assert.Equal(VectorToken, signer.Sign(VectorClaims()));
    }

    [Fact]
    public void CanonicalPayload_HexMatchesPythonCrossImplVector()
    {
        var signer = MakeSigner(VectorKid, [(VectorKid, Convert.FromBase64String(VectorSecretBase64))]);

        var payloadSegment = signer.Sign(VectorClaims()).Split('.')[1];
        var payloadBytes = Base64Url.DecodeFromChars(payloadSegment);
        var hex = Convert.ToHexString(payloadBytes).ToLowerInvariant();

        Assert.Equal(VectorPayloadHex, hex);
    }

    // -----------------------------------------------------------------------------------------
    // ensure_ascii parity — hostile characters the .NET default encoders would mangle.
    // Independent-reviewer addition (spec-15 Phase 2 review): the expected hex is produced by the
    // Python stub's _canonical_json over the SAME claim set with a channel_endpoint_id carrying
    // / < > & ' + (must NOT be escaped), " and \ (short-escaped), a control char () and an
    // astral emoji U+1F600 (UTF-16 surrogate pair). This locks CPython
    // json.dumps(ensure_ascii=True) parity for the interop trust boundary.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CanonicalPayload_EnsureAsciiParity_SpecialCharsMatchPython()
    {
        const string trickyEndpoint = "a/b<c>d&e'f+g\"h\\i\u0001\U0001F600";
        const string expectedHex =
            "7b22616c6c6f7765645f6167656e745f706f6c6963795f696473223a5b226167656e742d706f6c6963" +
            "792d61222c226167656e742d706f6c6963792d62225d2c22616c6c6f7765645f746f6f6c5f706f6c69" +
            "63795f696473223a5b22746f6f6c2d706f6c6963792d5c7530306663225d2c226368616e6e656c223a" +
            "22766f696365222c226368616e6e656c5f656e64706f696e745f6964223a22612f623c633e64266527" +
            "662b675c22685c5c695c75303030315c75643833645c7564653030222c22636f6e66696775726174696f" +
            "6e5f76657273696f6e223a226366672d7631222c22636f7272656c6174696f6e5f6964223a22636f72" +
            "725f3030303030303030303030303030303030303030303030303030303030303031222c2265787069" +
            "7265735f6174223a22323032362d30372d32355430303a30353a30305a222c226973737565645f6174" +
            "223a22323032362d30372d32355430303a30303a30305a222c2273657373696f6e5f6964223a227365" +
            "73735f3030303030303030303030303030303030303030303030303030303030303031222c2274656e" +
            "616e745f6964223a2274656e616e742d616263227d";

        var signer = MakeSigner(VectorKid, [(VectorKid, Convert.FromBase64String(VectorSecretBase64))]);
        var claims = VectorClaims() with { ChannelEndpointId = trickyEndpoint };

        var payloadSegment = signer.Sign(claims).Split('.')[1];
        var hex = Convert.ToHexString(Base64Url.DecodeFromChars(payloadSegment)).ToLowerInvariant();

        Assert.Equal(expectedHex, hex);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static JwsTenantContextSigner MakeSigner(
        string activeKid,
        (string Kid, byte[] Secret)[] keys,
        TimeProvider? clock = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Secrets:TenantContextSigningKeys:ActiveKid"] = activeKid,
            ["Secrets:TenantContextSigningKeys:KeyIds"] = string.Join(",", keys.Select(k => k.Kid)),
        };
        foreach (var (kid, secret) in keys)
            settings[$"Secrets:TenantContextSigningKeys:Keys:{kid}"] = Convert.ToBase64String(secret);

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var store = new ConfigurationSecretStore(config);

        return clock is null
            ? new JwsTenantContextSigner(store)
            : new JwsTenantContextSigner(store, clock);
    }

    private static byte[] RandomKey() => RandomNumberGenerator.GetBytes(32);

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static TenantContextClaims SampleClaims() => new(
        TenantId: "tenant-sample",
        Channel: TenantChannel.Messaging,
        ChannelEndpointId: "page-42",
        ConfigurationVersion: "cfg-9",
        CorrelationId: "corr_sample-correlation-id",
        SessionId: "sess_sample-session-id",
        AllowedAgentPolicyIds: ["agent-policy-1", "agent-policy-2"],
        AllowedToolPolicyIds: ["tool-policy-1"],
        IssuedAt: Utc("2026-07-25T11:59:00Z"),
        ExpiresAt: Utc("2026-07-25T12:05:00Z"));

    private static TenantContextClaims ClaimsWindow(string issuedAt, string expiresAt) => SampleClaims() with
    {
        IssuedAt = Utc(issuedAt),
        ExpiresAt = Utc(expiresAt),
    };

    private static TenantContextClaims VectorClaims() => new(
        TenantId: "tenant-abc",
        Channel: TenantChannel.Voice,
        ChannelEndpointId: "did:+15550001111",
        ConfigurationVersion: "cfg-v1",
        CorrelationId: "corr_00000000000000000000000000000001",
        SessionId: "sess_00000000000000000000000000000001",
        AllowedAgentPolicyIds: ["agent-policy-a", "agent-policy-b"],
        AllowedToolPolicyIds: ["tool-policy-ü"], // ü -> ü (ensure_ascii)
        IssuedAt: Utc("2026-07-25T00:00:00Z"),
        ExpiresAt: Utc("2026-07-25T00:05:00Z"));

    private static string ForgeToken(byte[] key, string headerJson, string payloadB64)
    {
        var headerB64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(headerJson));
        var signingInput = $"{headerB64}.{payloadB64}";
        var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static string FlipCharInSegment(string token, int segment, int index)
    {
        var parts = token.Split('.');
        var chars = parts[segment].ToCharArray();
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        parts[segment] = new string(chars);
        return string.Join('.', parts);
    }

    private static void AssertClaimsEqual(TenantContextClaims expected, TenantContextClaims actual)
    {
        Assert.Equal(expected.TenantId, actual.TenantId);
        Assert.Equal(expected.Channel, actual.Channel);
        Assert.Equal(expected.ChannelEndpointId, actual.ChannelEndpointId);
        Assert.Equal(expected.ConfigurationVersion, actual.ConfigurationVersion);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.AllowedAgentPolicyIds, actual.AllowedAgentPolicyIds);
        Assert.Equal(expected.AllowedToolPolicyIds, actual.AllowedToolPolicyIds);
        Assert.Equal(expected.IssuedAt, actual.IssuedAt);
        Assert.Equal(expected.ExpiresAt, actual.ExpiresAt);
    }

    private static void AssertMessageClean(
        string message, string keyString, string keyBase64, string kid, TenantContextClaims claims)
    {
        Assert.DoesNotContain(keyString, message, StringComparison.Ordinal);
        Assert.DoesNotContain(keyBase64, message, StringComparison.Ordinal);
        Assert.DoesNotContain(kid, message, StringComparison.Ordinal);
        Assert.DoesNotContain(claims.TenantId, message, StringComparison.Ordinal);
        Assert.DoesNotContain(claims.CorrelationId, message, StringComparison.Ordinal);
        Assert.DoesNotContain(claims.SessionId, message, StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
