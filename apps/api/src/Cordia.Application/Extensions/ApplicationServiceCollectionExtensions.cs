using System.Text.Json;
using Cordia.Application.Behaviours;
using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.AdvanceOperation;
using Cordia.Application.Handlers.BookingFulfillment.DepositEvent;
using Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;
using Cordia.Application.Handlers.BookingFulfillment.Sla;
using Cordia.Application.Handlers.BookingFulfillment.UserAction;
using Cordia.Application.Handlers.Capacity.ReleaseExpiredHolds;
using Cordia.Application.Messaging;
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
            registry.Register("booking.scheduled.hold_expiry.v1",
                env => new ReleaseExpiredHoldsCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    ExtractHoldId(env.Payload),
                    env.MessageId));

            // T6 (spec 07): deposit-expiry capacity release — releases all active holds for the op.
            // Emitted by ProcessDepositEventCommandHandler on AwaitingPayment→Expired (spec 06 §10).
            // HoldId is null because the deposit-expiry event carries only operation_id + tenant_id.
            registry.Register("capacity.hold_release.requested.v1",
                env => new ReleaseExpiredHoldsCommand(
                    env.TenantId,
                    env.OperationId ?? string.Empty,
                    null,
                    env.MessageId));

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
}
