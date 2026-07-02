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

    // ── §20: AutoBookGrant consent-evidence + constraint validation (review 2026-07-03, C11) ────

    private static Cordia.Domain.Waitlist.AutoBookGrant Grant(string evidenceId) => new(
        ConsentEvidenceId: evidenceId,
        ConsentedAt: DateTimeOffset.UtcNow,
        MaxPartySize: 4,
        ConstraintFrom: DateTimeOffset.UtcNow,
        ConstraintTo: DateTimeOffset.UtcNow.AddDays(30),
        MaxDepositAmountMinor: 5000,
        DepositCurrency: "AUD");

    [Fact]
    public void AutoBook_WithRecordedConsent_Passes()
    {
        var cmd = ValidCommand(null) with { AutoBook = Grant("consent_ev_1") };

        Assert.True(new EnrollWaitlistValidator().Validate(cmd).IsValid);
    }

    [Fact]
    public void AutoBook_EmptyConsentEvidence_IsRejected()
    {
        // An empty evidence id must never authorize silent auto-booking (§20).
        var cmd = ValidCommand(null) with { AutoBook = Grant("") };

        var result = new EnrollWaitlistValidator().Validate(cmd);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("ConsentEvidenceId"));
    }

    [Fact]
    public void AutoBook_InvertedConstraintWindow_IsRejected()
    {
        var grant = Grant("consent_ev_1") with
        {
            ConstraintFrom = DateTimeOffset.UtcNow.AddDays(30),
            ConstraintTo = DateTimeOffset.UtcNow,
        };
        var cmd = ValidCommand(null) with { AutoBook = grant };

        Assert.False(new EnrollWaitlistValidator().Validate(cmd).IsValid);
    }

    [Fact]
    public void Permits_EmptyConsentEvidence_IsFailSecure()
    {
        // Defense in depth for grants persisted before the validator existed: the domain check
        // itself refuses to authorize an evidence-less grant, so the match falls back to an offer.
        var grant = Grant("");

        Assert.False(grant.Permits(2, DateTimeOffset.UtcNow.AddDays(1), null));
        Assert.True((grant with { ConsentEvidenceId = "consent_ev_1" })
            .Permits(2, DateTimeOffset.UtcNow.AddDays(1), null));
    }
}
