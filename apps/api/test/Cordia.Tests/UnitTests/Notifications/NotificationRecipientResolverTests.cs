#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Providers;
using Moq;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// The recipient resolver is what turns a guest on the confirmed revision into somewhere to
/// deliver. Before it read the revision it returned <c>SHA256(tenant|operation)</c> for every
/// operation, so every booking had a fictional destination and <c>no_destination</c> was
/// unreachable.
///
/// These pin the two properties that matter: a real destination is found, and the persisted /
/// logged handle never carries it.
/// </summary>
[Trait("Category", "Unit")]
public class NotificationRecipientResolverTests
{
    private const string TenantId = "tenant-a";
    private const string OperationId = "op-1";
    private const string IntentId = "int-1";

    private readonly Mock<IBookingOperationRepository> _operations = new();
    private readonly Mock<IBookingIntentRepository> _intents = new();
    private readonly Mock<IWaitlistOfferRepository> _offers = new();
    private readonly Mock<IWaitlistEnrollmentRepository> _enrollments = new();

    // Every payload in this suite uses the LEGACY inline shape, so the contact store is never
    // consulted — a default mock returns null from GetByRefAsync, which is also the correct
    // answer for an erased ref.
    private readonly Mock<IGuestContactRepository> _guestContacts = new();

    private NotificationRecipientResolver BuildResolver()
        => new(_operations.Object, _intents.Object, _offers.Object, _enrollments.Object,
               _guestContacts.Object);

    /// <summary>Wires operation → offer → enrollment, the waitlist notification's only route.</summary>
    private void GivenWaitlistOffer(string customerRef, string channel = "sms", string operationId = "op_wl_1")
    {
        _offers.Setup(o => o.GetByOperationIdAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WaitlistOffer
            {
                TenantId = TenantId,
                OfferId = "wlo_1",
                EnrollmentId = "wle_1",
                OperationId = operationId,
            });
        _enrollments.Setup(e => e.GetAsync("wle_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WaitlistEnrollment
            {
                TenantId = TenantId,
                EnrollmentId = "wle_1",
                CustomerRef = customerRef,
                NotificationChannel = channel,
            });
    }

    private void GivenOperation(string tenantId = TenantId, string operationId = OperationId)
        => _operations
            .Setup(o => o.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = tenantId,
                OperationId = operationId,
                IntentId = IntentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.Accepted,
            });

    private void GivenRevisionPayload(object payload, string tenantId = TenantId)
        => _intents
            .Setup(i => i.GetRevisionAsync(tenantId, IntentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = IntentId,
                Revision = 1,
                CanonicalPayload = JsonSerializer.Serialize(payload),
                PayloadHash = "hash",
            });

    [Fact]
    public async Task ResolvesThePhoneFromTheGuestOnTheRevision()
    {
        const string phone = "+61400111222";
        GivenOperation();
        GivenRevisionPayload(new
        {
            operation = "create",
            customer = new { customer_id = "cust-1", phone_e164 = phone },
        });

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        Assert.NotNull(recipient);
        Assert.Equal(phone, recipient.Destination);
        Assert.Equal(NotificationChannel.Sms, recipient.Channel);
    }

    [Fact]
    public async Task TheMinimizedHandleNeverCarriesTheContactValue()
    {
        const string phone = "+61400111222";
        GivenOperation();
        GivenRevisionPayload(new
        {
            operation = "create",
            customer = new { phone_e164 = phone },
        });

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        // RecipientRef is persisted on notifications.recipient_ref and logged as
        // RenderedMessage.MinimizedRecipient — it must be a token, never the number.
        Assert.NotNull(recipient);
        Assert.DoesNotContain(phone, recipient.RecipientRef);
        Assert.DoesNotContain("400111222", recipient.RecipientRef);
        Assert.StartsWith("recip_", recipient.RecipientRef);
    }

    /// <summary>
    /// Inv-9. `RecipientRef` feeds `NotificationIdentity.Derive(...)`, so if it moved when a guest
    /// was present, a message redelivered across the deploy that added guests would derive a
    /// different `notification_id`, miss logical dedup, and notify the guest twice.
    ///
    /// The ref must therefore stay exactly what the previous stub produced —
    /// `SHA256(tenant|operationId)` — whether or not a guest is attached.
    /// </summary>
    [Fact]
    public async Task TheHandleIsUnchangedByTheGuestSoNotificationIdentityCannotMove()
    {
        GivenOperation();
        GivenRevisionPayload(new
        {
            operation = "create",
            customer = new { phone_e164 = "+61400111222" },
        });
        var withGuest = await BuildResolver().ResolveAsync(TenantId, OperationId);

        // Same tenant + operation, but no guest on the revision.
        var bare = new NotificationRecipientResolverTests();
        bare.GivenOperation();
        bare.GivenRevisionPayload(new { operation = "create" });
        var withoutGuest = await bare.BuildResolver().ResolveAsync(TenantId, OperationId);

        Assert.Equal(withoutGuest!.RecipientRef, withGuest!.RecipientRef);
        // ...while the destination is the part that differs.
        Assert.Equal("+61400111222", withGuest.Destination);
        Assert.Null(withoutGuest.Destination);
    }

    [Fact]
    public async Task NeverTakesTheDestinationFromOutsideTheCustomerNode()
    {
        // CanonicalBookingPayload (used by the READ projection) searches the whole payload
        // recursively, stops at the first key match, and its key list includes a bare "phone"
        // beside explicit business/venue support. Fine for a display projection; not for
        // deciding where to deliver — the venue's own number must never capture the send.
        GivenOperation();
        GivenRevisionPayload(new
        {
            business = new { name = "Serenity Spa", phone = "+61399990000" },
            operation = "create",
        });

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        Assert.NotNull(recipient);
        Assert.Null(recipient.Destination);
        Assert.DoesNotContain("399990000", recipient.RecipientRef);
    }

    [Fact]
    public async Task PrefersTheGuestPhoneOverAVenuePhoneInTheSamePayload()
    {
        GivenOperation();
        GivenRevisionPayload(new
        {
            business = new { name = "Serenity Spa", phone = "+61399990000" },
            operation = "create",
            customer = new { phone_e164 = "+61400111222" },
        });

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        Assert.Equal("+61400111222", recipient!.Destination);
    }

    [Fact]
    public async Task FallsBackToThePlaceholderHandleWhenTheRevisionHasNoGuest()
    {
        GivenOperation();
        GivenRevisionPayload(new { operation = "create", service_id = "svc-1" });

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        // No guest ⇒ nowhere to deliver, but the pre-existing handle is preserved so nothing
        // that worked before this change starts failing.
        Assert.NotNull(recipient);
        Assert.Null(recipient.Destination);
        Assert.StartsWith("recip_", recipient.RecipientRef);
    }

    [Fact]
    public async Task DoesNotResolveAnEmailDestination_TheContractHasNoEmailSlot()
    {
        GivenOperation();
        // `extra: forbid` on CustomerRef means this payload could not be produced by either
        // lane; the test exists to pin that the resolver does not pretend otherwise. A guest who
        // asked to be emailed must not be handed an SMS channel with no address.
        GivenRevisionPayload(new
        {
            operation = "create",
            customer = new { email = "guest@example.com" },
        });

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        Assert.NotNull(recipient);
        Assert.Null(recipient.Destination);
        Assert.Equal(NotificationChannel.Sms, recipient.Channel);
    }

    // -------------------------------------------------------------------------
    // Inv-13 — one tenant's notification must never resolve another tenant's guest.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DoesNotResolveAnotherTenantsGuest()
    {
        // The operation row belongs to tenant-b, but we ask as tenant-a. GetAsync leans on the
        // ambient query filter, so this simulates a mis-scoped context: the explicit re-check
        // must refuse it rather than hand back tenant-b's phone.
        GivenOperation(tenantId: "tenant-b");
        GivenRevisionPayload(
            new { operation = "create", customer = new { phone_e164 = "+61400999888" } },
            tenantId: "tenant-b");

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        Assert.NotNull(recipient);
        Assert.Null(recipient.Destination);
        Assert.DoesNotContain("999888", recipient.RecipientRef);
    }

    [Fact]
    public async Task FallsBackWhenTheOperationIsUnknown()
    {
        _operations
            .Setup(o => o.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var recipient = await BuildResolver().ResolveAsync(TenantId, "op-missing");

        Assert.NotNull(recipient);
        Assert.Null(recipient.Destination);
    }

    [Fact]
    public async Task FallsBackWhenTheCanonicalPayloadIsUnparseable()
    {
        GivenOperation();
        _intents
            .Setup(i => i.GetRevisionAsync(TenantId, IntentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = IntentId,
                Revision = 1,
                CanonicalPayload = "{not json",
                PayloadHash = "hash",
            });

        var recipient = await BuildResolver().ResolveAsync(TenantId, OperationId);

        // A malformed historical row must not throw inside the notification lane.
        Assert.NotNull(recipient);
        Assert.Null(recipient.Destination);
    }

    // -------------------------------------------------------------------------
    // The waitlist lane (spec 10 §13). A waitlist offer's op_wl_* id has no
    // booking_operations row, so the booking lookup always misses for it — which is why an
    // earlier no-destination guard terminal-failed this lane.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResolvesAWaitlistOfferDestinationFromTheEnrollment()
    {
        _operations.Setup(o => o.GetAsync("op_wl_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);
        GivenWaitlistOffer("+61400111222");

        var recipient = await BuildResolver().ResolveAsync(TenantId, "op_wl_1");

        Assert.Equal("+61400111222", recipient!.Destination);
        Assert.Equal(NotificationChannel.Sms, recipient.Channel);
    }

    [Fact]
    public async Task AnEnrollmentIdentifiedOnlyByNameHasNoDestination()
    {
        // CustomerRef is captured as "Name or contact handle". A name is not somewhere a message
        // can be sent, and saying otherwise would be the exact fiction this work removes.
        _operations.Setup(o => o.GetAsync("op_wl_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);
        GivenWaitlistOffer("John Smith");

        var recipient = await BuildResolver().ResolveAsync(TenantId, "op_wl_1");

        Assert.NotNull(recipient);
        Assert.Null(recipient.Destination);
    }

    [Fact]
    public async Task ResolvesAnEmailEnrollmentOnTheEmailChannel()
    {
        _operations.Setup(o => o.GetAsync("op_wl_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);
        GivenWaitlistOffer("guest@example.com", channel: "email");

        var recipient = await BuildResolver().ResolveAsync(TenantId, "op_wl_1");

        Assert.Equal("guest@example.com", recipient!.Destination);
        Assert.Equal(NotificationChannel.Email, recipient.Channel);
    }

    [Fact]
    public async Task ANationalNumberIsNotTreatedAsDeliverable()
    {
        // Without a country there is nothing to dial. PhoneMatching deliberately does not guess.
        _operations.Setup(o => o.GetAsync("op_wl_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);
        GivenWaitlistOffer("0400111222");

        var recipient = await BuildResolver().ResolveAsync(TenantId, "op_wl_1");

        Assert.Null(recipient!.Destination);
    }

    [Fact]
    public async Task DoesNotResolveAnotherTenantsEnrollment()
    {
        _operations.Setup(o => o.GetAsync("op_wl_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);
        _offers.Setup(o => o.GetByOperationIdAsync("op_wl_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WaitlistOffer
            {
                TenantId = "tenant-b",
                OfferId = "wlo_1",
                EnrollmentId = "wle_1",
                OperationId = "op_wl_1",
            });

        var recipient = await BuildResolver().ResolveAsync(TenantId, "op_wl_1");

        Assert.Null(recipient!.Destination);
    }
}
