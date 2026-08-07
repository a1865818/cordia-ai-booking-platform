using Cordia.Application.Handlers.BookingFulfillment.Finalize;
using Cordia.Domain.Bookings;
using Cordia.Domain.Capacity;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Providers;

namespace Cordia.Tests.UnitTests.Hardening;

/// <summary>
/// Regression cover for hardening changes that shipped without dedicated tests.
///
/// <para>Each of these is a rule that is invisible at the call site — a precedence order, a
/// conflict resolution, a soft-failure contract — so nothing but a test states what the behaviour
/// is supposed to be, and a later edit can quietly reverse it.</para>
/// </summary>
[Trait("Category", "Unit")]
public class HardeningRegressionTests
{
    // ── Canonical-payload key precedence (FinalizeBookingCommandHandler) ────────────────────────

    /// <summary>
    /// The CALLER's order decides, not the document's.
    ///
    /// <para>The search walks one name at a time in the order the call site listed them. Walking the
    /// document instead returned whichever listed name it met first, so `customer_id` outranked
    /// `contact_ref` only when the producer happened to emit it earlier.</para>
    /// </summary>
    [Fact]
    public void ReadFirstString_HonoursTheCallersPrecedence_NotTheDocumentOrder()
    {
        // `contact_ref` appears FIRST in the document; `customer_id` is listed first by the caller.
        const string payload = """{"contact_ref":"gc_ref","customer_id":"cust_1"}""";

        Assert.Equal(
            "cust_1",
            FinalizeBookingCommandHandler.ReadFirstString(payload, "customer_id", "contact_ref"));
    }

    /// <summary>
    /// A shallow match beats a deep one, even when the deep one sits under an earlier property.
    ///
    /// <para>This is the case that made the precedence merely incidental: a depth-first descent
    /// exhausted the whole `preorder` subtree before looking at the next top-level key, so a nested
    /// `customer_id` won. That value becomes the booking's <c>CustomerId</c>, which a verification
    /// grant is later matched against — locking the real guest out of their own booking.</para>
    /// </summary>
    [Fact]
    public void ReadFirstString_PrefersAShallowMatchOverADeeplyNestedOne()
    {
        const string payload = """
            {"preorder":{"items":[{"meta":{"customer_id":"cust_stale"}}]},
             "customer":{"customer_id":"cust_real"}}
            """;

        Assert.Equal("cust_real", FinalizeBookingCommandHandler.ReadFirstString(payload, "customer_id"));
    }

    [Fact]
    public void ReadFirstString_FallsThroughABlankValueToTheNextName()
    {
        const string payload = """{"customer_id":"   ","contact_ref":"gc_ref"}""";

        Assert.Equal(
            "gc_ref",
            FinalizeBookingCommandHandler.ReadFirstString(payload, "customer_id", "contact_ref"));
    }

    [Fact]
    public void ReadFirstString_TreatsUnparseableJsonAsAbsent()
        => Assert.Null(FinalizeBookingCommandHandler.ReadFirstString("{not json", "customer_id"));

    // ── Notification channel conflict (NotificationRecipientResolver) ───────────────────────────

    /// <summary>
    /// The channel is derived from the DELIVERABLE ADDRESS, not from what the enrolment asked for.
    ///
    /// <para>An enrolment stating "sms" whose only contact is an email address cannot be texted.
    /// Honouring the stated channel produced a notification addressed to an email over the SMS
    /// transport — accepted by nothing, delivered to no one.</para>
    /// </summary>
    [Theory]
    [InlineData("sms", NotificationChannel.Email, NotificationChannel.Email)]
    [InlineData("email", NotificationChannel.Sms, NotificationChannel.Sms)]
    [InlineData("SMS", NotificationChannel.Email, NotificationChannel.Email)]
    public void ChannelFor_LetsTheAddressWinWhenTheStatedChannelConflicts(
        string stated, NotificationChannel implied, NotificationChannel expected)
        => Assert.Equal(expected, NotificationRecipientResolver.ChannelFor(stated, implied));

    /// <summary>When the two AGREE, the stated channel is honoured — it is not being ignored.</summary>
    [Theory]
    [InlineData("sms", NotificationChannel.Sms, NotificationChannel.Sms)]
    [InlineData("email", NotificationChannel.Email, NotificationChannel.Email)]
    public void ChannelFor_HonoursTheStatedChannelWhenTheAddressAgrees(
        string stated, NotificationChannel implied, NotificationChannel expected)
        => Assert.Equal(expected, NotificationRecipientResolver.ChannelFor(stated, implied));

    /// <summary>
    /// `channel` is the in-conversation transport and is never contradicted by the address: it is
    /// not an address at all, so there is nothing to conflict with.
    /// </summary>
    [Fact]
    public void ChannelFor_KeepsTheInConversationChannelRegardlessOfTheAddress()
        => Assert.Equal(
            NotificationChannel.Channel,
            NotificationRecipientResolver.ChannelFor("channel", NotificationChannel.Email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("carrier-pigeon")]
    public void ChannelFor_FallsBackToTheAddressForAnythingUnrecognised(string? stated)
        => Assert.Equal(
            NotificationChannel.Sms,
            NotificationRecipientResolver.ChannelFor(stated, NotificationChannel.Sms));

    // ── Canonical payload → slot key: every failure is soft ─────────────────────────────────────

    /// <summary>
    /// A payload whose root is not an object degrades to "not tracked" rather than throwing.
    ///
    /// <para><c>JsonElement.TryGetProperty</c> raises <c>InvalidOperationException</c> — not
    /// <c>JsonException</c> — on a non-object element, so the parse's catch never saw it and a
    /// historical revision shaped like <c>[]</c> aborted the operation that read it.</para>
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("{not json")]
    public void DeriveFromCanonicalPayload_TreatsAnUnusablePayloadAsUntracked(string payload)
    {
        var (capacity, slotId) = CapacitySlotKey.DeriveFromCanonicalPayload(payload);

        Assert.Equal(1, capacity);
        Assert.Null(slotId);
    }

    [Fact]
    public void DeriveFromCanonicalPayload_ReadsPartySizeAndSlotFromAWellFormedPayload()
    {
        var (capacity, slotId) = CapacitySlotKey.DeriveFromCanonicalPayload(
            """{"party_size":4,"resource_id":"r1","starts_at":"2026-09-02T09:00:00.000Z"}""");

        Assert.Equal(4, capacity);
        Assert.NotNull(slotId);
        Assert.StartsWith("r1|", slotId);
    }

    // ── Email as a DESTINATION, not merely a non-blank string ───────────────────────────────────

    /// <summary>
    /// The counterpart to phone normalisation: a handle whose only channel cannot be delivered to
    /// is the same dead ref, reached through the other field.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("a")]
    [InlineData("guest@")]
    [InlineData("@example.com")]
    [InlineData("guest@localhost")]
    [InlineData("Guest <guest@example.com>")]
    [InlineData("a@b.com, c@d.com")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormaliseEmail_RejectsAnythingThatIsNotASingleDeliverableAddress(string? email)
        => Assert.Null(ContactAddress.NormaliseEmail(email));

    [Theory]
    [InlineData("guest@example.com", "guest@example.com")]
    [InlineData("  guest@example.com  ", "guest@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk", "first.last+tag@sub.example.co.uk")]
    public void NormaliseEmail_AcceptsAndTrimsARealAddress(string input, string expected)
        => Assert.Equal(expected, ContactAddress.NormaliseEmail(input));

    /// <summary>An address past the RFC path ceiling is refused rather than stored.</summary>
    [Fact]
    public void NormaliseEmail_RejectsAnAbsurdlyLongAddress()
        => Assert.Null(ContactAddress.NormaliseEmail(new string('a', 250) + "@example.com"));
}
