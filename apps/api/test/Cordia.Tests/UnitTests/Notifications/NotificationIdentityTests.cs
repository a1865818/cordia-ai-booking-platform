using Cordia.Domain.Enums;
using Cordia.Domain.Notifications;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Unit tests for <see cref="NotificationIdentity.Derive"/> (spec 10 §22.1 / Inv-9).
/// Covers: determinism, collision-resistance, id format.
/// </summary>
[Trait("Category", "Unit")]
public class NotificationIdentityTests
{
    private const string TenantId = "tenant-1";
    private const string OperationId = "op-abc";
    private const string RecipientRef = "cust-xyz";
    private const string TemplateVersion = "v1";
    private const int MaterialRevision = 1;

    // (1) Same inputs produce the same id
    [Fact]
    public void Derive_SameInputs_ReturnsSameId()
    {
        var id1 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, TemplateVersion, MaterialRevision);
        var id2 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, TemplateVersion, MaterialRevision);

        Assert.Equal(id1, id2);
    }

    // (1b) EXHAUSTIVE label coverage: Derive indexes EventLabels directly, so a NotificationEvent
    // member without an entry crashes the send inside the inbox transaction (poison loop → DLQ).
    // WaitlistOfferExpired shipped without one and every offer-expired notification died on
    // KeyNotFoundException — this test makes the next enum addition fail here instead.
    [Fact]
    public void Derive_SupportsEveryNotificationEventMember()
    {
        foreach (var eventType in Enum.GetValues<NotificationEvent>())
        {
            var id = NotificationIdentity.Derive(
                TenantId, OperationId, eventType, RecipientRef, TemplateVersion, MaterialRevision);
            Assert.StartsWith("ntf_", id);
        }
    }

    // (2) Changing materialRevision yields a different id
    [Fact]
    public void Derive_DifferentMaterialRevision_ReturnsDifferentId()
    {
        var id1 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, TemplateVersion, 1);
        var id2 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, TemplateVersion, 2);

        Assert.NotEqual(id1, id2);
    }

    // (3) Changing eventType yields a different id
    [Fact]
    public void Derive_DifferentEventType_ReturnsDifferentId()
    {
        var id1 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, TemplateVersion, MaterialRevision);
        var id2 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.RequestAccepted, RecipientRef, TemplateVersion, MaterialRevision);

        Assert.NotEqual(id1, id2);
    }

    // (4) Changing recipientRef yields a different id
    [Fact]
    public void Derive_DifferentRecipientRef_ReturnsDifferentId()
    {
        var id1 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, "cust-a", TemplateVersion, MaterialRevision);
        var id2 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, "cust-b", TemplateVersion, MaterialRevision);

        Assert.NotEqual(id1, id2);
    }

    // (5) Changing templateVersion yields a different id
    [Fact]
    public void Derive_DifferentTemplateVersion_ReturnsDifferentId()
    {
        var id1 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, "v1", MaterialRevision);
        var id2 = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, "v2", MaterialRevision);

        Assert.NotEqual(id1, id2);
    }

    // (6) Id has "ntf_" prefix
    [Fact]
    public void Derive_ReturnsIdWithNtfPrefix()
    {
        var id = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, TemplateVersion, MaterialRevision);

        Assert.StartsWith("ntf_", id);
    }

    // (7) Id is "ntf_" + 64 lowercase hex chars (SHA-256 = 32 bytes = 64 hex)
    [Fact]
    public void Derive_ReturnsNtfPrefixPlusSixtyFourHexChars()
    {
        var id = NotificationIdentity.Derive(TenantId, OperationId, NotificationEvent.BookingConfirmed, RecipientRef, TemplateVersion, MaterialRevision);

        // Total length = 4 ("ntf_") + 64 hex = 68
        Assert.Equal(68, id.Length);
        Assert.Matches("^ntf_[0-9a-f]{64}$", id);
    }
}
