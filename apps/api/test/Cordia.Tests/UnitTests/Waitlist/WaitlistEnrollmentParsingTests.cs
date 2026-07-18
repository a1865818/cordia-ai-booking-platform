using Cordia.Domain.Entities;
using Cordia.Domain.Waitlist;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Unit tests for the typed projection of the <see cref="WaitlistEnrollment"/> jsonb columns
/// (<c>auto_book</c> / <c>deposit_terms</c>, spec 11 §4.4) and the <see cref="AutoBookGrant.Permits"/>
/// consent-constraint check (§20).
/// </summary>
[Trait("Category", "Unit")]
public class WaitlistEnrollmentParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private static AutoBookGrant Grant() => new(
        ConsentEvidenceId: "consent_1",
        ConsentedAt: Now,
        MaxPartySize: 4,
        ConstraintFrom: Now,
        ConstraintTo: Now.AddDays(7),
        MaxDepositAmountMinor: 5000,
        DepositCurrency: "AUD");

    [Fact]
    public void AutoBookGrant_RoundTripsThroughJsonbColumn()
    {
        var grant = Grant();
        var enrollment = new WaitlistEnrollment { AutoBook = grant.ToJson() };

        var restored = enrollment.GetAutoBookGrant();

        Assert.Equal(grant, restored);
    }

    [Fact]
    public void DepositTerms_RoundTripsThroughJsonbColumn()
    {
        var terms = new DepositTerms(2500, "AUD");
        var enrollment = new WaitlistEnrollment { DepositTerms = terms.ToJson() };

        Assert.Equal(terms, enrollment.GetDepositTerms());
    }

    [Fact]
    public void NullColumns_ParseToNull()
    {
        var enrollment = new WaitlistEnrollment { AutoBook = null, DepositTerms = null };

        Assert.Null(enrollment.GetAutoBookGrant());
        Assert.Null(enrollment.GetDepositTerms());
    }

    [Fact]
    public void Permits_WithinAllConstraints_ReturnsTrue()
    {
        var grant = Grant();
        Assert.True(grant.Permits(2, Now.AddDays(1), new DepositTerms(3000, "AUD")));
        Assert.True(grant.Permits(4, Now.AddDays(1), requiredDeposit: null)); // no deposit required
    }

    [Theory]
    [InlineData(5)]   // party too large (max 4)
    public void Permits_PartyOverMax_ReturnsFalse(int partySize)
        => Assert.False(Grant().Permits(partySize, Now.AddDays(1), null));

    [Fact]
    public void Permits_SlotOutsideConsentedWindow_ReturnsFalse()
    {
        var grant = Grant();
        Assert.False(grant.Permits(2, Now.AddDays(-1), null));  // before ConstraintFrom
        Assert.False(grant.Permits(2, Now.AddDays(30), null));  // after ConstraintTo
    }

    [Fact]
    public void Permits_DepositOverCeilingOrWrongCurrency_ReturnsFalse()
    {
        var grant = Grant();
        Assert.False(grant.Permits(2, Now.AddDays(1), new DepositTerms(6000, "AUD")));  // over ceiling (5000)
        Assert.False(grant.Permits(2, Now.AddDays(1), new DepositTerms(1000, "USD")));  // currency mismatch
    }
}
