using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.CommitPreorder;

/// <summary>
/// Commits the pre-order and completes the saga (spec 11 §5.3, step 6). The saga row (state
/// <c>Completed</c>, with its immutable <c>PreorderReference</c> + checkpoints) IS the committed
/// pre-order record. Advancing the terminal transition under the version+state guard makes the
/// single combined-outcome notification (Inv-8) emit exactly once: a duplicate delivery finds the
/// saga already Completed and settles without re-notifying. Runs in the inbox transaction.
/// </summary>
public sealed class CommitPreorderCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<CommitPreorderCommandHandler> logger)
    : IRequestHandler<CommitPreorderCommand, Unit>
{
    /// <summary>Combined success outcome: booking + pre-order committed, deposit held (Inv-8).</summary>
    public const string OutcomeConfirmedEventType = "notification.booking_confirmed.v1";

    public async Task<Unit> Handle(CommitPreorderCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId || saga.SagaState != PreorderSagaState.CommittingPreorder)
        {
            logger.LogInformation("Preorder commit: saga {SagaId} not in CommittingPreorder; settling.", command.SagaId);
            return Unit.Value;
        }

        var won = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, command.SagaId, saga.Version,
            PreorderSagaState.CommittingPreorder, PreorderSagaState.Completed, now, ct: ct);
        if (won == 0)
        {
            // Lost the race to a concurrent/duplicate commit — the winner emits the single outcome.
            logger.LogInformation("Preorder commit: saga {SagaId} completed concurrently; settling.", command.SagaId);
            return Unit.Value;
        }

        await outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = command.TenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = OutcomeConfirmedEventType,
            CorrelationId = command.CorrelationId ?? command.OperationId,
            OperationId = command.OperationId,
            Payload = JsonSerializer.Serialize(new
            {
                saga_id = command.SagaId,
                operation_id = command.OperationId,
                deposit_session_id = saga.DepositSessionId,
                preorder = true,
            }),
            OccurredAt = now,
        }, ct);

        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.committed", command.SagaId, command.OperationId, now,
            new { saga_id = command.SagaId, deposit_session_id = saga.DepositSessionId }), ct);
        return Unit.Value;
    }
}
