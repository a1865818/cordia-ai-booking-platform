using Cordia.Application.Handlers.Waitlist.Enroll;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Spec 11 §5.1 — the enrollment's captured verification level is stamped verbatim onto the
/// acceptance confirmation; an unknown value would be silently coerced to Unverified at accept
/// time, discarding the assurance the "verified follow-up" depends on (review PR #65, finding 8).
/// The validator must reject unknown values at the edge instead.
/// </summary>
[Trait("Category", "Unit")]
public class EnrollWaitlistValidatorTests
{
    private static EnrollWaitlistCommand ValidCommand(string? verificationLevel) => new(
        TenantId: "t_1",
        Actor: "cust_1",
        CustomerRef: "cust_1",
        AcceptableFrom: DateTimeOffset.UtcNow,
        AcceptableTo: DateTimeOffset.UtcNow.AddDays(7),
        PartySize: 2,
        LocationId: "loc_1",
        ServiceId: "svc_1",
        OfferLifetimeSeconds: 900,
        Deposit: null,
        NotificationChannel: "email",
        AutoBook: null,
        IdentityRef: "id_cust_1",
        VerificationLevel: verificationLevel);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unverified")]
    [InlineData("channel_verified")]
    [InlineData("identity_verified")]
    [InlineData("high_assurance")]
    [InlineData("Channel_Verified")] // case-insensitive, matches MapVerificationLevel
    public void KnownOrAbsentVerificationLevel_Passes(string? level)
    {
        var result = new EnrollWaitlistValidator().Validate(ValidCommand(level));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("identityVerified")]
    [InlineData("high-assurance")]
    public void UnknownVerificationLevel_IsRejectedAtTheEdge(string level)
    {
        var result = new EnrollWaitlistValidator().Validate(ValidCommand(level));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "VerificationLevel");
    }
}
