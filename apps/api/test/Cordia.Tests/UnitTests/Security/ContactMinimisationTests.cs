using Cordia.Domain.Bookings;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// `customer_ref` leaves the ungated console list reads MINIMISED (spec 14, Inv-12).
///
/// <para>It was documented as opaque and was not: `bookings.customer_id` resolves through
/// <c>customer_id → contact_ref → caller_id → phone_e164 → email</c>, and
/// <c>waitlist_enrollments.customer_ref</c> is the value the notification resolver hands to the
/// SMS/email adapter as the send destination. The console mints an opaque <c>contact_ref</c>, but
/// the agent lane never does — so agent-created bookings put a raw phone number on
/// <c>GET /api/customers</c>, <c>/api/Bookings/schedule</c>, <c>/api/conversation-sessions</c> and
/// <c>/api/waitlist/enrollments</c>, all of which need nothing but tenant scope: no grant, no
/// reason code, and no audit row — a lower bar than the transcript read of the same data.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ContactMinimisationTests
{
    [Theory]
    [InlineData("+61400111222")]
    [InlineData("+61 400 111 222")]
    [InlineData("(+61) 400-111-222")]
    [InlineData("0400111222")]
    public void APhoneNumberNeverSurvivesIntact(string raw)
    {
        var masked = ContactMinimisation.Mask(raw);

        Assert.NotEqual(raw, masked);
        Assert.DoesNotContain("400111", masked);
        // Enough tail to recognise a guest already known, never enough to learn one.
        Assert.EndsWith("222", masked);
        Assert.True(ContactMinimisation.IsContactAddress(raw));
    }

    [Fact]
    public void AnEmailKeepsOnlyItsFirstCharacterAndDomain()
    {
        var masked = ContactMinimisation.Mask("priya.nguyen@example.com");

        Assert.Equal("p•••@example.com", masked);
        Assert.DoesNotContain("nguyen", masked);
    }

    [Theory]
    // The opaque handle the console mints — already minimised, and the projection's whole point.
    [InlineData("gc_9f2c41ab8e004d10")]
    // A provider reference, and an internal operation id.
    [InlineData("prov-1")]
    [InlineData("op_7c1d")]
    public void AnOpaqueHandlePassesThroughUntouched(string raw)
    {
        Assert.Equal(raw, ContactMinimisation.Mask(raw));
        Assert.False(ContactMinimisation.IsContactAddress(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentReferenceIsLeftAlone(string? raw)
    {
        Assert.Equal(raw, ContactMinimisation.Mask(raw));
        Assert.False(ContactMinimisation.IsContactAddress(raw));
    }

    [Fact]
    public void MaskingIsStableSoAGroupedProjectionKeepsOneLabelPerGuest()
    {
        // Grouping happens on the RAW value, but the label must not flap between reads.
        Assert.Equal(
            ContactMinimisation.Mask("+61400111222"),
            ContactMinimisation.Mask("+61 400 111 222"));
    }

    [Fact]
    public void AVeryShortNumberIsNotPartiallyRevealed()
    {
        // Nothing to keep back if the tail IS the number.
        Assert.Equal("•••", ContactMinimisation.Mask("+12"));
    }
}
