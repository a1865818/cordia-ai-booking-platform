using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.BookingSignal;

/// <summary>
/// Handles the 06→saga <c>booking_committed</c> signal (spec 11 §5.3): state/version-guarded advance
/// <c>CommittingBooking → CommittingPreorder</c>, then enqueue <c>commit_preorder</c> (the saga binds
/// the pre-order and emits the single combined outcome, Inv-8). Idempotent — settles if the saga is
/// missing, tenant-mismatched, or no longer in <c>CommittingBooking</c> (duplicate/late delivery,
/// Inv-5/Inv-9). Runs inside the inbox transaction; never opens its own.
/// </summary>
public sealed class PreorderBookingCommittedCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IScheduledJobRepository jobs,
    ILogger<PreorderBookingCommittedCommandHandler> logger)
    : IRequestHandler<PreorderBookingCommittedCommand, Unit>
{
    public async Task<Unit> Handle(PreorderBookingCommittedCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetByOperationAsync(command.OperationId, ct);
        if (saga is null || saga.TenantId != command.TenantId || saga.SagaState != PreorderSagaState.CommittingBooking)
        {
            logger.LogInformation(
                "Preorder booking-committed: saga for operation {OperationId} not in CommittingBooking; settling.",
                command.OperationId);
            return Unit.Value;
        }

        var won = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, saga.SagaId, saga.Version,
            PreorderSagaState.CommittingBooking, PreorderSagaState.CommittingPreorder, now, ct: ct);
        if (won == 0)
            return Unit.Value; // concurrent advance — settle

        await outboxRepo.AddAsync(
            PreorderSagaMessages.StepMessage(
                command.TenantId, PreorderSagaMessages.CommitPreorder, saga.SagaId, command.OperationId, now, command.CorrelationId), ct);
        // Per-step reconcile timeout (§5.3): backstop if the CommitPreorder hand-off is lost.
        await PreorderSagaSupport.ScheduleStepTimeoutAsync(
            jobs, command.TenantId, saga.SagaId, command.OperationId, PreorderSagaState.CommittingPreorder, now, ct);
        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.booking_committed", saga.SagaId, command.OperationId, now), ct);
        return Unit.Value;
    }
}
