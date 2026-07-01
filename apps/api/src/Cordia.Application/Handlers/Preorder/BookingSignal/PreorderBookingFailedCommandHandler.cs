using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.BookingSignal;

/// <summary>
/// Handles the 06→saga <c>booking_failed</c> signal (spec 11 §5.3): state/version-guarded advance
/// <c>CommittingBooking → CompensatingItems</c>, then enqueue <c>compensate_items</c> (release the
/// reserved items and emit the one combined failure outcome). Idempotent — settles if the saga is
/// missing, tenant-mismatched, or no longer in <c>CommittingBooking</c> (duplicate/late delivery,
/// Inv-5/Inv-9). Runs inside the inbox transaction; never opens its own.
/// </summary>
public sealed class PreorderBookingFailedCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<PreorderBookingFailedCommandHandler> logger)
    : IRequestHandler<PreorderBookingFailedCommand, Unit>
{
    public async Task<Unit> Handle(PreorderBookingFailedCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetByOperationAsync(command.OperationId, ct);
        if (saga is null || saga.TenantId != command.TenantId || saga.SagaState != PreorderSagaState.CommittingBooking)
        {
            logger.LogInformation(
                "Preorder booking-failed: saga for operation {OperationId} not in CommittingBooking; settling.",
                command.OperationId);
            return Unit.Value;
        }

        var won = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, saga.SagaId, saga.Version,
            PreorderSagaState.CommittingBooking, PreorderSagaState.CompensatingItems, now,
            setFailureReason: "booking_failed", ct: ct);
        if (won == 0)
            return Unit.Value; // concurrent advance — settle

        await outboxRepo.AddAsync(
            PreorderSagaMessages.StepMessage(
                command.TenantId, PreorderSagaMessages.CompensateItems, saga.SagaId, command.OperationId, now, command.CorrelationId), ct);
        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.booking_failed", saga.SagaId, command.OperationId, now), ct);
        return Unit.Value;
    }
}
