using System.Text.Json;
using Cordia.Application.Services;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Preorder;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.StartSaga;

/// <summary>
/// Starts a pre-order saga and runs the §5.3 live revalidation gate (Inv-10).
///
/// <para><b>Runs inside the inbox transaction</b> — never opens its own. Idempotent: if a saga
/// already exists for the operation, settles (a duplicate/late start). Otherwise it inserts the
/// saga in <c>Revalidating</c>, revalidates every line against the live published menu, and:</para>
/// <list type="bullet">
/// <item>all valid → advance to ReservingItems (items-first for every pre-order) and enqueue that
/// step; audit <c>preorder.revalidated</c>. Booking is delegated to the 06 engine after items reserve.</item>
/// <item>any stale → advance to FailedFinal, emit the AlternativesRequired outcome with the stale
/// lines, audit <c>preorder.revalidation_failed</c>; NOTHING was acquired, so no compensation.</item>
/// </list>
/// </summary>
public sealed class StartPreorderSagaCommandHandler(
    IPreorderSagaRepository sagaRepo,
    LiveItemRevalidationService revalidation,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IScheduledJobRepository jobs,
    ILogger<StartPreorderSagaCommandHandler> logger)
    : IRequestHandler<StartPreorderSagaCommand, Unit>
{
    /// <summary>Outbox event: revalidation found stale items; the customer must re-confirm alternatives (spec 06).</summary>
    public const string AlternativesRequiredEventType = "preorder.alternatives_required.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Unit> Handle(StartPreorderSagaCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Idempotent: a saga already exists for this operation → duplicate/late start.
        var existing = await sagaRepo.GetByOperationAsync(command.OperationId, ct);
        if (existing is not null)
        {
            logger.LogInformation(
                "Preorder start: saga already exists for operation {OperationId}; settling.", command.OperationId);
            return Unit.Value;
        }

        PreorderReference? reference;
        try
        {
            reference = JsonSerializer.Deserialize<PreorderReference>(command.PreorderReferenceJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            // A malformed reference cannot be revalidated or committed; settle (fail-secure, no saga).
            logger.LogWarning(ex, "Preorder start: unparseable PreorderReference for operation {OperationId}; settling.",
                command.OperationId);
            return Unit.Value;
        }

        if (reference?.Lines is null || reference.Lines.Count == 0)
        {
            logger.LogInformation(
                "Preorder start: empty pre-order for operation {OperationId}; settling.", command.OperationId);
            return Unit.Value;
        }

        var sagaId = $"pos_{Guid.NewGuid():N}";
        await sagaRepo.AddAsync(new PreorderSaga
        {
            TenantId = command.TenantId,
            SagaId = sagaId,
            OperationId = command.OperationId,
            SagaState = PreorderSagaState.Revalidating,
            PreorderReference = command.PreorderReferenceJson,
            // Never starts standalone — booking is delegated to the 06 engine (IsStandalone is set only
            // later by the operator convert path, §21.4).
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);

        var result = await revalidation.RevalidateAsync(reference, ct);

        if (result.IsOk)
        {
            // Items-first for every pre-order (§5.3): reserve the food items, then trigger the 06
            // engine, which owns capacity (07), payment (09), and provider-create (06). The saga no
            // longer holds capacity or authorizes payment itself (booking delegated to 06).
            PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.Revalidating, PreorderSagaState.ReservingItems);
            await sagaRepo.TryAdvanceStateAsync(
                command.TenantId, sagaId, expectedVersion: 0, PreorderSagaState.Revalidating, PreorderSagaState.ReservingItems, now, ct: ct);
            await outboxRepo.AddAsync(
                PreorderSagaMessages.StepMessage(command.TenantId, PreorderSagaMessages.ReserveItems, sagaId, command.OperationId, now, command.CorrelationId), ct);
            // Per-step reconcile timeout (§5.3): backstop if the ReserveItems hand-off is lost.
            await PreorderSagaSupport.ScheduleStepTimeoutAsync(
                jobs, command.TenantId, sagaId, command.OperationId, PreorderSagaState.ReservingItems, now, ct);
            await AuditAsync(command.TenantId, "preorder.revalidated", sagaId, command.OperationId, now,
                new { saga_id = sagaId, line_count = reference.Lines.Count }, ct);
            return Unit.Value;
        }

        // Stale: nothing acquired → straight to FailedFinal + AlternativesRequired outcome.
        PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.Revalidating, PreorderSagaState.FailedFinal);
        await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, sagaId, expectedVersion: 0, PreorderSagaState.Revalidating, PreorderSagaState.FailedFinal, now, ct: ct);

        await outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = command.TenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = AlternativesRequiredEventType,
            CorrelationId = command.CorrelationId ?? command.OperationId,
            OperationId = command.OperationId,
            Payload = JsonSerializer.Serialize(new
            {
                saga_id = sagaId,
                operation_id = command.OperationId,
                stale_lines = result.StaleLines.Select(l => new { item_id = l.ItemId, reason = l.Reason }),
            }),
            OccurredAt = now,
        }, ct);

        await AuditAsync(command.TenantId, "preorder.revalidation_failed", sagaId, command.OperationId, now,
            new { saga_id = sagaId, stale = result.StaleLines.Select(l => new { l.ItemId, l.Reason }) }, ct);
        return Unit.Value;
    }

    private Task AuditAsync(
        string tenantId, string eventType, string sagaId, string operationId,
        DateTimeOffset now, object metadata, CancellationToken ct)
        => auditRepo.AddAsync(new AuditEvent
        {
            TenantId = tenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = "system",
            SubjectRef = sagaId,
            CorrelationId = sagaId,
            OperationId = operationId,
            Metadata = JsonSerializer.Serialize(metadata),
            OccurredAt = now,
        }, ct);
}
