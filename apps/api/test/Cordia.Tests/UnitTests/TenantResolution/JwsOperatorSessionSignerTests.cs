using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cordia.Domain.Exceptions;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Security;
using Cordia.Infrastructure.TenantResolution;
using Microsoft.Extensions.Configuration;

namespace Cordia.Tests.UnitTests.TenantResolution;

/// <summary>
/// Unit tests for <see cref="JwsOperatorSessionSigner"/> (G1): round-trip, tamper rejection,
/// key rotation, expiry/skew, unknown kid, wrong typ (including tenantctx+jws), message hygiene.
/// Cloned from <see cref="JwsTenantContextSignerTests"/>; no langgraph cross-impl vector
/// (operator claims are not shared with the Python verifier).
/// </summary>
[Trait("Category", "Unit")]
public class JwsOperatorSessionSignerTests
{
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

    [Fact]
    public void Sign_HeaderTypIsOpctxJws()
    {
        var signer = MakeSigner("kid-a", [("kid-a", RandomKey())]);
        var headerJson = Encoding.UTF8.GetString(
            Base64Url.DecodeFromChars(signer.Sign(SampleClaims()).Split('.')[0]));

        Assert.Contains("\"typ\":\"opctx+jws\"", headerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("tenantctx+jws", headerJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_TamperedPayloadByte_Throws()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var token = signer.Sign(SampleClaims());
        var tampered = FlipCharInSegment(token, segment: 1, index: 6);

        Assert.Throws<TenantContextVerificationException>(() => signer.Verify(tampered));
    }

    [Fact]
    public void Rotation_ZeroDowntimeOverlap()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();
        var clock = new FixedClock(Utc("2026-07-25T12:00:00Z"));

        var signerA = MakeSigner("kid-a", [("kid-a", keyA)], clock);
        var tokenA = signerA.Sign(SampleClaims());
        AssertClaimsEqual(SampleClaims(), signerA.Verify(tokenA));

        var signerOverlap = MakeSigner("kid-b", [("kid-a", keyA), ("kid-b", keyB)], clock);
        AssertClaimsEqual(SampleClaims(), signerOverlap.Verify(tokenA));
        var tokenB = signerOverlap.Sign(SampleClaims());

        var signerRetired = MakeSigner("kid-b", [("kid-b", keyB)], clock);
        Assert.Throws<TenantContextVerificationException>(() => signerRetired.Verify(tokenA));
        AssertClaimsEqual(SampleClaims(), signerRetired.Verify(tokenB));
    }

    [Fact]
    public void Verify_ExpiredToken_Throws()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)])
            .Sign(ClaimsWindow("2026-07-25T00:00:00Z", "2026-07-25T00:05:00Z"));

        var late = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T01:00:00Z")));

        Assert.Throws<TenantContextVerificationException>(() => late.Verify(token));
    }

    [Fact]
    public void Verify_FutureIssuedAtBeyondSkew_Throws()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)])
            .Sign(ClaimsWindow("2026-07-25T12:00:00Z", "2026-07-25T13:00:00Z"));

        var early = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T11:00:00Z")));

        Assert.Throws<TenantContextVerificationException>(() => early.Verify(token));
    }

    [Fact]
    public void Verify_FutureIssuedAtWithinSkew_Succeeds()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)])
            .Sign(ClaimsWindow("2026-07-25T12:00:30Z", "2026-07-25T13:00:00Z"));

        var slightlyEarly = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));

        var verified = slightlyEarly.Verify(token);
        Assert.Equal(Utc("2026-07-25T12:00:30Z"), verified.IssuedAt);
    }

    [Fact]
    public void Verify_UnknownKid_Throws()
    {
        var key = RandomKey();
        var token = MakeSigner("kid-a", [("kid-a", key)]).Sign(SampleClaims());

        var other = MakeSigner("kid-z", [("kid-z", RandomKey())], new FixedClock(Utc("2026-07-25T12:00:00Z")));

        Assert.Throws<TenantContextVerificationException>(() => other.Verify(token));
    }

    [Fact]
    public void Verify_WrongTyp_Throws()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var payloadB64 = signer.Sign(SampleClaims()).Split('.')[1];

        var forged = ForgeToken(key, "{\"alg\":\"HS256\",\"kid\":\"kid-a\",\"typ\":\"wrong+typ\"}", payloadB64);

        Assert.Throws<TenantContextVerificationException>(() => signer.Verify(forged));
    }

    [Fact]
    public void Verify_TenantContextTyp_Throws()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var payloadB64 = signer.Sign(SampleClaims()).Split('.')[1];

        var forged = ForgeToken(key, "{\"alg\":\"HS256\",\"kid\":\"kid-a\",\"typ\":\"tenantctx+jws\"}", payloadB64);

        Assert.Throws<TenantContextVerificationException>(() => signer.Verify(forged));
    }

    [Fact]
    public void Verify_UnsupportedAlg_Throws()
    {
        var key = RandomKey();
        var signer = MakeSigner("kid-a", [("kid-a", key)], new FixedClock(Utc("2026-07-25T12:00:00Z")));
        var payloadB64 = signer.Sign(SampleClaims()).Split('.')[1];

        var forged = ForgeToken(key, "{\"alg\":\"none\",\"kid\":\"kid-a\",\"typ\":\"opctx+jws\"}", payloadB64);

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

        var failures = new List<string>
        {
            FlipCharInSegment(goodToken, segment: 2, index: 3),
            ForgeToken(key, "{\"alg\":\"HS256\",\"kid\":\"kid-hygiene-secret\",\"typ\":\"x\"}", payloadB64),
            "malformed.token",
        };

        var otherVerifier = MakeSigner("kid-other", [("kid-other", RandomKey())], clock);

        foreach (var bad in failures)
        {
            var ex = Assert.Throws<TenantContextVerificationException>(() => signer.Verify(bad));
            AssertMessageClean(ex.Message, keyString, keyBase64, kid, claims);
        }

        var unknownKidEx = Assert.Throws<TenantContextVerificationException>(() => otherVerifier.Verify(goodToken));
        AssertMessageClean(unknownKidEx.Message, keyString, keyBase64, kid, claims);
    }

    private static JwsOperatorSessionSigner MakeSigner(
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
            ? new JwsOperatorSessionSigner(store)
            : new JwsOperatorSessionSigner(store, clock);
    }

    private static byte[] RandomKey() => RandomNumberGenerator.GetBytes(32);

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static OperatorSessionClaims SampleClaims() => new(
        TenantId: "tenant-sample",
        ActorId: "opr-sample",
        ActorRole: "owner",
        SessionId: "opsess-sample",
        IssuedAt: Utc("2026-07-25T11:59:00Z"),
        ExpiresAt: Utc("2026-07-25T19:59:00Z"));

    private static OperatorSessionClaims ClaimsWindow(string issuedAt, string expiresAt) => SampleClaims() with
    {
        IssuedAt = Utc(issuedAt),
        ExpiresAt = Utc(expiresAt),
    };

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

    private static void AssertClaimsEqual(OperatorSessionClaims expected, OperatorSessionClaims actual)
    {
        Assert.Equal(expected.TenantId, actual.TenantId);
        Assert.Equal(expected.ActorId, actual.ActorId);
        Assert.Equal(expected.ActorRole, actual.ActorRole);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.IssuedAt, actual.IssuedAt);
        Assert.Equal(expected.ExpiresAt, actual.ExpiresAt);
    }

    private static void AssertMessageClean(
        string message, string keyString, string keyBase64, string kid, OperatorSessionClaims claims)
    {
        Assert.DoesNotContain(keyString, message, StringComparison.Ordinal);
        Assert.DoesNotContain(keyBase64, message, StringComparison.Ordinal);
        Assert.DoesNotContain(kid, message, StringComparison.Ordinal);
        Assert.DoesNotContain(claims.TenantId, message, StringComparison.Ordinal);
        Assert.DoesNotContain(claims.ActorId, message, StringComparison.Ordinal);
        Assert.DoesNotContain(claims.SessionId, message, StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
