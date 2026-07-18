using System.Text.Json;
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.Notifications.SendNotification;
using Cordia.Application.Handlers.Waitlist.Expire;
using Cordia.Application.Handlers.Waitlist.Match;
using Cordia.Application.Messaging;
using Cordia.Domain.Enums;
using Cordia.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Spec 11 §5.1 — the customer-facing offer / offer-expired events MUST have registered consumers.
/// Without these registrations <c>PostgresMessageTransport</c> treats the outbox rows as delivered
/// and silently drops them: every offer would expire unanswered (review PR #65, finding 1).
/// </summary>
[Trait("Category", "Unit")]
public class WaitlistOfferNotificationRegistrationTests
{
    private static ConsumerRegistry BuildProductionRegistry()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ConsumerRegistry>();
    }

    private static JobEnvelope Envelope(string messageType) =>
        new(
            MessageId: "msg_wl_notify_001",
            MessageType: messageType,
            OccurredAt: DateTimeOffset.UtcNow,
            TenantId: "tenant_test",
            CorrelationId: "enr_001",
            CausationId: null,
            OperationId: "op_wl_abc",
            IntentId: null,
            IntentRevision: null,
            AttemptHint: 0,
            Payload: JsonDocument.Parse("{\"offer_id\":\"wlo_001\"}").RootElement);

    [Fact]
    public void OfferNotified_MapsToCriticalWaitlistOfferSend_NotOperationBound()
    {
        var registry = BuildProductionRegistry();

        var found = registry.TryGet(MatchWaitlistOfferCommandHandler.OfferNotifiedEventType, out var factory);

        Assert.True(found);
        var request = Assert.IsType<SendNotificationCommand>(
            factory!(Envelope(MatchWaitlistOfferCommandHandler.OfferNotifiedEventType)));
        Assert.Equal(NotificationEvent.WaitlistOffer, request.EventType);
        Assert.Equal(MessagePriority.Critical, request.Priority);
        Assert.Equal("op_wl_abc", request.OperationId);
        Assert.False(request.RequiresOperation); // no booking_operations row until accept
    }

    [Fact]
    public void OfferExpired_MapsToWaitlistOfferExpiredSend_NotOperationBound()
    {
        var registry = BuildProductionRegistry();

        var found = registry.TryGet(ExpireWaitlistOfferCommandHandler.OfferExpiredEventType, out var factory);

        Assert.True(found);
        var request = Assert.IsType<SendNotificationCommand>(
            factory!(Envelope(ExpireWaitlistOfferCommandHandler.OfferExpiredEventType)));
        Assert.Equal(NotificationEvent.WaitlistOfferExpired, request.EventType);
        Assert.Equal(MessagePriority.Normal, request.Priority);
        Assert.Equal("op_wl_abc", request.OperationId);
        Assert.False(request.RequiresOperation);
    }
}
