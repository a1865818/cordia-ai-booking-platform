using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.CompensateItems;

/// <summary>
/// Releases the reserved pre-order items as compensation (spec 11 §5.3) and terminates the saga at
/// FailedFinal, emitting the single combined failure outcome (Inv-8). The saga holds no capacity of
/// its own — capacity/payment are owned by the 06 engine — so there is no hold to unwind. Release is
/// idempotent (keyed by saga). Runs in the inbox transaction; settles when the saga is missing or no
/// longer in CompensatingItems (Inv-5/Inv-9).
/// </summary>
public sealed class CompensatePreorderItemsCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IPreorderItemReservation items,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<CompensatePreorderItemsCommandHandler> logger)
    : IRequestHandler<CompensatePreorderItemsCommand, Unit>
{
    public async Task<Unit> Handle(CompensatePreorderItemsCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId || saga.SagaState != PreorderSagaState.CompensatingItems)
        {
            logger.LogInformation("Preorder compensate-items: saga {SagaId} not in CompensatingItems; settling.", command.SagaId);
            return Unit.Value;
        }

        await items.ReleaseAsync(command.TenantId, command.SagaId, ct);

        var won = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, command.SagaId, saga.Version,
            PreorderSagaState.CompensatingItems, PreorderSagaState.FailedFinal, now, ct: ct);

        if (won == 0)
            return Unit.Value; // concurrent advance — settle

        await outboxRepo.AddAsync(PreorderSagaSupport.FailureOutcome(
            command.TenantId, command.SagaId, command.OperationId, saga.FailureReason ?? "items_released", now, command.CorrelationId), ct);

        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.compensated_items", command.SagaId, command.OperationId, now,
            new { saga_id = command.SagaId }), ct);
        return Unit.Value;
    }
}
