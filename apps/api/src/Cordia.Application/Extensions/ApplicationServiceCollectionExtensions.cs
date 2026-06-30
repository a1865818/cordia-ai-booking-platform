using System.Text.Json;
using Cordia.Application.Behaviours;
using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.AdvanceOperation;
using Cordia.Application.Handlers.BookingFulfillment.DepositEvent;
using Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;
using Cordia.Application.Handlers.BookingFulfillment.Sla;
using Cordia.Application.Handlers.BookingFulfillment.UserAction;
using Cordia.Application.Handlers.Capacity.ReleaseExpiredHolds;
using Cordia.Application.Handlers.Menu.IngestVendorMenu;
using Cordia.Application.Handlers.Notifications;
using Cordia.Application.Handlers.Notifications.MissingTerminalAudit;
using Cordia.Application.Handlers.Notifications.Reconcile;
using Cordia.Application.Handlers.Notifications.Retry;
using Cordia.Application.Handlers.Notifications.SendNotification;
using Cordia.Application.Messaging;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Application.Extensions;

/// <summary>Registers the Application layer (MediatR handlers, validators).</summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(ApplicationServiceCollectionExtensions).Assembly;

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            // Pipeline order: 1) Validation → 2) Inbox dedup → 3) Handler.
            // Validation runs first so structurally invalid queue-driven requests are rejected
            // before the inbox transaction is opened.
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationPipelineBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(InboxDeduplicationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly);

        // Consumer dispatch seam (§3, OD-1): registry is a singleton catalog written once at startup;
        // dispatcher is scoped because IMediator is scoped.
        // The booking driver consumer is pre-registered for the two message types that trigger
        // operation advancement (spec 06 §6.2). The driver command is a PLAIN IRequest (not
        // IInboxConsumerRequest) so InboxDeduplicationBehavior does not wrap the multi-step loop.
        services.AddSingleton(_ =>
        {
            var registry = new ConsumerRegistry();
            // T11: pass MessageType + Payload from the envelope so the driver can reconstruct a
            // JobEnvelope for DLQ quarantine if a PoisonMessageException or invariant-violation
            // exception escapes a step handler (Inv-14, §17.2).
            Func<JobEnvelope, IBaseRequest> driver = env =>
                new AdvanceBookingOperationCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    env.MessageId,
                    env.MessageType,
                    env.Payload);
            registry.Register("booking.command.requested.v1", driver);
            registry.Register("booking.step.continue.v1", driver);
            // booking.reconcile.requested.v1: sweeper/external trigger for Reconciling ops.
            // The driver routes by op.Status so this is harmless for non-Reconciling ops.
            registry.Register("booking.reconcile.requested.v1", driver);

            // T7: deposit lifecycle events — inbox-deduplicated consumers.
            registry.Register("payment.deposit_paid.v1",
                env => new ProcessDepositEventCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    DepositEventKind.Paid,
                    env.MessageId));
            registry.Register("payment.deposit_expired.v1",
                env => new ProcessDepositEventCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    DepositEventKind.Expired,
                    env.MessageId));

            // T7: user action completed event — inbox-deduplicated consumer.
            registry.Register("booking.user_action.completed.v1",
                env => new ResumeFromUserActionCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    env.MessageId));

            // T6 (spec 07): scheduled hold-expiry job — releases a single hold by ID.
            // hold_id is extracted from the job payload JSON (set by CapacityExpiryScheduler).
            // Scope.Hold: if hold_id is missing the handler FAILS CLOSED (FIX 5 — no bulk release).
            registry.Register("booking.scheduled.hold_expiry.v1",
                env => new ReleaseExpiredHoldsCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    ExtractHoldId(env.Payload),
                    env.MessageId,
                    ReleaseScope.Hold));

            // T6 (spec 07): deposit-expiry capacity release — releases all active holds for the op.
            // Emitted by ProcessDepositEventCommandHandler on AwaitingPayment→Expired (spec 06 §10).
            // HoldId is null; Scope.Operation triggers bulk-release-by-op (intentional).
            registry.Register("capacity.hold_release.requested.v1",
                env => new ReleaseExpiredHoldsCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    null,
                    env.MessageId,
                    ReleaseScope.Operation));

            // Spec 10 T2 — notification delivery lane. These factories are the concrete realization
            // of the spec §5 notification.requested.v1 logical contract (full documentation in T6).
            // Each message type maps an outbox-emitted notification event to a SendNotificationCommand
            // routed through InboxDeduplicationBehavior (consumer "notification.sender").
            //
            // notification.sla_escalation.v1 is intentionally NOT registered here — it is an
            // ops-internal signal; spec 06 already creates the operations_work_item on SLA breach
            // and it is not a customer-facing catalogue message.
            registry.Register("notification.booking_confirmed.v1",
                env => new SendNotificationCommand(env.TenantId, env.OperationId ?? "", env.MessageId,
                    NotificationEvent.BookingConfirmed, MessagePriority.High, env.CorrelationId));
            registry.Register("notification.booking_failed.v1",
                env => new SendNotificationCommand(env.TenantId, env.OperationId ?? "", env.MessageId,
                    NotificationEvent.FinalFailure, MessagePriority.High, env.CorrelationId));
            registry.Register("notification.still_confirming.v1",
                env => new SendNotificationCommand(env.TenantId, env.OperationId ?? "", env.MessageId,
                    NotificationEvent.StillPending, MessagePriority.Normal, env.CorrelationId));
            registry.Register("notification.user_action_required.v1",
                env => new SendNotificationCommand(env.TenantId, env.OperationId ?? "", env.MessageId,
                    NotificationEvent.ActionRequired, MessagePriority.Critical, env.CorrelationId));
            registry.Register("notification.alternatives_required.v1",
                env => new SendNotificationCommand(env.TenantId, env.OperationId ?? "", env.MessageId,
                    NotificationEvent.NoCapacityAlternatives, MessagePriority.High, env.CorrelationId));
            registry.Register("notification.rejected.v1",
                env => new SendNotificationCommand(env.TenantId, env.OperationId ?? "", env.MessageId,
                    NotificationEvent.FinalFailure, MessagePriority.High, env.CorrelationId));
            registry.Register("notification.deposit_expired.v1",
                env => new SendNotificationCommand(env.TenantId, env.OperationId ?? "", env.MessageId,
                    NotificationEvent.FinalFailure, MessagePriority.High, env.CorrelationId));
            // notification.waitlisted.v1 is the informational "added to waitlist" event and is
            // intentionally NOT pushed via this lane — the catalogue's WaitlistOffer is the
            // deadline-bound spec-11 waitlist *match* offer (with {offer_expires_at}), which a
            // waitlist-entry payload cannot fill (Inv-16). Spec 11 will register its own Critical
            // WaitlistOffer message type.

            // T5a (spec 10): durable notification retry — fired by ScheduledJobPollerHostedService
            // after next_eligible_at has passed. notification_id is extracted from the payload.
            registry.Register("notification.retry.requested.v1",
                env => new RetryNotificationCommand(
                    env.TenantId,
                    ExtractNotificationId(env.Payload) ?? string.Empty,
                    env.MessageId));

            // Spec 11 — menu ingestion lane (menu.commands). Realizes the menu.commands.ingest.v1
            // queue contract; routed through InboxDeduplicationBehavior (consumer "menu.processor").
            // catalog_id + source_artifact_ref are extracted from the job payload.
            registry.Register("menu.commands.ingest.v1",
                env => new IngestVendorMenuCommand(
                    env.TenantId,
                    ExtractString(env.Payload, "catalog_id") ?? string.Empty,
                    ExtractString(env.Payload, "source_artifact_ref") ?? string.Empty,
                    env.MessageId,
                    env.CorrelationId));

            return registry;
        });
        services.AddScoped<IQueueConsumerDispatcher, MediatRQueueConsumerDispatcher>();

        // Spec 06 T6 retry/escalation helpers — scoped (share the ambient DbContext / UoW).
        services.AddScoped<RetryScheduler>();
        services.AddScoped<ManualReviewEscalator>();

        // Spec 07 T5 — capacity expiry scheduler; scoped to share the ambient DbContext.
        services.AddScoped<CapacityExpiryScheduler>();

        // T9: SLA evaluator seam — scoped; resolved by SlaEvaluatorHostedService through domain interface
        // so Infrastructure does not reference Application directly.
        services.AddScoped<ISlaEvaluator, MediatRSlaEvaluator>();

        // T4 (spec 10): notification reconcile seam — scoped; resolved by ReconcileNotificationsHostedService
        // through the domain interface so Infrastructure does not reference Application directly.
        services.AddScoped<INotificationReconciler, MediatRNotificationReconciler>();

        // T5b (spec 10): missing-terminal-notification auditor seam — scoped; resolved by
        // MissingNotificationAuditHostedService through the domain interface so Infrastructure
        // does not reference Application directly.
        services.AddScoped<IMissingNotificationAuditor, MediatRMissingNotificationAuditor>();

        // T5a (spec 10): durable retry scheduler — scoped; injected into SendNotificationCommandHandler,
        // ReconcileNotificationCommandHandler, and RetryNotificationCommandHandler.
        services.AddScoped<NotificationRetryScheduler>();

        return services;
    }

    /// <summary>
    /// Safely extracts the <c>hold_id</c> string from a job-payload <see cref="JsonElement"/>.
    /// Returns <see langword="null"/> when the element is Undefined/Null or the property is absent
    /// or not a string — guards against malformed payloads without throwing.
    /// </summary>
    private static string? ExtractHoldId(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        return payload.TryGetProperty("hold_id", out var h) && h.ValueKind == JsonValueKind.String
            ? h.GetString()
            : null;
    }

    /// <summary>
    /// Safely extracts the <c>notification_id</c> string from a job-payload
    /// <see cref="JsonElement"/>. Returns <see langword="null"/> when the element is
    /// Undefined/Null, the property is absent, or the value is not a string.
    /// </summary>
    private static string? ExtractNotificationId(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        return payload.TryGetProperty("notification_id", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()
            : null;
    }

    /// <summary>
    /// Safely extracts a string property named <paramref name="propertyName"/> from a job-payload
    /// <see cref="JsonElement"/>. Returns <see langword="null"/> when the element is Undefined/Null,
    /// the property is absent, or the value is not a string.
    /// </summary>
    private static string? ExtractString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        return payload.TryGetProperty(propertyName, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }
}
