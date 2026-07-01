using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Preorder;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.Reconcile;

/// <summary>
/// Re-checks a stuck saga and either resumes it or escalates (spec 11 §5.3, Inv-7/Inv-9). Each
/// resumable saga-owned step maps to its own message type, so resuming is just re-enqueuing that step
/// — its own state-guard + checkpoints make the re-entry idempotent (a step that actually completed
/// has already advanced, so the re-enqueued message settles as a no-op). A saga waiting on the spec-06
/// engine (<c>CommittingBooking</c>, divergence #2) is nudged by re-emitting
/// <c>booking.command.requested.v1</c> — the 06 driver is idempotent and re-emits its terminal
/// booking-outcome signal if the operation already finished. A saga that never acquired anything
/// (Revalidating) fails clean; one already parked in Reconciling escalates to ManualReview. Runs in
/// the inbox transaction; settles for unknown/terminal sagas.
/// </summary>
public sealed class ReconcilePreorderSagaCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<ReconcilePreorderSagaCommandHandler> logger)
    : IRequestHandler<ReconcilePreorderSagaCommand, Unit>
{
    /// <summary>
    /// Maps a resumable saga-owned state to the step message type that re-drives it. The dead
    /// hold-capacity / authorize-payment / commit-booking / compensate-hold steps were removed
    /// (booking is delegated to 06), so those states are no longer resumable here.
    /// <see cref="PreorderSagaState.CommittingBooking"/> is handled separately (06 re-nudge).
    /// </summary>
    private static string? ResumeMessageType(PreorderSagaState state) => state switch
    {
        PreorderSagaState.ReservingItems => PreorderSagaMessages.ReserveItems,
        PreorderSagaState.CommittingPreorder => PreorderSagaMessages.CommitPreorder,
        PreorderSagaState.CompensatingItems => PreorderSagaMessages.CompensateItems,
        PreorderSagaState.CompensatingBooking => PreorderSagaMessages.CompensateBooking,
        _ => null,
    };

    public async Task<Unit> Handle(ReconcilePreorderSagaCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId)
        {
            logger.LogInformation("Preorder reconcile: saga {SagaId} not found; settling.", command.SagaId);
            return Unit.Value;
        }

        if (saga.SagaState is PreorderSagaState.Completed
            or PreorderSagaState.FailedFinal
            or PreorderSagaState.ManualReview)
        {
            return Unit.Value; // already terminal — nothing to recover
        }

        // Waiting on the spec-06 engine: re-nudge it rather than re-driving a saga step (Inv-7). The
        // 06 driver keys off the operation id and is idempotent; if the operation already reached a
        // terminal outcome it re-signals the saga (booking_committed / booking_failed).
        if (saga.SagaState == PreorderSagaState.CommittingBooking)
        {
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
            await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                command.TenantId, "preorder.reconcile_resumed", command.SagaId, command.OperationId, now,
                new { saga_id = command.SagaId, resumed_state = saga.SagaState.ToString(), renudged_06 = true }), ct);
            return Unit.Value;
        }

        var resume = ResumeMessageType(saga.SagaState);
        if (resume is not null)
        {
            await outboxRepo.AddAsync(
                PreorderSagaMessages.StepMessage(command.TenantId, resume, command.SagaId, command.OperationId, now, command.CorrelationId), ct);
            await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                command.TenantId, "preorder.reconcile_resumed", command.SagaId, command.OperationId, now,
                new { saga_id = command.SagaId, resumed_state = saga.SagaState.ToString() }), ct);
            return Unit.Value;
        }

        // Non-resumable: Revalidating acquired nothing → fail clean; Reconciling is already parked → escalate.
        var next = saga.SagaState == PreorderSagaState.Revalidating
            ? PreorderSagaState.FailedFinal
            : PreorderSagaState.ManualReview;
        PreorderSagaStateMachine.EnsureAllowed(saga.SagaState, next);
        var won = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, command.SagaId, saga.Version, saga.SagaState, next, now,
            setFailureReason: $"reconcile_escalated:{saga.SagaState}", ct: ct);
        if (won == 0)
            return Unit.Value;

        await outboxRepo.AddAsync(PreorderSagaSupport.FailureOutcome(
            command.TenantId, command.SagaId, command.OperationId, $"reconcile_{next}", now, command.CorrelationId), ct);
        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.reconcile_escalated", command.SagaId, command.OperationId, now,
            new { saga_id = command.SagaId, to = next.ToString() }), ct);
        return Unit.Value;
    }
}
