using System.Text;
using Cordia.Api.Security;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>The webhook HMAC and its replay window (#74), without a host.</summary>
[Trait("Category", "Unit")]
public class WebhookSignatureTests
{
    private const string Secret = "unit-webhook-secret";
    private const string Tenant = "tenant-a";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"notification_id":"n1","status":"delivered"}""");
    private static string Ts(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString();

    private static bool Verify(string timestamp, string? signature = null, string tenant = Tenant, byte[]? body = null)
        => WebhookSignature.Verify(
            Secret, timestamp, signature ?? WebhookSignature.Compute(Secret, timestamp, Tenant, Body), tenant, body ?? Body, Now);

    [Fact]
    public void AGenuineSignature_Verifies()
        => Assert.True(Verify(Ts(Now)));

    [Fact]
    public void TheSignature_IsSha256HexWithItsSchemePrefix()
        => Assert.Matches("^sha256=[0-9a-f]{64}$", WebhookSignature.Compute(Secret, Ts(Now), Tenant, Body));

    [Fact]
    public void AnAlteredBody_DoesNotVerify()
        => Assert.False(Verify(Ts(Now), body: Encoding.UTF8.GetBytes("""{"notification_id":"n1","status":"bounced"}""")));

    [Fact]
    public void ADifferentTenant_DoesNotVerify()
        => Assert.False(Verify(Ts(Now), tenant: "tenant-b"));

    [Fact]
    public void ASignatureForAnotherTimestamp_DoesNotVerify()
        => Assert.False(Verify(Ts(Now), signature: WebhookSignature.Compute(Secret, Ts(Now.AddSeconds(-1)), Tenant, Body)));

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void ATimestampOutsideTheWindow_DoesNotVerify(int offsetSeconds)
        => Assert.False(Verify(Ts(Now.AddSeconds(offsetSeconds))));

    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public void ATimestampAtTheEdgeOfTheWindow_Verifies(int offsetSeconds)
        => Assert.True(Verify(Ts(Now.AddSeconds(offsetSeconds))));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-5")]
    [InlineData("99999999999999999999")]
    public void AMalformedTimestamp_DoesNotVerify(string timestamp)
        => Assert.False(WebhookSignature.Verify(Secret, timestamp, "sha256=00", Tenant, Body, Now));

    [Fact]
    public void AMissingSignature_DoesNotVerify()
        => Assert.False(WebhookSignature.Verify(Secret, Ts(Now), null, Tenant, Body, Now));

    [Fact]
    public void AWrongLengthSignature_DoesNotVerify()
        => Assert.False(Verify(Ts(Now), signature: "sha256=abc"));

    /// <summary>
    /// The tenant/body boundary is unambiguous: tenant "a" with a body starting "b." must not sign
    /// the same bytes as tenant "a.b" with the rest of that body.
    /// </summary>
    [Fact]
    public void ShiftingBytesBetweenTenantAndBody_ChangesTheSignature()
    {
        var ts = Ts(Now);
        var asA = WebhookSignature.Compute(Secret, ts, "a", Encoding.UTF8.GetBytes("b.{}"));
        var asAb = WebhookSignature.Compute(Secret, ts, "a.b", Encoding.UTF8.GetBytes("{}"));

        Assert.NotEqual(asA, asAb);
    }
}
