using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.ReserveItems;

/// <summary>
/// Reserves the pre-order items (spec 11 §5.3, step 2). On success advances to CommittingBooking and
/// triggers the spec-06 operation engine (<c>booking.command.requested.v1</c>) — 06 owns capacity
/// (07), payment (09), and provider-create (06) and signals the saga on its terminal outcome. On
/// Unavailable routes to CompensatingItems (release any partial reservation → FailedFinal; the saga
/// holds no capacity of its own). Runs in the inbox transaction; settles when the saga is missing or
/// no longer in ReservingItems (Inv-5/Inv-9).
/// </summary>
public sealed class ReservePreorderItemsCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IPreorderItemReservation items,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IScheduledJobRepository jobs,
    ILogger<ReservePreorderItemsCommandHandler> logger)
    : IRequestHandler<ReservePreorderItemsCommand, Unit>
{
    public async Task<Unit> Handle(ReservePreorderItemsCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId || saga.SagaState != PreorderSagaState.ReservingItems)
        {
            logger.LogInformation("Preorder reserve-items: saga {SagaId} not in ReservingItems; settling.", command.SagaId);
            return Unit.Value;
        }

        var reference = PreorderSagaSupport.ParseReference(saga.PreorderReference);
        var status = reference is null
            ? ItemReservationStatus.Unavailable
            : await items.ReserveAsync(command.TenantId, command.SagaId, reference, ct);

        if (status == ItemReservationStatus.Reserved)
        {
            // Version+state-guarded advance: if a concurrent/duplicate execution already advanced this
            // step, we lose the race (0 rows) and MUST NOT enqueue the 06 trigger again (that would
            // double-drive the booking) — settle instead.
            var won = await sagaRepo.TryAdvanceStateAsync(
                command.TenantId, command.SagaId, saga.Version,
                PreorderSagaState.ReservingItems, PreorderSagaState.CommittingBooking, now, ct: ct);
            if (won == 0)
            {
                logger.LogInformation("Preorder reserve-items: saga {SagaId} advanced concurrently; settling.", command.SagaId);
                return Unit.Value;
            }

            // Trigger the spec-06 operation engine for this operation. 06 drives
            // Accepted → … → Booked (owning capacity/payment/create) and, because the operation is
            // marked IsPreorder, signals the saga (preorder.commands.booking_committed.v1 /
            // booking_failed.v1) instead of notifying the customer directly. The driver keys off the
            // envelope operation id; the payload carries only non-sensitive routing fields (Inv-12).
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = command.TenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = command.OperationId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = command.CorrelationId ?? command.OperationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = command.OperationId,
                    tenant_id = command.TenantId,
                }),
                OccurredAt = now,
            }, ct);
            // Per-step reconcile timeout (§5.3): the saga now waits on the async 06 engine signal;
            // this is the backstop if that signal is never delivered (the longest window — 06 spans
            // capacity/payment/create).
            await PreorderSagaSupport.ScheduleStepTimeoutAsync(
                jobs, command.TenantId, command.SagaId, command.OperationId, PreorderSagaState.CommittingBooking, now, ct);
            await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                command.TenantId, "preorder.items_reserved", command.SagaId, command.OperationId, now), ct);
            return Unit.Value;
        }

        // Unavailable: release any partial reservation (the saga holds no capacity of its own now
        // that booking is delegated to 06) → CompensatingItems → FailedFinal.
        var advanced = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, command.SagaId, saga.Version,
            PreorderSagaState.ReservingItems, PreorderSagaState.CompensatingItems, now, setFailureReason: "items_unavailable", ct: ct);
        if (advanced == 0)
        {
            logger.LogInformation("Preorder reserve-items: saga {SagaId} advanced concurrently; settling.", command.SagaId);
            return Unit.Value;
        }

        await outboxRepo.AddAsync(
            PreorderSagaMessages.StepMessage(command.TenantId, PreorderSagaMessages.CompensateItems, command.SagaId, command.OperationId, now, command.CorrelationId), ct);

        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.items_unavailable", command.SagaId, command.OperationId, now,
            new { saga_id = command.SagaId }), ct);
        return Unit.Value;
    }
}
